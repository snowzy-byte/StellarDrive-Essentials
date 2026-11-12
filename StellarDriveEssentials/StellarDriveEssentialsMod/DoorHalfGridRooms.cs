using System;
using System.Collections.Generic;
using HarmonyLib;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using Ships.Interface.Values;
using Ships.Rooms;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class DoorHalfGridRooms
{
    private static Func<ShipRooms, HullPart, Vector3Int[]> _blockedMask;
    private static Func<ShipRooms, HullPart, Vector3Int[]> _scanMask;
    private static Action<ShipRooms, Vector3Int[], Vector3, Quaternion> _convertRelative;

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        if (ShipRoomsConstants.RoomVoxelResolution != 4)
            throw new NotSupportedException("Half-block door seals require the native four-voxel room grid.");
        _blockedMask = AccessTools.MethodDelegate<Func<ShipRooms, HullPart, Vector3Int[]>>(
            AccessTools.Method(typeof(ShipRooms), "GetBlockedOutChunkVoxels"));
        _scanMask = AccessTools.MethodDelegate<Func<ShipRooms, HullPart, Vector3Int[]>>(
            AccessTools.Method(typeof(ShipRooms), "GetRoomScanVoxels"));
        _convertRelative = AccessTools.MethodDelegate<Action<ShipRooms, Vector3Int[], Vector3, Quaternion>>(
            AccessTools.Method(typeof(ShipRooms), "ConvertRelativeVoxel"));

        Patch(harmony, "InitializeStatefulPart", nameof(BeforeInitialize));
        Patch(harmony, "RemoveStatefulPart", nameof(BeforeRemove));
        Patch(harmony, "OnStatefulPartStateChange", nameof(BeforeStateChange));
        Patch(harmony, "InitializeRelativeStatefulPart", nameof(BeforeRelativeInitialize));
        Patch(harmony, "RemoveRelativeStatefulPart", nameof(BeforeRelativeRemove));
        Patch(harmony, "OnRelativeStatefulPartStateChange", nameof(BeforeRelativeStateChange));
    }

    private static void Patch(HarmonyLib.Harmony harmony, string native, string prefix) =>
        harmony.Patch(AccessTools.Method(typeof(ShipRooms), native), prefix: new HarmonyMethod(typeof(DoorHalfGridRooms), prefix));

    internal static bool IsFloatingDoor(StatefulPart part) => HalfGridDoors.IsHalfGridDoor(part);

    private static bool BeforeInitialize(ShipRooms __instance, StatefulPart part, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part)) return true;
        Apply(__instance, part, ____voxelUpdater, true, true, !((IDoorState)part.State).IsOpen, false, default, default);
        return false;
    }

    private static bool BeforeRemove(ShipRooms __instance, StatefulPart part, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part)) return true;
        Apply(__instance, part, ____voxelUpdater, false, true, !((IDoorState)part.State).IsOpen, false, default, default);
        return false;
    }

    private static bool BeforeStateChange(ShipRooms __instance, StatefulPartStateChangeEvent change, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(change.Part)) return true;
        ApplyStateChange(__instance, change, ____voxelUpdater, false, default, default);
        return false;
    }

    private static bool BeforeRelativeInitialize(ShipRooms __instance, StatefulPart part, Vector3 offset, Quaternion rotation, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part)) return true;
        Apply(__instance, part, ____voxelUpdater, true, true, !((IDoorState)part.State).IsOpen, true, offset, rotation);
        return false;
    }

    private static bool BeforeRelativeRemove(ShipRooms __instance, StatefulPart part, Vector3 offset, Quaternion rotation, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(part)) return true;
        Apply(__instance, part, ____voxelUpdater, false, true, !((IDoorState)part.State).IsOpen, true, offset, rotation);
        return false;
    }

    private static bool BeforeRelativeStateChange(ShipRooms __instance, StatefulPartStateChangeEvent change, Vector3 offset, Quaternion rotation, ShipRoomsVoxelUpdater ____voxelUpdater)
    {
        if (!IsFloatingDoor(change.Part)) return true;
        ApplyStateChange(__instance, change, ____voxelUpdater, true, offset, rotation);
        return false;
    }

    private static void ApplyStateChange(ShipRooms rooms, StatefulPartStateChangeEvent change, ShipRoomsVoxelUpdater updater,
        bool relative, Vector3 offset, Quaternion rotation)
    {
        if (!(change.PreviousState is IDoorState previous) || !(change.NewState is IDoorState current)
            || previous.IsOpen == current.IsOpen)
            return;
        // The fixed half-width panels are independent of the animated central door.
        Apply(rooms, change.Part, updater, !current.IsOpen, false, true, relative, offset, rotation);
    }

    private static void Apply(ShipRooms rooms, StatefulPart part, ShipRoomsVoxelUpdater updater, bool add,
        bool panels, bool centralDoor, bool relative, Vector3 offset, Quaternion rotation)
    {
        FloatingPartPlacement placement = (FloatingPartPlacement)part.Placement;
        Vector3Int[] walls;
        Vector3Int[] scans;
        if (centralDoor)
        {
            // Reuse the native exact room raster at an origin aligned to the room grid.
            // A four-voxel cardinal transform then preserves the original raster multiplicities.
            HullPart prototype = new HullPart((HullPartSettings)part.Settings,
                new HullPartPlacement(new Vector3Int(0, 0, 0), VoxelPartType.Interior, PartOrientation.Forward, PartOrientation.Up));
            BuildRoomMasks(placement, _blockedMask(rooms, prototype), _scanMask(rooms, prototype), panels, true, out walls, out scans);
        }
        else
        {
            BuildRoomMasks(placement, Array.Empty<Vector3Int>(), Array.Empty<Vector3Int>(), panels, false, out walls, out scans);
        }
        if (relative)
        {
            // Native offset already uses room-voxel units; do not multiply it again.
            _convertRelative(rooms, walls, offset, rotation);
            _convertRelative(rooms, scans, offset, rotation);
        }
        if (add) updater.AddHullVoxel(walls, scans);
        else updater.RemoveHullVoxel(walls, scans);
    }

    // Pure integer/cardinal geometry, usable by offline checks without Unity rotation calls.
    // Prototypes use native origin/Interior/Forward/Up coordinates, including its +2 grid-center bias.
    internal static void BuildRoomMasks(FloatingPartPlacement placement, Vector3Int[] nativeDoorWalls,
        Vector3Int[] nativeDoorScans, bool includePanels, bool includeDoor, out Vector3Int[] walls, out Vector3Int[] scans)
    {
        if (!HalfGridDoors.IsHalfPlacement(placement))
            throw new ArgumentException("Not a supported half-block door placement.", nameof(placement));
        var wallList = new List<Vector3Int>();
        var scanList = new List<Vector3Int>();
        if (includeDoor)
        {
            foreach (Vector3Int point in nativeDoorWalls) wallList.Add(TransformNativeMaskPoint(point, placement));
            foreach (Vector3Int point in nativeDoorScans) scanList.Add(TransformNativeMaskPoint(point, placement));
        }
        if (includePanels)
        {
            // Fixed panels are exactly x=[-1,-.5] and [1.5,2], y=[-.5,1.5], z=0.
            // Keep edge duplicates: native room WallCount tracks overlapping surfaces by count.
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

    internal static Vector3Int TransformNativeMaskPoint(Vector3Int point, FloatingPartPlacement placement) =>
        TransformLocalQuarterPoint(point.x - 2, point.y - 2, point.z - 2, placement);

    private static Vector3Int TransformLocalQuarterPoint(int x, int y, int z, FloatingPartPlacement placement)
    {
        int rotatedX;
        int rotatedZ;
        switch (placement.ForwardOrientation)
        {
            case PartOrientation.Forward: rotatedX = x; rotatedZ = z; break;
            case PartOrientation.Right: rotatedX = z; rotatedZ = -x; break;
            case PartOrientation.Back: rotatedX = -x; rotatedZ = -z; break;
            case PartOrientation.Left: rotatedX = -z; rotatedZ = x; break;
            default: throw new ArgumentOutOfRangeException(nameof(placement));
        }
        return new Vector3Int(rotatedX + Quarter(placement.Coord.x) + 2,
            y + Quarter(placement.Coord.y) + 2, rotatedZ + Quarter(placement.Coord.z) + 2);
    }

    private static int Quarter(float value)
    {
        double scaled = value * 4d;
        double rounded = Math.Round(scaled);
        if (double.IsNaN(scaled) || double.IsInfinity(scaled) || Math.Abs(scaled - rounded) > .0001d
            || rounded < int.MinValue + 16d || rounded > int.MaxValue - 16d)
            throw new ArgumentOutOfRangeException(nameof(value), "Door coordinate must align with the quarter-block room grid.");
        return (int)rounded;
    }
}
