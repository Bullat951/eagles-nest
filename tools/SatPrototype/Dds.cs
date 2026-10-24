using System.IO.Compression;
using SkiaSharp;

/// <summary>DXT5 DDS (top mip) → RGBA, and texture access from DCS's zips (cached).</summary>
public sealed class Tex
{
    public int W, H;
    public byte[] Rgba = [];  // R, G, B, A per pixel

    private (float R, float G, float B)? _mean;

    /// <summary>Average colour (0..1), for normalising detail textures.</summary>
    public (float R, float G, float B) Mean
    {
        get
        {
            if (_mean is { } m) return m;
            double r = 0, g = 0, b = 0;
            int n = W * H;
            for (int i = 0; i < n; i += 7) { r += Rgba[4 * i]; g += Rgba[4 * i + 1]; b += Rgba[4 * i + 2]; }
            var k = 255.0 * ((n + 6) / 7);
            _mean = ((float)(r / k), (float)(g / k), (float)(b / k));
            return _mean.Value;
        }
    }

    public static Tex Decode(byte[] dds)
    {
        int h = BitConverter.ToInt32(dds, 12), w = BitConverter.ToInt32(dds, 16);
        if (System.Text.Encoding.ASCII.GetString(dds, 84, 4) != "DXT5") throw new InvalidDataException("not DXT5");
        var t = new Tex { W = w, H = h, Rgba = new byte[w * h * 4] };
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> al = stackalloc byte[8];
        Span<int> cr = stackalloc int[4], cg = stackalloc int[4], cb = stackalloc int[4];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            int o = 128 + (by * bw + bx) * 16;
            al[0] = dds[o]; al[1] = dds[o + 1];
            for (int i = 2; i < 8; i++)
                al[i] = al[0] > al[1] ? (byte)(((8 - i) * al[0] + (i - 1) * al[1]) / 7)
                    : i < 6 ? (byte)(((6 - i) * al[0] + (i - 1) * al[1]) / 5) : (byte)(i == 6 ? 0 : 255);
            ulong abits = 0;
            for (int i = 0; i < 6; i++) abits |= (ulong)dds[o + 2 + i] << (8 * i);
            int c0 = dds[o + 8] | dds[o + 9] << 8, c1 = dds[o + 10] | dds[o + 11] << 8;
            cr[0] = (c0 >> 11 & 31) * 255 / 31; cg[0] = (c0 >> 5 & 63) * 255 / 63; cb[0] = (c0 & 31) * 255 / 31;
            cr[1] = (c1 >> 11 & 31) * 255 / 31; cg[1] = (c1 >> 5 & 63) * 255 / 63; cb[1] = (c1 & 31) * 255 / 31;
            cr[2] = (2 * cr[0] + cr[1]) / 3; cg[2] = (2 * cg[0] + cg[1]) / 3; cb[2] = (2 * cb[0] + cb[1]) / 3;
            cr[3] = (cr[0] + 2 * cr[1]) / 3; cg[3] = (cg[0] + 2 * cg[1]) / 3; cb[3] = (cb[0] + 2 * cb[1]) / 3;
            uint cbits = BitConverter.ToUInt32(dds, o + 12);
            for (int i = 0; i < 16; i++)
            {
                int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                if (x >= w || y >= h) continue;
                int k = (int)(cbits >> (2 * i)) & 3, p = (y * w + x) * 4;
                t.Rgba[p] = (byte)cr[k]; t.Rgba[p + 1] = (byte)cg[k]; t.Rgba[p + 2] = (byte)cb[k];
                t.Rgba[p + 3] = al[(int)(abits >> (3 * i)) & 7];
            }
        }
        return t;
    }

    /// <summary>Bilinear sample with wrap; u, v in texture units (1 = whole texture). Returns 0..1 RGBA.</summary>
    public (float R, float G, float B, float A) Sample(double u, double v)
    {
        double fx = (u - Math.Floor(u)) * W - 0.5, fy = (v - Math.Floor(v)) * H - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float r = 0, g = 0, b = 0, a = 0;
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
        {
            int x = ((x0 + dx) % W + W) % W, y = ((y0 + dy) % H + H) % H, p = (y * W + x) * 4;
            float wgt = (dx == 0 ? 1 - tx : tx) * (dy == 0 ? 1 - ty : ty);
            r += Rgba[p] * wgt; g += Rgba[p + 1] * wgt; b += Rgba[p + 2] * wgt; a += Rgba[p + 3] * wgt;
        }
        return (r / 255, g / 255, b / 255, a / 255);
    }

    public void SavePng(string path, int maxSize = 1024, bool alpha = false)
    {
        int step = Math.Max(1, Math.Max(W, H) / maxSize), w = W / step, h = H / step;
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = (y * step * W + x * step) * 4;
            byte a = Rgba[p + 3];
            bmp.SetPixel(x, y, alpha ? new SKColor(a, a, a) : new SKColor(Rgba[p], Rgba[p + 1], Rgba[p + 2]));
        }
        using var f = File.Create(path);
        bmp.Encode(f, SKEncodedImageFormat.Png, 100);
    }
}

public static class Zips
{
    private static readonly Dictionary<string, ZipArchive> Open = [];
    private static readonly Dictionary<string, Tex?> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string Dir = @"E:\DCS World\Mods\terrains\Caucasus\vfstextures";

    /// <summary>A texture by name ("field_1_mask.tif") from any season-summer or season-free zip.</summary>
    public static Tex? Get(string name)
    {
        if (Cache.TryGetValue(name, out var t)) return t;
        var entry = name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? name : name + ".dds";
        foreach (var zp in Directory.EnumerateFiles(Dir, "*.zip"))
        {
            var fn = Path.GetFileName(zp);
            if (fn.Contains("Winter") || fn.Contains("Spring") || fn.Contains("Autumn") || fn.Contains(".min.")) continue;
            if (!Open.TryGetValue(zp, out var z)) Open[zp] = z = ZipFile.OpenRead(zp);
            var e = z.Entries.FirstOrDefault(x => x.Name.Equals(entry, StringComparison.OrdinalIgnoreCase));
            if (e is null) continue;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            try { t = Tex.Decode(ms.ToArray()); } catch { t = null; }
            Cache[name] = t;
            return t;
        }
        Cache[name] = null;
        return null;
    }
}
