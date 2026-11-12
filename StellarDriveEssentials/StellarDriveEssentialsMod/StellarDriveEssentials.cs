using System;
using System.Reflection;
using MelonLoader;

[assembly: MelonInfo(typeof(StellarDriveEssentials.EssentialsMod), "StellarDrive Essentials", "1.0.3", "Ghost")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]

namespace StellarDriveEssentials;

public sealed class EssentialsMod : MelonMod
{
    public override void OnInitializeMelon()
    {
        EssentialsParts.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
        try
        {
            IntershipSignals.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
        }
        catch (Exception exception)
        {
            LoggerInstance.Error("Could not enable intership signal transmitters: " + exception);
        }
        try
        {
            FlightSpeedHud.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
        }
        catch (Exception exception)
        {
            LoggerInstance.Error("Could not enable pilot-seat speed HUD: " + exception);
        }
        try
        {
            Instruments.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
            SensorMeasurements.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
        }
        catch (Exception exception)
        {
            LoggerInstance.Error("Could not enable distance sensor and altimeter: " + exception);
        }
        try
        {
            SignalTimer.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
            SignalDelay.Install(message => LoggerInstance.Warning(message));
        }
        catch (Exception exception)
        {
            LoggerInstance.Error("Could not enable signal timer: " + exception);
        }
        try
        {
            DoorHalfGridRooms.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
            DoorHalfGridPanels.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
            HalfGridDoors.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
        }
        catch (Exception exception)
        {
            LoggerInstance.Error("Could not enable half-block door placement: " + exception);
        }
        try
        {
            DoorSignals.Install(HarmonyInstance, message => LoggerInstance.Warning(message));
        }
        catch (Exception exception)
        {
            LoggerInstance.Error("Could not enable door signal inputs: " + exception);
        }

    }

    public override void OnUpdate()
    {
        PilotSpeedFeed.Update();
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        PilotSpeedFeed.Clear();
        DoorSignals.ConfigureLoadedParts();
    }

    public override void OnDeinitializeMelon()
    {
        FlightSpeedHud.Release();
        IntershipSignals.Release();
        SensorMeasurements.Release();
        SignalTimer.Release();
        DelayAppearance.Release();
        Instruments.Release();
        LidarSensor.Release();
        HalfGridDoors.Release();
        DoorHalfGridPanels.Release();
        DoorSignals.Release();
        EssentialsParts.Release();
    }
}
