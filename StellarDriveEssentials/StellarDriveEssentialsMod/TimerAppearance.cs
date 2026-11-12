using System;
using System.IO;
using System.Linq;
using System.Resources;
using System.Globalization;
using HarmonyLib;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using TMPro;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class TimerAppearance
{
    private const float LayoutScale = .65f;
    private const float SocketScale = .45f;
    private const float BaseLift = .038f;
    private static Mesh _mesh;
    private static Material[] _materials;
    private static Material _screenMaterial;
    private static Material _displayFrameMaterial;
    private static Mesh _displayMesh;
    private static float _socketApothem;
    private static float _topSurfaceY;
    private static readonly Vector3 BodyCenter = new Vector3(0f, .215f, 0f);
    private static readonly Vector3 BodySize = new Vector3(.278f, .43f, .278f);

    internal static void Configure(PartSettings settings, GameObject prefab)
        => Configure(settings, prefab, false);

    internal static void Configure(PartSettings settings, GameObject prefab, bool delay)
    {
        settings.fullLabel = delay ? "Signal Delay" : "Signal Timer";
        settings.description = delay
            ? "IN: signal to delay. TIME: delay in seconds (0 to 86400); connect a Constant, or leave unplugged for 5 s. OUT: replays every input change after that delay, including short pulses, negative values and zero. Changing TIME restarts the delay and clears pending changes. Pending changes reset on pickup or save reload."
            : "IN: signal to hold. TIME: hold duration in seconds (0 to 86400); connect a Constant, or leave unplugged for 5 s. OUT: follows IN immediately and keeps its last nonzero value for the set time after IN turns off. A new signal refreshes the hold. Running countdowns reset when reloading the save.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(MathGateState);
        settings.customCreationClass = string.Empty;
        settings.mass = .5f;
        settings.size = BodySize;
        ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
        if (bounds == null) throw new MissingMemberException("The timer donor bounds are unavailable.");
        bounds.center = BodyCenter;
        bounds.bounds = BodySize;
        IShipPartVisual original = prefab.GetComponentInChildren<IShipPartVisual>(true);
        if (original == null || original.GetType().Name != "SignalSplitterVisuals")
            throw new MissingMemberException("The timer donor visual layout has changed.");
        GameObject root = original.GameObject;
        MeshFilter body = root.transform.Find("VelocitySensor")?.GetComponent<MeshFilter>();
        if (body == null) throw new MissingMemberException("The timer donor mesh is unavailable.");
        EnsureMeshAndMaterials(body.GetComponent<MeshRenderer>().sharedMaterial);
        TimerColliders.EnsureMesh(_socketApothem, BodySize.y);
        body.name = "Hourglass";
        body.sharedMesh = _mesh;
        body.GetComponent<MeshRenderer>().sharedMaterials = _materials;
        Transform arrow = root.transform.Find("Arrow");
        if (arrow) UnityEngine.Object.DestroyImmediate(arrow.gameObject);
        UnityEngine.Object.DestroyImmediate((MonoBehaviour)original);
        TimerReadoutVisuals visuals = root.AddComponent<TimerReadoutVisuals>();
        visuals.IsDelay = delay;
        CreateDisplayWindow(root);
        visuals.Screen = Instruments.CreateLabel(root, "TimerCountdown", "0.0 s",
            new Vector3(0f, _topSurfaceY + .0011f, 0f), Quaternion.Euler(90f, 0f, 0f), .0036f);
        TMP_FontAsset font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f && f.name == "LiberationSans SDF");
        if (font)
        {
            visuals.Screen.font = font;
            visuals.Screen.fontSharedMaterial = font.material;
        }
        visuals.Screen.color = Color.white;
        visuals.Screen.fontStyle = FontStyles.Normal;
        visuals.Screen.fontWeight = FontWeight.Regular;
        visuals.Screen.characterSpacing = 1f;
        visuals.Screen.rectTransform.anchorMin = visuals.Screen.rectTransform.anchorMax = new Vector2(.5f, .5f);
        visuals.Screen.rectTransform.pivot = new Vector2(.5f, .5f);
        visuals.Screen.rectTransform.anchoredPosition = Vector2.zero;
        visuals.Screen.rectTransform.sizeDelta = new Vector2(36f, 10f);
        visuals.Screen.fontSizeMin = 5.5f;
        visuals.Screen.fontSizeMax = 8.5f;
        visuals.Screen.margin = new Vector4(1f, 0f, 1f, 0f);
        visuals.Screen.alignment = TextAlignmentOptions.Center;
        SocketObject[] sockets = prefab.GetComponentsInChildren<SocketObject>(true);
        if (sockets.Length != 1 || sockets[0].Id != 0 || sockets[0].Type != CableType.Signal)
            throw new MissingMemberException("The timer donor signal socket has changed.");
        SocketObject donor = sockets[0];
        // Native instancing copies visual/interaction subtrees separately, without
        // the SocketObject root scale. Capture the donor scales before cloning
        // so every port receives the same reduction rather than compounding it.
        Vector3 visualScale = donor.visualObject.transform.localScale * SocketScale;
        Vector3 interactionScale = donor.interactive.transform.localScale * SocketScale;
        string[] names = { "IN", "TIME", "OUT" };
        for (byte index = 0; index < 3; index++)
        {
            SocketObject socket = index == 0 ? donor : UnityEngine.Object.Instantiate(donor.gameObject, donor.transform.parent, false).GetComponent<SocketObject>();
            AccessTools.Field(typeof(SocketObject), "id").SetValue(socket, index);
            AccessTools.Field(typeof(SocketObject), "relayedSockets").SetValue(socket, Array.Empty<SocketObject>());
            socket.name = names[index];
            Quaternion socketRotation = Quaternion.Euler(0f, (1 - index) * 45f, 0f);
            Vector3 outward = socketRotation * Vector3.back;
            socket.transform.localPosition = outward * (_socketApothem + .001f) + Vector3.up * .040f;
            socket.transform.localRotation = socketRotation;
            socket.visualObject.transform.localScale = visualScale;
            socket.interactive.transform.localScale = interactionScale;
            foreach (SphereCollider collider in socket.interactive.GetComponentsInChildren<SphereCollider>(true))
                collider.radius = .075f;
        }
        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
        {
            interaction.gameObject.name = settings.fullLabel;
            BoxCollider collider = interaction.GetComponent<BoxCollider>();
            if (collider == null) continue;
            // A rectangular pickup volume covers the two beveled sockets even
            // though their visible faces are outside the actual octagonal cap.
            collider.transform.localPosition = Vector3.zero;
            TimerColliders.ReplaceBox(collider);
        }
    }

    private static void EnsureMeshAndMaterials(Material opaque)
    {
        if (_mesh && _materials != null) return;
        string obj;
        using (Stream stream = typeof(TimerAppearance).Assembly.GetManifestResourceStream("StellarDriveEssentials.Timer.Hourglass.obj"))
        using (var reader = new StreamReader(stream ?? throw new MissingManifestResourceException("The hourglass model is missing.")))
            obj = reader.ReadToEnd();
        _mesh = ObjModel.FromObj(obj, "Essentials_Hourglass");
        if (!_mesh || _mesh.subMeshCount != 4 || _mesh.bounds.size.y <= 0f)
            throw new InvalidDataException("The supplied hourglass must contain four material groups.");
        Bounds bounds = _mesh.bounds;
        float scale = .6f * LayoutScale / bounds.size.y;
        // Source OBJ dimensions: the lower cap's straight band is y=.5..2,
        // its ledge is y=2.5, and the top cap's flat face is y=30. Stretch only
        // that band so the sockets fit; move everything above it as one piece.
        float bandBottom = (.5f - bounds.min.y) * scale;
        float bandTop = (2f - bounds.min.y) * scale;
        _socketApothem = bounds.extents.z * scale;
        _topSurfaceY = (30f - bounds.min.y) * scale + BaseLift;
        Vector3[] vertices = _mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 vertex = new Vector3(vertices[i].x - bounds.center.x, vertices[i].y - bounds.min.y, vertices[i].z - bounds.center.z) * scale;
            vertex.y += BaseLift * Mathf.Clamp01((vertex.y - bandBottom) / (bandTop - bandBottom));
            vertices[i] = vertex;
        }
        _mesh.vertices = vertices;
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        _mesh.colors = colors;
        _mesh.RecalculateBounds();
        _mesh.RecalculateNormals();
        _mesh.RecalculateTangents();
        Material glass = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m && m.name == "DockingDoorGlass");
        if (!glass) throw new MissingMemberException("The native door glass material is unavailable.");
        _materials = new[]
        {
            MakeMaterial(opaque, "TimerFrame", new Color(.62f, .66f, .68f), .35f, .65f),
            MakeMaterial(opaque, "TimerSupports", new Color(.19f, .22f, .24f), .2f, .75f),
            MakeMaterial(glass, "TimerGlass", new Color(.74f, .80f, .82f, .20f), 0f, .2f),
            MakeMaterial(opaque, "TimerSand", new Color(.64f, .56f, .39f), 0f, 1f)
        };
        _screenMaterial = MakeMaterial(opaque, "TimerScreenPanel", new Color(.014f, .019f, .025f), 0f, 1f);
        _displayFrameMaterial = MakeMaterial(opaque, "TimerDisplayBezel", new Color(.21f, .24f, .27f), .35f, .55f);
        _displayMesh = CreateDisplayMesh();
    }

    private static void CreateDisplayWindow(GameObject parent)
    {
        var window = new GameObject("TimerDisplayWindow") { layer = parent.transform.Find("Hourglass").gameObject.layer };
        window.transform.SetParent(parent.transform, false);
        window.transform.localPosition = Vector3.up * (_topSurfaceY + .0001f);
        window.AddComponent<MeshFilter>().sharedMesh = _displayMesh;
        window.AddComponent<MeshRenderer>().sharedMaterials = new[] { _displayFrameMaterial, _screenMaterial };
    }

    private static Mesh CreateDisplayMesh()
    {
        var vertices = new Vector3[32];
        DisplayRing(vertices, 0, .164f, .056f, .006f, 0f);
        DisplayRing(vertices, 8, .162f, .054f, .005f, .0007f);
        DisplayRing(vertices, 16, .146f, .038f, .003f, .0007f);
        DisplayRing(vertices, 24, .146f, .038f, .003f, .0005f);
        var frame = new int[144];
        int at = 0;
        for (int ring = 0; ring < 3; ring++)
            for (int i = 0; i < 8; i++)
            {
                int a = ring * 8 + i, b = ring * 8 + (i + 1) % 8;
                frame[at++] = a; frame[at++] = a + 8; frame[at++] = b;
                frame[at++] = b; frame[at++] = a + 8; frame[at++] = b + 8;
            }
        var glass = new int[18];
        for (int i = 0; i < 6; i++)
        {
            glass[i * 3] = 24;
            glass[i * 3 + 1] = 26 + i;
            glass[i * 3 + 2] = 25 + i;
        }
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        var mesh = new Mesh { name = "EssentialsTimerDisplayWindow", hideFlags = HideFlags.DontUnloadUnusedAsset,
            vertices = vertices, colors = colors, uv = new Vector2[vertices.Length], subMeshCount = 2 };
        mesh.SetTriangles(frame, 0);
        mesh.SetTriangles(glass, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static void DisplayRing(Vector3[] vertices, int start, float width, float depth, float corner, float y)
    {
        float x = width * .5f, z = depth * .5f;
        vertices[start] = new Vector3(-x + corner, y, -z);
        vertices[start + 1] = new Vector3(x - corner, y, -z);
        vertices[start + 2] = new Vector3(x, y, -z + corner);
        vertices[start + 3] = new Vector3(x, y, z - corner);
        vertices[start + 4] = new Vector3(x - corner, y, z);
        vertices[start + 5] = new Vector3(-x + corner, y, z);
        vertices[start + 6] = new Vector3(-x, y, z - corner);
        vertices[start + 7] = new Vector3(-x, y, -z + corner);
    }

    internal static Material MakeMaterial(Material donor, string name, Color color, float metal, float roughness)
    {
        var material = new Material(donor) { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        foreach (string property in new[] { "_Albedo", "_ColorPalette", "_EmissionMap", "_RoughnessMap" })
            // Custom/PlanetObject always multiplies albedo by the palette sampled
            // through UV1. OBJ has only UV0; the donor palette is black at (0,0).
            if (material.HasProperty(property)) material.SetTexture(property,
                property == "_Albedo" || property == "_ColorPalette" ? Texture2D.whiteTexture : null);
        foreach (string property in new[] { "_Color", "_AlbedoColor", "_BaseColor" })
            if (material.HasProperty(property)) material.SetColor(property, color);
        if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.black);
        if (material.HasProperty("_Metalness")) material.SetFloat("_Metalness", metal);
        if (material.HasProperty("_Roughness")) material.SetFloat("_Roughness", roughness);
        return material;
    }

    internal static string FormatCountdown(double seconds)
    {
        if (!SignalTimer.Finite(seconds)) return "-- s";
        if (seconds <= 0d) return "0.0 s";
        double tenths = Math.Max(1d, Math.Ceiling(Math.Min(SignalTimer.MaximumSeconds, seconds) * 10d - 1e-9d));
        return (tenths / 10d).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    internal static void Release()
    {
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        if (_materials != null)
            foreach (Material material in _materials)
                if (material) UnityEngine.Object.Destroy(material);
        if (_screenMaterial) UnityEngine.Object.Destroy(_screenMaterial);
        if (_displayFrameMaterial) UnityEngine.Object.Destroy(_displayFrameMaterial);
        if (_displayMesh) UnityEngine.Object.Destroy(_displayMesh);
        _mesh = null;
        _materials = null;
        _screenMaterial = null;
        _displayFrameMaterial = null;
        _displayMesh = null;
    }
}

