using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Services;
using System.IO;
using System.Linq;
using System.Resources;
using System.Globalization;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using TMPro;
using UnityEngine;
using Ships.Physics.Collision;
using Core.Services;
using UI.Interaction;
using UnityEngine.UI;
using Ships.Parts.Common.Model;
using Ships.Parts.Common.Utils;
using Ships.Services;
using WorldTracking.Interface.Services;

namespace StellarDriveEssentials;
internal static class SignalTimer
{
    internal const ushort PartId = 8903;
    internal const double DefaultSeconds = 5d;
    internal const double MaximumSeconds = 86400d;
    private static ConditionalWeakTable<StatefulPart, SignalHold> _holds = new ConditionalWeakTable<StatefulPart, SignalHold>();
    private static ConditionalWeakTable<ConnectionsServerTracker, TimerClock> _clocks = new ConditionalWeakTable<ConnectionsServerTracker, TimerClock>();
    private sealed class TimerClock
    {
        internal double Seconds;
    }

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        harmony.Patch(AccessTools.Method(typeof(SignalConnectionsServerUpdater), "ExecuteOperations"), prefix: new HarmonyMethod(typeof(SignalTimer), nameof(BeforeEvaluation)), transpiler: new HarmonyMethod(typeof(SignalTimer), nameof(Transpiler)));
        harmony.Patch(AccessTools.Method(typeof(SignalConnectionGroupValues), "GetSocketSignal"), prefix: new HarmonyMethod(typeof(SignalTimer), nameof(BeforeSocketSignal)));
        TimerPortHints.Install(harmony);
        TimerColliders.Install(harmony);
        EssentialsParts.Register(new EssentialsPartDefinition { Id = PartId, Name = "EssentialsSignalTimer", Donor = "VelocitySensor", BuildRow = 1, Configure = TimerAppearance.Configure });
    }

    private static SignalHold CreateHold(StatefulPart part) => new SignalHold();
    private static TimerClock CreateClock(ConnectionsServerTracker tracker) => new TimerClock();
    private static void BeforeEvaluation(ConnectionsServerTracker ____connections, float deltaTime)
    {
        if (ReferenceEquals(____connections, null) || !Finite(deltaTime) || deltaTime <= 0f)
            return;
        _clocks.GetValue(____connections, CreateClock).Seconds += deltaTime;
    }

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo update = AccessTools.Method(typeof(TrackedShipServer), nameof(TrackedShipServer.UpdatePartState));
        MethodInfo apply = AccessTools.Method(typeof(SignalTimer), nameof(ApplyEvaluatedState));
        FieldInfo connections = AccessTools.Field(typeof(SignalConnectionsServerUpdater), "_connections");
        int matches = code.FindAll(c => c.Calls(update)).Count;
        if (matches != 4 || connections == null || apply == null)
            throw new MissingMemberException("The native signal evaluator layout has changed; Signal Timer was not registered.");
        foreach (CodeInstruction instruction in code)
        {
            if (!instruction.Calls(update))
            {
                yield return instruction;
                continue;
            }

            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.labels.AddRange(instruction.labels);
            load.blocks.AddRange(instruction.blocks);
            yield return load;
            yield return new CodeInstruction(OpCodes.Ldfld, connections);
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Call, apply);
        }
    }

    private static void ApplyEvaluatedState(TrackedShipServer ship, ushort partId, IStatefulPartState proposed, ConnectionsServerTracker connections, float deltaTime)
    {
        if (!(proposed is MathGateState) || !ship.TryGetStatefulPart(partId, out StatefulPart part) || ReferenceEquals(part.Settings, null) || (part.Settings.id != PartId && part.Settings.id != SignalDelay.PartId) || !(part.State is MathGateState previous))
        {
            ship.UpdatePartState(partId, proposed);
            return;
        }

        TimerClock clock = _clocks.GetValue(connections, CreateClock);
        if (part.Settings.id == SignalDelay.PartId)
        {
            SignalDelay.Apply(ship, part, previous, connections, clock, clock.Seconds);
            return;
        }

        SignalHold hold = _holds.GetValue(part, CreateHold);
        double elapsed;
        if (!ReferenceEquals(hold.Clock, clock))
        {
            hold.Value = hold.RemainingSeconds = 0d;
            hold.Clock = clock;
            elapsed = deltaTime;
        }
        else
            elapsed = clock.Seconds - hold.LastEvaluationTime;
        hold.LastEvaluationTime = clock.Seconds;
        bool enabled = !previous.IsHeldByPlayer;
        double input = 0d;
        double duration = DefaultSeconds;
        if (enabled)
        {
            input = connections.GetSocketSignalValue(new EntityPartSocket(ship.Id, partId, 0));
            var timeSocket = new EntityPartSocket(ship.Id, partId, 1);
            duration = connections.TryGetSignalConnectionGroupWithSocket(timeSocket, out _) ? ResolveDuration(connections.GetSocketSignalValue(timeSocket), true) : DefaultSeconds;
        }

        double output = hold.Step(input, duration, elapsed, enabled);
        if (previous.Output != output)
            ship.UpdatePartState(partId, previous with { Output = output });
    }

    private static bool BeforeSocketSignal(EntityPartSocket entityPartSocket, IShipsProvider ____ships, ref double __result)
    {
        if (entityPartSocket.Socket.SocketId != 2 || !____ships.TryGetShip(entityPartSocket.EntityId, out var ship) || !ship.TryGetStatefulPart(entityPartSocket.Socket.PartId, out StatefulPart part) || ReferenceEquals(part.Settings, null) || (part.Settings.id != PartId && part.Settings.id != SignalDelay.PartId) || !(part.State is MathGateState timer))
            return true;
        __result = !timer.IsHeldByPlayer && Finite(timer.Output) ? timer.Output : 0d;
        return false;
    }

    internal static double ResolveDuration(double value, bool connected) => !connected ? DefaultSeconds : Finite(value) ? Math.Min(MaximumSeconds, Math.Max(0d, value)) : 0d;
    internal static bool TryReadRemaining(IShipsProvider ships, PartContext context, out double seconds)
    {
        seconds = 0d;
        if (ships == null || !ships.TryGetShip(context.ShipId, out var ship) || !ship.TryGetStatefulPart(context.PartId, out StatefulPart part) || ReferenceEquals(part.Settings, null) || (part.Settings.id != PartId && part.Settings.id != SignalDelay.PartId) || !(part.State is MathGateState timer))
            return false;
        if (part.Settings.id == SignalDelay.PartId)
            return SignalDelay.TryReadRemaining(part, out seconds);
        if (timer.IsHeldByPlayer || !_holds.TryGetValue(part, out SignalHold hold) || hold.Value == 0d)
            return true;
        double age = hold.Clock is TimerClock clock ? Math.Max(0d, clock.Seconds - hold.LastEvaluationTime) : 0d;
        seconds = Math.Max(0d, hold.RemainingSeconds - age);
        return true;
    }

    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    internal static void Release()
    {
        _holds = new ConditionalWeakTable<StatefulPart, SignalHold>();
        _clocks = new ConditionalWeakTable<ConnectionsServerTracker, TimerClock>();
        SignalDelay.Release();
        TimerAppearance.Release();
        TimerColliders.Release();
    }
}

