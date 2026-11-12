using System;
using System.Reflection;
using HarmonyLib;
using Players;
using Players.Interface.Model;
using Ships;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Parts.Common.Services;
using Ships.Services;
using Ships.Visuals;
using UnityEngine;
using WorldTracking.Interface.Values;

namespace StellarDriveEssentials;

internal static class PilotSpeedFeed
{
    private static Action<string> _log;
    private static bool _warned;
    private static float _lastUpdate = float.NegativeInfinity, _nextSample;
    private static float _speed = float.NaN;
    private static TrackedShipClient _rootShip;
    private static ShipPartVisuals _visual;
    private static ShipVelocityProvider _provider;
    private static RelativeSpaceType _reference;
    private static uint _controlledShipId = uint.MaxValue;

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log;
        Type updater = Assembly.Load("Assembly-CSharp").GetType("Managers.Client.Services.PilotHudUpdater", true);
        MethodInfo target = AccessTools.DeclaredMethod(updater, "UpdatePilotHud", new[] { typeof(TrackedPlayerLocalClient) });
        if (target == null) throw new MissingMethodException(updater.FullName, "UpdatePilotHud");
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(PilotSpeedFeed), nameof(AfterPilotHudUpdate)));
    }

    private static void AfterPilotHudUpdate(TrackedPlayerLocalClient player, ShipsClientTracker ____shipsClientTracker)
    {
        try
        {
            if (ReferenceEquals(player, null)
                || !TryGetPilotedShip(player.State, ____shipsClientTracker, out TrackedShipClient controlled))
            { Clear(); return; }
            float now = Time.unscaledTime;
            _lastUpdate = now;
            RelativeSpaceType reference = FlightSpeedScale.VelocityReference(ClientOrigin.LocalPhysicsSpace);
            if (now >= _nextSample || reference != _reference || _rootShip == null || _controlledShipId != controlled.Id)
            {
                _controlledShipId = controlled.Id;
                _nextSample = now + .1f;
                _speed = float.NaN;
                _reference = reference;
                try
                {
                    if (____shipsClientTracker.TryGetTrackedRootShip(controlled.Id, out TrackedShipClient root))
                    {
                        if (!ReferenceEquals(_rootShip, root) || !ReferenceEquals(_visual, root.VisualObject) || !_provider)
                        {
                            _rootShip = root;
                            _visual = root.VisualObject;
                            _provider = _visual ? _visual.GetComponent<ShipVelocityProvider>() : null;
                        }
                        if (_provider) _speed = _provider.GetVisualVelocityRelativeTo(reference).magnitude;
                    }
                }
                catch (Exception error) { Warn(error); }
            }
            try { FlightSpeedHud.Show(_speed, _reference); }
            catch (Exception error) { FlightSpeedHud.Failed(error); }
        }
        catch (Exception error)
        {
            Clear();
            Warn(error);
        }
    }

    internal static bool TryGetPilotedShip(PlayerState state, ShipsClientTracker ships, out TrackedShipClient ship)
    {
        ship = null;
        if (state.Type != PlayerStateType.Piloting || ReferenceEquals(ships, null)
            || !ships.TryGetTrackedShip(state.EntityId, out TrackedShipClient candidate)
            || !candidate.TryGetStatefulPart(state.PartId, out var seat)
            || !FlightSpeedScale.IsPilotSeat(seat.State)) return false;
        ship = candidate;
        return true;
    }

    internal static bool IsStale(float lastUpdate, float now) =>
        float.IsNaN(lastUpdate) || float.IsNaN(now) || now < lastUpdate || now - lastUpdate > .35f;

    internal static void Update()
    {
        if (FlightSpeedHud.IsShown && IsStale(_lastUpdate, Time.unscaledTime)) Clear();
    }

    private static void Warn(Exception error)
    {
        if (_warned) return;
        _warned = true;
        _log?.Invoke("Pilot speed temporarily unavailable: " + error.Message);
    }

    internal static void Clear()
    {
        FlightSpeedHud.Hide();
        _rootShip = null;
        _visual = null;
        _provider = null;
        _speed = float.NaN;
        _nextSample = 0f;
        _lastUpdate = float.NegativeInfinity;
        _controlledShipId = uint.MaxValue;
    }

    internal static void Release()
    {
        Clear();
        _log = null;
        _warned = false;
    }
}
