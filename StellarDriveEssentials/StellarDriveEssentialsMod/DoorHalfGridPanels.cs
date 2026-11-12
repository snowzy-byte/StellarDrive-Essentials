using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using Ships.Parts.Common.Hull;
using Ships.Parts.Common.Model.Reactions;
using Ships.Physics.Collision;
using Ships.Visuals;
using Ships.Services;
using UnityEngine;
using Ships.Visuals.Model;

namespace StellarDriveEssentials;

internal static class DoorHalfGridPanels
{
    private const string VisualName = "Essentials_HalfGridSidePanels";
    private const string ColliderName = "Essentials_HalfGridSideColliders";
    private static readonly Vector3[] Centers = { new Vector3(-.75f, .5f, 0f), new Vector3(1.75f, .5f, 0f) };
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
        if (AccessTools.Field(typeof(ShipPartVisuals), "_floatingParts") == null
            || AccessTools.Field(typeof(HullMeshBuilder), "meshSegments") == null)
            throw new MissingMemberException("The native door/wall visual layout has changed.");
        harmony.Patch(AccessTools.Method(typeof(ShipPartVisuals), nameof(ShipPartVisuals.AddStatefulPart)),
            postfix: new HarmonyMethod(typeof(DoorHalfGridPanels), nameof(AfterVisualAdd)));
        harmony.Patch(AccessTools.Method(typeof(ShipStatefulColliders), "GenerateStatefulCollider"),
            postfix: new HarmonyMethod(typeof(DoorHalfGridPanels), nameof(AfterColliderGenerated)));
        // Only placed-part visuals receive side walls; the native preview stays unchanged.
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
                GameObject child = new GameObject(slice.Mesh.name) { layer = model.layer };
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
        HullMeshSegmentsSettings segments = Resources.FindObjectsOfTypeAll<HullMeshSegmentsSettings>()
            .FirstOrDefault(s => s && s.Flat && s.flatMetalMaterial && s.edgeMetalMaterial);
        HullPartSettings wall = Resources.FindObjectsOfTypeAll<HullPartSettings>().FirstOrDefault(s => s
            && !s.hasItsOwnMesh && s.hullMaterialType == HullMaterialType.Metal && s.hullMeshSegmentType == HullMeshSegmentType.Flat);
        if (!segments || !wall)
            throw new MissingMemberException("Native iron-wall geometry/material assets are unavailable.");

        GameObject owner = new GameObject("Essentials_IronWallMeshBuilder") { hideFlags = HideFlags.HideAndDontSave };
        var sources = new List<Mesh>();
        try
        {
            // An independent builder avoids changing the live ship-mesh service buffers.
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
                Mesh combined = new Mesh { name = "Essentials_IronSideWall_" + pair.Key.Type, hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    combined.CombineMeshes(new[]
                    {
                        new CombineInstance { mesh = pair.Value.Mesh, transform = PanelTransform(false) },
                        new CombineInstance { mesh = pair.Value.Mesh, transform = PanelTransform(true) }
                    }, true, true);
                    if (pair.Key.Type == HullChunkMeshSliceType.Flat)
                    {
                        Vector3[] vertices = combined.vertices;
                        var uv = new Vector2[vertices.Length];
                        for (int index = 0; index < vertices.Length; index++)
                            uv[index] = new Vector2(vertices[index].x * segments.flatUvScaling.x + segments.flatUvOffset.x,
                                vertices[index].y * segments.flatUvScaling.y + segments.flatUvOffset.y);
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

    private static Matrix4x4 PanelTransform(bool right) => Matrix4x4.TRS(
        new Vector3(right ? 1.75f : -.75f, 0f, -.5f), Quaternion.identity, new Vector3(.5f, 1f, 1f));

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
