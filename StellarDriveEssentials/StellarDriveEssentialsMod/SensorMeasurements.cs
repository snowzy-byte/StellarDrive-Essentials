using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Core.Services;
using Core.Values;
using HarmonyLib;
using Managers.Interface.Services;
using Planets.Interface.Model;
using Planets.Interface.Services;
using Planets.Terrain;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Services;
using Ships.Services;
using Ships.Parts.Common.Model;
using Ships.Physics;
using UnityEngine;
using UnityEngine.SceneManagement;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;
using WorldTracking.Interface.Services;
using Planets.Terrain.Evaluation.Config;

namespace StellarDriveEssentials;
internal static class SensorMeasurements
{
    internal const double MaxDistance = 10000d;
    internal const double LidarMaxDistance = 1000000d;
    internal const double InvalidReading = -double.MaxValue;
    private const double SampleInterval = .1d;
    private const int MaximumHits = 4096;
    private static ConditionalWeakTable<SignalConnectionsServerUpdater, Context> _contexts = new ConditionalWeakTable<SignalConnectionsServerUpdater, Context>();
    private static Action<string> _log;
    private struct Entry
    {
        internal TrackedShipServer Ship;
        internal StatefulPart Part;
        internal bool Enabled;
    }

    private sealed class Context
    {
        internal double Elapsed = SampleInterval;
        internal readonly List<Entry> Entries = new List<Entry>();
        internal readonly HashSet<uint> OutputGroups = new HashSet<uint>();
        internal RaycastHit[] Hits = new RaycastHit[32];
        internal IPlanetsServerProvider Planets;
        internal ISpacesServerProvider Spaces;
        internal IWorldTransposeServer Transpose;
        internal ISceneServerProvider Scenes;
        internal bool SceneLookupAttempted;
        internal bool Warned;
        internal bool WarnedTerrain;
        internal bool WarnedPhysics;
        internal ulong SamplingPlanet;
        internal readonly Func<double, double, double, double> TerrainRadius;
        internal readonly Dictionary<ulong, TerrainReference> SeaReferences = new Dictionary<ulong, TerrainReference>();
        internal Context()
        {
            TerrainRadius = SampleTerrain;
        }

        private double SampleTerrain(double x, double y, double z) => Planets.GetPlanetTerrainElevationAtPosition(SamplingPlanet, new Vector3((float)x, (float)y, (float)z));
        internal void ResolveServices(bool needAltimeter, bool needDistance)
        {
            if (needAltimeter || needDistance)
            {
                if (!Alive(Planets))
                    Planets = ServiceLocator.GetService<IPlanetsServerProvider>();
                if (!Alive(Spaces))
                    Spaces = ServiceLocator.GetService<ISpacesServerProvider>();
                if (!Alive(Transpose))
                    Transpose = ServiceLocator.GetService<IWorldTransposeServer>();
            }

            if (needDistance && (!SceneLookupAttempted || (Scenes != null && !Alive(Scenes))))
            {
                Scenes = ServiceLocator.GetService<ISceneServerProvider>();
                SceneLookupAttempted = true;
            }
        }
    }

