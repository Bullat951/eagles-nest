namespace Dcb.Client.Core;

/// <summary>Radio line of sight over the terrain grid: the straight line between two points
/// (DCS x/z, altitude ASL in metres) against the ground, with the Earth's bulge for radio
/// propagation (effective radius 4/3 of the Earth's).</summary>
public static class LineOfSight
{
    private const double EffectiveEarthRadius = 6_371_000 * 4.0 / 3.0;

    /// <summary>True when nothing on the grid rises above the line. Samples every half grid
    /// step (at most 2 000 samples); parts of the path off the grid count as clear.</summary>
    public static bool Visible(TerrainGrid g, double x1, double z1, double alt1, double x2, double z2, double alt2)
    {
        var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
        var n = (int)Math.Clamp(Math.Ceiling(length / (g.Step / 2)), 1, 2000);
        for (int k = 1; k < n; k++)
        {
            var t = (double)k / n;
            double x = x1 + (x2 - x1) * t, z = z1 + (z2 - z1) * t;
            if (HeightAt(g, x, z) is not { } ground) continue;
            // Bulge of the Earth between the ends, seen from the straight line.
            var d1 = length * t;
            var d2 = length - d1;
            var bulge = d1 * d2 / (2 * EffectiveEarthRadius);
            var line = alt1 + (alt2 - alt1) * t;
            if (ground + bulge > line) return false;
        }
        return true;
    }

    /// <summary>Ground height (m ASL) by bilinear interpolation; null off the grid.</summary>
    public static double? HeightAt(TerrainGrid g, double x, double z)
    {
        var fi = (x - g.X0) / g.Step;
        var fj = (z - g.Z0) / g.Step;
        if (fi < 0 || fj < 0 || fi > g.Nx - 1 || fj > g.Nz - 1) return null;
        int i = Math.Min((int)fi, g.Nx - 2), j = Math.Min((int)fj, g.Nz - 2);
        double ti = fi - i, tj = fj - j;
        return (g.HeightAt(i, j) * (1 - tj) + g.HeightAt(i, j + 1) * tj) * (1 - ti) +
               (g.HeightAt(i + 1, j) * (1 - tj) + g.HeightAt(i + 1, j + 1) * tj) * ti;
    }
}
