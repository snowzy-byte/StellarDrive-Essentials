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

namespace StellarDriveEssentials;

internal static class SignalTimer
{
    internal const ushort PartId = 8903;
    internal const double DefaultSeconds = 5d;
    internal const double MaximumSeconds = 86400d;
    private static ConditionalWeakTable<StatefulPart, SignalHold> _holds = new ConditionalWeakTable<StatefulPart, SignalHold>();
    private static ConditionalWeakTable<ConnectionsServerTracker, TimerClock> _clocks = new ConditionalWeakTable<ConnectionsServerTracker, TimerClock>();

    private sealed class TimerClock { internal double Seconds; }

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        harmony.Patch(AccessTools.Method(typeof(SignalConnectionsServerUpdater), "ExecuteOperations"),
            prefix: new HarmonyMethod(typeof(SignalTimer), nameof(BeforeEvaluation)),
            transpiler: new HarmonyMethod(typeof(SignalTimer), nameof(Transpiler)));
        harmony.Patch(AccessTools.Method(typeof(SignalConnectionGroupValues), "GetSocketSignal"),
            prefix: new HarmonyMethod(typeof(SignalTimer), nameof(BeforeSocketSignal)));
        TimerPortHints.Install(harmony);
        TimerColliders.Install(harmony);
        EssentialsParts.Register(new EssentialsPartDefinition
        {
            Id = PartId, Name = "EssentialsSignalTimer", Donor = "VelocitySensor",
            BuildRow = 1, 
            Configure = TimerAppearance.Configure
        });
    }

    private static SignalHold CreateHold(StatefulPart part) => new SignalHold();
    private static TimerClock CreateClock(ConnectionsServerTracker tracker) => new TimerClock();

    private static void BeforeEvaluation(ConnectionsServerTracker ____connections, float deltaTime)
    {
        if (ReferenceEquals(____connections, null) || !Finite(deltaTime) || deltaTime <= 0f) return;
        _clocks.GetValue(____connections, CreateClock).Seconds += deltaTime;
    }

    // Keep native dependency ordering: both inputs are evaluated before this part,
    // and downstream groups see its updated output during the very same tick.
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

    private static void ApplyEvaluatedState(TrackedShipServer ship, ushort partId, IStatefulPartState proposed,
        ConnectionsServerTracker connections, float deltaTime)
    {
        if (!(proposed is MathGateState) || !ship.TryGetStatefulPart(partId, out StatefulPart part)
            || ReferenceEquals(part.Settings, null) || (part.Settings.id != PartId && part.Settings.id != SignalDelay.PartId)
            || !(part.State is MathGateState previous))
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
        else elapsed = clock.Seconds - hold.LastEvaluationTime;
        hold.LastEvaluationTime = clock.Seconds;
        // ExecuteOperations already owns the loaded-ship operation cache.
        bool enabled = !previous.IsHeldByPlayer;
        double input = 0d;
        double duration = DefaultSeconds;
        if (enabled)
        {
            input = connections.GetSocketSignalValue(new EntityPartSocket(ship.Id, partId, 0));
            var timeSocket = new EntityPartSocket(ship.Id, partId, 1);
            duration = connections.TryGetSignalConnectionGroupWithSocket(timeSocket, out _)
                ? ResolveDuration(connections.GetSocketSignalValue(timeSocket), true) : DefaultSeconds;
        }
        double output = hold.Step(input, duration, elapsed, enabled);
        if (previous.Output != output)
            ship.UpdatePartState(partId, previous with { Output = output });
    }

    private static bool BeforeSocketSignal(EntityPartSocket entityPartSocket, IShipsProvider ____ships, ref double __result)
    {
        if (entityPartSocket.Socket.SocketId != 2 || !____ships.TryGetShip(entityPartSocket.EntityId, out var ship)
            || !ship.TryGetStatefulPart(entityPartSocket.Socket.PartId, out StatefulPart part)
            || ReferenceEquals(part.Settings, null) || (part.Settings.id != PartId && part.Settings.id != SignalDelay.PartId)
            || !(part.State is MathGateState timer))
            return true;
        __result = !timer.IsHeldByPlayer && Finite(timer.Output) ? timer.Output : 0d;
        return false;
    }

    internal static double ResolveDuration(double value, bool connected) => !connected ? DefaultSeconds
        : Finite(value) ? Math.Min(MaximumSeconds, Math.Max(0d, value)) : 0d;

    internal static bool TryReadRemaining(IShipsProvider ships, PartContext context, out double seconds)
    {
        seconds = 0d;
        if (ships == null || !ships.TryGetShip(context.ShipId, out var ship)
            || !ship.TryGetStatefulPart(context.PartId, out StatefulPart part)
            || ReferenceEquals(part.Settings, null) || (part.Settings.id != PartId && part.Settings.id != SignalDelay.PartId)
            || !(part.State is MathGateState timer)) return false;
        if (part.Settings.id == SignalDelay.PartId) return SignalDelay.TryReadRemaining(part, out seconds);
        if (timer.IsHeldByPlayer || !_holds.TryGetValue(part, out SignalHold hold) || hold.Value == 0d) return true;
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

// Runtime timing uses simulation delta, so pausing stops the countdown. Native
// MathGateState owns the networked output and the two-input/one-output contract.
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
