using System;
using System.Globalization;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using WorldTracking.Interface.Model.Spaces;
using WorldTracking.Interface.Model.Spaces.Common;

namespace StellarDriveEssentials;

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

    internal static RelativeSpaceType VelocityReference(ISpace space) => space is PlanetSurfaceSpace
        ? RelativeSpaceType.Surface : space is PlanetRingSpace ? RelativeSpaceType.Rings : RelativeSpaceType.Space;

    internal static float DisplaySpeed(float speed) => float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0f
        ? float.NaN : (float)Math.Round(speed, 0, MidpointRounding.AwayFromZero);

    internal static bool TryLog(float speed, out double logarithm)
    {
        logarithm = 0d;
        if (float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0f) return false;
        logarithm = Math.Log10(speed);
        return true;
    }

    internal static string FormatSpeed(float speed)
    {
        if (float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0f) return "-- m/s";
        string number = DisplaySpeed(speed).ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
        return number + " m/s";
    }

    internal static int FillZeroTicks(FlightSpeedTick[] ticks)
    {
        if (ticks == null) return 0;
        int count = Math.Min(11, ticks.Length);
        for (int i = 0; i < count; i++)
            ticks[i] = new FlightSpeedTick { Value = i, Position = i * Width / 10f, Major = i % 5 == 0, Label = i % 5 == 0 };
        return count;
    }

    internal static string FormatTick(double value)
    {
        if (value == 0d) return "0";
        if (value >= 1e6d && value < 1e9d) return (value / 1e6d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (value >= 1e3d && value < 1e6d) return (value / 1e3d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return value < .01d || value >= 1e9d
            ? value.ToString("0.#E+0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static double Advance(double current, double target, float deltaTime)
    {
        if (double.IsNaN(target) || double.IsInfinity(target)) return current;
        if (double.IsNaN(current) || double.IsInfinity(current)) return target;
        if (float.IsNaN(deltaTime) || deltaTime <= 0f) return current;
        double amount = 1d - Math.Exp(-16d * Math.Min(deltaTime, .1f));
        double next = current + (target - current) * amount;
        return Math.Abs(next - target) < .0001d ? target : next;
    }

    internal static int FillTicks(double center, FlightSpeedTick[] ticks)
    {
        if (ticks == null || double.IsNaN(center) || double.IsInfinity(center) || Math.Abs(center) > 100d) return 0;
        double left = center - VisibleDecades / 2d;
        int count = 0;
        for (int exponent = (int)Math.Floor(left); exponent <= (int)Math.Ceiling(center + VisibleDecades / 2d); exponent++)
        {
            double decade = Math.Pow(10d, exponent);
            for (int multiplier = 1; multiplier <= 9; multiplier++)
            {
                double value = multiplier * decade;
                double position = (Math.Log10(value) - left) / VisibleDecades * Width;
                if (position < 0d || position > Width) continue;
                if (count == ticks.Length) return count;
                ticks[count++] = new FlightSpeedTick
                {
                    Value = value, Position = (float)position,
                    Major = multiplier == 1, Label = value >= 1d && (multiplier == 1 || multiplier == 5)
                };
            }
        }
        return count;
    }
}
