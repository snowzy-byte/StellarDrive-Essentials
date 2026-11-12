using System;
using System.IO;
using System.Linq;
using System.Resources;
using HarmonyLib;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using Ships.Physics.Collision;
using TMPro;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class LidarSensor
{
    internal const ushort PartId = 8906;
    internal const double DisabledReading = -double.MaxValue / 2d;
    internal const float Height = .30f;
    internal const float LayoutReduction = Height / .45f;
    internal const float Scale = Height / 20.8f;
    internal static Vector3 RayOrigin => new Vector3(0f, 3.5f * Scale, 12.7f * Scale);
    internal static Vector3 RayDirection => Vector3.forward;
    private static Mesh _mesh;
    private static Mesh _laser;
    private static Material[] _materials;
    private static Action<string> _log;

    internal static void Register(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log;
        harmony.Patch(AccessTools.Method(typeof(ShipStatefulColliders), "GenerateStatefulCollider"),
            postfix: new HarmonyMethod(typeof(LidarSensor), nameof(AfterColliderGenerated)));
        EssentialsParts.Register(new EssentialsPartDefinition
        {
            Id = PartId, Name = "EssentialsLidarSensor", Donor = "VelocitySensor",
            BuildRow = 0, Configure = Configure
        });
    }

    internal static bool IsEnabled(ConnectionsServerTracker connections, uint ship, ushort part)
    {
        if (ReferenceEquals(connections, null)) return false;
        var enable = new EntityPartSocket(ship, part, 1);
        return connections.TryGetSignalConnectionGroupWithSocket(enable, out _)
            && Positive(connections.GetSocketSignalValue(enable));
    }

    internal static bool Positive(double signal) => signal > 0d && !double.IsNaN(signal) && !double.IsInfinity(signal);

    private static void Configure(PartSettings settings, GameObject prefab)
    {
        settings.fullLabel = "LIDAR Sensor";
        settings.description = "Long-range distance sensor, up to 1000 km straight ahead from the lens along the red laser. OUT: whole meters. ENABLE: connect a positive signal to scan; zero, negative or no cable turns it off. Disabled or no hit outputs -1. Uses native planet terrain beyond collider range; other objects require loaded colliders. Updates 10 times per second.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(ConstantState);
        settings.customCreationClass = string.Empty;
        settings.mass = 1f;
        IShipPartVisual original = prefab.GetComponentInChildren<IShipPartVisual>(true);
        GameObject root = original?.GameObject;
        MeshFilter body = root?.transform.Find("VelocitySensor")?.GetComponent<MeshFilter>();
        if (!body) throw new MissingMemberException("LIDAR donor body is unavailable.");
        EnsureMesh(body.GetComponent<MeshRenderer>().sharedMaterial);
        prefab.transform.localScale = Vector3.one;
        root.transform.localScale = Vector3.one;
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        body.transform.localScale = Vector3.one;
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.identity;
        body.name = "EssentialsLidarBody";
        body.sharedMesh = _mesh;
        body.GetComponent<MeshRenderer>().sharedMaterials = _materials;
        Transform arrow = root.transform.Find("Arrow");
        if (arrow) UnityEngine.Object.DestroyImmediate(arrow.gameObject);
        UnityEngine.Object.DestroyImmediate((MonoBehaviour)original);
        var laser = new GameObject("LidarActiveLaser") { layer = body.gameObject.layer };
        laser.transform.SetParent(root.transform, false);
        laser.AddComponent<MeshFilter>().sharedMesh = _laser;
        laser.AddComponent<MeshRenderer>().sharedMaterial = _materials[4];
        laser.SetActive(false);
        SensorReadoutVisuals visual = root.AddComponent<SensorReadoutVisuals>();
        visual.IsLidar = true;
        visual.ActivityVisual = laser;
        visual.Screen = Instruments.CreateLabel(root, "LidarReading", "LIDAR\nOFF",
            new Vector3(0f, 7.1f * Scale, 8.8f * Scale), Quaternion.Euler(90f, 0f, 0f), .0038f * LayoutReduction);
        visual.Screen.color = Color.white;
        visual.Screen.fontStyle = FontStyles.Normal;
        visual.Screen.fontSizeMax = 12f;
        visual.Screen.fontSizeMin = 5f;
        visual.Screen.rectTransform.anchorMin = visual.Screen.rectTransform.anchorMax = new Vector2(.5f,.5f);
        visual.Screen.rectTransform.pivot = new Vector2(.5f,.5f);
        visual.Screen.rectTransform.anchoredPosition = Vector2.zero;
        visual.Screen.rectTransform.sizeDelta = new Vector2(72f,22f);
        ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
        bounds.center = _mesh.bounds.center;
        bounds.bounds = _mesh.bounds.size;
        settings.size = bounds.bounds;

        SocketObject[] sockets = prefab.GetComponentsInChildren<SocketObject>(true);
        if (sockets.Length != 1 || sockets[0].Id != 0 || sockets[0].Type != CableType.Signal)
            throw new MissingMemberException("LIDAR donor socket layout changed.");
        SocketObject donor = sockets[0];
        Vector3 visualScale = donor.visualObject.transform.localScale * (.45f * LayoutReduction);
        Vector3 interactiveScale = donor.interactive.transform.localScale * (.45f * LayoutReduction);
        for (byte id = 0; id < 2; id++)
        {
            SocketObject socket = id == 0 ? donor : UnityEngine.Object.Instantiate(donor.gameObject, donor.transform.parent, false).GetComponent<SocketObject>();
            AccessTools.Field(typeof(SocketObject), "id").SetValue(socket, id);
            AccessTools.Field(typeof(SocketObject), "relayedSockets").SetValue(socket, Array.Empty<SocketObject>());
            socket.name = id == 0 ? "OUT" : "ENABLE";
            socket.transform.localPosition = new Vector3((id == 0 ? -.115f : .115f) * LayoutReduction, 3.85f * Scale, -10f * Scale - .0015f * LayoutReduction);
            socket.transform.localRotation = Quaternion.identity;
            socket.visualObject.transform.localScale = visualScale;
            socket.interactive.transform.localScale = interactiveScale;
            foreach (SphereCollider sphere in socket.interactive.GetComponentsInChildren<SphereCollider>(true)) sphere.radius = .075f;
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

    private static void EnsureMesh(Material donor)
    {
        if (_mesh) return;
        using (Stream stream = typeof(LidarSensor).Assembly.GetManifestResourceStream("StellarDriveEssentials.Lidar.Model.obj"))
        using (var reader = new StreamReader(stream ?? throw new MissingManifestResourceException("LIDAR model is missing.")))
            _mesh = ObjModel.FromObj(reader.ReadToEnd(), "Essentials_StylizedLidar");
        if (!_mesh || _mesh.subMeshCount != 6) throw new InvalidDataException("LIDAR OBJ material groups changed.");
        // The embedded OBJ already contains meter coordinates. Do not depend on
        // a second runtime scale conversion, or apply source units twice.
        if (Math.Abs(_mesh.bounds.size.y - Height) > .0005f || _mesh.bounds.size.x > Height * 1.2f || _mesh.bounds.size.z > Height * 1.6f)
            throw new InvalidDataException("LIDAR mesh is outside its compact meter-scale dimensions.");
        Vector3[] vertices = _mesh.vertices;
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        _mesh.colors = colors;
        _mesh.RecalculateBounds(); _mesh.RecalculateNormals(); _mesh.RecalculateTangents();
        _mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _laser = new Mesh { name = "EssentialsLidarLaser", hideFlags = HideFlags.DontUnloadUnusedAsset,
            vertices = vertices, uv = _mesh.uv, normals = _mesh.normals, colors = colors, triangles = _mesh.GetTriangles(4) };
        _laser.RecalculateBounds();
        _mesh.SetTriangles(Array.Empty<int>(), 4);
        Material glass = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m && m.name == "DockingDoorGlass");
        _materials = new[]
        {
            TimerAppearance.MakeMaterial(donor, "LidarMetal", new Color(.70f,.75f,.79f), .4f,.55f),
            TimerAppearance.MakeMaterial(donor, "LidarChassis", new Color(.12f,.19f,.23f), .1f,.8f),
            TimerAppearance.MakeMaterial(donor, "LidarOptics", new Color(.025f,.035f,.045f),0f,.8f),
            TimerAppearance.MakeMaterial(glass ? glass : donor, "LidarGlass", new Color(.24f,.76f,.85f,.3f),0f,.15f),
            TimerAppearance.MakeMaterial(donor, "LidarLaser",new Color(1f,.06f,.08f),0f,.4f),
            TimerAppearance.MakeMaterial(donor,"LidarBolts",new Color(.85f,.88f,.9f),.6f,.45f)
        };
    }

    private static void AfterColliderGenerated(StatefulPart part, Transform __result)
    {
        if (part == null || ReferenceEquals(part.Settings,null) || part.Settings.id != PartId || !__result) return;
        ConfigureColliders(__result.GetComponent<BoxCollider>());
    }

    private static void ConfigureColliders(BoxCollider original)
    {
        if (!original) return;
        double[][] boxes = CollisionBoxes();
        for (int i = 0; i < boxes.Length; i++)
        {
            BoxCollider box = i == 0 ? original : original.gameObject.AddComponent<BoxCollider>();
            var b = boxes[i];
            box.center = new Vector3((float)b[0],(float)b[1],(float)b[2]);
            box.size = new Vector3((float)b[3],(float)b[4],(float)b[5]);
            if (i == 0) continue;
            box.sharedMaterial = original.sharedMaterial; box.isTrigger = original.isTrigger;
            box.contactOffset = original.contactOffset; box.enabled = original.enabled;
        }
    }

    internal static double[][] CollisionBoxes() => new[]
    {
        new[] {0d,.775d*Scale,.8d*Scale,24.8d*Scale,1.55d*Scale,23.2d*Scale},
        new[] {0d,3.85d*Scale,.9d*Scale,23.2d*Scale,6d*Scale,21.8d*Scale},
        new[] {0d,14.2d*Scale,-1.2d*Scale,17.2d*Scale,13.2d*Scale,18.4d*Scale},
        new[] {0d,3.5d*Scale,12.25d*Scale,2d*Scale,2d*Scale,.7d*Scale}
    };

    internal static void Release()
    {
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        if (_laser) UnityEngine.Object.Destroy(_laser);
        if (_materials != null) foreach (Material material in _materials) if (material) UnityEngine.Object.Destroy(material);
        _mesh = _laser = null; _materials = null;
    }
}
