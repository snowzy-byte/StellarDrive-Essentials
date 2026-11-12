using Planets.Terrain.Evaluation.Config;

namespace StellarDriveEssentials;

internal static class SeaLevelReference
{
    internal static double GetTerrainCeiling(TerrainConfig config)
    {
        if (config == null) return SensorMeasurements.LidarMaxDistance;
        int output = 100;
        int remaining = (config.colorElevationSteps?.Length ?? 0) + 1;
        while (remaining-- > 0)
        {
            TerrainGenerationStep step = FindStep(config, output);
            if (step is ColorElevationStep color) { output = color.inputId; continue; }
            if (step is ClampStep clamp && !float.IsNaN(clamp.max) && !float.IsInfinity(clamp.max)
                && !float.IsNaN(clamp.smoothness) && !float.IsInfinity(clamp.smoothness))
                return clamp.max + System.Math.Abs(clamp.smoothness) + 1d;
            break;
        }
        // A conservative envelope for graphs without a final bounded clamp.
        // It is only broad-phase culling; actual readings use native terrain.
        return SensorMeasurements.LidarMaxDistance;
    }

    // The native terrain graph selects output 100. A final hard clamp creates
    // the flat sea/ice surface; earlier clamps do not necessarily survive later noise.
    internal static double GetOffset(TerrainConfig config)
    {
        if (config == null) return 0d;
        int output = 100;
        // Only color operations preserve elevation here. This bound also rejects cycles,
        // without allocating a graph/list/visited set on every instrument sample.
        int remaining = (config.colorElevationSteps?.Length ?? 0) + 1;
        while (remaining-- > 0)
        {
            TerrainGenerationStep step = FindStep(config, output);
            if (step is ColorElevationStep color)
            {
                output = color.inputId;
                continue;
            }
            if (step is ClampStep clamp && clamp.smoothness == 0f
                && !float.IsNaN(clamp.min) && !float.IsInfinity(clamp.min)
                && !float.IsNaN(clamp.max) && !float.IsInfinity(clamp.max) && clamp.max >= clamp.min)
                return clamp.min;
            return 0d;
        }
        return 0d;
    }

    private static TerrainGenerationStep FindStep(TerrainConfig config, int output) =>
        // Match TerrainOperationStack.BuildElevationGraph's native first-producer order.
        FindStep(config.noiseSteps, output) ?? FindStep(config.clampSteps, output)
        ?? FindStep(config.absSteps, output) ?? FindStep(config.craterSteps, output)
        ?? FindStep(config.colorElevationSteps, output) ?? FindStep(config.addSteps, output);

    private static TerrainGenerationStep FindStep(TerrainGenerationStep[] steps, int output)
    {
        if (steps != null)
            foreach (TerrainGenerationStep step in steps)
                if (step != null && step.outputId == output) return step;
        return null;
    }
}
