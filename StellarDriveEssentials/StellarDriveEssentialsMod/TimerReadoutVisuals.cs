using System;
using Core.Services;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Parts.Common;
using Ships.Parts.Common.Model;
using Ships.Parts.Common.Model.Reactions;
using Ships.Parts.Common.Utils;
using Ships.Services;
using TMPro;
using UnityEngine;
using WorldTracking.Interface.Services;

namespace StellarDriveEssentials;

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
        if (!(state is MathGateState timer)) return;
        _pulse |= _state != null && _state.Output == 0d && timer.Output != 0d;
        _state = timer;
        RefreshDisplay();
    }

    public override void ReactToNetworkStateChange(StatefulPartStateChangeEvent change) => SetNetworkState(change.NewState);

    private void LateUpdate()
    {
        if (!_hasContext || !Screen) return;
        _refresh += Time.unscaledDeltaTime;
        if (_refresh < .1f) return;
        _refresh %= .1f;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (!Screen || !_hasContext || _state == null) return;
        double remaining;
        // Host/singleplayer reads the authoritative hold and simulation clock.
        // No network-state events are generated solely to animate this display.
        if (_server && SignalTimer.TryReadRemaining(_server, _part, out remaining))
        {
            Write(remaining);
            return;
        }
        // Native networking sends output values, not the transition queue. A
        // remote client cannot reconstruct a running delay countdown reliably.
        if (IsDelay) { Write(double.NaN); return; }
        // On remote clients, present native received circuit values. Joining an
        // already-running hold has no start timestamp, so show -- until known.
        if (!_connections || _time == null) { Write(double.NaN); return; }
        double now = _time.CurrentWorldTime;
        double elapsed = SignalTimer.Finite(_lastTime) ? Math.Max(0d, now - _lastTime) : 0d;
        _lastTime = now;
        var inputSocket = new EntityPartSocket(_part.ShipId, _part.PartId, 0);
        var durationSocket = new EntityPartSocket(_part.ShipId, _part.PartId, 1);
        double input = _connections.GetSocketSignalValue(inputSocket);
        double duration = _connections.TryGetSignalConnectionGroupWithSocket(durationSocket, out _)
            ? SignalTimer.ResolveDuration(_connections.GetSocketSignalValue(durationSocket), true) : SignalTimer.DefaultSeconds;
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
        if (text == _lastText) return;
        _lastText = text;
        Screen.text = text;
    }
}
