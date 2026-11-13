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
using System.Linq;
using Ships.Parts.Common.Hull;
using Ships.Parts.Common.Model.Reactions;
using Ships.Physics.Collision;
using Ships.Visuals;
using Ships.Services;
using Ships.Visuals.Model;
using Ships.Interface.Values;
using Ships.Rooms;
using System.Reflection;
using System.Reflection.Emit;
using Managers.Interface.Model;
using Ships;
using Ships.Physics.Proxies;

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
        harmony.Patch(AccessTools.Method(typeof(PartPreviewDetection), "TryFindSnapInRayHit"), postfix: new HarmonyMethod(typeof(HalfGridDoors), nameof(AfterRaySnap)));
        harmony.Patch(AccessTools.Method(typeof(PlacementSnapDetector), "DetectPlacement"), postfix: new HarmonyMethod(typeof(HalfGridDoors), nameof(AfterPositionSnap)));
        harmony.Patch(AccessTools.Method(typeof(ShipGrid), nameof(ShipGrid.AddStatefulPart)), postfix: new HarmonyMethod(typeof(HalfGridDoors), nameof(AfterGridAdd)));
        harmony.Patch(AccessTools.Method(typeof(ShipGrid), nameof(ShipGrid.RemoveStatefulPart)), prefix: new HarmonyMethod(typeof(HalfGridDoors), nameof(BeforeGridRemove)));
        harmony.Patch(AccessTools.Method(typeof(ShipGrid), nameof(ShipGrid.IsHullAvailable), new[] { typeof(HullPartPosition) }), prefix: new HarmonyMethod(typeof(HalfGridDoors), nameof(BeforeAvailability)));
        PlacementValidation.Install(harmony);
    }

    internal static bool IsDoorSettings(PartSettings settings) => !ReferenceEquals(settings, null) && settings is HullPartSettings && (settings.internalStateClass == "DoorState" || settings.internalStateClass == "DockingDoorState") && settings.size.x == 2f && settings.size.y == 2f && settings.size.z == 1f;
    internal static bool IsHalfGridDoor(StatefulPart part) => part != null && part.State is IDoorState && IsDoorSettings(part.Settings) && part.Placement is FloatingPartPlacement placement && IsHalfPlacement(placement);
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
            case PartOrientation.Forward:
                return new Vector3(0f, 0f, 1f);
            case PartOrientation.Right:
                return new Vector3(1f, 0f, 0f);
            case PartOrientation.Back:
                return new Vector3(0f, 0f, -1f);
            case PartOrientation.Left:
                return new Vector3(-1f, 0f, 0f);
            default:
                throw new ArgumentOutOfRangeException(nameof(forward));
        }
    }

    internal static Vector3 Right(PartOrientation forward)
    {
        Vector3 normal = Forward(forward);
        return new Vector3(normal.z, 0f, -normal.x);
    }

    private static void AfterRaySnap(SnapDetectionState state, RaycastHit hit, ref Pivot? pivot, ref IPartPlacement partPlacement, ref bool __result)
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
        if (IsDoorSettings(props.PartSettings) && __result is HullPartPlacement hull && TrySnap(hull, props.Pos, props.PartSettings, props.ShipParts, out FloatingPartPlacement placement))
            __result = placement;
    }

    private static bool TrySnap(HullPartPlacement original, Vector3 aim, PartSettings settings, IShipPartPlacements ship, out FloatingPartPlacement placement, PartContext? ignoredPart = null)
    {
        if (!TryGetHalfPlacement(original, aim, out placement))
            return false;
        foreach (HullPartPosition slot in CoveredSlots(placement))
            if (!ship.IsHullPartAvailable(slot))
                return false;
        Bounds bounds = settings.Bounds;
        bounds.size = new Vector3(3f, bounds.size.y, bounds.size.z);
        return ship.IsFloatingPlacementFree(placement, bounds, ignoredPart);
    }

    internal static bool TryGetHalfPlacement(HullPartPlacement original, Vector3 aim, out FloatingPartPlacement placement)
    {
        placement = default;
        if (!IsCardinal(original.ForwardOrientation) || original.UpOrientation != PartOrientation.Up || (original.Position.Type != VoxelPartType.ForwardWall && original.Position.Type != VoxelPartType.RightWall))
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
                Vector3Int coord = forward.z != 0f ? new Vector3Int((int)Math.Round(right.x * tangent), y + vertical, normal) : new Vector3Int(normal, y + vertical, (int)Math.Round(right.z * tangent));
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

internal static class DoorHalfGridPanels
{
    private const string VisualName = "Essentials_HalfGridSidePanels";
    private const string ColliderName = "Essentials_HalfGridSideColliders";
    private static readonly Vector3[] Centers =
    {
        new Vector3(-.75f, .5f, 0f),
        new Vector3(1.75f, .5f, 0f)
    };
    private static readonly Vector3 PanelSize = new Vector3(.5f, 2f, .2f);
    private static readonly List<WallSlice> WallSlices = new List<WallSlice>();
    private static readonly List<GameObject> Created = new List<GameObject>();
    private static readonly HashSet<string> Reported = new HashSet<string>();
    private static Action<string> _log;
    private sealed class WallSlice
    {
        internal Mesh Mesh;
        internal Material Material;
    }

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log;
        if (AccessTools.Field(typeof(ShipPartVisuals), "_floatingParts") == null || AccessTools.Field(typeof(HullMeshBuilder), "meshSegments") == null)
            throw new MissingMemberException("The native door/wall visual layout has changed.");
        harmony.Patch(AccessTools.Method(typeof(ShipPartVisuals), nameof(ShipPartVisuals.AddStatefulPart)), postfix: new HarmonyMethod(typeof(DoorHalfGridPanels), nameof(AfterVisualAdd)));
        harmony.Patch(AccessTools.Method(typeof(ShipStatefulColliders), "GenerateStatefulCollider"), postfix: new HarmonyMethod(typeof(DoorHalfGridPanels), nameof(AfterColliderGenerated)));
    }

    private static void AfterVisualAdd(StatefulPart part, Dictionary<ushort, IShipPartVisual> ____floatingParts)
    {
        if (!HalfGridDoors.IsHalfGridDoor(part))
            return;
        try
        {
            if (____floatingParts.TryGetValue(part.Id, out IShipPartVisual visual) && visual?.GameObject != null)
                CreateVisualPanels(visual.GameObject);
        }
        catch (Exception error)
        {
            ReportOnce("visual", error);
        }
    }

    private static void AfterColliderGenerated(StatefulPart part, Transform __result)
    {
        if (!HalfGridDoors.IsHalfGridDoor(part) || !__result || __result.Find(ColliderName))
            return;
        GameObject holder = null;
        try
        {
            Collider donor = __result.GetComponentInChildren<Collider>(true);
            int layer = LayerMask.NameToLayer("Ship");
            if (layer < 0)
                layer = __result.gameObject.layer;
            holder = new GameObject(ColliderName);
            holder.transform.SetParent(__result, false);
            holder.layer = layer;
            holder.tag = "ShipFloatingPart";
            for (int index = 0; index < Centers.Length; index++)
            {
                GameObject child = new GameObject(index == 0 ? "Left fixed panel" : "Right fixed panel");
                child.transform.SetParent(holder.transform, false);
                child.transform.localPosition = Centers[index];
                child.layer = layer;
                child.tag = "ShipFloatingPart";
                BoxCollider box = child.AddComponent<BoxCollider>();
                box.size = PanelSize;
                if (donor)
                    box.sharedMaterial = donor.sharedMaterial;
            }

            Created.Add(holder);
        }
        catch (Exception error)
        {
            if (holder)
                UnityEngine.Object.Destroy(holder);
            ReportOnce("collision", error);
        }
    }

    private static GameObject CreateVisualPanels(GameObject model)
    {
        Transform existing = model.transform.Find(VisualName);
        if (existing)
            return existing.gameObject;
        EnsureIronWallMeshes();
        GameObject panels = new GameObject(VisualName);
        try
        {
            panels.transform.SetParent(model.transform, false);
            panels.layer = model.layer;
            foreach (WallSlice slice in WallSlices)
            {
                GameObject child = new GameObject(slice.Mesh.name)
                {
                    layer = model.layer
                };
                child.transform.SetParent(panels.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = slice.Mesh;
                child.AddComponent<MeshRenderer>().sharedMaterial = slice.Material;
            }

            Created.Add(panels);
            return panels;
        }
        catch
        {
            UnityEngine.Object.Destroy(panels);
            throw;
        }
    }

    private static void EnsureIronWallMeshes()
    {
        if (WallSlices.Count != 0 && WallSlices.All(s => s.Mesh && s.Material))
            return;
        ClearWallMeshes();
        HullMeshSegmentsSettings segments = Resources.FindObjectsOfTypeAll<HullMeshSegmentsSettings>().FirstOrDefault(s => s && s.Flat && s.flatMetalMaterial && s.edgeMetalMaterial);
        HullPartSettings wall = Resources.FindObjectsOfTypeAll<HullPartSettings>().FirstOrDefault(s => s && !s.hasItsOwnMesh && s.hullMaterialType == HullMaterialType.Metal && s.hullMeshSegmentType == HullMeshSegmentType.Flat);
        if (!segments || !wall)
            throw new MissingMemberException("Native iron-wall geometry/material assets are unavailable.");
        GameObject owner = new GameObject("Essentials_IronWallMeshBuilder")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        var sources = new List<Mesh>();
        try
        {
            HullMeshBuilder builder = owner.AddComponent<HullMeshBuilder>();
            AccessTools.Field(typeof(HullMeshBuilder), "meshSegments").SetValue(builder, segments);
            var parts = new[]
            {
                new HullPart(wall, new HullPartPlacement(new Vector3Int(0, 0, 0), VoxelPartType.ForwardWall, PartOrientation.Forward, PartOrientation.Up)),
                new HullPart(wall, new HullPartPlacement(new Vector3Int(0, 1, 0), VoxelPartType.ForwardWall, PartOrientation.Forward, PartOrientation.Up))
            };
            var generated = builder.BuildChunkMesh(new Vector3Int(0, 0, 0), parts, Array.Empty<HullPart>());
            foreach (var pair in generated)
                sources.Add(pair.Value.Mesh);
            foreach (var pair in generated)
            {
                Mesh combined = new Mesh
                {
                    name = "Essentials_IronSideWall_" + pair.Key.Type,
                    hideFlags = HideFlags.HideAndDontSave
                };
                try
                {
                    combined.CombineMeshes(new[] { new CombineInstance { mesh = pair.Value.Mesh, transform = PanelTransform(false) }, new CombineInstance { mesh = pair.Value.Mesh, transform = PanelTransform(true) } }, true, true);
                    if (pair.Key.Type == HullChunkMeshSliceType.Flat)
                    {
                        Vector3[] vertices = combined.vertices;
                        var uv = new Vector2[vertices.Length];
                        for (int index = 0; index < vertices.Length; index++)
                            uv[index] = new Vector2(vertices[index].x * segments.flatUvScaling.x + segments.flatUvOffset.x, vertices[index].y * segments.flatUvScaling.y + segments.flatUvOffset.y);
                        combined.uv = uv;
                    }

                    WallSlices.Add(new WallSlice { Mesh = combined, Material = segments.GetSliceMaterial(pair.Key) });
                }
                catch
                {
                    UnityEngine.Object.Destroy(combined);
                    throw;
                }
            }

            if (WallSlices.Count == 0)
                throw new InvalidOperationException("Native wall generation produced no geometry.");
        }
        catch
        {
            ClearWallMeshes();
            throw;
        }
        finally
        {
            foreach (Mesh source in sources)
                if (source)
                    UnityEngine.Object.Destroy(source);
            UnityEngine.Object.Destroy(owner);
        }
    }

    private static Matrix4x4 PanelTransform(bool right) => Matrix4x4.TRS(new Vector3(right ? 1.75f : -.75f, 0f, -.5f), Quaternion.identity, new Vector3(.5f, 1f, 1f));
    private static void ReportOnce(string stage, Exception error)
    {
        if (Reported.Add(stage))
            _log?.Invoke("Half-grid door side panels could not be added (" + stage + "): " + error);
    }

    private static void ClearWallMeshes()
    {
        foreach (WallSlice slice in WallSlices)
            if (slice.Mesh)
                UnityEngine.Object.Destroy(slice.Mesh);
        WallSlices.Clear();
    }

    internal static void Release()
    {
        foreach (GameObject item in Created)
            if (item)
                UnityEngine.Object.Destroy(item);
        Created.Clear();
        ClearWallMeshes();
        Reported.Clear();
    }
}

internal static class DoorHalfGridRooms
{
    private static Func<ShipRooms, HullPart, Vector3Int[]> _blockedMask;
    private static Func<ShipRooms, HullPart, Vector3Int[]> _scanMask;
    private static Action<ShipRooms, Vector3Int[], Vector3, Quaternion> _convertRelative;
    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        if (ShipRoomsConstants.RoomVoxelResolution != 4)
            throw new NotSupportedException("Half-block door seals require the native four-voxel room grid.");
        _blockedMask = AccessTools.MethodDelegate<Func<ShipRooms, HullPart, Vector3Int[]>>(AccessTools.Method(typeof(ShipRooms), "GetBlockedOutChunkVoxels"));
        _scanMask = AccessTools.MethodDelegate<Func<ShipRooms, HullPart, Vector3Int[]>>(AccessTools.Method(typeof(ShipRooms), "GetRoomScanVoxels"));
        _convertRelative = AccessTools.MethodDelegate<Action<ShipRooms, Vector3Int[], Vector3, Quaternion>>(AccessTools.Method(typeof(ShipRooms), "ConvertRelativeVoxel"));
        Patch(harmony, "InitializeStatefulPart", nameof(BeforeInitialize));
        Patch(harmony, "RemoveStatefulPart", nameof(BeforeRemove));
        Patch(harmony, "OnStatefulPartStateChange", nameof(BeforeStateChange));
        Patch(harmony, "InitializeRelativeStatefulPart", nameof(BeforeRelativeInitialize));
        Patch(harmony, "RemoveRelativeStatefulPart", nameof(BeforeRelativeRemove));
        Patch(harmony, "OnRelativeStatefulPartStateChange", nameof(BeforeRelativeStateChange));
    }

    private static void Patch(HarmonyLib.Harmony harmony, string native, string prefix) => harmony.Patch(AccessTools.Method(typeof(ShipRooms), native), prefix: new HarmonyMethod(typeof(DoorHalfGridRooms), prefix));
    internal static bool IsFloatingDoor(StatefulPart part) => HalfGridDoors.IsHalfGridDoor(part);
    private static bool BeforeInitialize(ShipRooms __instance, StatefulPart part, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part))
            return true;
        Apply(__instance, part, ____voxelUpdater, true, true, !((IDoorState)part.State).IsOpen, false, default, default);
        return false;
    }

    private static bool BeforeRemove(ShipRooms __instance, StatefulPart part, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part))
            return true;
        Apply(__instance, part, ____voxelUpdater, false, true, !((IDoorState)part.State).IsOpen, false, default, default);
        return false;
    }

    private static bool BeforeStateChange(ShipRooms __instance, StatefulPartStateChangeEvent change, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(change.Part))
            return true;
        ApplyStateChange(__instance, change, ____voxelUpdater, false, default, default);
        return false;
    }

    private static bool BeforeRelativeInitialize(ShipRooms __instance, StatefulPart part, Vector3 offset, Quaternion rotation, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part))
            return true;
        Apply(__instance, part, ____voxelUpdater, true, true, !((IDoorState)part.State).IsOpen, true, offset, rotation);
        return false;
    }

    private static bool BeforeRelativeRemove(ShipRooms __instance, StatefulPart part, Vector3 offset, Quaternion rotation, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part))
            return true;
        Apply(__instance, part, ____voxelUpdater, false, true, !((IDoorState)part.State).IsOpen, true, offset, rotation);
        return false;
    }

    private static bool BeforeRelativeStateChange(ShipRooms __instance, StatefulPartStateChangeEvent change, Vector3 offset, Quaternion rotation, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(change.Part))
            return true;
        ApplyStateChange(__instance, change, ____voxelUpdater, true, offset, rotation);
        return false;
    }

    private static void ApplyStateChange(ShipRooms rooms, StatefulPartStateChangeEvent change, ShipRoomsVoxelUpdater updater, bool relative, Vector3 offset, Quaternion rotation)
    {
        if (!(change.PreviousState is IDoorState previous) || !(change.NewState is IDoorState current) || previous.IsOpen == current.IsOpen)
            return;
        Apply(rooms, change.Part, updater, !current.IsOpen, false, true, relative, offset, rotation);
    }

    private static void Apply(ShipRooms rooms, StatefulPart part, ShipRoomsVoxelUpdater updater, bool add, bool panels, bool centralDoor, bool relative, Vector3 offset, Quaternion rotation)
    {
        FloatingPartPlacement placement = (FloatingPartPlacement)part.Placement;
        Vector3Int[] walls;
        Vector3Int[] scans;
        if (centralDoor)
        {
            HullPart prototype = new HullPart((HullPartSettings)part.Settings, new HullPartPlacement(new Vector3Int(0, 0, 0), VoxelPartType.Interior, PartOrientation.Forward, PartOrientation.Up));
            BuildRoomMasks(placement, _blockedMask(rooms, prototype), _scanMask(rooms, prototype), panels, true, out walls, out scans);
        }
        else
        {
            BuildRoomMasks(placement, Array.Empty<Vector3Int>(), Array.Empty<Vector3Int>(), panels, false, out walls, out scans);
        }

        if (relative)
        {
            _convertRelative(rooms, walls, offset, rotation);
            _convertRelative(rooms, scans, offset, rotation);
        }

        if (add)
            updater.AddHullVoxel(walls, scans);
        else
            updater.RemoveHullVoxel(walls, scans);
    }

    internal static void BuildRoomMasks(FloatingPartPlacement placement, Vector3Int[] nativeDoorWalls, Vector3Int[] nativeDoorScans, bool includePanels, bool includeDoor, out Vector3Int[] walls, out Vector3Int[] scans)
    {
        if (!HalfGridDoors.IsHalfPlacement(placement))
            throw new ArgumentException("Not a supported half-block door placement.", nameof(placement));
        var wallList = new List<Vector3Int>();
        var scanList = new List<Vector3Int>();
        if (includeDoor)
        {
            foreach (Vector3Int point in nativeDoorWalls)
                wallList.Add(TransformNativeMaskPoint(point, placement));
            foreach (Vector3Int point in nativeDoorScans)
                scanList.Add(TransformNativeMaskPoint(point, placement));
        }

        if (includePanels)
        {
            AddPanel(-4, -2, placement, wallList, scanList);
            AddPanel(6, 8, placement, wallList, scanList);
        }

        walls = wallList.ToArray();
        scans = scanList.ToArray();
    }

    private static void AddPanel(int startX, int endX, FloatingPartPlacement placement, List<Vector3Int> walls, List<Vector3Int> scans)
    {
        for (int x = startX; x <= endX; x++)
            for (int y = -2; y <= 6; y++)
            {
                walls.Add(TransformLocalQuarterPoint(x, y, 0, placement));
                scans.Add(TransformLocalQuarterPoint(x, y, -1, placement));
                scans.Add(TransformLocalQuarterPoint(x, y, 1, placement));
            }
    }

    internal static Vector3Int TransformNativeMaskPoint(Vector3Int point, FloatingPartPlacement placement) => TransformLocalQuarterPoint(point.x - 2, point.y - 2, point.z - 2, placement);
    private static Vector3Int TransformLocalQuarterPoint(int x, int y, int z, FloatingPartPlacement placement)
    {
        int rotatedX;
        int rotatedZ;
        switch (placement.ForwardOrientation)
        {
            case PartOrientation.Forward:
                rotatedX = x;
                rotatedZ = z;
                break;
            case PartOrientation.Right:
                rotatedX = z;
                rotatedZ = -x;
                break;
            case PartOrientation.Back:
                rotatedX = -x;
                rotatedZ = -z;
                break;
            case PartOrientation.Left:
                rotatedX = -z;
                rotatedZ = x;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(placement));
        }

        return new Vector3Int(rotatedX + Quarter(placement.Coord.x) + 2, y + Quarter(placement.Coord.y) + 2, rotatedZ + Quarter(placement.Coord.z) + 2);
    }

    private static int Quarter(float value)
    {
        double scaled = value * 4d;
        double rounded = Math.Round(scaled);
        if (double.IsNaN(scaled) || double.IsInfinity(scaled) || Math.Abs(scaled - rounded) > .0001d || rounded < int.MinValue + 16d || rounded > int.MaxValue - 16d)
            throw new ArgumentOutOfRangeException(nameof(value), "Door coordinate must align with the quarter-block room grid.");
        return (int)rounded;
    }
}