internal sealed class SignalHold
{
    internal double Value;
    internal double RemainingSeconds;
    internal object Clock;
    internal double LastEvaluationTime;
    internal double Step(double input, double seconds, double deltaTime, bool enabled)
    {
        if (!enabled)
        {
            Value = RemainingSeconds = 0d;
            return 0d;
        }

        if (SignalTimer.Finite(input) && input != 0d)
        {
            Value = input;
            RemainingSeconds = SignalTimer.ResolveDuration(seconds, true);
        }
        else
        {
            if (SignalTimer.Finite(deltaTime) && deltaTime > 0d)
                RemainingSeconds = Math.Max(0d, RemainingSeconds - deltaTime);
            if (RemainingSeconds <= 1e-9d)
                Value = RemainingSeconds = 0d;
        }

        return Value;
    }
}

internal static class SignalDelay
{
    internal const ushort PartId = 8905;
    private static ConditionalWeakTable<StatefulPart, SignalLag> _delays = new ConditionalWeakTable<StatefulPart, SignalLag>();
    private static Action<string> _log;
    internal static void Install(Action<string> log)
    {
        _log = log;
        EssentialsParts.Register(new EssentialsPartDefinition { Id = PartId, Name = "EssentialsSignalDelay", Donor = "VelocitySensor", BuildRow = 1, Configure = DelayAppearance.Configure });
    }

    internal static void Apply(TrackedShipServer ship, StatefulPart part, MathGateState previous, ConnectionsServerTracker connections, object clock, double now)
    {
        SignalLag delay = _delays.GetValue(part, _ => new SignalLag());
        if (!ReferenceEquals(delay.Clock, clock))
        {
            delay.Reset();
            delay.Clock = clock;
        }

        bool enabled = !previous.IsHeldByPlayer;
        double input = enabled ? connections.GetSocketSignalValue(new EntityPartSocket(ship.Id, part.Id, 0)) : 0d;
        var time = new EntityPartSocket(ship.Id, part.Id, 1);
        double duration = connections.TryGetSignalConnectionGroupWithSocket(time, out _) ? SignalTimer.ResolveDuration(connections.GetSocketSignalValue(time), true) : SignalTimer.DefaultSeconds;
        bool wasOverflowed = delay.Overflowed;
        double output = delay.Step(input, duration, now, enabled);
        if (delay.Overflowed && !wasOverflowed)
            _log?.Invoke("Signal Delay pending-change limit reached on ship " + ship.Id + ", part " + part.Id + ". Output stopped. Change TIME or pick up the part to reset; reduce delay or input frequency.");
        if (previous.Output != output)
            ship.UpdatePartState(part.Id, previous with { Output = output });
    }

