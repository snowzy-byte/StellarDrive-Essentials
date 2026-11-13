using System;
using System.Linq;
using HarmonyLib;
using Ships.Interface.Model.Parts.StateTypes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using System.Globalization;
using Ships.Interface.Model.Parts.State;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using System.Reflection;
using Players;
using Players.Interface.Model;
using Ships;
using Ships.Parts.Common.Services;
using Ships.Services;
using Ships.Visuals;
using WorldTracking.Interface.Values;

namespace StellarDriveEssentials;
internal static class FlightSpeedHud
{
    private static readonly FlightSpeedTick[] Ticks = new FlightSpeedTick[FlightSpeedScale.MaximumTicks];
    private static readonly RectTransform[] TickBars = new RectTransform[FlightSpeedScale.MaximumTicks];
    private static readonly TMP_Text[] TickLabels = new TMP_Text[8];
    private static readonly double[] LabelValues = new double[8];
    private static Action<string> _log;
    private static GameObject _root, _ruler;
    private static TMP_Text _value, _title;
    private static RectTransform _selected;
    private static bool _released, _failed, _hasReading, _wasZero;
    private static RelativeSpaceType _lastReference;
    private static float _lastSpeed, _retryFontAt;
    private static double _center, _drawnCenter = double.NaN;
    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log;
        _released = _failed = false;
        PilotSpeedFeed.Install(harmony, log);
    }

    internal static bool IsShown => _root && _root.activeSelf;

    internal static void Show(float speed, RelativeSpaceType reference)
    {
        if (_released || _failed || !Prepare())
            return;
        speed = FlightSpeedScale.DisplaySpeed(speed);
        bool entering = !_root.activeSelf;
        if (entering)
        {
            _root.SetActive(true);
            _hasReading = false;
            _drawnCenter = double.NaN;
        }

        if (!_hasReading || reference != _lastReference)
            _title.text = reference == RelativeSpaceType.Surface ? "SHIP SPEED  |  SURFACE" : reference == RelativeSpaceType.Rings ? "SHIP SPEED  |  RINGS" : "SHIP SPEED  |  SPACE";
        if (!_hasReading || !_lastSpeed.Equals(speed))
        {
            _value.text = FlightSpeedScale.FormatSpeed(speed);
            _lastSpeed = speed;
        }

        _hasReading = true;
        _lastReference = reference;
        bool showRuler = !float.IsNaN(speed);
        if (_ruler.activeSelf != showRuler)
            _ruler.SetActive(showRuler);
        if (!showRuler)
        {
            _drawnCenter = double.NaN;
            return;
        }

        if (speed == 0f)
        {
            if (!_wasZero || entering || double.IsNaN(_drawnCenter))
                DrawRuler(0d, true);
            _wasZero = true;
            _drawnCenter = 0d;
            return;
        }

        FlightSpeedScale.TryLog(speed, out double target);
        bool reset = entering || _wasZero || double.IsNaN(_drawnCenter);
        _center = reset ? target : FlightSpeedScale.Advance(_center, target, Time.unscaledDeltaTime);
        _wasZero = false;
        if (!reset && _center == _drawnCenter)
            return;
        DrawRuler(_center, false);
        _drawnCenter = _center;
    }

    internal static void Hide()
    {
        if (_root && _root.activeSelf)
            _root.SetActive(false);
        _hasReading = false;
        _wasZero = false;
    }

    internal static void Failed(Exception error)
    {
        if (!_failed)
            _log?.Invoke("Could not display the pilot speed HUD: " + error);
        _failed = true;
        if (_root)
            _root.SetActive(false);
    }

    private static bool Prepare()
    {
        if (_root)
            return true;
        if (Time.unscaledTime < _retryFontAt)
            return false;
        _retryFontAt = Time.unscaledTime + 1f;
        TMP_FontAsset font = TMP_Settings.defaultFontAsset;
        if (!font)
            font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f);
        if (!font)
            return false;
        try
        {
            _root = new GameObject("EssentialsPilotSpeedHud", typeof(RectTransform))
            {
                layer = 5,
                hideFlags = HideFlags.HideAndDontSave
            };
            _root.SetActive(false);
            Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30;
            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            CanvasGroup group = _root.AddComponent<CanvasGroup>();
            group.interactable = group.blocksRaycasts = false;
            RectTransform panel = Rect("Readout", _root.transform, 0f, 0f, 300f, 120f);
            panel.anchorMin = panel.anchorMax = panel.pivot = Vector2.one;
            panel.anchoredPosition = new Vector2(-28f, -28f);
            _title = Text("Title", panel, font, "SHIP SPEED", 16f, 9f, 268f, 18f, 11f, new Color(1f, 1f, 1f, .65f));
            _value = Text("Speed", panel, font, "-- m/s", 16f, 28f, 268f, 38f, 28f, Color.white);
            _value.enableAutoSizing = true;
            _value.fontSizeMin = 18f;
            _value.fontSizeMax = 28f;
            RectTransform ruler = Rect("Ruler", panel, 16f, 76f, FlightSpeedScale.Width, 34f);
            _ruler = ruler.gameObject;
            Bar("Baseline", ruler, 0f, 0f, FlightSpeedScale.Width, 1f, new Color(1f, 1f, 1f, .4f));
            for (int i = 0; i < TickBars.Length; i++)
            {
                TickBars[i] = Bar("Tick" + i, ruler, 0f, 0f, 1f, 8f, new Color(1f, 1f, 1f, .7f));
                TickBars[i].gameObject.SetActive(false);
            }

            for (int i = 0; i < TickLabels.Length; i++)
            {
                TickLabels[i] = Text("Scale" + i, ruler, font, "", 0f, 19f, 60f, 15f, 10f, new Color(1f, 1f, 1f, .65f));
                TickLabels[i].alignment = TextAlignmentOptions.Center;
                TickLabels[i].gameObject.SetActive(false);
                LabelValues[i] = double.NaN;
            }

            _selected = Bar("Selected", ruler, FlightSpeedScale.Width / 2f - 1f, -5f, 2f, 24f, Color.white);
            return true;
        }
        catch
        {
            if (_root)
                Object.Destroy(_root);
            _root = _ruler = null;
            _value = null;
            throw;
        }
    }

    private static void DrawRuler(double center, bool zero)
    {
        int count = zero ? FlightSpeedScale.FillZeroTicks(Ticks) : FlightSpeedScale.FillTicks(center, Ticks);
        int labels = 0;
        _selected.anchoredPosition = new Vector2(zero ? 0f : FlightSpeedScale.Width / 2f - 1f, 5f);
        float lastLabel = -100f;
        for (int i = 0; i < TickBars.Length; i++)
        {
            bool active = i < count;
            if (TickBars[i].gameObject.activeSelf != active)
                TickBars[i].gameObject.SetActive(active);
            if (!active)
                continue;
            FlightSpeedTick tick = Ticks[i];
            TickBars[i].anchoredPosition = new Vector2(tick.Position, 0f);
            TickBars[i].sizeDelta = new Vector2(1f, tick.Major ? 16f : tick.Label ? 12f : 7f);
            if (!tick.Label || labels == TickLabels.Length || (!zero && (tick.Position < 16f || tick.Position > FlightSpeedScale.Width - 16f)) || tick.Position - lastLabel < 42f)
                continue;
            TMP_Text text = TickLabels[labels];
            if (!text.gameObject.activeSelf)
                text.gameObject.SetActive(true);
            text.alignment = zero && tick.Value == 0d ? TextAlignmentOptions.MidlineLeft : zero && tick.Value == 10d ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Center;
            text.rectTransform.anchoredPosition = new Vector2(zero && tick.Value == 0d ? 0f : zero && tick.Value == 10d ? FlightSpeedScale.Width - 60f : tick.Position - 30f, -19f);
            if (LabelValues[labels] != tick.Value)
            {
                text.text = FlightSpeedScale.FormatTick(tick.Value);
                LabelValues[labels] = tick.Value;
            }

            labels++;
            lastLabel = tick.Position;
        }

        for (int i = labels; i < TickLabels.Length; i++)
            if (TickLabels[i].gameObject.activeSelf)
                TickLabels[i].gameObject.SetActive(false);
    }

    private static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.gameObject.layer = 5;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    private static RectTransform Bar(string name, Transform parent, float x, float y, float width, float height, Color color)
    {
        RectTransform rect = Rect(name, parent, x, y, width, height);
        Image image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return rect;
    }

    private static TMP_Text Text(string name, Transform parent, TMP_FontAsset font, string value, float x, float y, float width, float height, float size, Color color)
    {
        TMP_Text text = Rect(name, parent, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Truncate;
        text.richText = text.raycastTarget = false;
        return text;
    }

    internal static void Release()
    {
        _released = true;
        PilotSpeedFeed.Release();
        if (_root)
            Object.Destroy(_root);
        _root = _ruler = null;
        _value = _title = null;
        _selected = null;
        _hasReading = false;
        _drawnCenter = double.NaN;
        Array.Clear(TickBars, 0, TickBars.Length);
        Array.Clear(TickLabels, 0, TickLabels.Length);
        _log = null;
    }
}

