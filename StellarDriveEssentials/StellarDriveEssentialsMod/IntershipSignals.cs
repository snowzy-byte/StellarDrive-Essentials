using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships.Cables;
using Ships.Cables.SignalChannels;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.SignalTransmitter;
using UnityEngine;
using System.IO;
using System.Resources;
using Players.Utils;
using Ships.Physics.Collision;
using TMPro;

namespace StellarDriveEssentials;
internal static class IntershipSignals
{
    internal const ushort PartId = 8904;
    private static ConditionalWeakTable<SignalChannelsMap, Network> _networks = new ConditionalWeakTable<SignalChannelsMap, Network>();
    private static readonly List<WeakReference<Network>> Networks = new List<WeakReference<Network>>();
    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        Type map = typeof(SignalChannelsMap);
        AntennaAppearance.Install(harmony);
        harmony.Patch(AccessTools.Method(map, "Setup"), postfix: new HarmonyMethod(typeof(IntershipSignals), nameof(AfterSetup)));
        harmony.Patch(AccessTools.Method(map, "OnStatefulPartAdded"), prefix: new HarmonyMethod(typeof(IntershipSignals), nameof(BeforeAdded)));
        harmony.Patch(AccessTools.Method(map, "OnStatefulPartUpdated"), prefix: new HarmonyMethod(typeof(IntershipSignals), nameof(BeforeChanged)));
        harmony.Patch(AccessTools.Method(map, "OnStatefulPartRemoved"), prefix: new HarmonyMethod(typeof(IntershipSignals), nameof(BeforeRemoved)));
        EssentialsParts.Register(new EssentialsPartDefinition { Id = PartId, Name = "EssentialsIntershipSignalTransmitter", Donor = "SignalTransmitter", BuildRow = 1, Configure = Configure });
    }

    private static void Configure(PartSettings settings, GameObject prefab)
    {
        settings.fullLabel = "Intership Signal Transmitter";
        settings.description = "Sends and receives signals between ships using the same channel. Place a transmitter on each ship and connect its signal socket. Channel names are case-sensitive; leave the channel empty to disable the link.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(SignalTransmitterState);
        settings.customCreationClass = string.Empty;
        SocketObject[] sockets = prefab.GetComponentsInChildren<SocketObject>(true);
        if (sockets.Length != 1 || sockets[0].Id != 0 || sockets[0].Type != CableType.Signal || sockets[0].RelayedSockets.Count != 0 || prefab.GetComponentInChildren<SignalTransmitterVisuals>(true) == null)
            throw new InvalidOperationException("The native Signal Transmitter prefab layout has changed.");
        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
            interaction.gameObject.name = settings.fullLabel;
        AntennaAppearance.Configure(settings, prefab);
    }

    private static void AfterSetup(SignalChannelsMap __instance, IShipsProvider ships, ConnectionsTracker connectionsTracker)
    {
        if (_networks.TryGetValue(__instance, out Network previous))
            previous.Dispose();
        _networks.Remove(__instance);
        var network = new Network(ships, connectionsTracker);
        _networks.Add(__instance, network);
        Networks.RemoveAll(reference => !reference.TryGetTarget(out var target) || target.Disposed);
        Networks.Add(new WeakReference<Network>(network));
    }

    private static bool IsCustom(StatefulPart part) => !ReferenceEquals(part, null) && !ReferenceEquals(part.Settings, null) && part.Settings.id == PartId;
    private static bool BeforeAdded(SignalChannelsMap __instance, uint shipId, StatefulPart statefulPart)
    {
        if (!IsCustom(statefulPart))
            return true;
        if (_networks.TryGetValue(__instance, out Network network))
            network.Update(shipId, statefulPart);
        return false;
    }

    private static bool BeforeChanged(SignalChannelsMap __instance, uint shipId, StatefulPartStateChangeEvent changeEvent)
    {
        StatefulPart part = changeEvent.Part;
        if (!IsCustom(part))
            return true;
        if (_networks.TryGetValue(__instance, out Network network))
            network.Update(shipId, part);
        return false;
    }

    private static bool BeforeRemoved(SignalChannelsMap __instance, uint shipId, StatefulPart statefulPart)
    {
        if (!IsCustom(statefulPart))
            return true;
        if (_networks.TryGetValue(__instance, out Network network))
            network.Graph.Remove(new PartContext(shipId, statefulPart.Id));
        return false;
    }

    internal static void Release()
    {
        foreach (var reference in Networks)
            if (reference.TryGetTarget(out Network network))
                network.Dispose();
        Networks.Clear();
        _networks = new ConditionalWeakTable<SignalChannelsMap, Network>();
        AntennaAppearance.Release();
    }

    private sealed class Network
    {
        internal readonly WirelessRelayGraph Graph;
        internal bool Disposed;
        private readonly IShipsProvider _ships;
        internal Network(IShipsProvider ships, ConnectionsTracker connections)
        {
            _ships = ships;
            Graph = new WirelessRelayGraph((a, b) => connections.CreateConnection(Link(a, b), CableType.Signal), (a, b) => connections.DeleteConnection(Link(a, b), CableType.Signal));
            _ships.OnShipShipStateReadUntracked += OnShipRemoved;
        }

        internal void Update(uint shipId, StatefulPart part)
        {
            if (Disposed)
                return;
            var state = part.State as SignalTransmitterState;
            Graph.Set(new PartContext(shipId, part.Id), state?.Channel, state != null && !state.IsHeldByPlayer);
        }

        private static SocketConnection Link(PartContext a, PartContext b) => new SocketConnection(new EntityPartSocket(a.ShipId, a.PartId, 0), new EntityPartSocket(b.ShipId, b.PartId, 0), SocketConnectionType.RelayedByTransmitter);
        private void OnShipRemoved(IShipStateRead ship) => Graph.RemoveShip(ship.Id);
        internal void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            _ships.OnShipShipStateReadUntracked -= OnShipRemoved;
            Graph.Clear();
        }
    }
}