    internal static bool TryReadRemaining(StatefulPart part, out double seconds)
    {
        seconds = 0d;
        if (part.State is MathGateState state && !state.IsHeldByPlayer && _delays.TryGetValue(part, out var delay))
            seconds = delay.Remaining;
        return true;
    }

    internal static void Release()
    {
        _delays = new ConditionalWeakTable<StatefulPart, SignalLag>();
        _log = null;
    }
}

internal sealed class SignalLag
{
    internal const int MaximumPendingChanges = 65536;
    private struct Change
    {
        internal double Due;
        internal double Value;
    }

    private readonly Queue<Change> _pending = new Queue<Change>();
    internal object Clock;
    private bool _initialized;
    private double _duration;
    private double _input;
    private double _now;
    internal double Output { get; private set; }
    internal bool Overflowed { get; private set; }
    internal double Remaining => Overflowed ? double.NaN : _pending.Count == 0 ? 0d : Math.Max(0d, _pending.Peek().Due - _now);
    internal int PendingCount => _pending.Count;

    internal double Step(double input, double seconds, double now, bool enabled)
    {
        if (!enabled || !SignalTimer.Finite(now))
        {
            Reset();
            return 0d;
        }

        if (!SignalTimer.Finite(input))
            input = 0d;
        seconds = SignalTimer.ResolveDuration(seconds, true);
        if (!_initialized || seconds != _duration || now < _now)
        {
            Reset();
            _duration = seconds;
            _initialized = true;
        }

        _now = now;
        if (Overflowed)
            return 0d;
        if (seconds == 0d)
        {
            _pending.Clear();
            _input = Output = input;
            return Output;
        }

        while (_pending.Count > 0 && _pending.Peek().Due <= now + 1e-9d)
            Output = _pending.Dequeue().Value;
        if (input != _input)
        {
            if (_pending.Count >= MaximumPendingChanges)
            {
                _pending.Clear();
                _pending.TrimExcess();
                Overflowed = true;
                return Output = 0d;
            }

            _pending.Enqueue(new Change { Due = now + seconds, Value = input });
            _input = input;
        }

        return Output;
    }

    internal void Reset()
    {
        _pending.Clear();
        _initialized = false;
        Overflowed = false;
        _duration = _input = _now = Output = 0d;
    }
}

internal static class TimerAppearance
{
    internal const float ModelHeight = .428f;
    internal const float BodyHalfWidth = .129f;
    internal const float CornerBevel = .034f;
    private const float SocketScale = .45f;
    private static Mesh _mesh;
    private static Material[] _materials;
    private static Material _screenMaterial;
    private static Material _displayFrameMaterial;
    private static Mesh _displayMesh;
    private static float _socketApothem;
    private static float _topSurfaceY;
    private static readonly Vector3 BodyCenter = new Vector3(0f, .215f, 0f);
    private static readonly Vector3 BodySize = new Vector3(.258f, .43f, .258f);
    internal static void Configure(PartSettings settings, GameObject prefab) => Configure(settings, prefab, false);
    internal static void Configure(PartSettings settings, GameObject prefab, bool delay)
    {
        settings.fullLabel = delay ? "Signal Delay" : "Signal Timer";
        settings.description = delay ? "Repeats every change from IN at OUT after a delay, including short pulses and zero. TIME sets the delay in seconds; leave it unplugged for 5 seconds. Changing TIME or reloading the save clears pending signals." : "Passes IN to OUT immediately, then holds the last nonzero value when IN returns to zero. TIME sets the hold duration in seconds; leave it unplugged for 5 seconds. A new signal refreshes the timer.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(MathGateState);
        settings.customCreationClass = string.Empty;
        settings.mass = .5f;
        settings.size = BodySize;
        ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
        if (bounds == null)
            throw new MissingMemberException("The timer donor bounds are unavailable.");
        bounds.center = BodyCenter;
        bounds.bounds = BodySize;
        IShipPartVisual original = prefab.GetComponentInChildren<IShipPartVisual>(true);
        if (original == null || original.GetType().Name != "SignalSplitterVisuals")
            throw new MissingMemberException("The timer donor visual layout has changed.");
        GameObject root = original.GameObject;
        MeshFilter body = root.transform.Find("VelocitySensor")?.GetComponent<MeshFilter>();
        if (body == null)
            throw new MissingMemberException("The timer donor mesh is unavailable.");
        EnsureMeshAndMaterials(body.GetComponent<MeshRenderer>().sharedMaterial);
        TimerColliders.EnsureMesh(_socketApothem, BodySize.y);
        prefab.transform.localScale = Vector3.one;
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.identity;
        body.transform.localScale = Vector3.one;
        body.name = "Hourglass";
        body.sharedMesh = _mesh;
        body.GetComponent<MeshRenderer>().sharedMaterials = _materials;
        Transform arrow = root.transform.Find("Arrow");
        if (arrow)
            UnityEngine.Object.DestroyImmediate(arrow.gameObject);
        UnityEngine.Object.DestroyImmediate((MonoBehaviour)original);
        TimerReadoutVisuals visuals = root.AddComponent<TimerReadoutVisuals>();
        visuals.IsDelay = delay;
        CreateDisplayWindow(root);
        visuals.Screen = Instruments.CreateLabel(root, "TimerCountdown", "0.0 s", new Vector3(0f, _topSurfaceY + .0011f, 0f), Quaternion.Euler(90f, 0f, 0f), .0036f);
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
        Vector3 visualScale = donor.visualObject.transform.localScale * SocketScale;
        Vector3 interactionScale = donor.interactive.transform.localScale * .65f;
        string[] names =
        {
            "IN",
            "TIME",
            "OUT"
        };
        for (byte index = 0; index < 3; index++)
        {
            SocketObject socket = index == 0 ? donor : UnityEngine.Object.Instantiate(donor.gameObject, donor.transform.parent, false).GetComponent<SocketObject>();
            AccessTools.Field(typeof(SocketObject), "id").SetValue(socket, index);
            AccessTools.Field(typeof(SocketObject), "relayedSockets").SetValue(socket, Array.Empty<SocketObject>());
            socket.name = names[index];
            Quaternion socketRotation = Quaternion.Euler(0f, (1 - index) * 45f, 0f);
            double[] position = SocketPosition(index);
            socket.transform.localPosition = new Vector3((float)position[0], (float)position[1], (float)position[2]);
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
            if (collider == null)
                continue;
            collider.transform.localPosition = Vector3.zero;
            TimerColliders.ReplaceBox(collider);
        }
    }

