using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Map;

/// <summary>DCS's own F10 map-mode palette, <c>vfsTextures/Map.zip: mapmodepalette.tif</c>
/// (64×512, read from the local install): the left half is a height ramp (row 0 = highest,
/// white; row 511 = lowest, pale tan), the right half 16 class swatches of 32 rows (fields,
/// built-up, forest, water…, alpha included).</summary>
public sealed class MapModePalette
{
    /// <summary>Height at the top of the ramp. ED does not document the scale; linear 0–5000 m
    /// puts snow above ~4 200 m and Syria's lowlands in the tan/brown band. Not yet calibrated
    /// against a DCS F10 screenshot.</summary>
    public const double RampTopMeters = 5000;

    public Color[] Ramp { get; }
    public Color[] Classes { get; }

    private MapModePalette(Color[] ramp, Color[] classes) => (Ramp, Classes) = (ramp, classes);

    public Color AtHeight(double meters)
    {
        var row = (int)Math.Round((Ramp.Length - 1) * (1 - Math.Clamp(meters / RampTopMeters, 0, 1)));
        return Ramp[row];
    }

    /// <summary>Water: the palette's water swatch (class 8, translucent blue) over the lowest ramp colour.</summary>
    public Color Water
    {
        get
        {
            Color top = Classes[8], under = AtHeight(0);
            double a = top.A / 255.0;
            return Color.FromRgb((byte)(top.R * a + under.R * (1 - a)), (byte)(top.G * a + under.G * (1 - a)), (byte)(top.B * a + under.B * (1 - a)));
        }
    }

    private static readonly Dictionary<string, MapModePalette?> Cache = [];

    public static MapModePalette? For(string terrainId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(terrainId, out var cached)) return cached;
            MapModePalette? palette = null;
            try
            {
                if (DcsInstall.Find() is { } dcs)
                {
                    var zip = Path.Combine(dcs, "Mods", "terrains", terrainId, "vfsTextures", "Map.zip");
                    if (File.Exists(zip)) palette = Load(zip);
                }
            }
            catch (Exception)
            {
                palette = null;  // our own relief colours instead
            }
            Cache[terrainId] = palette;
            return palette;
        }
    }

    private static MapModePalette Load(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("mapmodepalette.tif") ?? throw new FileNotFoundException("mapmodepalette.tif");
        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);
        ms.Position = 0;
        var frame = new TiffBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        Color At(int x, int y)
        {
            var o = (y * w + x) * 4;
            return Color.FromArgb(px[o + 3], px[o + 2], px[o + 1], px[o]);
        }
        var ramp = Enumerable.Range(0, h).Select(y => At(w / 4, y)).ToArray();
        var swatch = h / 16;
        var classes = Enumerable.Range(0, 16).Select(k => At(3 * w / 4, k * swatch + swatch / 2)).ToArray();
        return new MapModePalette(ramp, classes);
    }
}