internal sealed class WirelessRelayGraph
{
    private sealed class Channel
    {
        internal PartContext Anchor;
        internal readonly HashSet<PartContext> Members = new HashSet<PartContext>();
    }

    private readonly Dictionary<string, Channel> _channels = new Dictionary<string, Channel>(StringComparer.Ordinal);
    private readonly Dictionary<PartContext, string> _memberships = new Dictionary<PartContext, string>();
    private readonly Action<PartContext, PartContext> _connect;
    private readonly Action<PartContext, PartContext> _disconnect;
    internal WirelessRelayGraph(Action<PartContext, PartContext> connect, Action<PartContext, PartContext> disconnect)
    {
        _connect = connect;
        _disconnect = disconnect;
    }

    internal void Set(PartContext part, string channel, bool enabled)
    {
        string key = enabled && !string.IsNullOrWhiteSpace(channel) ? channel.Trim() : null;
        if (_memberships.TryGetValue(part, out string previous) && string.Equals(previous, key, StringComparison.Ordinal))
            return;
        Remove(part);
        if (key == null)
            return;
        if (!_channels.TryGetValue(key, out Channel group))
        {
            group = new Channel
            {
                Anchor = part
            };
            _channels.Add(key, group);
        }
        else
            _connect(group.Anchor, part);
        group.Members.Add(part);
        _memberships.Add(part, key);
    }

    internal void Remove(PartContext part)
    {
        if (!_memberships.TryGetValue(part, out string key))
            return;
        Channel group = _channels[key];
        if (group.Anchor == part)
        {
            PartContext[] remaining = group.Members.Where(member => member != part).ToArray();
            if (remaining.Length > 0)
            {
                PartContext next = remaining[0];
                for (int i = 1; i < remaining.Length; i++)
                    _connect(next, remaining[i]);
                foreach (PartContext member in remaining)
                    _disconnect(part, member);
                group.Anchor = next;
            }
        }
        else
            _disconnect(group.Anchor, part);
        group.Members.Remove(part);
        _memberships.Remove(part);
        if (group.Members.Count == 0)
            _channels.Remove(key);
    }

    internal void RemoveShip(uint shipId)
    {
        PartContext[] removed = _memberships.Keys.Where(part => part.ShipId == shipId).ToArray();
        foreach (PartContext part in removed)
            if (_memberships.TryGetValue(part, out string channel) && _channels[channel].Anchor != part)
                Remove(part);
        foreach (PartContext part in removed)
            Remove(part);
    }

    internal void Clear()
    {
        foreach (Channel group in _channels.Values)
            foreach (PartContext member in group.Members)
                if (member != group.Anchor)
                    _disconnect(group.Anchor, member);
        _memberships.Clear();
        _channels.Clear();
    }
}

