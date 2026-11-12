using System;
using System.IO;
using System.Resources;
using HarmonyLib;
using Players.Utils;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.SignalTransmitter;
using Ships.Physics.Collision;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class AntennaAppearance
{
    internal const float ModelHeight = 1f;
    internal const float BaseLift = .06f;
    internal const float SourceHeight = 36.76522f;
    private static Mesh _mesh;
    private static Material[] _materials;

    internal static void Install(HarmonyLib.Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(ShipStatefulColliders), "GenerateStatefulCollider"),
        postfix: new HarmonyMethod(typeof(AntennaAppearance), nameof(AfterColliderGenerated)));

    internal static void Configure(PartSettings settings, GameObject prefab)
    {
        SignalTransmitterVisuals visual = prefab.GetComponentInChildren<SignalTransmitterVisuals>(true);
        MeshFilter body = visual?.transform.Find("SignalTransmitter")?.GetComponent<MeshFilter>();
        if (!body) throw new MissingMemberException("The native transmitter mesh is unavailable.");
        EnsureMesh(body.GetComponent<MeshRenderer>().sharedMaterial);
        body.name = "EssentialsAntenna";
        body.sharedMesh = _mesh;
        body.GetComponent<MeshRenderer>().sharedMaterials = _materials;
        ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
        bounds.center = _mesh.bounds.center;
        bounds.bounds = _mesh.bounds.size;
        settings.size = _mesh.bounds.size;
        settings.mass = 1f;

        SocketObject socket = prefab.GetComponentInChildren<SocketObject>(true);
        float scale = ModelHeight / SourceHeight;
        socket.transform.localPosition = new Vector3(0f, .039f, -2.6f * scale - .0015f);
        socket.transform.localRotation = Quaternion.identity;
        // The instancer copies these subtrees separately. Scaling only their
        // SocketObject parent produces a full-size socket again in the world.
        socket.visualObject.transform.localScale *= .45f;
        socket.interactive.transform.localScale *= .45f;
        foreach (SphereCollider sphere in socket.interactive.GetComponentsInChildren<SphereCollider>(true)) sphere.radius = .075f;

        Transform canvas = visual.transform.Find("Canvas");
        if (canvas)
        {
            // The donor projects text onto its own surface and changes rotation
            // every frame. Keep this antenna's input horizontal across its front.
            foreach (LookAtPlayerFlat2Sides aligner in canvas.GetComponents<LookAtPlayerFlat2Sides>())
                UnityEngine.Object.DestroyImmediate(aligner);
            canvas.localPosition = new Vector3(0f, .15f, -.084f);
            canvas.localRotation = Quaternion.identity;
            canvas.localScale *= .6f;
        }
        // Retain SignalTransmitterVisuals, its TMP input, and native interaction:
        // channel editing and synchronization continue to use the native path.
        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
        {
            interaction.gameObject.name = settings.fullLabel;
            BoxCollider box = interaction.GetComponent<BoxCollider>();
            if (!box) continue;
            box.transform.localPosition = Vector3.zero;
            ConfigureColliders(box);
        }
    }

    private static void EnsureMesh(Material donor)
    {
        if (_mesh) return;
        using (Stream stream = typeof(AntennaAppearance).Assembly.GetManifestResourceStream("StellarDriveEssentials.Transmitter.Antenna.obj"))
        using (var reader = new StreamReader(stream ?? throw new MissingManifestResourceException("The antenna OBJ is missing.")))
            _mesh = ObjModel.FromObj(reader.ReadToEnd(), "Essentials_StylizedAntenna");
        if (!_mesh || _mesh.subMeshCount != 4 || Math.Abs(_mesh.bounds.size.y - SourceHeight) > .001f)
            throw new InvalidDataException("The antenna model's dimensions or material groups changed.");
        Vector3[] vertices = _mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            double[] point = TransformPoint(v.x, v.y, v.z);
            vertices[i] = new Vector3((float)point[0], (float)point[1], (float)point[2]);
        }
        _mesh.vertices = vertices;
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        _mesh.colors = colors;
        _mesh.RecalculateBounds();
        _mesh.RecalculateNormals();
        _mesh.RecalculateTangents();
        _mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _materials = new[]
        {
            TimerAppearance.MakeMaterial(donor, "AntennaLightMetal", new Color(.70f, .75f, .79f), .45f, .5f),
            TimerAppearance.MakeMaterial(donor, "AntennaBrightMetal", new Color(.83f, .86f, .88f), .6f, .4f),
            TimerAppearance.MakeMaterial(donor, "AntennaJunctionBoxes", new Color(.13f, .20f, .24f), .1f, .8f),
            TimerAppearance.MakeMaterial(donor, "AntennaInk", new Color(.035f, .045f, .055f), 0f, 1f)
        };
    }

    internal static double[] TransformPoint(double x, double y, double z)
    {
        double scale = ModelHeight / (double)SourceHeight;
        // Enlarge the base's straight band to fit the reduced socket, keeping the
        // mast and antenna arrays at the supplied proportions above it.
        return new[] { x * scale, y * scale + BaseLift * Math.Max(0d, Math.Min(1d, y / .7d)), z * scale };
    }

    private static void AfterColliderGenerated(StatefulPart part, Transform __result)
    {
        if (part == null || ReferenceEquals(part.Settings, null) || part.Settings.id != IntershipSignals.PartId || !__result) return;
        ConfigureColliders(__result.GetComponent<BoxCollider>());
    }

    private static void ConfigureColliders(BoxCollider original)
    {
        if (!original) return;
        // Separate low-cost boxes leave the air around the mast open, including
        // the socket at the base. A full antenna-sized box would hide that socket.
        double[][] boxes = CollisionBoxes();
        for (int i = 0; i < boxes.Length; i++)
        {
            BoxCollider box = i == 0 ? original : original.gameObject.AddComponent<BoxCollider>();
            double[] b = boxes[i];
            box.center = new Vector3((float)b[0], (float)b[1], (float)b[2]);
            box.size = new Vector3((float)b[3], (float)b[4], (float)b[5]);
            if (i == 0) continue;
            box.sharedMaterial = original.sharedMaterial;
            box.isTrigger = original.isTrigger;
            box.contactOffset = original.contactOffset;
            box.enabled = original.enabled;
        }
    }

    internal static double[][] CollisionBoxes()
    {
        double s = ModelHeight / (double)SourceHeight;
        return new[]
        {
            new[] {0d, (.7d*s+BaseLift)*.5d, 0d, 5.2d*s, .7d*s+BaseLift, 5.2d*s},
            new[] {0d, 17.6d*s+BaseLift, 0d, .96d*s, 34.4d*s, .84d*s},
            new[] {0d, 13.646465d*s+BaseLift, .12222d*s, 34.4d*s, 5.60405d*s, 7.35556d*s},
            new[] {0d, 21.95d*s+BaseLift, 0d, 13.6d*s, 9.0244d*s, 3.546d*s},
            new[] {0d, 31.78261d*s+BaseLift, 2.9d*s, 7.7697d*s, 9.96522d*s, 10.8182d*s}
        };
    }

    internal static void Release()
    {
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        if (_materials != null)
            foreach (Material material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _mesh = null;
        _materials = null;
    }
}