    internal static double[] SocketPosition(byte index)
    {
        if (index > 2) throw new ArgumentOutOfRangeException(nameof(index));
        double angle = (1 - index) * Math.PI / 4d;
        double face = index == 1 ? BodyHalfWidth : (2d * BodyHalfWidth - CornerBevel) / Math.Sqrt(2d);
        return new[] { -Math.Sin(angle) * (face + .001d), .040d, -Math.Cos(angle) * (face + .001d) };
    }

    private static void EnsureMeshAndMaterials(Material opaque)
    {
        if (_mesh && _materials != null)
            return;
        string obj;
        using (Stream stream = typeof(TimerAppearance).Assembly.GetManifestResourceStream("StellarDriveEssentials.Timer.Hourglass.obj"))
        using (var reader = new StreamReader(stream ?? throw new MissingManifestResourceException("The hourglass model is missing.")))
            obj = reader.ReadToEnd();
        _mesh = ObjModel.FromObj(obj, "Essentials_Hourglass");
        if (!_mesh || _mesh.subMeshCount != 4 || _mesh.bounds.size.y <= 0f)
            throw new InvalidDataException("The supplied hourglass must contain four material groups.");
        Bounds bounds = _mesh.bounds;
        if (Math.Abs(bounds.size.y - ModelHeight) > .0005f || Math.Abs(bounds.size.x - BodyHalfWidth * 2f) > .0005f
            || Math.Abs(bounds.size.z - BodyHalfWidth * 2f) > .0005f || Math.Abs(bounds.min.y) > .0005f)
            throw new InvalidDataException("Timer OBJ must use its approved meter dimensions.");
        _socketApothem = bounds.extents.z;
        _topSurfaceY = bounds.max.y;
        Vector3[] vertices = _mesh.vertices;
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = Color.white;
        _mesh.colors = colors;
        _mesh.RecalculateBounds();
        _mesh.RecalculateNormals();
        _mesh.RecalculateTangents();
        Material glass = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m && m.name == "DockingDoorGlass");
        if (!glass)
            throw new MissingMemberException("The native door glass material is unavailable.");
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
        var window = new GameObject("TimerDisplayWindow")
        {
            layer = parent.transform.Find("Hourglass").gameObject.layer
        };
        window.transform.SetParent(parent.transform, false);
        window.transform.localPosition = Vector3.up * (_topSurfaceY + .0001f);
        window.AddComponent<MeshFilter>().sharedMesh = _displayMesh;
        window.AddComponent<MeshRenderer>().sharedMaterials = new[]
        {
            _displayFrameMaterial,
            _screenMaterial
        };
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
                frame[at++] = a;
                frame[at++] = a + 8;
                frame[at++] = b;
                frame[at++] = b;
                frame[at++] = a + 8;
                frame[at++] = b + 8;
            }

        var glass = new int[18];
        for (int i = 0; i < 6; i++)
        {
            glass[i * 3] = 24;
            glass[i * 3 + 1] = 26 + i;
            glass[i * 3 + 2] = 25 + i;
        }

        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = Color.white;
        var mesh = new Mesh
        {
            name = "EssentialsTimerDisplayWindow",
            hideFlags = HideFlags.DontUnloadUnusedAsset,
            vertices = vertices,
            colors = colors,
            uv = new Vector2[vertices.Length],
            subMeshCount = 2
        };
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
        var material = new Material(donor)
        {
            name = name,
            hideFlags = HideFlags.DontUnloadUnusedAsset
        };
        foreach (string property in new[]
        {
            "_Albedo",
            "_ColorPalette",
            "_EmissionMap",
            "_RoughnessMap"
        }

        )
            if (material.HasProperty(property))
                material.SetTexture(property, property == "_Albedo" || property == "_ColorPalette" ? Texture2D.whiteTexture : null);
        foreach (string property in new[]
        {
            "_Color",
            "_AlbedoColor",
            "_BaseColor"
        }

        )
            if (material.HasProperty(property))
                material.SetColor(property, color);
        if (material.HasProperty("_EmissionColor"))
            material.SetColor("_EmissionColor", Color.black);
        if (material.HasProperty("_Metalness"))
            material.SetFloat("_Metalness", metal);
        if (material.HasProperty("_Roughness"))
            material.SetFloat("_Roughness", roughness);
        return material;
    }

    internal static string FormatCountdown(double seconds)
    {
        if (!SignalTimer.Finite(seconds))
            return "-- s";
        if (seconds <= 0d)
            return "0.0 s";
        double tenths = Math.Max(1d, Math.Ceiling(Math.Min(SignalTimer.MaximumSeconds, seconds) * 10d - 1e-9d));
        return (tenths / 10d).ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    internal static void Release()
    {
        if (_mesh)
            UnityEngine.Object.Destroy(_mesh);
        if (_materials != null)
            foreach (Material material in _materials)
                if (material)
                    UnityEngine.Object.Destroy(material);
        if (_screenMaterial)
            UnityEngine.Object.Destroy(_screenMaterial);
        if (_displayFrameMaterial)
            UnityEngine.Object.Destroy(_displayFrameMaterial);
        if (_displayMesh)
            UnityEngine.Object.Destroy(_displayMesh);
        _mesh = null;
        _materials = null;
        _screenMaterial = null;
        _displayFrameMaterial = null;
        _displayMesh = null;
    }
}