internal struct FlightSpeedTick
{
    internal double Value;
    internal float Position;
    internal bool Major;
    internal bool Label;
}

internal static class FlightSpeedScale
{
    internal const int MaximumTicks = 40;
    internal const float Width = 268f;
    internal const double VisibleDecades = 2.4d;
    internal static bool IsPilotSeat(IStatefulPartState state) => state is PilotSeatState || state is PilotSeatAdvState;
    internal static RelativeSpaceType VelocityReference(ISpace space) => space is PlanetSurfaceSpace ? RelativeSpaceType.Surface : space is PlanetRingSpace ? RelativeSpaceType.Rings : RelativeSpaceType.Space;
    internal static float DisplaySpeed(float speed) => float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0f ? float.NaN : (float)Math.Round(speed, 0, MidpointRounding.AwayFromZero);
    internal static bool TryLog(float speed, out double logarithm)
    {
        logarithm = 0d;
        if (float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0f)
            return false;
        logarithm = Math.Log10(speed);
        return true;
    }

    internal static string FormatSpeed(float speed)
    {
        if (float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0f)
            return "-- m/s";
        string number = DisplaySpeed(speed).ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
        return number + " m/s";
    }

    internal static int FillZeroTicks(FlightSpeedTick[] ticks)
    {
        if (ticks == null)
            return 0;
        int count = Math.Min(11, ticks.Length);
        for (int i = 0; i < count; i++)
            ticks[i] = new FlightSpeedTick
            {
                Value = i,
                Position = i * Width / 10f,
                Major = i % 5 == 0,
                Label = i % 5 == 0
            };
        return count;
    }