    private sealed class TerrainReference
    {
        internal IPlanetProperties Planet;
        internal PlanetTerrain Terrain;
    }

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        _log = log;
        harmony.Patch(AccessTools.Method(typeof(SignalConnectionsServerUpdater), "UpdateSignalValues"), prefix: new HarmonyMethod(typeof(SensorMeasurements), nameof(BeforeSignalUpdate)));
        harmony.Patch(AccessTools.Method(typeof(SignalConnectionGroupValues), "GetSocketSignal"), prefix: new HarmonyMethod(typeof(SensorMeasurements), nameof(BeforeSocketSignal)));
        harmony.Patch(AccessTools.Method(typeof(PartStateFactory), nameof(PartStateFactory.BuildDefaultState)), postfix: new HarmonyMethod(typeof(SensorMeasurements), nameof(AfterDefaultState)));
    }

    private static Context CreateContext(SignalConnectionsServerUpdater updater) => new Context();
    private static void BeforeSignalUpdate(SignalConnectionsServerUpdater __instance, ShipsServerTracker ____ships, ConnectionsServerTracker ____connections, float deltaTime)
    {
        if (ReferenceEquals(____ships, null))
            return;
        Context context = _contexts.GetValue(__instance, CreateContext);
        if (!Finite(deltaTime) || deltaTime < 0f)
        {
            context.Elapsed = SampleInterval;
            return;
        }

        context.Elapsed += Math.Min(deltaTime, 1f);
        if (context.Elapsed < SampleInterval)
            return;
        context.Elapsed %= SampleInterval;
        context.Entries.Clear();
        bool needAltimeter = false;
        bool needDistance = false;
        foreach (TrackedShipServer ship in ____ships.Ships)
            foreach (StatefulPart part in ship.StatefulParts)
            {
                if (part == null || ReferenceEquals(part.Settings, null))
                    continue;
                ushort id = part.Settings.id;
                if (!Instruments.IsInstrument(id))
                    continue;
                bool enabled = id != LidarSensor.PartId || LidarSensor.IsEnabled(____connections, ship.Id, part.Id);
                context.Entries.Add(new Entry { Ship = ship, Part = part, Enabled = enabled });
                needAltimeter |= id == Instruments.AltimeterId;
                needDistance |= enabled && id != Instruments.AltimeterId;
            }

        if (context.Entries.Count == 0)
            return;
        try
        {
            context.ResolveServices(needAltimeter, needDistance);
            foreach (Entry entry in context.Entries)
            {
                try
                {
                    ConstantState state = entry.Part.State as ConstantState;
                    if (state == null)
                        continue;
                    if (!entry.Enabled)
                    {
                        PublishDisabled(entry.Ship, entry.Part, state);
                        continue;
                    }

                    bool valid = false;
                    double reading = -1d;
                    if (!state.IsHeldByPlayer)
                    {
                        valid = entry.Part.Settings.id == Instruments.AltimeterId ? TryMeasureAltitude(context, entry.Ship, entry.Part, out reading) : TryMeasureDistance(context, ____ships, entry.Ship, entry.Part, out reading);
                    }

                    Publish(entry.Ship, entry.Part, state, reading, valid);
                }
                catch (Exception error)
                {
                    InvalidateEntry(entry);
                    if (!context.Warned)
                    {
                        context.Warned = true;
                        _log?.Invoke("Instrument measurement unavailable on ship " + entry.Ship.Id + ", part " + entry.Part.Id + ": " + error.Message);
                    }
                }
            }
        }
        catch (Exception error)
        {
            foreach (Entry entry in context.Entries)
                InvalidateEntry(entry);
            if (!context.Warned)
            {
                context.Warned = true;
                _log?.Invoke("Instrument measurements temporarily unavailable: " + error.Message);
            }
        }
        finally
        {
            if (!ReferenceEquals(____connections, null))
            {
                context.OutputGroups.Clear();
                foreach (Entry entry in context.Entries)
                    if (____connections.TryGetSignalConnectionGroupWithSocket(new EntityPartSocket(entry.Ship.Id, entry.Part.Id, 0), out var group))
                        context.OutputGroups.Add(group.Id);
                foreach (uint group in context.OutputGroups)
                {
                    try
                    {
                        ____connections.UpdateSignalGroupValue(group);
                    }
                    catch (Exception error)
                    {
                        if (!context.Warned)
                        {
                            context.Warned = true;
                            _log?.Invoke("Instrument output group temporarily unavailable: " + error.Message);
                        }
                    }
                }
            }
        }
    }

    private static void InvalidateEntry(Entry entry)
    {
        try
        {
            if (entry.Part.State is ConstantState state)
                if (!entry.Enabled)
                    PublishDisabled(entry.Ship, entry.Part, state);
                else
                    Publish(entry.Ship, entry.Part, state, -1d, false);
        }
        catch
        {
        }
    }

    private static void Publish(TrackedShipServer ship, StatefulPart part, ConstantState previous, double reading, bool valid)
    {
        valid &= IsReadingValid(reading);
        double next = valid ? RoundMeters(reading) : InvalidReading;
        if (previous.ConstantValue == next && !previous.IsInUse)
            return;
        ship.UpdatePartState(part.Id, previous with { ConstantValue = next, IsInUse = false });
    }

    private static void PublishDisabled(TrackedShipServer ship, StatefulPart part, ConstantState previous)
    {
        if (previous.ConstantValue != LidarSensor.DisabledReading || previous.IsInUse)
            ship.UpdatePartState(part.Id, previous with { ConstantValue = LidarSensor.DisabledReading, IsInUse = false });
    }

    private static void AfterDefaultState(Ships.Interface.Settings.PartSettings partSettings, ref Ships.Interface.Model.Parts.StateTypes.IStatefulPartState __result)
    {
        if (!ReferenceEquals(partSettings, null) && Instruments.IsInstrument(partSettings.id))
            __result = new ConstantState
            {
                ConstantValue = partSettings.id == LidarSensor.PartId ? LidarSensor.DisabledReading : InvalidReading
            };
    }

    private static bool BeforeSocketSignal(EntityPartSocket entityPartSocket, IShipsProvider ____ships, ref double __result)
    {
        if (entityPartSocket.Socket.SocketId != 0 || !____ships.TryGetShip(entityPartSocket.EntityId, out var ship) || !ship.TryGetStatefulPart(entityPartSocket.Socket.PartId, out StatefulPart part) || ReferenceEquals(part.Settings, null) || !Instruments.IsInstrument(part.Settings.id) || !(part.State is ConstantState state))
            return true;
        __result = !state.IsHeldByPlayer && IsReadingValid(state.ConstantValue) ? RoundMeters(state.ConstantValue) : -1d;
        return false;
    }

    internal static bool IsReadingValid(double reading) => Finite(reading) && reading != InvalidReading && reading != LidarSensor.DisabledReading;
    internal static double GetRange(ushort settingsId) => settingsId == LidarSensor.PartId ? LidarMaxDistance : MaxDistance;
    private static Vector3 LocalRayOrigin(StatefulPart part) => part.LocalPosition + part.LocalRotation * (part.Settings.id == LidarSensor.PartId ? LidarSensor.RayOrigin : new Vector3(0f, .09f, .24f));
    private static Vector3 LocalRayDirection(StatefulPart part) => part.Settings.id == LidarSensor.PartId ? LidarSensor.RayDirection : Vector3.forward;
    internal static double RoundMeters(double reading)
    {
        double rounded = Math.Round(reading, 0, MidpointRounding.AwayFromZero);
        return rounded == 0d ? 0d : rounded;
    }

    private static bool TryMeasureDistance(Context context, ShipsServerTracker ships, TrackedShipServer ship, StatefulPart part, out double reading)
    {
        bool physical = false;
        reading = -1d;
        try
        {
            physical = TryMeasurePhysicalDistance(context, ships, ship, part, out reading);
        }
        catch (Exception error)
        {
            if (!context.WarnedPhysics)
            {
                context.WarnedPhysics = true;
                _log?.Invoke("Distance physics query temporarily unavailable; using planet terrain: " + error.Message);
            }
        }

        if (physical && reading <= .25d)
            return true;
        double maximum = physical ? reading : GetRange(part.Settings.id);
        if (TryMeasureTerrain(context, ship, part, maximum, out double terrain))
        {
            reading = terrain;
            return true;
        }

        return physical;
    }

    private static bool TryMeasureTerrain(Context context, TrackedShipServer ship, StatefulPart part, double maximum, out double reading)
    {
        reading = -1d;
        if (!Alive(context.Planets) || !Alive(context.Transpose) || !Alive(context.Spaces) || !context.Spaces.TryGetSpace(ship.RelativeSpaceId, out ISpace localSpace) || localSpace == null)
            return false;
        Vector3 local = LocalRayOrigin(part);
        var origin = new Vector3Double(local.x, local.y, local.z);
        bool found = false;
        foreach (IPlanetProperties planet in context.Planets.Planets)
        {
            if (planet?.SurfaceSpace == null || !Finite(planet.Radius) || planet.Radius <= 0f)
                continue;
            try
            {
                Vector3Double position = context.Transpose.Position(localSpace, planet.SurfaceSpace, origin);
                Vector3Double center = planet.SurfaceSpace.Center;
                Vector3 direction = context.Transpose.Rotation(localSpace, planet.SurfaceSpace, part.LocalRotation) * LocalRayDirection(part);
                if (!context.SeaReferences.TryGetValue(planet.Id, out TerrainReference reference) || !ReferenceEquals(reference.Planet, planet))
                {
                    reference = new TerrainReference
                    {
                        Planet = planet,
                        Terrain = (planet as Component)?.GetComponent<PlanetTerrain>()
                    };
                    context.SeaReferences[planet.Id] = reference;
                }

                double ceiling = SeaLevelReference.GetTerrainCeiling(reference.Terrain ? reference.Terrain.terrainConfig : null);
                context.SamplingPlanet = planet.Id;
                if (TerrainDistance.TryIntersect(position.x + center.x, position.y + center.y, position.z + center.z, direction.x, direction.y, direction.z, planet.Radius + ceiling, maximum, context.TerrainRadius, out double hit))
                {
                    reading = maximum = hit;
                    found = true;
                }
            }
            catch (Exception error)
            {
                if (!context.WarnedTerrain)
                {
                    context.WarnedTerrain = true;
                    _log?.Invoke("Distance terrain query temporarily unavailable: " + error.Message);
                }
            }
        }

        return found;
    }

    private static bool TryMeasurePhysicalDistance(Context context, ShipsServerTracker ships, TrackedShipServer ship, StatefulPart part, out double reading)
    {
        reading = -1d;
        if (!ships.TryGetTrackedRootShip(ship.Id, out TrackedShipServer root) || !root.IsRigidbodyLoaded)
            return false;
        GameObject body = ship.RigidbodyObject;
        GameObject rootBody = root.RigidbodyObject;
        if (body == null || rootBody == null || body.scene != rootBody.scene)
            return false;
        PhysicsScene physics = body.scene.GetPhysicsScene();
        if (!physics.IsValid())
            return false;
        if (Alive(context.Scenes) && (!context.Scenes.TryGetSceneSpaceId(body.scene, out uint sceneSpace) || sceneSpace != root.SpaceId))
            return false;
        double maximum = GetRange(part.Settings.id);
        Vector3 localDirection = part.LocalRotation * LocalRayDirection(part);
        Vector3 origin = body.transform.TransformPoint(LocalRayOrigin(part));
        Vector3 direction = body.transform.TransformDirection(localDirection).normalized;
        if (!Finite(origin.x) || !Finite(origin.y) || !Finite(origin.z) || !Finite(direction.x) || !Finite(direction.y) || !Finite(direction.z) || direction.sqrMagnitude < .5f)
            return false;
        int count;
        while (true)
        {
            count = physics.Raycast(origin, direction, context.Hits, (float)maximum, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count < context.Hits.Length)
                break;
            if (context.Hits.Length >= MaximumHits)
                return false;
            Array.Resize(ref context.Hits, Math.Min(context.Hits.Length * 2, MaximumHits));
        }

        double nearest = maximum + 1d;
        for (int index = 0; index < count; index++)
        {
            RaycastHit hit = context.Hits[index];
            Collider collider = hit.collider;
            if (collider == null)
                continue;
            ShipPartIdentifier hitPart = collider.GetComponentInParent<ShipPartIdentifier>();
            ShipRigidbody hitBody = hitPart != null ? collider.GetComponentInParent<ShipRigidbody>() : null;
            if (hitPart != null && hitBody != null && IsOwnSensorPart(hitPart.Id, part.Id, hitBody.gameObject == body))
                continue;
            double distance = NormalizeDistanceForRange(hit.distance, true, maximum);
            if (distance >= 0d && distance < nearest)
                nearest = distance;
        }

        reading = NormalizeDistanceForRange(nearest, nearest <= maximum, maximum);
        return reading >= 0d;
    }

    private static bool TryMeasureAltitude(Context context, TrackedShipServer ship, StatefulPart part, out double reading)
    {
        reading = -1d;
        if (!Alive(context.Planets) || !Alive(context.Spaces) || !Alive(context.Transpose) || !context.Spaces.TryGetSpace(ship.RelativeSpaceId, out ISpace localSpace) || localSpace == null || !context.Spaces.TryGetSpace(ship.SpaceId, out ISpace physicalSpace) || physicalSpace == null)
            return false;
        ISpace outside = context.Spaces.GetSpaceOutsideOfShip(ship.RelativeSpaceId);
        if (outside == null || outside is ShipRelativeSpace)
            return false;
        Vector3 local = part.LocalPosition;
        if (!Finite(local.x) || !Finite(local.y) || !Finite(local.z))
            return false;
        var localDouble = new Vector3Double(local.x, local.y, local.z);
        ulong? preferredId = outside is IPlanetSpace planetSpace ? planetSpace.PlanetId : outside is OrbitSpace orbit ? orbit.PlanetId : (ulong? )null;
        IPlanetProperties preferred = null;
        if (preferredId.HasValue)
        {
            foreach (IPlanetProperties candidate in context.Planets.Planets)
                if (candidate != null && candidate.Id == preferredId.Value)
                {
                    preferred = candidate;
                    break;
                }
        }

        if (preferred != null && TryAltitudeForPlanet(context, ship, localDouble, preferred, out reading, out _))
            return true;
        double nearest = double.PositiveInfinity;
        bool found = false;
        foreach (IPlanetProperties planet in context.Planets.Planets)
            if (planet != null && TryAltitudeForPlanet(context, ship, localDouble, planet, out double altitude, out double distance) && distance < nearest)
            {
                nearest = distance;
                reading = altitude;
                found = true;
            }

        return found;
    }

    private static bool TryAltitudeForPlanet(Context context, TrackedShipServer ship, Vector3Double local, IPlanetProperties planet, out double altitude, out double distance)
    {
        altitude = -1d;
        distance = double.PositiveInfinity;
        if (planet.SurfaceSpace == null || !Finite(planet.Radius) || planet.Radius <= 0f)
            return false;
        Vector3Double position = context.Transpose.Position(ship.RelativeSpaceId, planet.SurfaceSpace, local);
        Vector3Double center = planet.SurfaceSpace.Center;
        double x = position.x + center.x;
        double y = position.y + center.y;
        double z = position.z + center.z;
        if (!context.SeaReferences.TryGetValue(planet.Id, out TerrainReference reference) || !ReferenceEquals(reference.Planet, planet))
        {
            reference = new TerrainReference
            {
                Planet = planet,
                Terrain = (planet as Component)?.GetComponent<PlanetTerrain>()
            };
            context.SeaReferences[planet.Id] = reference;
        }

        double seaOffset = reference.Terrain != null ? SeaLevelReference.GetOffset(reference.Terrain.terrainConfig) : 0d;
        double seaRadius = planet.Radius + seaOffset;
        altitude = CalculateAltitude(x, y, z, seaRadius);
        if (!Finite(altitude))
            return false;
        distance = altitude + seaRadius;
        return Finite(distance);
    }

    internal static double CalculateAltitude(double x, double y, double z, double radius)
    {
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(radius) || radius <= 0d)
            return double.NaN;
        double largest = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
        if (largest == 0d)
            return -radius;
        double sx = x / largest;
        double sy = y / largest;
        double sz = z / largest;
        double result = largest * Math.Sqrt(sx * sx + sy * sy + sz * sz) - radius;
        return Finite(result) ? result : double.NaN;
    }

    internal static double NormalizeDistance(double distance, bool hit) => NormalizeDistanceForRange(distance, hit, MaxDistance);
    internal static double NormalizeDistanceForRange(double distance, bool hit, double maximum) => hit && Finite(distance) && Finite(maximum) && maximum > 0d && distance >= 0d && distance <= maximum ? distance : -1d;
    internal static bool IsOwnSensorPart(ushort hitPartId, ushort sensorPartId, bool sameShipBody) => sameShipBody && hitPartId == sensorPartId;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool Alive(object value) => value != null && (!(value is UnityEngine.Object unity) || unity != null);
    internal static void Release() => _contexts = new ConditionalWeakTable<SignalConnectionsServerUpdater, Context>();
}

