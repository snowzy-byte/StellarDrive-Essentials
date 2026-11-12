using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships.Cables;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Placement;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class SocketAnchors
{
    private sealed class Anchor { internal Vector3 Local; }
    private static ConditionalWeakTable<SocketObject, Anchor> _anchors = new ConditionalWeakTable<SocketObject, Anchor>();

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ConnectionsTracker), "GetSocketPosition",
            new[] { typeof(IPartPlacement), typeof(SocketObject) }),
            prefix: new HarmonyMethod(typeof(SocketAnchors), nameof(BeforePosition)));
        EssentialsParts.PartsReady += TrackParts;
    }

    private static void TrackParts()
    {
        for (ushort id = Instruments.DistanceId; id <= LidarSensor.PartId; id++)
        {
            var settings = EssentialsParts.Get(id);
            if (!settings || !settings.part) continue;
            TrackPrefab(settings.part);
        }
    }

    internal static void TrackPrefab(GameObject prefab)
    {
        foreach (SocketObject socket in prefab.GetComponentsInChildren<SocketObject>(true))
        {
            // Compose local transforms instead of subtracting world positions:
            // the SDK stores prefabs 100 km below the origin, losing float precision.
            Vector3 local = Vector3.zero;
            Transform transform = socket.transform;
            while (transform != prefab.transform)
            {
                if (!transform) throw new InvalidOperationException("Socket is outside its Essentials prefab.");
                local = transform.localPosition + transform.localRotation * Vector3.Scale(transform.localScale, local);
                transform = transform.parent;
            }
            Track(socket, local);
        }
    }

    internal static void Track(SocketObject socket, Vector3 local)
    {
        _anchors.Remove(socket);
        _anchors.Add(socket, new Anchor { Local = local });
    }

    private static bool BeforePosition(IPartPlacement placement, SocketObject socketObject, ref Vector3 __result)
    {
        if (ReferenceEquals(socketObject, null) || !_anchors.TryGetValue(socketObject, out Anchor anchor)) return true;
        __result = placement.LocalPosition + placement.Rotation * anchor.Local;
        return false;
    }

    internal static void Release()
    {
        EssentialsParts.PartsReady -= TrackParts;
        _anchors = new ConditionalWeakTable<SocketObject, Anchor>();
    }
}
