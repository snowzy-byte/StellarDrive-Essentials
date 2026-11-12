using System;
using System.IO;
using System.Linq;
using System.Resources;
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

internal static class DelayAppearance
{
    internal const float Width = .25f;
    internal const float ModelScale = Width / 21f;
    internal const float SocketScale = .65f;
    internal const float SocketInteractionScale = .85f;
    internal const float SocketHeight = .07f;
    internal const float LabelScale = .003f * Width / .42f;
    // Measured on the shipped SignalSocket mesh: a 12.72 x 11.59 x 3.12 cm
    // plate that reaches 5.12 cm below and 6.47 cm above its socket transform.
    private const double NativePlateWidth = .1272d;
    private const double NativePlateDepth = .0312d;
    private const double NativePlateBelow = .05117d;
    private const double NativePlateAbove = .0647d;
    private static Mesh _mesh;
    private static Material[] _materials;

    internal static void Configure(PartSettings settings, GameObject prefab)
    {
        settings.fullLabel = "Signal Delay";
        settings.description = "IN: signal to delay. TIME: delay in seconds (0 to 86400); connect a Constant, or leave unplugged for 5 s. OUT: replays every input change after that delay, including short pulses, negative values and zero. Changing TIME restarts the delay and clears pending changes. Pending changes reset on pickup or save reload. On queue overload output stops; change TIME or pick up the part to reset.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(MathGateState);
        settings.customCreationClass = string.Empty;
        settings.mass = .5f;
        settings.size = new Vector3(Width, .12f, Width);
        ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
        if (!bounds) throw new MissingMemberException("The delay donor bounds are unavailable.");
        bounds.center = new Vector3(0f, .06f, 0f);
        bounds.bounds = settings.size;
        IShipPartVisual original = prefab.GetComponentInChildren<IShipPartVisual>(true);
        if (original == null || original.GetType().Name != "SignalSplitterVisuals")
            throw new MissingMemberException("The delay donor visual layout has changed.");
        GameObject root = original.GameObject;
        MeshFilter body = root.transform.Find("VelocitySensor")?.GetComponent<MeshFilter>();
        if (!body) throw new MissingMemberException("The delay donor mesh is unavailable.");
        EnsureMesh(body.GetComponent<MeshRenderer>().sharedMaterial);
        body.name = "DelayModule";
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.identity;
        body.transform.localScale = Vector3.one;
        body.sharedMesh = _mesh;
        body.GetComponent<MeshRenderer>().sharedMaterials = _materials;
        Transform arrow = root.transform.Find("Arrow");
        if (arrow) UnityEngine.Object.DestroyImmediate(arrow.gameObject);
        UnityEngine.Object.DestroyImmediate((MonoBehaviour)original);
        TimerReadoutVisuals visuals = root.AddComponent<TimerReadoutVisuals>();
        visuals.IsDelay = true;
        // Use the supplied central black screen; no extra panel is added over the OBJ.
        double[] screenPosition = ScreenPosition();
        visuals.Screen = Instruments.CreateLabel(root, "DelayCountdown", "0.0 s",
            new Vector3((float)screenPosition[0], (float)screenPosition[1], (float)screenPosition[2]), Quaternion.Euler(90f, 0f, 0f), LabelScale);
        TMP_FontAsset font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f && f.name == "LiberationSans SDF");
        if (font) { visuals.Screen.font = font; visuals.Screen.fontSharedMaterial = font.material; }
        visuals.Screen.color = Color.white;
        visuals.Screen.fontStyle = FontStyles.Normal;
        visuals.Screen.fontWeight = FontWeight.Regular;
        visuals.Screen.characterSpacing = 1f;
        visuals.Screen.rectTransform.anchorMin = visuals.Screen.rectTransform.anchorMax = new Vector2(.5f, .5f);
        visuals.Screen.rectTransform.pivot = new Vector2(.5f, .5f);
        visuals.Screen.rectTransform.anchoredPosition = Vector2.zero;
        visuals.Screen.rectTransform.sizeDelta = new Vector2(32f, 14f);
        visuals.Screen.fontSizeMin = 5f;
        visuals.Screen.fontSizeMax = 8.5f;
        visuals.Screen.margin = new Vector4(1f, 0f, 1f, 0f);
        visuals.Screen.alignment = TextAlignmentOptions.Center;
        SocketObject[] sockets = prefab.GetComponentsInChildren<SocketObject>(true);
        if (sockets.Length != 1 || sockets[0].Id != 0 || sockets[0].Type != CableType.Signal)
            throw new MissingMemberException("The delay donor socket layout has changed.");
        SocketObject donor = sockets[0];
        Vector3 visualScale = donor.visualObject.transform.localScale * SocketScale;
        Vector3 interactionScale = donor.interactive.transform.localScale * SocketInteractionScale;
        string[] names = { "IN", "TIME", "OUT" };
        for (byte index = 0; index < 3; index++)
        {
            SocketObject socket = index == 0 ? donor : UnityEngine.Object.Instantiate(donor.gameObject, donor.transform.parent, false).GetComponent<SocketObject>();
            AccessTools.Field(typeof(SocketObject), "id").SetValue(socket, index);
            AccessTools.Field(typeof(SocketObject), "relayedSockets").SetValue(socket, Array.Empty<SocketObject>());
            socket.name = names[index];
            double[] position = SocketPosition(index);
            socket.transform.localPosition = new Vector3((float)position[0], (float)position[1], (float)position[2]);
            socket.transform.localRotation = Quaternion.Euler(0f, (float)SocketYaw(index), 0f);
            socket.visualObject.transform.localScale = visualScale;
            socket.interactive.transform.localScale = interactionScale;
            foreach (SphereCollider collider in socket.interactive.GetComponentsInChildren<SphereCollider>(true)) collider.radius = .075f;
        }
        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
        {
            interaction.gameObject.name = settings.fullLabel;
            BoxCollider box = interaction.GetComponent<BoxCollider>();
            if (!box) continue;
            box.transform.localPosition = Vector3.zero;
            ConfigureColliders(box);
        }
    }

    internal static double[] SocketPosition(byte index)
    {
        double edge = (double)Width / 2d;
        switch (index)
        {
            case 0: return new[] { 0d, (double)SocketHeight, -edge };
            case 1: return new[] { edge, (double)SocketHeight, 0d };
            case 2: return new[] { 0d, (double)SocketHeight, edge };
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    internal static double SocketYaw(byte index)
    {
        switch (index)
        {
            case 0: return 0d;
            case 1: return -90d;
            case 2: return 180d;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    // The visible connector is the native socket plate seated in the casing
    // wall, so the physical proxy follows the measured plate instead of the
    // old bronze posts that used to float above the flat lid.
    internal static double[][] SocketPanelBoxes()
    {
        double width = NativePlateWidth * SocketScale;
        double height = (NativePlateAbove + NativePlateBelow) * SocketScale;
        double depth = NativePlateDepth * SocketScale;
        double center = SocketHeight + (NativePlateAbove - NativePlateBelow) * SocketScale * .5d;
        double edge = (double)Width / 2d;
        return new[]
        {
            new[] { 0d, center, -(edge - depth * .5d), width, height, depth },
            new[] { edge - depth * .5d, center, 0d, depth, height, width },
            new[] { 0d, center, edge - depth * .5d, width, height, depth }
        };
    }

    internal static double[] ScreenPosition() => new[] { 0d, 9.8d * ((double)Width / 21d) + .0008d, 0d };

    private static void EnsureMesh(Material opaque)
    {
        if (_mesh && _materials != null) return;
        using (Stream stream = typeof(DelayAppearance).Assembly.GetManifestResourceStream("StellarDriveEssentials.Delay.Model.obj"))
        using (var reader = new StreamReader(stream ?? throw new MissingManifestResourceException("The delay model is missing.")))
            _mesh = ObjModel.FromObj(reader.ReadToEnd(), "Essentials_DelayModule");
        if (_mesh.subMeshCount != 6) throw new InvalidDataException("The delay OBJ must contain its six material groups.");
        Vector3[] vertices = _mesh.vertices;
        for (int i = 0; i < vertices.Length; i++) vertices[i] *= ModelScale;
        _mesh.vertices = vertices;
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        _mesh.colors = colors;
        _mesh.RecalculateBounds();
        _mesh.RecalculateTangents();
        _materials = new[]
        {
            TimerAppearance.MakeMaterial(opaque, "DelayChassis", new Color(.53f, .58f, .61f), .35f, .65f),
            TimerAppearance.MakeMaterial(opaque, "DelayGasket", new Color(.08f, .10f, .12f), 0f, .85f),
            TimerAppearance.MakeMaterial(opaque, "DelayLid", new Color(.62f, .66f, .68f), .3f, .6f),
            TimerAppearance.MakeMaterial(opaque, "DelayTerminals", new Color(.35f, .39f, .42f), .45f, .6f),
            TimerAppearance.MakeMaterial(opaque, "DelayScreenFrame", new Color(.20f, .23f, .25f), .2f, .7f),
            TimerAppearance.MakeMaterial(opaque, "DelayScreen", new Color(.012f, .017f, .023f), 0f, 1f)
        };
    }

    internal static double[][] CollisionBoxes()
    {
        double scale = (double)Width / 21d;
        // Chassis, gasket line, casing that carries the ports and the display.
        var boxes = new[]
        {
            new[] { 0d, 1.125d, 0d, 18d, 2.25d, 18d },
            new[] { 0d, 2.35d, 0d, 17.85d, .2d, 17.85d },
            new[] { 0d, 6.02d, 0d, 17.7d, 7.14d, 17.7d },
            new[] { 0d, 9.625d, 0d, 7.24d, .35d, 6.9d }
        };
        foreach (double[] box in boxes) for (int i = 0; i < box.Length; i++) box[i] *= scale;
        return boxes.Concat(SocketPanelBoxes()).ToArray();
    }

    internal static void ConfigureColliders(BoxCollider original)
    {
        if (!original) return;
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

    internal static void Release()
    {
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        if (_materials != null) foreach (Material material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _mesh = null;
        _materials = null;
    }
}
