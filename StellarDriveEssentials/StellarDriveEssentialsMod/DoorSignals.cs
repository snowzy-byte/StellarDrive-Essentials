using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships;
using Ships.Cables;
using Ships.Cables.VisualObjects;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using UnityEngine;

namespace StellarDriveEssentials;
internal static class DoorSignals
{
    private const string SocketName = "Essentials_DoorOpenSignal";
    private static readonly Dictionary<int, byte> Inputs = new Dictionary<int, byte>();
    private static readonly HashSet<int> Failed = new HashSet<int>();
    private static readonly List<PrefabReplacement> Replacements = new List<PrefabReplacement>();
    private static GameObject _holder;
    private static readonly FieldInfo IdField = AccessTools.Field(typeof(SocketObject), "id");
    private static readonly FieldInfo TypeField = AccessTools.Field(typeof(SocketObject), "type");
    private static readonly FieldInfo RelayField = AccessTools.Field(typeof(SocketObject), "relayedSockets");
    private static Action<string> _log;
    private static ConditionalWeakTable<StatefulPart, CommandHistory> _commands = new ConditionalWeakTable<StatefulPart, CommandHistory>();
    private sealed class CommandHistory
    {
        internal bool Initialized;
        internal bool Open;
        internal int InputMask;
        internal double Front;
        internal double Rear;
    }

