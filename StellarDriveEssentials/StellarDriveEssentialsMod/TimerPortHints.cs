using System;
using Core.Services;
using HarmonyLib;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Services;
using TMPro;
using UI.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace StellarDriveEssentials;

internal static class TimerPortHints
{
    internal static void Install(HarmonyLib.Harmony harmony)
    {
        var hint = new HarmonyMethod(typeof(TimerPortHints), nameof(AfterSocketHover));
        harmony.Patch(AccessTools.Method(typeof(InteractionTooltipsActivator), "OnSocketHover"), postfix: hint);
        harmony.Patch(AccessTools.Method(typeof(InteractionTooltipsActivator), "OnPlugHover"), postfix: hint);
    }

    private static void AfterSocketHover(IPartSocket socket, InteractionCanvas ____instantiatedCanvas)
    {
        if (socket == null || socket.Type != CableType.Signal || !____instantiatedCanvas) return;
        IShipsClientProvider ships = ServiceLocator.GetService<IShipsClientProvider>();
        if (ships == null || !ships.TryGetShip(socket.EntityPartSocket.EntityId, out var ship)
            || !ship.TryGetStatefulPart(socket.Socket.PartId, out var part)
            || ReferenceEquals(part.Settings, null) || (part.Settings.id != SignalTimer.PartId && part.Settings.id != SignalDelay.PartId && part.Settings.id != LidarSensor.PartId)) return;
        string description = part.Settings.id == LidarSensor.PartId ? LidarDescription(socket.Socket.SocketId)
            : part.Settings.id == SignalDelay.PartId ? DelayDescription(socket.Socket.SocketId) : Description(socket.Socket.SocketId);
        if (description == null) return;
        Transform wrapper = ____instantiatedCanvas.tipsWrapper;
        if (!wrapper || wrapper.Find("EssentialsTimerSocketHint")) return;
        TMP_Text template = wrapper.GetComponentInChildren<TMP_Text>(true);
        if (!template) return;
        var label = new GameObject("EssentialsTimerSocketHint", typeof(RectTransform));
        label.transform.SetParent(wrapper, false);
        label.transform.SetAsFirstSibling();
        var text = label.AddComponent<TextMeshProUGUI>();
        text.font = template.font;
        text.fontSharedMaterial = template.fontSharedMaterial;
        text.fontSize = 20f;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.richText = true;
        text.raycastTarget = false;
        text.text = description;
        text.rectTransform.sizeDelta = new Vector2(420f, 80f);
        LayoutElement layout = label.AddComponent<LayoutElement>();
        layout.preferredWidth = 420f;
        layout.preferredHeight = 80f;
    }

    internal static string Description(byte socketId)
    {
        switch (socketId)
        {
            case 0: return "<b>SIGNAL INPUT</b>\nConnect the signal the timer should hold.";
            case 1: return "<b>HOLD TIME (SECONDS)</b>\nConnect a Constant. Unplugged: 5 seconds.";
            case 2: return "<b>SIGNAL OUTPUT</b>\nConnect the component that should receive the held signal.";
            default: return null;
        }
    }

    internal static string DelayDescription(byte socketId)
    {
        switch (socketId)
        {
            case 0: return "<b>SIGNAL INPUT</b>\nConnect the signal to delay, including pulses and zero.";
            case 1: return "<b>DELAY TIME (SECONDS)</b>\nConnect a Constant. Unplugged: 5 seconds.";
            case 2: return "<b>SIGNAL OUTPUT</b>\nReceives each input change after the configured delay.";
            default: return null;
        }
    }

    internal static string LidarDescription(byte socketId)
    {
        switch (socketId)
        {
            case 0: return "<b>DISTANCE OUTPUT</b>\nWhole meters. Disabled or no hit: -1.";
            case 1: return "<b>ENABLE</b>\nPositive signal turns on. Zero, negative or unplugged turns off.";
            default: return null;
        }
    }
}
