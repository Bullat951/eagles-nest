using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dcb.Client.Core;

namespace Dcb.Client.Map;

/// <summary>Colours a terrain grid: row 0 of the image is the northernmost grid row. Used by
/// the DCS Map tile renderer.</summary>
public static class TerrainImage
{
    private static readonly (double Height, Color Color)[] Tint =
    [
        (-50, Color.FromRgb(0xB5, 0xCC, 0x9C)),
        (300, Color.FromRgb(0xCB, 0xDA, 0xA8)),
        (800, Color.FromRgb(0xE2, 0xD8, 0xA8)),
        (1500, Color.FromRgb(0xD2, 0xB8, 0x8C)),
        (2500, Color.FromRgb(0xB6, 0x98, 0x78)),
        (3500, Color.FromRgb(0xE6, 0xE2, 0xDC)),
        (5000, Color.FromRgb(0xFF, 0xFF, 0xFF)),
    ];
    private static readonly Color Water = Color.FromRgb(0xA6, 0xC6, 0xE2);
    private static readonly Color Shallow = Color.FromRgb(0xBA, 0xD5, 0xEA);

    /// <summary>Relief image of a grid: elevation tint, hill shading, water.</summary>
    public static BitmapSource Render(TerrainGrid g)
    {
        var px = new byte[g.Nx * g.Nz * 4];
        for (int r = 0; r < g.Nx; r++)
        {
            int i = g.Nx - 1 - r;
            for (int j = 0; j < g.Nz; j++)
            {
                var surface = g.SurfaceAt(i, j);
                var c = surface is Surface.Water or Surface.ShallowWater
                    ? surface == Surface.Water ? Water : Shallow
                    : Shade(TintFor(g.HeightAt(i, j)), ShadeAt(g, i, j));
                int o = (r * g.Nz + j) * 4;
                px[o] = c.B;
                px[o + 1] = c.G;
                px[o + 2] = c.R;
                px[o + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(g.Nz, g.Nx, 96, 96, PixelFormats.Bgra32, null, px, g.Nz * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Hill-shade factor of a grid cell (1 on flat ground, 0.55–1.25): light from the
    /// north-west, 45° up. Coarse grids smooth slopes away, so relief is exaggerated in
    /// proportion to the cell size (×4 at 2 km, ×1 at 500 m).</summary>
    public static double ShadeAt(TerrainGrid g, int i, int j)
    {
        const double lx = -0.5, ly = 0.5, lz = 0.7071;
        var exaggeration = Math.Clamp(4 * g.Step / 2000, 1, 4);
        double dEast = (g.HeightAt(i, Math.Min(j + 1, g.Nz - 1)) - g.HeightAt(i, Math.Max(j - 1, 0))) / (2 * g.Step);
        double dNorth = (g.HeightAt(Math.Min(i + 1, g.Nx - 1), j) - g.HeightAt(Math.Max(i - 1, 0), j)) / (2 * g.Step);
        double nx = -dEast * exaggeration, ny = -dNorth * exaggeration, nz = 1;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return Math.Clamp((nx * lx + ny * ly + nz * lz) / len / lz, 0.55, 1.25);
    }

    private static Color TintFor(double h)
    {
        if (h <= Tint[0].Height) return Tint[0].Color;
        for (int k = 1; k < Tint.Length; k++)
        {
            if (h <= Tint[k].Height)
            {
                var (h0, c0) = Tint[k - 1];
                var (h1, c1) = Tint[k];
                var t = (h - h0) / (h1 - h0);
                return Color.FromRgb((byte)(c0.R + (c1.R - c0.R) * t), (byte)(c0.G + (c1.G - c0.G) * t), (byte)(c0.B + (c1.B - c0.B) * t));
            }
        }
        return Tint[^1].Color;
    }

    /// <summary>A colour with alpha laid over an opaque one.</summary>
    public static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        return Color.FromRgb((byte)(top.R * a + under.R * (1 - a)), (byte)(top.G * a + under.G * (1 - a)), (byte)(top.B * a + under.B * (1 - a)));
    }

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R * (1 - t) + b.R * t), (byte)(a.G * (1 - t) + b.G * t), (byte)(a.B * (1 - t) + b.B * t));

    public static Color Shade(Color c, double f) =>
        Color.FromRgb((byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));
}