internal static class DelayAppearance
{
    internal const float Width = .25f;
    internal const float ModelScale = Width / 21f;
    internal const float SocketScale = .65f;
    internal const float SocketInteractionScale = .85f;
    internal const float SocketHeight = .07f;
    internal const float LabelScale = .003f * Width / .42f;
    private const double NativePlateWidth = .1272d;
    private const double NativePlateDepth = .0312d;
    private const double NativePlateBelow = .05117d;
    private const double NativePlateAbove = .0647d;
    private static Mesh _mesh;
    private static Material[] _materials;
    internal static void Configure(PartSettings settings, GameObject prefab)
    {
        settings.fullLabel = "Signal Delay";
        settings.description = "Repeats every change from IN at OUT after a delay, including short pulses and zero. TIME sets the delay in seconds; leave it unplugged for 5 seconds. Changing TIME or reloading the save clears pending signals.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(MathGateState);
        settings.customCreationClass = string.Empty;
        settings.mass = .5f;
        settings.size = new Vector3(Width, .12f, Width);
        ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
        if (!bounds)
            throw new MissingMemberException("The delay donor bounds are unavailable.");
        bounds.center = new Vector3(0f, .06f, 0f);
        bounds.bounds = settings.size;
        IShipPartVisual original = prefab.GetComponentInChildren<IShipPartVisual>(true);
        if (original == null || original.GetType().Name != "SignalSplitterVisuals")
            throw new MissingMemberException("The delay donor visual layout has changed.");
        GameObject root = original.GameObject;
        MeshFilter body = root.transform.Find("VelocitySensor")?.GetComponent<MeshFilter>();
        if (!body)
            throw new MissingMemberException("The delay donor mesh is unavailable.");
        EnsureMesh(body.GetComponent<MeshRenderer>().sharedMaterial);
        body.name = "DelayModule";
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.identity;
        body.transform.localScale = Vector3.one;
        body.sharedMesh = _mesh;
        body.GetComponent<MeshRenderer>().sharedMaterials = _materials;
        Transform arrow = root.transform.Find("Arrow");
        if (arrow)
            UnityEngine.Object.DestroyImmediate(arrow.gameObject);
        UnityEngine.Object.DestroyImmediate((MonoBehaviour)original);
        TimerReadoutVisuals visuals = root.AddComponent<TimerReadoutVisuals>();
        visuals.IsDelay = true;
        double[] screenPosition = ScreenPosition();
        visuals.Screen = Instruments.CreateLabel(root, "DelayCountdown", "0.0 s", new Vector3((float)screenPosition[0], (float)screenPosition[1], (float)screenPosition[2]), Quaternion.Euler(90f, 0f, 0f), LabelScale);
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
        string[] names =
        {
            "IN",
            "TIME",
            "OUT"
        };
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
            foreach (SphereCollider collider in socket.interactive.GetComponentsInChildren<SphereCollider>(true))
                collider.radius = .075f;
        }

        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
        {
            interaction.gameObject.name = settings.fullLabel;
            BoxCollider box = interaction.GetComponent<BoxCollider>();
            if (!box)
                continue;
            box.transform.localPosition = Vector3.zero;
            ConfigureColliders(box);
        }
    }

    internal static double[] SocketPosition(byte index)
    {
        double edge = (double)Width / 2d;
        switch (index)
        {
            case 0:
                return new[]
                {
                    0d,
                    (double)SocketHeight,
                    -edge
                };
            case 1:
                return new[]
                {
                    edge,
                    (double)SocketHeight,
                    0d
                };
            case 2:
                return new[]
                {
                    0d,
                    (double)SocketHeight,
                    edge
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    internal static double SocketYaw(byte index)
    {
        switch (index)
        {
            case 0:
                return 0d;
            case 1:
                return -90d;
            case 2:
                return 180d;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    internal static double[][] SocketPanelBoxes()
    {
        double width = NativePlateWidth * SocketScale;
        double height = (NativePlateAbove + NativePlateBelow) * SocketScale;
        double depth = NativePlateDepth * SocketScale;
        double center = SocketHeight + (NativePlateAbove - NativePlateBelow) * SocketScale * .5d;
        double edge = (double)Width / 2d;
        return new[]
        {
            new[]
            {
                0d,
                center,
                -(edge - depth * .5d),
                width,
                height,
                depth
            },
            new[]
            {
                edge - depth * .5d,
                center,
                0d,
                depth,
                height,
                width
            },
            new[]
            {
                0d,
                center,
                edge - depth * .5d,
                width,
                height,
                depth
            }
        };
    }

    internal static double[] ScreenPosition() => new[]
    {
        0d,
        9.8d * ((double)Width / 21d) + .0008d,
        0d
    };
    private static void EnsureMesh(Material opaque)
    {
        if (_mesh && _materials != null)
            return;
        using (Stream stream = typeof(DelayAppearance).Assembly.GetManifestResourceStream("StellarDriveEssentials.Delay.Model.obj"))
        using (var reader = new StreamReader(stream ?? throw new MissingManifestResourceException("The delay model is missing.")))
            _mesh = ObjModel.FromObj(reader.ReadToEnd(), "Essentials_DelayModule");
        if (_mesh.subMeshCount != 6)
            throw new InvalidDataException("The delay OBJ must contain its six material groups.");
        Vector3[] vertices = _mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] *= ModelScale;
        _mesh.vertices = vertices;
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = Color.white;
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
        var boxes = new[]
        {
            new[]
            {
                0d,
                1.125d,
                0d,
                18d,
                2.25d,
                18d
            },
            new[]
            {
                0d,
                2.35d,
                0d,
                17.85d,
                .2d,
                17.85d
            },
            new[]
            {
                0d,
                6.02d,
                0d,
                17.7d,
                7.14d,
                17.7d
            },
            new[]
            {
                0d,
                9.625d,
                0d,
                7.24d,
                .35d,
                6.9d
            }
        };
        foreach (double[] box in boxes)
            for (int i = 0; i < box.Length; i++)
                box[i] *= scale;
        return boxes.Concat(SocketPanelBoxes()).ToArray();
    }

    internal static void ConfigureColliders(BoxCollider original)
    {
        if (!original)
            return;
        double[][] boxes = CollisionBoxes();
        for (int i = 0; i < boxes.Length; i++)
        {
            BoxCollider box = i == 0 ? original : original.gameObject.AddComponent<BoxCollider>();
            double[] b = boxes[i];
            box.center = new Vector3((float)b[0], (float)b[1], (float)b[2]);
            box.size = new Vector3((float)b[3], (float)b[4], (float)b[5]);
            if (i == 0)
                continue;
            box.sharedMaterial = original.sharedMaterial;
            box.isTrigger = original.isTrigger;
            box.contactOffset = original.contactOffset;
            box.enabled = original.enabled;
        }
    }

    internal static void Release()
    {
        if (_mesh)
            UnityEngine.Object.Destroy(_mesh);
        if (_materials != null)
            foreach (Material material in _materials)
                if (material)
                    UnityEngine.Object.Destroy(material);
        _mesh = null;
        _materials = null;
    }
}

internal static class TimerColliders
{
    private static Mesh _mesh;
    internal static void Install(HarmonyLib.Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(ShipStatefulColliders), "GenerateStatefulCollider"), postfix: new HarmonyMethod(typeof(TimerColliders), nameof(AfterColliderGenerated)));
    internal static void EnsureMesh(float apothem, float height)
    {
        if (_mesh)
            return;
        double[][] points = GetHullVertices(apothem, height);
        var vertices = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
            vertices[i] = new Vector3((float)points[i][0], (float)points[i][1], (float)points[i][2]);
        var indices = new int[84];
        int at = 0;
        for (int i = 0; i < 8; i++)
        {
            int next = (i + 1) % 8;
            indices[at++] = i;
            indices[at++] = i + 8;
            indices[at++] = next;
            indices[at++] = next;
            indices[at++] = i + 8;
            indices[at++] = next + 8;
        }

        for (int i = 1; i < 7; i++)
        {
            indices[at++] = 0;
            indices[at++] = i;
            indices[at++] = i + 1;
            indices[at++] = 8;
            indices[at++] = i + 9;
            indices[at++] = i + 8;
        }

        _mesh = new Mesh
        {
            name = "EssentialsTimerOctagonalCollider",
            hideFlags = HideFlags.DontUnloadUnusedAsset,
            vertices = vertices,
            triangles = indices
        };
        _mesh.RecalculateBounds();
    }

    internal static double[][] GetHullVertices(double apothem, double height)
    {
        var vertices = new double[16][];
        double inner = apothem - TimerAppearance.CornerBevel;
        double[] x = { apothem, inner, -inner, -apothem, -apothem, -inner, inner, apothem };
        double[] z = { inner, apothem, apothem, inner, -inner, -apothem, -apothem, -inner };
        for (int i = 0; i < 8; i++)
        {
            vertices[i] = new[] { x[i], 0d, z[i] };
            vertices[i + 8] = new[] { x[i], height, z[i] };
        }

        return vertices;
    }

    internal static void ReplaceBox(BoxCollider box)
    {
        if (!box || !_mesh)
            return;
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
        if (part == null || ReferenceEquals(part.Settings, null) || (part.Settings.id != SignalTimer.PartId && part.Settings.id != SignalDelay.PartId) || !__result)
            return;
        if (part.Settings.id == SignalDelay.PartId)
            DelayAppearance.ConfigureColliders(__result.GetComponent<BoxCollider>());
        else
            ReplaceBox(__result.GetComponent<BoxCollider>());
    }

    internal static void Release()
    {
        if (_mesh)
            UnityEngine.Object.Destroy(_mesh);
        _mesh = null;
    }
}

internal static class TimerPortHints
{
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        var hint = new HarmonyMethod(typeof(TimerPortHints), nameof(AfterSocketHover));
        harmony.Patch(AccessTools.Method(typeof(InteractionTooltipsActivator), "OnSocketHover"), postfix: hint);
        harmony.Patch(AccessTools.Method(typeof(InteractionTooltipsActivator), "OnPlugHover"), postfix: hint);
    }

    private static void AfterSocketHover(IPartSocket socket, InteractionCanvas ____instantiatedCanvas)
    {
        if (socket == null || socket.Type != CableType.Signal || !____instantiatedCanvas)
            return;
        IShipsClientProvider ships = ServiceLocator.GetService<IShipsClientProvider>();
        if (ships == null || !ships.TryGetShip(socket.EntityPartSocket.EntityId, out var ship) || !ship.TryGetStatefulPart(socket.Socket.PartId, out var part) || ReferenceEquals(part.Settings, null) || (part.Settings.id != SignalTimer.PartId && part.Settings.id != SignalDelay.PartId && part.Settings.id != LidarSensor.PartId))
            return;
        string description = part.Settings.id == LidarSensor.PartId ? LidarDescription(socket.Socket.SocketId) : part.Settings.id == SignalDelay.PartId ? DelayDescription(socket.Socket.SocketId) : Description(socket.Socket.SocketId);
        if (description == null)
            return;
        Transform wrapper = ____instantiatedCanvas.tipsWrapper;
        if (!wrapper || wrapper.Find("EssentialsTimerSocketHint"))
            return;
        TMP_Text template = wrapper.GetComponentInChildren<TMP_Text>(true);
        if (!template)
            return;
        var label = new GameObject("EssentialsTimerSocketHint", typeof(RectTransform));
        label.transform.SetParent(wrapper, false);
        label.transform.SetAsFirstSibling();
        var text = label.AddComponent<TextMeshProUGUI>();
        text.font = template.font;
        text.fontSharedMaterial = template.fontSharedMaterial;
        text.fontSize = 20f;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.richText = true;
        text.raycastTarget = false;
        text.text = description;
        text.rectTransform.sizeDelta = new Vector2(420f, 80f);
        LayoutElement layout = label.AddComponent<LayoutElement>();
        layout.preferredWidth = 420f;
        layout.preferredHeight = 80f;
    }

    internal static string Description(byte socketId)
    {
        switch (socketId)
        {
            case 0:
                return "<b>SIGNAL INPUT</b>\nConnect the signal the timer should hold.";
            case 1:
                return "<b>HOLD TIME (SECONDS)</b>\nConnect a Constant. Unplugged: 5 seconds.";
            case 2:
                return "<b>SIGNAL OUTPUT</b>\nConnect the component that should receive the held signal.";
            default:
                return null;
        }
    }

    internal static string DelayDescription(byte socketId)
    {
        switch (socketId)
        {
            case 0:
                return "<b>SIGNAL INPUT</b>\nConnect the signal to delay, including pulses and zero.";
            case 1:
                return "<b>DELAY TIME (SECONDS)</b>\nConnect a Constant. Unplugged: 5 seconds.";
            case 2:
                return "<b>SIGNAL OUTPUT</b>\nReceives each input change after the configured delay.";
            default:
                return null;
        }
    }

    internal static string LidarDescription(byte socketId)
    {
        switch (socketId)
        {
            case 0:
                return "<b>DISTANCE OUTPUT</b>\nWhole meters. Disabled or no hit: -1.";
            case 1:
                return "<b>ENABLE</b>\nPositive signal turns on. Zero, negative or unplugged turns off.";
            default:
                return null;
        }
    }
}

public sealed class TimerReadoutVisuals : DefaultShipPartVisuals, IContextAwarePart
{
    public TMP_Text Screen;
    public bool IsDelay;
    private PartContext _part;
    private ShipsServerTracker _server;
    private ConnectionsClientTracker _connections;
    private IWorldTimeProviderClient _time;
    private MathGateState _state;
    private readonly SignalHold _clientHold = new SignalHold();
    private double _lastTime = double.NaN;
    private float _refresh;
    private bool _hasContext;
    private bool _knownDuration;
    private bool _pulse;
    private string _lastText;
    public void SetContext(ShipPartContext context)
    {
        _part = context.Part;
        _server = ServiceLocator.GetService<ShipsServerTracker>();
        _connections = ServiceLocator.GetService<ConnectionsClientTracker>();
        _time = ServiceLocator.GetService<IWorldTimeProviderClient>();
        _hasContext = true;
    }

    public override void SetNetworkState(IStatefulPartState state)
    {
        if (!(state is MathGateState timer))
            return;
        _pulse |= _state != null && _state.Output == 0d && timer.Output != 0d;
        _state = timer;
        RefreshDisplay();
    }

    public override void ReactToNetworkStateChange(StatefulPartStateChangeEvent change) => SetNetworkState(change.NewState);
    private void LateUpdate()
    {
        if (!_hasContext || !Screen)
            return;
        _refresh += Time.unscaledDeltaTime;
        if (_refresh < .1f)
            return;
        _refresh %= .1f;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (!Screen || !_hasContext || _state == null)
            return;
        double remaining;
        if (_server && SignalTimer.TryReadRemaining(_server, _part, out remaining))
        {
            Write(remaining);
            return;
        }

        if (IsDelay)
        {
            Write(double.NaN);
            return;
        }

        if (!_connections || _time == null)
        {
            Write(double.NaN);
            return;
        }

        double now = _time.CurrentWorldTime;
        double elapsed = SignalTimer.Finite(_lastTime) ? Math.Max(0d, now - _lastTime) : 0d;
        _lastTime = now;
        var inputSocket = new EntityPartSocket(_part.ShipId, _part.PartId, 0);
        var durationSocket = new EntityPartSocket(_part.ShipId, _part.PartId, 1);
        double input = _connections.GetSocketSignalValue(inputSocket);
        double duration = _connections.TryGetSignalConnectionGroupWithSocket(durationSocket, out _) ? SignalTimer.ResolveDuration(_connections.GetSocketSignalValue(durationSocket), true) : SignalTimer.DefaultSeconds;
        if (_state.IsHeldByPlayer)
        {
            _clientHold.Step(0d, duration, 0d, false);
            _knownDuration = false;
            Write(0d);
        }
        else if (SignalTimer.Finite(input) && input != 0d)
        {
            _clientHold.Step(input, duration, 0d, true);
            _knownDuration = true;
            Write(_clientHold.RemainingSeconds);
        }
        else if (_state.Output == 0d)
        {
            _clientHold.Step(0d, duration, 0d, false);
            _knownDuration = false;
            Write(0d);
        }
        else
        {
            if (_pulse)
            {
                _clientHold.Step(_state.Output, duration, 0d, true);
                _knownDuration = true;
            }

            _clientHold.Step(0d, duration, elapsed, true);
            Write(_knownDuration ? _clientHold.RemainingSeconds : double.NaN);
        }

        _pulse = false;
    }

    private void Write(double remaining)
    {
        string text = TimerAppearance.FormatCountdown(remaining);
        if (text == _lastText)
            return;
        _lastText = text;
        Screen.text = text;
    }
}