internal static class AntennaAppearance
{
    internal const float ModelHeight = .5f;
    internal const float SocketVisualScale = .65f;
    internal const float SocketInteractionScale = .85f;
    internal const float ChannelCanvasScale = .005f;
    internal const double InteractionPadding = .006d;
    internal static Vector2 ChannelRectSize => new Vector2(28f, 7f);
    internal static Vector3 SocketPosition => new Vector3(0f, .07f, -.1015f);
    internal static Vector3 ChannelPosition => new Vector3(0f, .17714286f, -.1155f);
    private static Mesh _mesh;
    private static Material[] _materials;
    internal static void Install(HarmonyLib.Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(ShipStatefulColliders), "GenerateStatefulCollider"), postfix: new HarmonyMethod(typeof(AntennaAppearance), nameof(AfterColliderGenerated)));
    internal static void Configure(PartSettings settings, GameObject prefab)
    {
        SignalTransmitterVisuals visual = prefab.GetComponentInChildren<SignalTransmitterVisuals>(true);
        MeshFilter body = visual?.transform.Find("SignalTransmitter")?.GetComponent<MeshFilter>();
        if (!body)
            throw new MissingMemberException("The native transmitter mesh is unavailable.");
        EnsureMesh(body.GetComponent<MeshRenderer>().sharedMaterial);
        prefab.transform.localScale = Vector3.one;
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = Vector3.one;
        body.transform.localPosition = Vector3.zero;
        body.transform.localRotation = Quaternion.identity;
        body.transform.localScale = Vector3.one;
        body.name = "EssentialsAntenna";
        body.sharedMesh = _mesh;
        body.GetComponent<MeshRenderer>().sharedMaterials = _materials;
        ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
        bounds.center = _mesh.bounds.center;
        bounds.bounds = _mesh.bounds.size;
        settings.size = _mesh.bounds.size;
        settings.mass = 1f;
        SocketObject socket = prefab.GetComponentInChildren<SocketObject>(true);
        socket.transform.localPosition = SocketPosition;
        socket.transform.localRotation = Quaternion.identity;
        socket.visualObject.transform.localScale *= SocketVisualScale;
        socket.interactive.transform.localScale *= SocketInteractionScale;
        foreach (SphereCollider sphere in socket.interactive.GetComponentsInChildren<SphereCollider>(true))
            sphere.radius = .075f;
        Transform canvas = visual.transform.Find("Canvas");
        if (canvas)
        {
            foreach (LookAtPlayerFlat2Sides aligner in canvas.GetComponents<LookAtPlayerFlat2Sides>())
                UnityEngine.Object.DestroyImmediate(aligner);
            RectTransform panel = canvas as RectTransform;
            if (!panel || !visual.InputField)
                throw new MissingMemberException("The native channel input layout is unavailable.");
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(.5f, .5f);
            panel.sizeDelta = ChannelRectSize;
            panel.anchoredPosition3D = Vector3.zero;
            canvas.localScale = Vector3.one * ChannelCanvasScale;
            canvas.localRotation = Quaternion.identity;
            canvas.localPosition = ChannelPosition;
            TMP_InputField input = visual.InputField;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.pointSize = 5f;
            RectTransform field = input.GetComponent<RectTransform>();
            field.anchorMin = Vector2.zero;
            field.anchorMax = Vector2.one;
            field.pivot = new Vector2(.5f, .5f);
            field.offsetMin = field.offsetMax = Vector2.zero;
            field.anchoredPosition3D = Vector3.zero;
            field.localRotation = Quaternion.identity;
            field.localScale = Vector3.one;
            RectTransform viewport = input.textViewport;
            if (viewport)
            {
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.pivot = new Vector2(.5f, .5f);
                viewport.offsetMin = new Vector2(1f, .35f);
                viewport.offsetMax = new Vector2(-1f, -.35f);
                viewport.localRotation = Quaternion.identity;
                viewport.localScale = Vector3.one;
            }
            foreach (TMP_Text text in canvas.GetComponentsInChildren<TMP_Text>(true))
            {
                RectTransform rect = text.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(.5f, .5f);
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                rect.anchoredPosition3D = Vector3.zero;
                rect.localRotation = Quaternion.identity;
                rect.localScale = Vector3.one;
                text.enableAutoSizing = true;
                text.fontSizeMin = 2f;
                text.fontSizeMax = 5f;
                text.fontSize = 5f;
                text.fontStyle = FontStyles.Normal;
                text.alignment = TextAlignmentOptions.Center;
                text.margin = Vector4.zero;
                text.richText = false;
                text.color = Color.white;
            }
        }

        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
        {
            interaction.gameObject.name = settings.fullLabel;
            BoxCollider box = interaction.GetComponent<BoxCollider>();
            if (!box)
                continue;
            box.transform.localPosition = Vector3.zero;
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = Vector3.one;
            ConfigureColliders(box, true);
        }
    }

    private static void EnsureMesh(Material donor)
    {
        if (_mesh)
            return;
        using (Stream stream = typeof(AntennaAppearance).Assembly.GetManifestResourceStream("StellarDriveEssentials.Transmitter.Antenna.obj"))
        using (var reader = new StreamReader(stream ?? throw new MissingManifestResourceException("The antenna OBJ is missing.")))
            _mesh = ObjModel.FromObj(reader.ReadToEnd(), "Essentials_ParabolicAntenna50cm");
        if (!_mesh || _mesh.subMeshCount != 4 || Math.Abs(_mesh.bounds.size.y - ModelHeight) > .0005f || _mesh.bounds.size.x > .273f || _mesh.bounds.size.z > .266f || Math.Abs(_mesh.bounds.min.y) > .0005f)
            throw new InvalidDataException("The antenna model's dimensions or material groups changed.");
        Vector3[] vertices = _mesh.vertices;
        var colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = Color.white;
        _mesh.colors = colors;
        _mesh.RecalculateBounds();
        _mesh.RecalculateNormals();
        _mesh.RecalculateTangents();
        _mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _materials = new[]
        {
            TimerAppearance.MakeMaterial(donor, "AntennaLightMetal", new Color(.58f, .66f, .71f), .45f, .5f),
            TimerAppearance.MakeMaterial(donor, "AntennaBrightMetal", new Color(.70f, .77f, .81f), .6f, .4f),
            TimerAppearance.MakeMaterial(donor, "AntennaJunctionBoxes", new Color(.13f, .20f, .24f), .1f, .8f),
            TimerAppearance.MakeMaterial(donor, "AntennaInk", new Color(.035f, .045f, .055f), 0f, 1f)
        };
    }

    private static void AfterColliderGenerated(StatefulPart part, Transform __result)
    {
        if (part == null || ReferenceEquals(part.Settings, null) || part.Settings.id != IntershipSignals.PartId || !__result)
            return;
        ConfigureColliders(__result.GetComponent<BoxCollider>());
    }

    private static void ConfigureColliders(BoxCollider original, bool interaction = false)
    {
        if (!original)
            return;
        double[][] boxes = interaction ? InteractionBoxes() : CollisionBoxes();
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

    internal static double[][] InteractionBoxes()
    {
        double[][] physical = CollisionBoxes();
        var boxes = new double[physical.Length + 1][];
        for (int i = 0; i < physical.Length; i++)
        {
            double[] b = physical[i];
            boxes[i] = new[] { b[0], b[1], b[2], b[3] + 2d * InteractionPadding, b[4] + 2d * InteractionPadding, b[5] + 2d * InteractionPadding };
        }
        boxes[physical.Length] = new[] { 0d, .17714286d, -.1165d, .15d, .05d, .012d };
        return boxes;
    }

    internal static double[][] CollisionBoxes() => new[]
    {
        new[] { 0d, .017142857d, 0d, .228571429d, .034285714d, .228571429d },
        new[] { 0d, .071428571d, 0d, .2d, .108571429d, .199d },
        new[] { 0d, .177142857d, 0d, .18d, .102857143d, .222857143d },
        new[] { 0d, .289285714d, .014285714d, .037142857d, .121428571d, .037142857d },
        new[] { 0d, .35d, .028571429d, .14d, .031428571d, .04d },
        new[] { 0d, .364285714d, .074285714d, .27d, .17d, .045714286d },
        new[] { 0d, .253785714d, .074285714d, .18d, .051d, .045714286d },
        new[] { 0d, .474785714d, .074285714d, .18d, .051d, .045714286d },
        new[] { 0d, .364285714d, .137857143d, .027142857d, .027142857d, .027142857d }
    };

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
