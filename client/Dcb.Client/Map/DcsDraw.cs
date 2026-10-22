using System.Windows;
using Dcb.Client.Core;

namespace Dcb.Client.Map;

/// <summary>The view in DCS map coordinates (x north, z east, metres). Drawing itself is in
/// <see cref="Gpu.SkDraw"/>.</summary>
public static class DcsDraw
{
    public static Point Screen(MapView view, Theatre theatre, double x, double z)
    {
        var (lat, lon) = theatre.ToLatLon(x, z);
        var (sx, sy) = view.ToScreenLonLat(lon, lat);
        return new Point(sx, sy);
    }

    /// <summary>The view as a DCS x/z rectangle (corners mapped back, a little margin).</summary>
    public static (double MinX, double MinZ, double MaxX, double MaxZ) ViewArea(MapView view, Theatre theatre)
    {
        double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
        foreach (var (sx, sy) in new[] { (0.0, 0.0), (view.Width, 0.0), (0.0, view.Height), (view.Width, view.Height) })
        {
            var (x, z) = ToDcs(view, theatre, sx, sy);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        var margin = Math.Max(maxX - minX, maxZ - minZ) * 0.05;
        return (minX - margin, minZ - margin, maxX + margin, maxZ + margin);
    }

    /// <summary>Screen point → DCS x/z: Newton steps on the forward mapping (it is smooth).</summary>
    public static (double X, double Z) ToDcs(MapView view, Theatre theatre, double sx, double sy)
    {
        double x = (theatre.X0 + theatre.X1) / 2, z = (theatre.Z0 + theatre.Z1) / 2;
        for (int k = 0; k < 6; k++)
        {
            var p = Screen(view, theatre, x, z);
            var px = Screen(view, theatre, x + 1000, z);
            var pz = Screen(view, theatre, x, z + 1000);
            // Jacobian (screen per 1000 m) and its inverse.
            double a = (px.X - p.X) / 1000, b = (pz.X - p.X) / 1000, c = (px.Y - p.Y) / 1000, d = (pz.Y - p.Y) / 1000;
            var det = a * d - b * c;
            if (Math.Abs(det) < 1e-12) break;
            double ex = sx - p.X, ey = sy - p.Y;
            x += (d * ex - b * ey) / det;
            z += (-c * ex + a * ey) / det;
        }
        return (x, z);
    }
}