    internal static string FormatTick(double value)
    {
        if (value == 0d)
            return "0";
        if (value >= 1e6d && value < 1e9d)
            return (value / 1e6d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (value >= 1e3d && value < 1e6d)
            return (value / 1e3d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return value < .01d || value >= 1e9d ? value.ToString("0.#E+0", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static double Advance(double current, double target, float deltaTime)
    {
        if (double.IsNaN(target) || double.IsInfinity(target))
            return current;
        if (double.IsNaN(current) || double.IsInfinity(current))
            return target;
        if (float.IsNaN(deltaTime) || deltaTime <= 0f)
            return current;
        double amount = 1d - Math.Exp(-16d * Math.Min(deltaTime, .1f));
        double next = current + (target - current) * amount;
        return Math.Abs(next - target) < .0001d ? target : next;
    }

    internal static int FillTicks(double center, FlightSpeedTick[] ticks)
    {
        if (ticks == null || double.IsNaN(center) || double.IsInfinity(center) || Math.Abs(center) > 100d)
            return 0;
        double left = center - VisibleDecades / 2d;
        int count = 0;
        for (int exponent = (int)Math.Floor(left); exponent <= (int)Math.Ceiling(center + VisibleDecades / 2d); exponent++)
        {
            double decade = Math.Pow(10d, exponent);
            for (int multiplier = 1; multiplier <= 9; multiplier++)
            {
                double value = multiplier * decade;
                double position = (Math.Log10(value) - left) / VisibleDecades * Width;
                if (position < 0d || position > Width)
                    continue;
                if (count == ticks.Length)
                    return count;
                ticks[count++] = new FlightSpeedTick
                {
                    Value = value,
                    Position = (float)position,
                    Major = multiplier == 1,
                    Label = value >= 1d && (multiplier == 1 || multiplier == 5)
                };
            }
        }

        return count;
    }
}

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
        if (target == null)
            throw new MissingMethodException(updater.FullName, "UpdatePilotHud");
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(PilotSpeedFeed), nameof(AfterPilotHudUpdate)));
    }

    private static void AfterPilotHudUpdate(TrackedPlayerLocalClient player, ShipsClientTracker ____shipsClientTracker)
    {
        try
        {
            if (ReferenceEquals(player, null) || !TryGetPilotedShip(player.State, ____shipsClientTracker, out TrackedShipClient controlled))
            {
                Clear();
                return;
            }

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

                        if (_provider)
                            _speed = _provider.GetVisualVelocityRelativeTo(reference).magnitude;
                    }
                }
                catch (Exception error)
                {
                    Warn(error);
                }
            }

            try
            {
                FlightSpeedHud.Show(_speed, _reference);
            }
            catch (Exception error)
            {
                FlightSpeedHud.Failed(error);
            }
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
        if (state.Type != PlayerStateType.Piloting || ReferenceEquals(ships, null) || !ships.TryGetTrackedShip(state.EntityId, out TrackedShipClient candidate) || !candidate.TryGetStatefulPart(state.PartId, out var seat) || !FlightSpeedScale.IsPilotSeat(seat.State))
            return false;
        ship = candidate;
        return true;
    }

    internal static bool IsStale(float lastUpdate, float now) => float.IsNaN(lastUpdate) || float.IsNaN(now) || now < lastUpdate || now - lastUpdate > .35f;
    internal static void Update()
    {
        if (FlightSpeedHud.IsShown && IsStale(_lastUpdate, Time.unscaledTime))
            Clear();
    }

    private static void Warn(Exception error)
    {
        if (_warned)
            return;
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
