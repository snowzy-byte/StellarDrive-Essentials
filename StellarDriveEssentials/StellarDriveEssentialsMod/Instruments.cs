using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Research;
using Research.Interface.Values;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using TMPro;
using UnityEngine;

namespace StellarDriveEssentials;

internal static class Instruments
{
    internal const ushort DistanceId = 8901;
    internal const ushort AltimeterId = 8902;
    internal const string BuildTab = "Essentials";
    private static readonly HashSet<ResearchSettings> Researches = new HashSet<ResearchSettings>();
    private static bool _registeringResearch;
    private static Action<string> _log;

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log;
        SocketAnchors.Install(harmony);
        EssentialsParts.Register(new EssentialsPartDefinition
        {
            Id = DistanceId, Name = "EssentialsDistanceSensor", Donor = "VelocitySensor",
            BuildRow = 0, 
            Configure = (settings, prefab) => Configure(settings, prefab, false)
        });
        EssentialsParts.Register(new EssentialsPartDefinition
        {
            Id = AltimeterId, Name = "EssentialsAltimeter", Donor = "VelocitySensor",
            BuildRow = 0, 
            Configure = (settings, prefab) => Configure(settings, prefab, true)
        });
        LidarSensor.Register(harmony, log);
        EssentialsParts.PartsReady += EnsureLoadedResearch;
        var beforeClient = new HarmonyMethod(typeof(Instruments), nameof(BeforeClientResearch));
        harmony.Patch(AccessTools.Method(typeof(ResearchClientProxy), "Awake"), prefix: beforeClient);
        harmony.Patch(AccessTools.Method(typeof(ResearchClientProxy), "IsPartUnlocked"), prefix: beforeClient);
        harmony.Patch(AccessTools.PropertyGetter(typeof(ResearchClientProxy), "Researches"), prefix: beforeClient);
        harmony.Patch(AccessTools.Method(typeof(ResearchSettingsList), "GetTechsRequiredForPart"),
            prefix: new HarmonyMethod(typeof(Instruments), nameof(BeforeResearchLookup)));
    }

    private static void Configure(PartSettings settings, GameObject prefab, bool altitude)
    {
        settings.fullLabel = altitude ? "Altimeter" : "Distance Sensor";
        settings.description = altitude
            ? "Altitude above the planet's sea-level reference radius, rounded to the nearest whole meter on the display and output socket. Updates 10 times per second. Display -- / output -1 when unavailable."
            : "Distance along the arrow, up to 10 km, rounded to whole meters. Detects loaded obstacles and this ship's blocks, ignoring the sensor itself. Planet terrain also uses the native height field beyond collider loading distance, without loading more meshes. Point the arrow toward the target; sky or a missed target outputs -1. Updates 10 times per second.";
        settings.localizedDescription = null;
        settings.internalStateClass = nameof(ConstantState);
        settings.customCreationClass = string.Empty;
        SocketObject[] sockets = prefab.GetComponentsInChildren<SocketObject>(true);
        if (sockets.Length != 1 || sockets[0].Id != 0 || sockets[0].Type != CableType.Signal || sockets[0].RelayedSockets.Count != 0)
            throw new InvalidOperationException("The native Velocity Sensor socket layout has changed.");
        IShipPartVisual original = prefab.GetComponentInChildren<IShipPartVisual>(true);
        if (original == null || original.GetType().Name != "SignalSplitterVisuals")
            throw new InvalidOperationException("The native Velocity Sensor visual layout has changed.");
        GameObject visualRoot = original.GameObject;
        TMP_Text screen = CreateReadout(visualRoot, altitude);
        UnityEngine.Object.DestroyImmediate((MonoBehaviour)original);
        SensorReadoutVisuals readout = visualRoot.AddComponent<SensorReadoutVisuals>();
        readout.Screen = screen;
        readout.IsAltimeter = altitude;
        Transform arrow = visualRoot.transform.Find("Arrow");
        if (arrow != null)
        {
            if (altitude)
                UnityEngine.Object.DestroyImmediate(arrow.gameObject);
            else
            {
                arrow.localPosition = new Vector3(0f, .161f, .16f);
                arrow.localScale *= .4f;
            }
        }
        foreach (PartInteraction interaction in prefab.GetComponentsInChildren<PartInteraction>(true))
            interaction.gameObject.name = settings.fullLabel;
    }

    private static TMP_Text CreateReadout(GameObject visualRoot, bool altitude)
        => CreateLabel(visualRoot, "EssentialsInstrumentReadout", (altitude ? "ALT" : "DIST") + "\n-- m",
            new Vector3(0f, .165f, -.015f), Quaternion.Euler(90f, 0f, 0f), .005f);

    internal static TMP_Text CreateLabel(GameObject visualRoot, string name, string label, Vector3 position, Quaternion rotation, float scale)
    {
        PartSettings display = Resources.FindObjectsOfTypeAll<PartSettings>().FirstOrDefault(s => s && s.name == "SignalDisplay" && s.part);
        Transform donor = display?.part.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Canvas"
            && t.GetComponentInChildren<TMP_Text>(true) != null);
        if (donor == null)
            throw new MissingMemberException("The native signal display text template is unavailable.");
        GameObject temporary = new GameObject("Essentials_InactiveInstrumentLabel");
        temporary.SetActive(false);
        GameObject canvas = null;
        try
        {
            canvas = UnityEngine.Object.Instantiate(donor.gameObject, temporary.transform, false);
            foreach (MonoBehaviour component in canvas.GetComponentsInChildren<MonoBehaviour>(true))
                if (component.GetType().Name == "LookAtPlayerFlat2Sides" || component.GetType().Name == "GraphicRaycaster")
                    UnityEngine.Object.DestroyImmediate(component);
            TMP_Text text = canvas.GetComponentInChildren<TMP_Text>(true);
            text.raycastTarget = false;
            text.enableAutoSizing = true;
            text.fontSizeMin = 5f;
            text.fontSizeMax = 18f;
            text.text = label;
            canvas.name = name;
            canvas.transform.SetParent(visualRoot.transform, false);
            canvas.transform.localPosition = position;
            canvas.transform.localRotation = rotation;
            canvas.transform.localScale = Vector3.one * scale;
            return text;
        }
        catch
        {
            if (canvas) UnityEngine.Object.DestroyImmediate(canvas);
            throw;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(temporary);
        }
    }

    private static void BeforeClientResearch(ResearchSettingsList ___researchSettingsList) => EnsureResearch(___researchSettingsList);
    private static void BeforeResearchLookup(ResearchSettingsList __instance) => EnsureResearch(__instance);

    private static void EnsureLoadedResearch()
    {
        foreach (ResearchSettingsList list in Resources.FindObjectsOfTypeAll<ResearchSettingsList>())
            EnsureResearch(list);
    }

    private static void EnsureResearch(ResearchSettingsList list)
    {
        if (_registeringResearch || list == null || list.list == null) return;
        _registeringResearch = true;
        try
        {
            PartSettings distance = EssentialsParts.Get(DistanceId);
            PartSettings altitude = EssentialsParts.Get(AltimeterId);
            PartSettings timer = EssentialsParts.Get(SignalTimer.PartId);
            PartSettings delay = EssentialsParts.Get(SignalDelay.PartId);
            PartSettings transmitter = EssentialsParts.Get(IntershipSignals.PartId);
            PartSettings lidar = EssentialsParts.Get(LidarSensor.PartId);
            foreach (ResearchSettings research in list.list)
            {
                if (!research || research.unlockedParts == null || !research.unlockedParts.Any(p => p && p.name == "SignalSplitter")) continue;
                var parts = research.unlockedParts.ToList();
                if (distance && !parts.Contains(distance)) parts.Add(distance);
                if (altitude && !parts.Contains(altitude)) parts.Add(altitude);
                if (timer && !parts.Contains(timer)) parts.Add(timer);
                if (delay && !parts.Contains(delay)) parts.Add(delay);
                if (transmitter && !parts.Contains(transmitter)) parts.Add(transmitter);
                if (lidar && !parts.Contains(lidar)) parts.Add(lidar);
                if (parts.Count != research.unlockedParts.Length)
                {
                    research.unlockedParts = parts.ToArray();
                    Researches.Add(research);
                }
            }
        }
        finally { _registeringResearch = false; }
    }

    internal static bool IsInstrument(ushort id) => id == DistanceId || id == AltimeterId || id == LidarSensor.PartId;

    internal static string FormatReading(double reading, bool valid) => valid && SensorMeasurements.IsReadingValid(reading)
        ? SensorMeasurements.RoundMeters(reading).ToString("0", CultureInfo.InvariantCulture) + " m" : "-- m";

    internal static void Release()
    {
        SocketAnchors.Release();
        EssentialsParts.PartsReady -= EnsureLoadedResearch;
        foreach (ResearchSettings research in Researches)
            if (research && research.unlockedParts != null)
                research.unlockedParts = research.unlockedParts.Where(p => !p || (!IsInstrument(p.id) && p.id != SignalTimer.PartId && p.id != SignalDelay.PartId && p.id != IntershipSignals.PartId)).ToArray();
        Researches.Clear();
    }
}

public sealed class SensorReadoutVisuals : DefaultShipPartVisuals
{
    public TMP_Text Screen;
    public bool IsAltimeter;
    public bool IsLidar;
    public GameObject ActivityVisual;

    public override void SetNetworkState(IStatefulPartState state)
    {
        if (!(state is ConstantState sensor)) return;
        bool disabled = IsLidar && (sensor.IsHeldByPlayer || sensor.ConstantValue == LidarSensor.DisabledReading);
        if (ActivityVisual) ActivityVisual.SetActive(!disabled);
        if (Screen)
            Screen.text = (IsLidar ? "LIDAR" : IsAltimeter ? "ALT" : "DIST") + "\n" + (disabled ? "OFF" : Instruments.FormatReading(sensor.ConstantValue,
                !sensor.IsHeldByPlayer && SensorMeasurements.IsReadingValid(sensor.ConstantValue)));
    }

    public override void ReactToNetworkStateChange(StatefulPartStateChangeEvent change) => SetNetworkState(change.NewState);
}
