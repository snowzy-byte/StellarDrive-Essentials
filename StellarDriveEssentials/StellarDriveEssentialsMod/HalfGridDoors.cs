using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships.Grid;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Tools.Build;
using Tools.Build.Model;
using Tools.Build.PlacementSnapDetection;
using Tools.Build.SnapDetectors.Model;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class HalfGridDoors
{
    private static ConditionalWeakTable<ShipGrid, Occupancy> _occupancy = new ConditionalWeakTable<ShipGrid, Occupancy>();

    private sealed class Occupancy
    {
        internal readonly Dictionary<ushort, HullPartPosition[]> Parts = new Dictionary<ushort, HullPartPosition[]>();
        internal readonly Dictionary<HullPartPosition, int> Slots = new Dictionary<HullPartPosition, int>();
    }

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        harmony.Patch(AccessTools.Method(typeof(PartPreviewDetection), "TryFindSnapInRayHit"),
            postfix: new HarmonyMethod(typeof(HalfGridDoors), nameof(AfterRaySnap)));
        harmony.Patch(AccessTools.Method(typeof(PlacementSnapDetector), "DetectPlacement"),
            postfix: new HarmonyMethod(typeof(HalfGridDoors), nameof(AfterPositionSnap)));
        harmony.Patch(AccessTools.Method(typeof(ShipGrid), nameof(ShipGrid.AddStatefulPart)),
            postfix: new HarmonyMethod(typeof(HalfGridDoors), nameof(AfterGridAdd)));
        harmony.Patch(AccessTools.Method(typeof(ShipGrid), nameof(ShipGrid.RemoveStatefulPart)),
            prefix: new HarmonyMethod(typeof(HalfGridDoors), nameof(BeforeGridRemove)));
        harmony.Patch(AccessTools.Method(typeof(ShipGrid), nameof(ShipGrid.IsHullAvailable), new[] { typeof(HullPartPosition) }),
            prefix: new HarmonyMethod(typeof(HalfGridDoors), nameof(BeforeAvailability)));
        PlacementValidation.Install(harmony);
    }

    internal static bool IsDoorSettings(PartSettings settings) => !ReferenceEquals(settings, null)
        && settings is HullPartSettings && (settings.internalStateClass == "DoorState" || settings.internalStateClass == "DockingDoorState")
        && settings.size.x == 2f && settings.size.y == 2f && settings.size.z == 1f;

    internal static bool IsHalfGridDoor(StatefulPart part) => part != null && part.State is IDoorState
        && IsDoorSettings(part.Settings) && part.Placement is FloatingPartPlacement placement && IsHalfPlacement(placement);

    internal static bool IsHalfPlacement(FloatingPartPlacement placement)
    {
        if (placement.UpOrientation != PartOrientation.Up || !IsCardinal(placement.ForwardOrientation))
            return false;
        Vector3 right = Right(placement.ForwardOrientation);
        Vector3 forward = Forward(placement.ForwardOrientation);
        double tangent = Dot(placement.Coord, right);
        double normal = Dot(placement.Coord, forward);
        return NearInteger(tangent - .5) && NearInteger(placement.Coord.y) && NearInteger(normal - .5);
    }

    private static bool NearInteger(double value) => Math.Abs(value - Math.Round(value)) < .0001;
    private static double Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    internal static bool IsCardinal(PartOrientation forward) => forward >= PartOrientation.Forward && forward <= PartOrientation.Left;
    internal static Vector3 Forward(PartOrientation forward)
    {
        switch (forward)
        {
            case PartOrientation.Forward: return new Vector3(0f, 0f, 1f);
            case PartOrientation.Right: return new Vector3(1f, 0f, 0f);
            case PartOrientation.Back: return new Vector3(0f, 0f, -1f);
            case PartOrientation.Left: return new Vector3(-1f, 0f, 0f);
            default: throw new ArgumentOutOfRangeException(nameof(forward));
        }
    }
    internal static Vector3 Right(PartOrientation forward)
    {
        Vector3 normal = Forward(forward);
        return new Vector3(normal.z, 0f, -normal.x);
    }

    private static void AfterRaySnap(SnapDetectionState state, RaycastHit hit,
        ref Pivot? pivot, ref IPartPlacement partPlacement, ref bool __result)
    {
        if (!__result || !IsDoorSettings(state.PartSettings) || !(partPlacement is HullPartPlacement hull))
            return;
        IShipPartPlacements ship = hit.collider.GetComponentInParent<IShipPartPlacements>();
        if (ship != null && TrySnap(hull, ship.ConvertHitToLocalHit(hit).Pos, state.PartSettings, ship, out FloatingPartPlacement placement, state.IgnoredPart))
        {
            partPlacement = placement;
            pivot = null;
        }
    }

    private static void AfterPositionSnap(PlacementSnapDetectionProperties props, ref IPartPlacement __result)
    {
        if (IsDoorSettings(props.PartSettings) && __result is HullPartPlacement hull
            && TrySnap(hull, props.Pos, props.PartSettings, props.ShipParts, out FloatingPartPlacement placement))
            __result = placement;
    }

    private static bool TrySnap(HullPartPlacement original, Vector3 aim, PartSettings settings,
        IShipPartPlacements ship, out FloatingPartPlacement placement, PartContext? ignoredPart = null)
    {
        if (!TryGetHalfPlacement(original, aim, out placement))
            return false;
        foreach (HullPartPosition slot in CoveredSlots(placement))
            if (!ship.IsHullPartAvailable(slot))
                return false;
        // Native bounds collision also rejects existing floating doors/equipment.
        Bounds bounds = settings.Bounds;
        bounds.size = new Vector3(3f, bounds.size.y, bounds.size.z);
        return ship.IsFloatingPlacementFree(placement, bounds, ignoredPart);
    }

    internal static bool TryGetHalfPlacement(HullPartPlacement original, Vector3 aim, out FloatingPartPlacement placement)
    {
        placement = default;
        if (!IsCardinal(original.ForwardOrientation) || original.UpOrientation != PartOrientation.Up
            || (original.Position.Type != VoxelPartType.ForwardWall && original.Position.Type != VoxelPartType.RightWall))
            return false;
        Vector3 right = Right(original.ForwardOrientation);
        Vector3 origin = original.LocalPosition;
        double wanted = Math.Round(Dot(aim, right) * 2d, MidpointRounding.AwayFromZero) / 2d;
        double delta = wanted - Dot(origin, right);
        if (Math.Abs(Math.Abs(delta) - .5d) > .0001d)
            return false;
        Vector3 shifted = new Vector3(origin.x + right.x * (float)delta, origin.y, origin.z + right.z * (float)delta);
        placement = new FloatingPartPlacement(shifted, original.ForwardOrientation, original.UpOrientation);
        return IsHalfPlacement(placement);
    }

    internal static HullPartPosition[] CoveredSlots(FloatingPartPlacement placement)
    {
        Vector3 right = Right(placement.ForwardOrientation);
        Vector3 forward = Forward(placement.ForwardOrientation);
        Vector3 origin = placement.Coord;
        // Door + two fixed half-block panels occupy exactly three adjacent wall cells.
        double firstTangent = Math.Round(Dot(origin, right) - .5d);
        int y = (int)Math.Round(origin.y);
        int normal = forward.z != 0f ? (int)Math.Floor(origin.z) : (int)Math.Floor(origin.x);
        VoxelPartType type = forward.z != 0f ? VoxelPartType.ForwardWall : VoxelPartType.RightWall;
        var slots = new HullPartPosition[6];
        int index = 0;
        for (int horizontal = 0; horizontal < 3; horizontal++)
            for (int vertical = 0; vertical < 2; vertical++)
            {
                double tangent = firstTangent + horizontal;
                Vector3Int coord = forward.z != 0f
                    ? new Vector3Int((int)Math.Round(right.x * tangent), y + vertical, normal)
                    : new Vector3Int(normal, y + vertical, (int)Math.Round(right.z * tangent));
                slots[index++] = new HullPartPosition(coord, type);
            }
        return slots;
    }

    private static Occupancy NewOccupancy(ShipGrid grid) => new Occupancy();

    private static void AfterGridAdd(ShipGrid __instance, StatefulPart part)
    {
        if (!IsHalfGridDoor(part))
            return;
        Occupancy cache = _occupancy.GetValue(__instance, NewOccupancy);
        RemoveSlots(cache, part.Id);
        HullPartPosition[] slots = CoveredSlots((FloatingPartPlacement)part.Placement);
        cache.Parts.Add(part.Id, slots);
        foreach (HullPartPosition slot in slots)
        {
            cache.Slots.TryGetValue(slot, out int count);
            cache.Slots[slot] = count + 1;
        }
    }

    private static void BeforeGridRemove(ShipGrid __instance, StatefulPart part)
    {
        if (_occupancy.TryGetValue(__instance, out Occupancy cache))
            RemoveSlots(cache, part.Id);
    }

    private static void RemoveSlots(Occupancy cache, ushort id)
    {
        if (!cache.Parts.TryGetValue(id, out HullPartPosition[] slots))
            return;
        cache.Parts.Remove(id);
        foreach (HullPartPosition slot in slots)
            if (cache.Slots[slot] <= 1)
                cache.Slots.Remove(slot);
            else
                cache.Slots[slot]--;
    }

    private static bool BeforeAvailability(ShipGrid __instance, HullPartPosition hullPartPosition, ref bool __result)
    {
        if (_occupancy.TryGetValue(__instance, out Occupancy cache) && cache.Slots.ContainsKey(hullPartPosition))
        {
            __result = false;
            return false;
        }
        return true;
    }

    internal static void Release() => _occupancy = new ConditionalWeakTable<ShipGrid, Occupancy>();
}
