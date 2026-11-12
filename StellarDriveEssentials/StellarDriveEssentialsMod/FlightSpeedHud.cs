using System;
using System.Linq;
using HarmonyLib;
using Ships.Interface.Model.Parts.StateTypes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

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
        if (_released || _failed || !Prepare()) return;
        speed = FlightSpeedScale.DisplaySpeed(speed);
        bool entering = !_root.activeSelf;
        if (entering) { _root.SetActive(true); _hasReading = false; _drawnCenter = double.NaN; }
        if (!_hasReading || reference != _lastReference)
            _title.text = reference == RelativeSpaceType.Surface ? "SHIP SPEED  |  SURFACE"
                : reference == RelativeSpaceType.Rings ? "SHIP SPEED  |  RINGS" : "SHIP SPEED  |  SPACE";
        if (!_hasReading || !_lastSpeed.Equals(speed))
        {
            _value.text = FlightSpeedScale.FormatSpeed(speed);
            _lastSpeed = speed;
        }
        _hasReading = true;
        _lastReference = reference;
        bool showRuler = !float.IsNaN(speed);
        if (_ruler.activeSelf != showRuler) _ruler.SetActive(showRuler);
        if (!showRuler) { _drawnCenter = double.NaN; return; }
        if (speed == 0f)
        {
            if (!_wasZero || entering || double.IsNaN(_drawnCenter)) DrawRuler(0d, true);
            _wasZero = true;
            _drawnCenter = 0d;
            return;
        }
        FlightSpeedScale.TryLog(speed, out double target);
        bool reset = entering || _wasZero || double.IsNaN(_drawnCenter);
        _center = reset ? target : FlightSpeedScale.Advance(_center, target, Time.unscaledDeltaTime);
        _wasZero = false;
        if (!reset && _center == _drawnCenter) return;
        DrawRuler(_center, false);
        _drawnCenter = _center;
    }

    internal static void Hide()
    {
        if (_root && _root.activeSelf) _root.SetActive(false);
        _hasReading = false;
        _wasZero = false;
    }

    internal static void Failed(Exception error)
    {
        if (!_failed) _log?.Invoke("Could not display the pilot speed HUD: " + error);
        _failed = true;
        if (_root) _root.SetActive(false);
    }

    private static bool Prepare()
    {
        if (_root) return true;
        if (Time.unscaledTime < _retryFontAt) return false;
        _retryFontAt = Time.unscaledTime + 1f;
        TMP_FontAsset font = TMP_Settings.defaultFontAsset;
        if (!font) font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f);
        if (!font) return false;
        try
        {
            _root = new GameObject("EssentialsPilotSpeedHud", typeof(RectTransform))
                { layer = 5, hideFlags = HideFlags.HideAndDontSave };
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
            if (_root) Object.Destroy(_root);
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
            if (TickBars[i].gameObject.activeSelf != active) TickBars[i].gameObject.SetActive(active);
            if (!active) continue;
            FlightSpeedTick tick = Ticks[i];
            TickBars[i].anchoredPosition = new Vector2(tick.Position, 0f);
            TickBars[i].sizeDelta = new Vector2(1f, tick.Major ? 16f : tick.Label ? 12f : 7f);
            if (!tick.Label || labels == TickLabels.Length || (!zero && (tick.Position < 16f
                || tick.Position > FlightSpeedScale.Width - 16f)) || tick.Position - lastLabel < 42f) continue;
            TMP_Text text = TickLabels[labels];
            if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
            text.alignment = zero && tick.Value == 0d ? TextAlignmentOptions.MidlineLeft
                : zero && tick.Value == 10d ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Center;
            text.rectTransform.anchoredPosition = new Vector2(zero && tick.Value == 0d ? 0f
                : zero && tick.Value == 10d ? FlightSpeedScale.Width - 60f : tick.Position - 30f, -19f);
            if (LabelValues[labels] != tick.Value)
            {
                text.text = FlightSpeedScale.FormatTick(tick.Value);
                LabelValues[labels] = tick.Value;
            }
            labels++;
            lastLabel = tick.Position;
        }
        for (int i = labels; i < TickLabels.Length; i++)
            if (TickLabels[i].gameObject.activeSelf) TickLabels[i].gameObject.SetActive(false);
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

    private static TMP_Text Text(string name, Transform parent, TMP_FontAsset font, string value,
        float x, float y, float width, float height, float size, Color color)
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
        // The inactive parent delays TMP material initialization; outline setters require that material.
        return text;
    }

    internal static void Release()
    {
        _released = true;
        PilotSpeedFeed.Release();
        if (_root) Object.Destroy(_root);
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