internal static class SeaLevelReference
{
    internal static double GetTerrainCeiling(TerrainConfig config)
    {
        if (config == null)
            return SensorMeasurements.LidarMaxDistance;
        int output = 100;
        int remaining = (config.colorElevationSteps?.Length ?? 0) + 1;
        while (remaining-- > 0)
        {
            TerrainGenerationStep step = FindStep(config, output);
            if (step is ColorElevationStep color)
            {
                output = color.inputId;
                continue;
            }

            if (step is ClampStep clamp && !float.IsNaN(clamp.max) && !float.IsInfinity(clamp.max) && !float.IsNaN(clamp.smoothness) && !float.IsInfinity(clamp.smoothness))
                return clamp.max + System.Math.Abs(clamp.smoothness) + 1d;
            break;
        }

        return SensorMeasurements.LidarMaxDistance;
    }

    internal static double GetOffset(TerrainConfig config)
    {
        if (config == null)
            return 0d;
        int output = 100;
        int remaining = (config.colorElevationSteps?.Length ?? 0) + 1;
        while (remaining-- > 0)
        {
            TerrainGenerationStep step = FindStep(config, output);
            if (step is ColorElevationStep color)
            {
                output = color.inputId;
                continue;
            }

            if (step is ClampStep clamp && clamp.smoothness == 0f && !float.IsNaN(clamp.min) && !float.IsInfinity(clamp.min) && !float.IsNaN(clamp.max) && !float.IsInfinity(clamp.max) && clamp.max >= clamp.min)
                return clamp.min;
            return 0d;
        }

        return 0d;
    }

