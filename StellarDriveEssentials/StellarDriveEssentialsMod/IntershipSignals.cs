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
        settings.description = "Wireless signal link between ships. Place one on each ship, enter the same channel and connect signal cables to their sockets. The socket sends and receives; multiple units can share a channel. Channel names are case-sensitive; a blank channel disables the link. No added distance limit between tracked ships. Conflicting signals use the game's normal largest-magnitude rule.";
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
    internal const float ModelHeight = 1f;
    internal const float BaseLift = .06f;
    internal const float SourceHeight = 36.76522f;
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
        socket.visualObject.transform.localScale *= .45f;
        socket.interactive.transform.localScale *= .45f;
        foreach (SphereCollider sphere in socket.interactive.GetComponentsInChildren<SphereCollider>(true))
            sphere.radius = .075f;
        Transform canvas = visual.transform.Find("Canvas");
        if (canvas)
        {
            foreach (LookAtPlayerFlat2Sides aligner in canvas.GetComponents<LookAtPlayerFlat2Sides>())
                UnityEngine.Object.DestroyImmediate(aligner);
            canvas.localPosition = new Vector3(0f, .15f, -.084f);
            canvas.localRotation = Quaternion.identity;
            canvas.localScale *= .6f;
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

    private static void EnsureMesh(Material donor)
    {
        if (_mesh)
            return;
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
        for (int i = 0; i < colors.Length; i++)
            colors[i] = Color.white;
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
        return new[]
        {
            x * scale,
            y * scale + BaseLift * Math.Max(0d, Math.Min(1d, y / .7d)),
            z * scale
        };
    }

    private static void AfterColliderGenerated(StatefulPart part, Transform __result)
    {
        if (part == null || ReferenceEquals(part.Settings, null) || part.Settings.id != IntershipSignals.PartId || !__result)
            return;
        ConfigureColliders(__result.GetComponent<BoxCollider>());
    }

    private static void ConfigureColliders(BoxCollider original)
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

    internal static double[][] CollisionBoxes()
    {
        double s = ModelHeight / (double)SourceHeight;
        return new[]
        {
            new[]
            {
                0d,
                (.7d * s + BaseLift) * .5d,
                0d,
                5.2d * s,
                .7d * s + BaseLift,
                5.2d * s
            },
            new[]
            {
                0d,
                17.6d * s + BaseLift,
                0d,
                .96d * s,
                34.4d * s,
                .84d * s
            },
            new[]
            {
                0d,
                13.646465d * s + BaseLift,
                .12222d * s,
                34.4d * s,
                5.60405d * s,
                7.35556d * s
            },
            new[]
            {
                0d,
                21.95d * s + BaseLift,
                0d,
                13.6d * s,
                9.0244d * s,
                3.546d * s
            },
            new[]
            {
                0d,
                31.78261d * s + BaseLift,
                2.9d * s,
                7.7697d * s,
                9.96522d * s,
                10.8182d * s
            }
        };
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