internal static class PlacementValidation
{
    [ThreadStatic]
    private static PartSettings _previewSettings;
    [ThreadStatic]
    private static bool _queryingCompound;
    private static readonly MethodInfo GridGetter = AccessTools.PropertyGetter(typeof(TrackedShip), "Grid");
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        Type service = AccessTools.TypeByName("Managers.Server.Services.ShipInteractionService") ?? throw new TypeLoadException("The native placement service is unavailable.");
        MethodInfo build = FindRpc(service, "PlaceFloatingPart_ServerRPC");
        MethodInfo held = FindRpc(service, "PlaceHeldPart_ServerRPC");
        harmony.Patch(build, transpiler: new HarmonyMethod(typeof(PlacementValidation), nameof(ServerPlacementTranspiler)));
        harmony.Patch(held, transpiler: new HarmonyMethod(typeof(PlacementValidation), nameof(ServerPlacementTranspiler)));
        harmony.Patch(AccessTools.Method(typeof(PartPreviewDetection), "TryFindSnapInRayHit"), prefix: new HarmonyMethod(typeof(PlacementValidation), nameof(BeforeRayPreview)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(PlacementValidation), nameof(AfterPreview)));
        harmony.Patch(AccessTools.Method(typeof(PlacementSnapDetector), "DetectPlacement"), prefix: new HarmonyMethod(typeof(PlacementValidation), nameof(BeforePositionPreview)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(PlacementValidation), nameof(AfterPreview)));
        harmony.Patch(AccessTools.Method(typeof(ShipPartsAvailabilityProxy), nameof(ShipPartsAvailabilityProxy.IsFloatingPlacementFree)), prefix: new HarmonyMethod(typeof(PlacementValidation), nameof(BeforeClientAvailability)));
    }

    internal static MethodInfo FindRpc(Type service, string name)
    {
        MethodInfo[] matches = service.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Where(m => m.Name.StartsWith("RpcLogic___" + name + "___", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new MissingMethodException(service.FullName, name);
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

    internal static bool UseCompound(PartSettings settings, Bounds bounds) => !ReferenceEquals(settings, null) && settings.id == IntershipSignals.PartId && bounds.Equals(settings.Bounds);
    private static bool BeforeClientAvailability(ShipPartsAvailabilityProxy __instance, FloatingPartPlacement placement, Bounds bounds, PartContext? ignoredPart, ref bool __result)
    {
        if (_queryingCompound || !UseCompound(_previewSettings, bounds))
            return true;
        _queryingCompound = true;
        try
        {
            if (__instance.IsFloatingPlacementFree(placement, bounds, ignoredPart))
            {
                __result = true;
                return false;
            }

            __result = true;
            foreach (Bounds box in AntennaBounds())
                if (!__instance.IsFloatingPlacementFree(placement, box, ignoredPart))
                {
                    __result = false;
                    break;
                }
        }
        finally
        {
            _queryingCompound = false;
        }

        return false;
    }

    internal static IEnumerable<Bounds> AntennaBounds()
    {
        foreach (double[] b in AntennaAppearance.CollisionBoxes())
            yield return new Bounds(new Vector3((float)b[0], (float)b[1], (float)b[2]), new Vector3((float)b[3], (float)b[4], (float)b[5]));
    }

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
                var first = new CodeInstruction(OpCodes.Ldarg_1);
                first.MoveLabelsFrom(instruction);
                first.MoveBlocksFrom(instruction);
                yield return first;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, ships);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlacementValidation), nameof(RejectFloatingHull)));
                guards++;
            }
            else if (instruction.Calls(native))
            {
                var first = new CodeInstruction(OpCodes.Ldloc, checked((short)settings.LocalIndex));
                first.MoveLabelsFrom(instruction);
                first.MoveBlocksFrom(instruction);
                yield return first;
                if (!building)
                    yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(StatefulPart), nameof(StatefulPart.Settings)));
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PlacementValidation), nameof(IsServerPlacementFree)));
                queries++;
            }
            else
                yield return instruction;
        }

        if (queries != 1 || guards != (building ? 1 : 0))
            throw new InvalidOperationException("The native placement RPC layout changed; no partial placement patch is supported.");
    }

    internal static bool RejectFloatingHull(PartSettings settings, PlaceFloatingPartNetworkRequest request, ShipsServerTracker ships)
    {
        if (!(settings is HullPartSettings))
            return false;
        if (!HalfGridDoors.IsDoorSettings(settings) || !HalfGridDoors.IsHalfPlacement(request.Placement))
            return true;
        TrackedShipServer ship = ships?.GetShip(request.ShipId);
        ShipGrid grid = ship == null ? null : (ShipGrid)GridGetter.Invoke(ship, null);
        if (grid == null)
            return true;
        foreach (var slot in HalfGridDoors.CoveredSlots(request.Placement))
            if (!grid.IsHullAvailable(slot))
                return true;
        return false;
    }

    internal static bool IsServerPlacementFree(TrackedShipServer ship, FloatingPartPlacement placement, Bounds bounds, PartContext? ignoredPart, PartSettings settings)
    {
        if (UseCompound(settings, bounds))
        {
            if (ship.IsFloatingPartPlacementFree(placement, bounds, ignoredPart))
                return true;
            foreach (Bounds box in AntennaBounds())
                if (!ship.IsFloatingPartPlacementFree(placement, box, ignoredPart))
                    return false;
            return true;
        }

        if (HalfGridDoors.IsDoorSettings(settings) && HalfGridDoors.IsHalfPlacement(placement))
            bounds.size = new Vector3(3f, bounds.size.y, bounds.size.z);
        return ship.IsFloatingPartPlacementFree(placement, bounds, ignoredPart);
    }
}