    private static TerrainGenerationStep FindStep(TerrainConfig config, int output) => FindStep(config.noiseSteps, output) ?? FindStep(config.clampSteps, output) ?? FindStep(config.absSteps, output) ?? FindStep(config.craterSteps, output) ?? FindStep(config.colorElevationSteps, output) ?? FindStep(config.addSteps, output);
    private static TerrainGenerationStep FindStep(TerrainGenerationStep[] steps, int output)
    {
        if (steps != null)
            foreach (TerrainGenerationStep step in steps)
                if (step != null && step.outputId == output)
                    return step;
        return null;
    }
}

internal static class TerrainDistance
{
    internal const int MaximumSamples = 512;
    private const double Tolerance = .125d;
    internal static bool TryIntersect(double x, double y, double z, double dx, double dy, double dz, double outerRadius, double maximumDistance, Func<double, double, double, double> radiusAt, out double distance)
    {
        distance = -1d;
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(dx) || !Finite(dy) || !Finite(dz) || !Finite(outerRadius) || outerRadius <= 0d || !Finite(maximumDistance) || maximumDistance < 0d || radiusAt == null)
            return false;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!Finite(length) || length < 1e-12d)
            return false;
        dx /= length;
        dy /= length;
        dz /= length;
        double projection = x * dx + y * dy + z * dz;
        double px = x - projection * dx, py = y - projection * dy, pz = z - projection * dz;
        double discriminant = outerRadius * outerRadius - (px * px + py * py + pz * pz);
        if (!Finite(discriminant) || discriminant < 0d)
            return false;
        double root = Math.Sqrt(discriminant);
        double start = Math.Max(0d, -projection - root);
        double end = Math.Min(maximumDistance, -projection + root);
        if (end < start)
            return false;
        double minimumStep = Math.Max(.25d, (end - start) / (MaximumSamples - 2));
        double previous = start;
        double gap = Clearance(x, y, z, dx, dy, dz, start, radiusAt);
        if (!Finite(gap))
            return false;
        if (gap <= 0d)
        {
            distance = start;
            return true;
        }

        for (int sample = 1; sample < MaximumSamples && previous < end; sample++)
        {
            double next = Math.Min(end, previous + Math.Max(minimumStep, Math.Min(256d, gap * .25d)));
            double nextGap = Clearance(x, y, z, dx, dy, dz, next, radiusAt);
            if (!Finite(nextGap))
                return false;
            if (nextGap <= 0d)
            {
                double low = previous, high = next;
                for (int refine = 0; refine < 16 && high - low > Tolerance; refine++)
                {
                    double middle = (low + high) * .5d;
                    double middleGap = Clearance(x, y, z, dx, dy, dz, middle, radiusAt);
                    if (!Finite(middleGap))
                        return false;
                    if (middleGap > 0d)
                        low = middle;
                    else
                        high = middle;
                }

                distance = (low + high) * .5d;
                return true;
            }

            previous = next;
            gap = nextGap;
        }

        return false;
    }

    private static double Clearance(double x, double y, double z, double dx, double dy, double dz, double at, Func<double, double, double, double> radiusAt)
    {
        double px = x + dx * at, py = y + dy * at, pz = z + dz * at;
        double radius = Math.Sqrt(px * px + py * py + pz * pz);
        if (!Finite(radius) || radius < 1e-9d)
            return double.NaN;
        double surface = radiusAt(px / radius, py / radius, pz / radius);
        return Finite(surface) && surface > 0d ? radius - surface : double.NaN;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
