using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;

namespace StellarDriveEssentials;

internal static class SignalDelay
{
    internal const ushort PartId = 8905;
    private static ConditionalWeakTable<StatefulPart, SignalLag> _delays = new ConditionalWeakTable<StatefulPart, SignalLag>();
    private static Action<string> _log;

    internal static void Install(Action<string> log)
    {
        _log = log;
        EssentialsParts.Register(new EssentialsPartDefinition
        {
            Id = PartId, Name = "EssentialsSignalDelay", Donor = "VelocitySensor",
            BuildRow = 1, 
            Configure = DelayAppearance.Configure
        });
    }

    internal static void Apply(TrackedShipServer ship, StatefulPart part, MathGateState previous,
        ConnectionsServerTracker connections, object clock, double now)
    {
        SignalLag delay = _delays.GetValue(part, _ => new SignalLag());
        if (!ReferenceEquals(delay.Clock, clock)) { delay.Reset(); delay.Clock = clock; }
        bool enabled = !previous.IsHeldByPlayer;
        double input = enabled ? connections.GetSocketSignalValue(new EntityPartSocket(ship.Id, part.Id, 0)) : 0d;
        var time = new EntityPartSocket(ship.Id, part.Id, 1);
        double duration = connections.TryGetSignalConnectionGroupWithSocket(time, out _)
            ? SignalTimer.ResolveDuration(connections.GetSocketSignalValue(time), true) : SignalTimer.DefaultSeconds;
        bool wasOverflowed = delay.Overflowed;
        double output = delay.Step(input, duration, now, enabled);
        if (delay.Overflowed && !wasOverflowed)
            _log?.Invoke("Signal Delay pending-change limit reached on ship " + ship.Id + ", part " + part.Id
                + ". Output stopped. Change TIME or pick up the part to reset; reduce delay or input frequency.");
        if (previous.Output != output) ship.UpdatePartState(part.Id, previous with { Output = output });
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

// Store transitions, not a sample every tick. Equal-duration events stay ordered,
// so an entire short pulse is replayed after the delay instead of being filtered.
internal sealed class SignalLag
{
    internal const int MaximumPendingChanges = 65536;
    private struct Change { internal double Due; internal double Value; }
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
        if (!enabled || !SignalTimer.Finite(now)) { Reset(); return 0d; }
        if (!SignalTimer.Finite(input)) input = 0d;
        seconds = SignalTimer.ResolveDuration(seconds, true);
        // Changing TIME starts a new timeline; it cannot reorder old events.
        if (!_initialized || seconds != _duration || now < _now)
        {
            Reset();
            _duration = seconds;
            _initialized = true;
        }
        _now = now;
        if (Overflowed) return 0d;
        if (seconds == 0d)
        {
            _pending.Clear();
            _input = Output = input;
            return Output;
        }
        // Release due events first so a full queue with events due now does not overflow unnecessarily.
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
