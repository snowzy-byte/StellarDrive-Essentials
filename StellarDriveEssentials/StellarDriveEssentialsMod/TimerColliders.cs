using System;
using HarmonyLib;
using Ships.Interface.Model.Parts;
using Ships.Physics.Collision;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class TimerColliders
{
    private static Mesh _mesh;

    internal static void Install(HarmonyLib.Harmony harmony) =>
        harmony.Patch(AccessTools.Method(typeof(ShipStatefulColliders), "GenerateStatefulCollider"),
            postfix: new HarmonyMethod(typeof(TimerColliders), nameof(AfterColliderGenerated)));

    internal static void EnsureMesh(float apothem, float height)
    {
        if (_mesh) return;
        double[][] points = GetHullVertices(apothem, height);
        var vertices = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
            vertices[i] = new Vector3((float)points[i][0], (float)points[i][1], (float)points[i][2]);
        var indices = new int[84];
        int at = 0;
        for (int i = 0; i < 8; i++)
        {
            int next = (i + 1) % 8;
            indices[at++] = i; indices[at++] = i + 8; indices[at++] = next;
            indices[at++] = next; indices[at++] = i + 8; indices[at++] = next + 8;
        }
        for (int i = 1; i < 7; i++)
        {
            indices[at++] = 0; indices[at++] = i; indices[at++] = i + 1;
            indices[at++] = 8; indices[at++] = i + 9; indices[at++] = i + 8;
        }
        _mesh = new Mesh { name = "EssentialsTimerOctagonalCollider", hideFlags = HideFlags.DontUnloadUnusedAsset,
            vertices = vertices, triangles = indices };
        _mesh.RecalculateBounds();
    }

    internal static double[][] GetHullVertices(double apothem, double height)
    {
        var vertices = new double[16][];
        double radius = apothem / Math.Cos(Math.PI / 8d);
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.PI / 8d + i * Math.PI / 4d;
            double x = Math.Cos(angle) * radius, z = Math.Sin(angle) * radius;
            vertices[i] = new[] { x, 0d, z };
            vertices[i + 8] = new[] { x, height, z };
        }
        return vertices;
    }

    internal static void ReplaceBox(BoxCollider box)
    {
        if (!box || !_mesh) return;
        var collider = box.gameObject.AddComponent<MeshCollider>();
        collider.enabled = false;
        collider.convex = true;
        collider.sharedMesh = _mesh;
        collider.sharedMaterial = box.sharedMaterial;
        collider.isTrigger = box.isTrigger;
        collider.contactOffset = box.contactOffset;
        bool enabled = box.enabled;
        UnityEngine.Object.DestroyImmediate(box);
        collider.enabled = enabled;
    }

    private static void AfterColliderGenerated(StatefulPart part, Transform __result)
    {
        if (part == null || ReferenceEquals(part.Settings, null)
            || (part.Settings.id != SignalTimer.PartId && part.Settings.id != SignalDelay.PartId) || !__result) return;
        if (part.Settings.id == SignalDelay.PartId) DelayAppearance.ConfigureColliders(__result.GetComponent<BoxCollider>());
        else ReplaceBox(__result.GetComponent<BoxCollider>());
    }

    internal static void Release()
    {
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        _mesh = null;
    }
}
