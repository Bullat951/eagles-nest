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

    /// <summary>Water: the palette's water swatch (translucent blue) over the lowest ramp colour.
    /// Syria has it as class 8; Caucasus and Persian Gulf as class 9, with black at 8.</summary>
    public Color Water
    {
        get
        {
            var top = Classes.Where(c => c.A is > 40 and < 220 && c.B > 2 * Math.Max(c.R, c.G) && c.B > 90)
                .DefaultIfEmpty(Classes[8]).First();
            var under = AtHeight(0);
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
                    // Syria: Map.zip with a TIFF; Caucasus, Persian Gulf: MapTextures.zip with a DXT5 DDS.
                    var textures = Path.Combine(dcs, "Mods", "terrains", terrainId, "vfsTextures");
                    if (File.Exists(Path.Combine(textures, "Map.zip"))) palette = Load(Path.Combine(textures, "Map.zip"));
                    else if (File.Exists(Path.Combine(textures, "MapTextures.zip"))) palette = Load(Path.Combine(textures, "MapTextures.zip"));
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
        var entry = archive.GetEntry("mapmodepalette.tif") ?? archive.GetEntry("mapmodepalette.tif.dds")
            ?? throw new FileNotFoundException("mapmodepalette.tif");
        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);
        int w, h;
        byte[] px;
        if (entry.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            (w, h, px) = Dxt5.Decode(ms.ToArray());
        else
        {
            ms.Position = 0;
            var frame = new TiffBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            (w, h) = (bgra.PixelWidth, bgra.PixelHeight);
            px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);
        }
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

/// <summary>The top level of a DXT5 (BC3) DDS as BGRA rows, top first: enough for DCS's
/// map palettes (no DX10 header, no other formats).</summary>
internal static class Dxt5
{
    public static (int Width, int Height, byte[] Bgra) Decode(byte[] dds)
    {
        if (dds.Length < 128 || BitConverter.ToUInt32(dds, 0) != 0x20534444) throw new InvalidDataException("not a DDS file");
        int h = (int)BitConverter.ToUInt32(dds, 12), w = (int)BitConverter.ToUInt32(dds, 16);
        if (System.Text.Encoding.ASCII.GetString(dds, 84, 4) != "DXT5") throw new InvalidDataException("DDS is not DXT5");
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        if (dds.Length < 128 + bw * bh * 16) throw new InvalidDataException("DDS is truncated");
        var px = new byte[w * h * 4];
        Span<byte> alpha = stackalloc byte[8];
        Span<uint> colour = stackalloc uint[4];  // as 0xAARRGGBB without alpha
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var o = 128 + (by * bw + bx) * 16;
                // Alpha: two endpoints, then 16 three-bit indices.
                alpha[0] = dds[o];
                alpha[1] = dds[o + 1];
                for (int i = 2; i < 8; i++)
                    alpha[i] = alpha[0] > alpha[1]
                        ? (byte)(((8 - i) * alpha[0] + (i - 1) * alpha[1]) / 7)
                        : i < 6 ? (byte)(((6 - i) * alpha[0] + (i - 1) * alpha[1]) / 5) : (byte)(i == 6 ? 0 : 255);
                ulong alphaBits = 0;
                for (int i = 0; i < 6; i++) alphaBits |= (ulong)dds[o + 2 + i] << (8 * i);
                // Colour: two RGB565 endpoints (always the four-colour mode in DXT5), then 2-bit indices.
                int c0 = dds[o + 8] | dds[o + 9] << 8, c1 = dds[o + 10] | dds[o + 11] << 8;
                var (r0, g0, b0) = Rgb565(c0);
                var (r1, g1, b1) = Rgb565(c1);
                colour[0] = Pack(r0, g0, b0);
                colour[1] = Pack(r1, g1, b1);
                colour[2] = Pack((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
                colour[3] = Pack((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
                var colourBits = BitConverter.ToUInt32(dds, o + 12);
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                    if (x >= w || y >= h) continue;
                    var c = colour[(int)(colourBits >> (2 * i)) & 3];
                    var p = (y * w + x) * 4;
                    px[p] = (byte)c;
                    px[p + 1] = (byte)(c >> 8);
                    px[p + 2] = (byte)(c >> 16);
                    px[p + 3] = alpha[(int)(alphaBits >> (3 * i)) & 7];
                }
            }
        return (w, h, px);
    }

    private static (int R, int G, int B) Rgb565(int c) =>
        ((c >> 11 & 31) * 255 / 31, (c >> 5 & 63) * 255 / 63, (c & 31) * 255 / 31);

    private static uint Pack(int r, int g, int b) => (uint)(r << 16 | g << 8 | b);
}
