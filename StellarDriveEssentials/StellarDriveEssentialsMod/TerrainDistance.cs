using System;

namespace StellarDriveEssentials;

// Intersect a directed ray with the native planet's radial height field. This
// needs no terrain meshes/colliders and evaluates only the possible surface shell.
internal static class TerrainDistance
{
    internal const int MaximumSamples = 512;
    private const double Tolerance = .125d;

    internal static bool TryIntersect(double x, double y, double z, double dx, double dy, double dz,
        double outerRadius, double maximumDistance, Func<double, double, double, double> radiusAt, out double distance)
    {
        distance = -1d;
        if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(dx) || !Finite(dy) || !Finite(dz)
            || !Finite(outerRadius) || outerRadius <= 0d || !Finite(maximumDistance) || maximumDistance < 0d || radiusAt == null)
            return false;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!Finite(length) || length < 1e-12d) return false;
        dx /= length; dy /= length; dz /= length;
        double projection = x * dx + y * dy + z * dz;
        // This perpendicular form avoids subtracting two almost-equal squared
        // radial distances on long rays aimed at a comparatively small planet.
        double px = x - projection * dx, py = y - projection * dy, pz = z - projection * dz;
        double discriminant = outerRadius * outerRadius - (px * px + py * py + pz * pz);
        if (!Finite(discriminant) || discriminant < 0d) return false;
        double root = Math.Sqrt(discriminant);
        double start = Math.Max(0d, -projection - root);
        double end = Math.Min(maximumDistance, -projection + root);
        if (end < start) return false;
        // Cover the complete possible shell even for nearly horizontal rays
        // just above flat ground; tiny clearance must not exhaust the budget
        // before reaching a distant hill. Refine actual sign crossings below.
        double minimumStep = Math.Max(.25d, (end - start) / (MaximumSamples - 2));

        double previous = start;
        double gap = Clearance(x, y, z, dx, dy, dz, start, radiusAt);
        if (!Finite(gap)) return false;
        if (gap <= 0d) { distance = start; return true; }
        for (int sample = 1; sample < MaximumSamples && previous < end; sample++)
        {
            // Long empty approaches skip quickly; near the surface use smaller
            // steps. Bound both query count and step size on grazing terrain rays.
            double next = Math.Min(end, previous + Math.Max(minimumStep, Math.Min(256d, gap * .25d)));
            double nextGap = Clearance(x, y, z, dx, dy, dz, next, radiusAt);
            if (!Finite(nextGap)) return false;
            if (nextGap <= 0d)
            {
                double low = previous, high = next;
                for (int refine = 0; refine < 16 && high - low > Tolerance; refine++)
                {
                    double middle = (low + high) * .5d;
                    double middleGap = Clearance(x, y, z, dx, dy, dz, middle, radiusAt);
                    if (!Finite(middleGap)) return false;
                    if (middleGap > 0d) low = middle; else high = middle;
                }
                distance = (low + high) * .5d;
                return true;
            }
            previous = next;
            gap = nextGap;
        }
        return false;
    }

    private static double Clearance(double x, double y, double z, double dx, double dy, double dz,
        double at, Func<double, double, double, double> radiusAt)
    {
        double px = x + dx * at, py = y + dy * at, pz = z + dz * at;
        double radius = Math.Sqrt(px * px + py * py + pz * pz);
        if (!Finite(radius) || radius < 1e-9d) return double.NaN;
        double surface = radiusAt(px / radius, py / radius, pz / radius);
        return Finite(surface) && surface > 0d ? radius - surface : double.NaN;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
