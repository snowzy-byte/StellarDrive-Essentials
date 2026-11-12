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
        EssentialsParts.Register(new EssentialsPartDefinition
        {
            Id = PartId, Name = "EssentialsIntershipSignalTransmitter", Donor = "SignalTransmitter",
            BuildRow = 1, Configure = Configure
        });
    }

    private static void Configure(PartSettings settings, GameObject prefab)
    {
        settings.fullLabel = "Intership Signal Transmitter";
        settings.description = "Wireless signal link between ships. Place one on each ship, enter the same channel and connect signal cables to their sockets. The socket sends and receives; multiple units can share a channel. Channel names are case-sensitive; a blank channel disables the link. No added distance limit between tracked ships. Conflicting signals use the game's normal largest-magnitude rule.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(SignalTransmitterState);
        settings.customCreationClass = string.Empty;
        SocketObject[] sockets = prefab.GetComponentsInChildren<SocketObject>(true);
        if (sockets.Length != 1 || sockets[0].Id != 0 || sockets[0].Type != CableType.Signal
            || sockets[0].RelayedSockets.Count != 0 || prefab.GetComponentInChildren<SignalTransmitterVisuals>(true) == null)
            throw new InvalidOperationException("The native Signal Transmitter prefab layout has changed.");
        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
            interaction.gameObject.name = settings.fullLabel;
        AntennaAppearance.Configure(settings, prefab);
    }

    private static void AfterSetup(SignalChannelsMap __instance, IShipsProvider ships, ConnectionsTracker connectionsTracker)
    {
        if (_networks.TryGetValue(__instance, out Network previous)) previous.Dispose();
        _networks.Remove(__instance);
        var network = new Network(ships, connectionsTracker);
        _networks.Add(__instance, network);
        Networks.RemoveAll(reference => !reference.TryGetTarget(out var target) || target.Disposed);
        Networks.Add(new WeakReference<Network>(network));
    }

    private static bool IsCustom(StatefulPart part) => !ReferenceEquals(part, null)
        && !ReferenceEquals(part.Settings, null) && part.Settings.id == PartId;

    private static bool BeforeAdded(SignalChannelsMap __instance, uint shipId, StatefulPart statefulPart)
    {
        if (!IsCustom(statefulPart)) return true;
        if (_networks.TryGetValue(__instance, out Network network)) network.Update(shipId, statefulPart);
        return false;
    }

    private static bool BeforeChanged(SignalChannelsMap __instance, uint shipId, StatefulPartStateChangeEvent changeEvent)
    {
        StatefulPart part = changeEvent.Part;
        if (!IsCustom(part)) return true;
        if (_networks.TryGetValue(__instance, out Network network)) network.Update(shipId, part);
        return false;
    }

    private static bool BeforeRemoved(SignalChannelsMap __instance, uint shipId, StatefulPart statefulPart)
    {
        if (!IsCustom(statefulPart)) return true;
        if (_networks.TryGetValue(__instance, out Network network)) network.Graph.Remove(new PartContext(shipId, statefulPart.Id));
        return false;
    }

    internal static void Release()
    {
        foreach (var reference in Networks)
            if (reference.TryGetTarget(out Network network)) network.Dispose();
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
            Graph = new WirelessRelayGraph(
                (a, b) => connections.CreateConnection(Link(a, b), CableType.Signal),
                (a, b) => connections.DeleteConnection(Link(a, b), CableType.Signal));
            _ships.OnShipShipStateReadUntracked += OnShipRemoved;
        }

        internal void Update(uint shipId, StatefulPart part)
        {
            if (Disposed) return;
            var state = part.State as SignalTransmitterState;
            Graph.Set(new PartContext(shipId, part.Id), state?.Channel, state != null && !state.IsHeldByPlayer);
        }

        private static SocketConnection Link(PartContext a, PartContext b) => new SocketConnection(
            new EntityPartSocket(a.ShipId, a.PartId, 0), new EntityPartSocket(b.ShipId, b.PartId, 0), SocketConnectionType.RelayedByTransmitter);

        private void OnShipRemoved(IShipStateRead ship) => Graph.RemoveShip(ship.Id);

        internal void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            _ships.OnShipShipStateReadUntracked -= OnShipRemoved;
            Graph.Clear();
        }
    }
}

// One spanning star per channel: N transmitters need N-1 virtual links, with no
// per-frame work. Use native relay edges so the existing evaluator retains its
// dependency ordering, zero propagation and signal conflict behavior.
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
        if (_memberships.TryGetValue(part, out string previous) && string.Equals(previous, key, StringComparison.Ordinal)) return;
        Remove(part);
        if (key == null) return;
        if (!_channels.TryGetValue(key, out Channel group))
        {
            group = new Channel { Anchor = part };
            _channels.Add(key, group);
        }
        else _connect(group.Anchor, part);
        group.Members.Add(part);
        _memberships.Add(part, key);
    }

    internal void Remove(PartContext part)
    {
        if (!_memberships.TryGetValue(part, out string key)) return;
        Channel group = _channels[key];
        if (group.Anchor == part)
        {
            PartContext[] remaining = group.Members.Where(member => member != part).ToArray();
            if (remaining.Length > 0)
            {
                PartContext next = remaining[0];
                // Add the replacement star before removing old edges, so surviving
                // endpoints remain connected throughout native group maintenance.
                for (int i = 1; i < remaining.Length; i++) _connect(next, remaining[i]);
                foreach (PartContext member in remaining) _disconnect(part, member);
                group.Anchor = next;
            }
        }
        else _disconnect(group.Anchor, part);
        group.Members.Remove(part);
        _memberships.Remove(part);
        if (group.Members.Count == 0) _channels.Remove(key);
    }

    internal void RemoveShip(uint shipId)
    {
        PartContext[] removed = _memberships.Keys.Where(part => part.ShipId == shipId).ToArray();
        // Remove leaves before anchors: rebuilding a star after each antenna on the same ship is quadratic.
        foreach (PartContext part in removed)
            if (_memberships.TryGetValue(part, out string channel) && _channels[channel].Anchor != part) Remove(part);
        foreach (PartContext part in removed) Remove(part);
    }

    internal void Clear()
    {
        foreach (Channel group in _channels.Values)
            foreach (PartContext member in group.Members)
                if (member != group.Anchor) _disconnect(group.Anchor, member);
        _memberships.Clear();
        _channels.Clear();
    }
}
