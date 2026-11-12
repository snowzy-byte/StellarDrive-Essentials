using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Managers.Interface.Model;
using Ships;
using Ships.Grid;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using Ships.Physics.Proxies;
using Ships.Services;
using Tools.Build;
using Tools.Build.Model;
using Tools.Build.PlacementSnapDetection;
using UnityEngine;

namespace StellarDriveEssentials;

// Placement-only checks. No collision settings, inventory rules or foreign
// prefabs are changed. Client and server use the same antenna compound shape.
internal static class PlacementValidation
{
    [ThreadStatic] private static PartSettings _previewSettings;
    [ThreadStatic] private static bool _queryingCompound;
    private static readonly MethodInfo GridGetter = AccessTools.PropertyGetter(typeof(TrackedShip), "Grid");

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        Type service = AccessTools.TypeByName("Managers.Server.Services.ShipInteractionService")
            ?? throw new TypeLoadException("The native placement service is unavailable.");
        MethodInfo build = FindRpc(service, "PlaceFloatingPart_ServerRPC");
        MethodInfo held = FindRpc(service, "PlaceHeldPart_ServerRPC");
        harmony.Patch(build, transpiler: new HarmonyMethod(typeof(PlacementValidation), nameof(ServerPlacementTranspiler)));
        harmony.Patch(held, transpiler: new HarmonyMethod(typeof(PlacementValidation), nameof(ServerPlacementTranspiler)));
        harmony.Patch(AccessTools.Method(typeof(PartPreviewDetection), "TryFindSnapInRayHit"),
            prefix: new HarmonyMethod(typeof(PlacementValidation), nameof(BeforeRayPreview)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(PlacementValidation), nameof(AfterPreview)));
        harmony.Patch(AccessTools.Method(typeof(PlacementSnapDetector), "DetectPlacement"),
            prefix: new HarmonyMethod(typeof(PlacementValidation), nameof(BeforePositionPreview)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(PlacementValidation), nameof(AfterPreview)));
        harmony.Patch(AccessTools.Method(typeof(ShipPartsAvailabilityProxy), nameof(ShipPartsAvailabilityProxy.IsFloatingPlacementFree)),
            prefix: new HarmonyMethod(typeof(PlacementValidation), nameof(BeforeClientAvailability)));
    }

    internal static MethodInfo FindRpc(Type service, string name)
    {
        MethodInfo[] matches = service.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name.StartsWith("RpcLogic___" + name + "___", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new MissingMethodException(service.FullName, name);
        return matches[0];
    }

    private static void BeforeRayPreview(SnapDetectionState state, out PartSettings __state)
    {
        __state = _previewSettings;
        _previewSettings = state.PartSettings;
    }

    private static void BeforePositionPreview(PlacementSnapDetectionProperties props, out PartSettings __state)
    {
        __state = _previewSettings;
        _previewSettings = props.PartSettings;
    }

    private static Exception AfterPreview(Exception __exception, PartSettings __state)
    {
        _previewSettings = __state;
        return __exception;
    }

    internal static bool UseCompound(PartSettings settings, Bounds bounds) => !ReferenceEquals(settings, null)
        && settings.id == IntershipSignals.PartId && bounds.Equals(settings.Bounds);

    private static bool BeforeClientAvailability(ShipPartsAvailabilityProxy __instance, FloatingPartPlacement placement,
        Bounds bounds, PartContext? ignoredPart, ref bool __result)
    {
        if (_queryingCompound || !UseCompound(_previewSettings, bounds)) return true;
        _queryingCompound = true;
        try
        {
            // Usual case: one native query. Only inspect separate mechanical
            // volumes when the enclosing AABB actually finds an obstruction.
            if (__instance.IsFloatingPlacementFree(placement, bounds, ignoredPart))
            {
                __result = true;
                return false;
            }
            __result = true;
            foreach (Bounds box in AntennaBounds())
                if (!__instance.IsFloatingPlacementFree(placement, box, ignoredPart)) { __result = false; break; }
        }
        finally { _queryingCompound = false; }
        return false;
    }

    internal static IEnumerable<Bounds> AntennaBounds()
    {
        foreach (double[] b in AntennaAppearance.CollisionBoxes())
            yield return new Bounds(new Vector3((float)b[0], (float)b[1], (float)b[2]),
                new Vector3((float)b[3], (float)b[4], (float)b[5]));
    }

    // Replace only the hull-type rejection and the physics call inside the
    // original RPC; player, inventory and native part creation stay in place.
    internal static IEnumerable<CodeInstruction> ServerPlacementTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        bool building = __originalMethod.GetParameters()[0].ParameterType == typeof(PlaceFloatingPartNetworkRequest);
        LocalVariableInfo settings = __originalMethod.GetMethodBody().LocalVariables.Single(v => v.LocalType == (building ? typeof(PartSettings) : typeof(StatefulPart)));
        FieldInfo ships = AccessTools.Field(__originalMethod.DeclaringType, "_ships");
        MethodInfo native = AccessTools.Method(typeof(TrackedShipServer), nameof(TrackedShipServer.IsFloatingPartPlacementFree));
        int guards = 0, queries = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (building && instruction.opcode == OpCodes.Isinst && Equals(instruction.operand, typeof(HullPartSettings)))
            {
                var first = new CodeInstruction(OpCodes.Ldarg_1); first.MoveLabelsFrom(instruction); first.MoveBlocksFrom(instruction);
                yield return first;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, ships);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlacementValidation), nameof(RejectFloatingHull)));
                guards++;
            }
            else if (instruction.Calls(native))
            {
                var first = new CodeInstruction(OpCodes.Ldloc, checked((short)settings.LocalIndex)); first.MoveLabelsFrom(instruction); first.MoveBlocksFrom(instruction);
                yield return first;
                if (!building) yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(StatefulPart), nameof(StatefulPart.Settings)));
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlacementValidation), nameof(IsServerPlacementFree)));
                queries++;
            }
            else yield return instruction;
        }
        if (queries != 1 || guards != (building ? 1 : 0))
            throw new InvalidOperationException("The native placement RPC layout changed; no partial placement patch is supported.");
    }

    internal static bool RejectFloatingHull(PartSettings settings, PlaceFloatingPartNetworkRequest request, ShipsServerTracker ships)
    {
        if (!(settings is HullPartSettings)) return false;
        if (!HalfGridDoors.IsDoorSettings(settings) || !HalfGridDoors.IsHalfPlacement(request.Placement)) return true;
        TrackedShipServer ship = ships?.GetShip(request.ShipId);
        ShipGrid grid = ship == null ? null : (ShipGrid)GridGetter.Invoke(ship, null);
        if (grid == null) return true;
        foreach (var slot in HalfGridDoors.CoveredSlots(request.Placement))
            if (!grid.IsHullAvailable(slot)) return true;
        return false;
    }

    internal static bool IsServerPlacementFree(TrackedShipServer ship, FloatingPartPlacement placement, Bounds bounds,
        PartContext? ignoredPart, PartSettings settings)
    {
        if (UseCompound(settings, bounds))
        {
            if (ship.IsFloatingPartPlacementFree(placement, bounds, ignoredPart)) return true;
            foreach (Bounds box in AntennaBounds())
                if (!ship.IsFloatingPartPlacementFree(placement, box, ignoredPart)) return false;
            return true;
        }
        if (HalfGridDoors.IsDoorSettings(settings) && HalfGridDoors.IsHalfPlacement(placement))
            bounds.size = new Vector3(3f, bounds.size.y, bounds.size.z);
        return ship.IsFloatingPartPlacementFree(placement, bounds, ignoredPart);
    }
}