    private static CommandHistory CreateCommandHistory(StatefulPart part) => new CommandHistory();
    private sealed class PrefabReplacement
    {
        internal PartSettings Settings;
        internal GameObject Original;
        internal GameObject Copy;
    }

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log;
        if (IdField == null || TypeField == null || RelayField == null)
            throw new MissingFieldException("The native socket layout has changed.");
        harmony.Patch(AccessTools.Method(typeof(PartSettingsProvider), "EnsureDictIsCached"), prefix: new HarmonyMethod(typeof(DoorSignals), nameof(BeforeSettingsCache)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(SignalConnectionsServerUpdater), "UpdateSignalValues"), postfix: new HarmonyMethod(typeof(DoorSignals), nameof(AfterSignalUpdate)));
    }

    private static void BeforeSettingsCache(Dictionary<ushort, PartSettings> ____partsDict)
    {
        if (____partsDict == null)
            ConfigureLoadedParts();
    }

    internal static void ConfigureLoadedParts()
    {
        PartSettings[] settings = Resources.FindObjectsOfTypeAll<PartSettings>();
        PartSettings splitter = settings.FirstOrDefault(s => s != null && s.name == "SignalSplitter" && s.part != null);
        SocketObject donor = splitter?.part.GetComponentsInChildren<SocketObject>(true).FirstOrDefault(s => s.Id == 2 && s.Type == CableType.Signal && s.visualObject != null && s.interactive != null);
        if (donor == null)
            return;
        foreach (PartSettings item in settings)
        {
            if (item == null || item.part == null || (item.internalStateClass != nameof(DoorState) && item.internalStateClass != nameof(DockingDoorState)))
                continue;
            int key = item.GetInstanceID();
            if (Inputs.ContainsKey(key) || Failed.Contains(key))
                continue;
            try
            {
                ConfigureDoor(item, donor);
            }
            catch (Exception exception)
            {
                Failed.Add(key);
                _log?.Invoke("Could not add signal input to " + item.name + ": " + exception.Message);
            }
        }
    }

    private static void ConfigureDoor(PartSettings settings, SocketObject donor)
    {
        GameObject prefab = settings.part;
        bool docking = settings.internalStateClass == nameof(DockingDoorState);
        byte inputId = docking ? (byte)1 : (byte)0;
        SocketObject[] existing = prefab.GetComponentsInChildren<SocketObject>(true);
        if (existing.Count(s => s.name.StartsWith(SocketName, StringComparison.Ordinal) && (s.Id == inputId || s.Id == inputId + 1) && s.Type == CableType.Signal) == 2)
        {
            Inputs[settings.GetInstanceID()] = inputId;
            return;
        }

        if (existing.Length != inputId || existing.Where((s, i) => s.Id != i).Any())
            throw new InvalidOperationException("Unexpected existing sockets; original door preserved.");
        if (_holder == null)
        {
            _holder = new GameObject("EssentialsDoorPrefabs")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _holder.transform.position = new Vector3(0f, -100000f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(_holder);
        }

        GameObject copy = UnityEngine.Object.Instantiate(prefab, _holder.transform, false);
        copy.name = prefab.name;
        copy.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            CreateSocket(copy.transform, donor, inputId, InputPosition(settings, docking, false), false);
            CreateSocket(copy.transform, donor, (byte)(inputId + 1), InputPosition(settings, docking, true), true);
            settings.part = copy;
            Replacements.Add(new PrefabReplacement { Settings = settings, Original = prefab, Copy = copy });
            Inputs[settings.GetInstanceID()] = inputId;
        }
        catch
        {
            UnityEngine.Object.DestroyImmediate(copy);
            throw;
        }
    }

    private static void CreateSocket(Transform parent, SocketObject donor, byte id, Vector3 position, bool rear)
    {
        GameObject root = new GameObject(SocketName + (rear ? "_Rear" : "_Front"))
        {
            layer = donor.gameObject.layer
        };
        root.transform.SetParent(parent, false);
        root.transform.localPosition = position;
        root.transform.localRotation = rear ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
        root.transform.localScale = Vector3.one;
        SocketObject socket = root.AddComponent<SocketObject>();
        IdField.SetValue(socket, id);
        TypeField.SetValue(socket, CableType.Signal);
        RelayField.SetValue(socket, Array.Empty<SocketObject>());
        socket.shouldCreateFluidBufferWhenTouchingAnotherSocket = false;
        socket.visualObject = UnityEngine.Object.Instantiate(donor.visualObject, root.transform, false);
        socket.interactive = UnityEngine.Object.Instantiate(donor.interactive, root.transform, false);
        foreach (SocketObject nested in socket.visualObject.GetComponentsInChildren<SocketObject>(true))
            UnityEngine.Object.DestroyImmediate(nested);
        foreach (SocketObject nested in socket.interactive.GetComponentsInChildren<SocketObject>(true))
            UnityEngine.Object.DestroyImmediate(nested);
        foreach (SocketInteractionObject interaction in socket.interactive.GetComponentsInChildren<SocketInteractionObject>(true))
            interaction.cableType = CableType.Signal;
        socket.visualObject.transform.localPosition = Vector3.zero;
        socket.visualObject.transform.localRotation = Quaternion.identity;
        socket.interactive.transform.localPosition = Vector3.zero;
        socket.interactive.transform.localRotation = Quaternion.identity;
        if (socket.interactive.GetComponentInChildren<SocketInteractionObject>(true) == null)
            throw new InvalidOperationException("Signal socket donor has no interaction component.");
    }

    private static Vector3 InputPosition(PartSettings settings, bool docking, bool rear)
    {
        ShipPartBounds bounds = settings.part.GetComponent<ShipPartBounds>();
        if (bounds == null)
            throw new InvalidOperationException("Door prefab has no bounds.");
        return bounds.center + new Vector3(docking ? bounds.bounds.x * .5f - .075f : -bounds.bounds.x * .5f + .25f, -bounds.bounds.y * .5f + .25f, (rear ? 1f : -1f) * (docking ? .08f : .105f));
    }

    private static void AfterSignalUpdate(ConnectionsServerTracker ____connections, ShipsServerTracker ____ships)
    {
        if (____connections == null || ____ships == null || Inputs.Count == 0)
            return;
        foreach (TrackedShipServer ship in ____ships.LoadedShipsWithChildren)
        {
            foreach (StatefulPart part in ship.GetAllStatefulParts<DoorState>())
                ApplyConfiguredDoor(ship, part, ____connections);
            foreach (StatefulPart part in ship.GetAllStatefulParts<DockingDoorState>())
                ApplyConfiguredDoor(ship, part, ____connections);
        }
    }

    private static void ApplyConfiguredDoor(TrackedShipServer ship, StatefulPart part, ConnectionsServerTracker connections)
    {
        if (part != null && part.Settings != null && Inputs.TryGetValue(part.Settings.GetInstanceID(), out byte socketId))
            ApplyDoorInputs(ship, part, socketId, connections);
    }

    internal static bool ApplyDoorInputs(TrackedShipServer ship, StatefulPart part, byte firstSocketId, ConnectionsServerTracker connections)
    {
        bool anyCommand = false;
        bool open = false;
        int inputMask = 0;
        double front = 0d, rear = 0d;
        for (int i = 0; i < 2; i++)
        {
            EntityPartSocket socket = new EntityPartSocket(ship.Id, part.Id, (byte)(firstSocketId + i));
            bool connected = connections.TryGetSignalConnectionGroupWithSocket(socket, out _);
            double signal = connected ? connections.GetSignalValue(socket) : 0d;
            if (TryGetCommand(connected, signal, out bool inputOpen))
            {
                anyCommand = true;
                open |= inputOpen;
                inputMask |= 1 << i;
                if (i == 0)
                    front = signal;
                else
                    rear = signal;
            }
        }

        if (!anyCommand)
        {
            _commands.Remove(part);
            return false;
        }

        CommandHistory history = _commands.GetValue(part, CreateCommandHistory);
        if (history.Initialized && history.Open == open && (open || (history.InputMask == inputMask && history.Front == front && history.Rear == rear)))
            return false;
        bool changed = ApplyDoorCommand(ship, part, open);
        history.Open = open;
        history.InputMask = inputMask;
        history.Front = front;
        history.Rear = rear;
        history.Initialized = true;
        return changed;
    }

    internal static bool ApplyDoor(TrackedShipServer ship, StatefulPart part, byte socketId, ConnectionsServerTracker connections)
    {
        EntityPartSocket socket = new EntityPartSocket(ship.Id, part.Id, socketId);
        bool connected = connections.TryGetSignalConnectionGroupWithSocket(socket, out _);
        return TryGetCommand(connected, connected ? connections.GetSignalValue(socket) : 0d, out bool open) && ApplyDoorCommand(ship, part, open);
    }

    private static bool ApplyDoorCommand(TrackedShipServer ship, StatefulPart part, bool open)
    {
        if (!(part.State is IDoorState state) || open == state.IsOpen)
            return false;
        if (open)
            ship.OpenDoor(part.Id);
        else
            ship.CloseDoor(part.Id);
        return true;
    }

    internal static bool TryGetCommand(bool connected, double signal, out bool open)
    {
        open = signal > 0d;
        return connected && signal != 0d && !double.IsNaN(signal) && !double.IsInfinity(signal);
    }

    internal static void Release()
    {
        foreach (PrefabReplacement replacement in Replacements)
            if (replacement.Settings != null && replacement.Settings.part == replacement.Copy)
                replacement.Settings.part = replacement.Original;
        Replacements.Clear();
        Inputs.Clear();
        Failed.Clear();
        _commands = new ConditionalWeakTable<StatefulPart, CommandHistory>();
        if (_holder != null)
            UnityEngine.Object.Destroy(_holder);
        _holder = null;
    }
}
