using SkiaSharp;

public static partial class Render
{
    // Land texture placement (client: Map/Satellite/LandTextures.cs).
    const double SX = 38.398, SZ = 38.3929, OX = -0.64, OZ = 1.10;

    public static void Run(double cx, double cz, double size, double mpp, string outPath, string layers)
    {
        if (Environment.GetEnvironmentVariable("PROTO_TOWN_ORIENT") is { } orient) TownOrient = int.Parse(orient);
        if (Environment.GetEnvironmentVariable("PROTO_TOWN_TILE") is { } tt) TownTileM = double.Parse(tt, System.Globalization.CultureInfo.InvariantCulture);
        int n = (int)(size / mpp);
        var img = new float[n * n * 3];
        var L = layers.Split(',').ToHashSet();
        // Output: north up (row 0 = max x), east right (col 0 = min z).
        double X(int row) => cx + size / 2 - (row + 0.5) * mpp;
        double Z(int col) => cz - size / 2 + (col + 0.5) * mpp;

        // Base colour.
        var landCache = new Dictionary<(int, int), Tex?>();
        for (int r = 0; r < n && L.Contains("base"); r++)
        for (int c = 0; c < n; c++)
        {
            double P = X(r) / SX + OX, Q = Z(c) / SZ + OZ;
            var (cr, cg, cb) = LandAt(landCache, P, Q);
            int o = (r * n + c) * 3;
            img[o] = cr; img[o + 1] = cg; img[o + 2] = cb;
        }
        Console.WriteLine("base done");

        var recs = Mesh.Load(cx, cz, size / 2 + 50);
        foreach (var g in recs.GroupBy(r => r.Shader + "/" + r.Class))
            Console.WriteLine($"  {g.Key}: {g.Count()} records, {g.Sum(r => r.Tris.Count)} triangles");
        var town = recs.FirstOrDefault(r => r.Class == "Town");
        if (town is not null)
        {
            var mi = town.TriManifold.SelectMany(x => x).ToList();
            Console.WriteLine($"town manifold idx {mi.Min()}..{mi.Max()}, matrices: arrays {town.MatsArrays.Length}, own {town.MatsOwn.Length}; assets {string.Join(",", town.TriAsset.Distinct().Take(8).Select(a => a >= 0 && a < Mesh.Assets.Count ? Mesh.Assets[a] : a.ToString()))}");
        }

        // Grain everywhere: land noise at two scales, normalised to its mean (adds structure, keeps colour).
        if (L.Contains("noise") && Zips.Get("landnoise.tif") is { } ln && Zips.Get("noise3.tif") is { } n3)
            for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                var a = ln.Sample(X(r) / NoiseTile, Z(c) / NoiseTile).R / ln.Mean.R;
                var b = n3.Sample(X(r) / FineNoiseTile, Z(c) / FineNoiseTile).R / n3.Mean.R;
                float k = Lerp(1, a, NoiseStrength) * Lerp(1, b, FineNoiseStrength);
                int o = (r * n + c) * 3;
                img[o] *= k; img[o + 1] *= k; img[o + 2] *= k;
            }

        foreach (var rec in recs)
        {
            Func<double, double, int, (float R, float G, float B, float A)?>? shade = rec.Class switch
            {
                "Forest" when L.Contains("forest") => Forest(),
                "Field" when rec.Shader.StartsWith("Blend") && L.Contains("field") => Field(rec),
                "Town" when L.Contains("town") => TownShade(rec),
                "Road" when L.Contains("road") => Solid(0.42f, 0.41f, 0.38f, 0.85f),
                "River" when L.Contains("river") => Solid(0.20f, 0.33f, 0.33f, 0.9f),
                "Runway" when L.Contains("runway") && rec.Shader is "OldRunway5.1" or "Runway5.0" => Solid(0.62f, 0.62f, 0.60f, 1f),
                _ => null,
            };
            if (shade is null) continue;
            for (int t = 0; t < rec.Tris.Count; t++)
                Raster(img, n, cx, cz, size, mpp, rec.Tris[t], t, shade);
        }

        using var bmp = new SKBitmap(n, n, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int r = 0; r < n; r++)
        for (int c = 0; c < n; c++)
        {
            int o = (r * n + c) * 3;
            bmp.SetPixel(c, r, new SKColor(B(img[o]), B(img[o + 1]), B(img[o + 2])));
        }
        using var f = File.Create(outPath);
        bmp.Encode(f, SKEncodedImageFormat.Png, 100);
        Console.WriteLine($"wrote {outPath} {n}x{n}");
    }

    // Tunables (metres per texture repeat, blend strengths).
    public static double NoiseTile = 400, FineNoiseTile = 40, ForestTile = 48, FieldTile = 300, TownMetersPerPixel = 1.0;
    public static float NoiseStrength = 0.6f, FineNoiseStrength = 0.35f, ForestStrength = 1.0f, ForestTint = 0.25f, FieldStrength = 0.35f;

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static byte B(float v) => (byte)Math.Clamp(v * 255, 0, 255);

    static (float, float, float) LandAt(Dictionary<(int, int), Tex?> cache, double P, double Q)
    {
        // bilinear over the 38.4 m texture
        P -= 0.5; Q -= 0.5;
        int p0 = (int)Math.Floor(P), q0 = (int)Math.Floor(Q);
        float fp = (float)(P - p0), fq = (float)(Q - q0);
        float r = 0, g = 0, b = 0;
        for (int dq = 0; dq < 2; dq++)
        for (int dp = 0; dp < 2; dp++)
        {
            int pp = p0 + dp, qq = q0 + dq, ti = (int)Math.Floor(pp / 256.0), tj = (int)Math.Floor(qq / 256.0);
            if (!cache.TryGetValue((ti, tj), out var t)) cache[(ti, tj)] = t = Zips.Get($"land_39_{ti}_{tj}.png");
            if (t is null) continue;
            int u = pp - ti * 256, v = qq - tj * 256, o = (v * 256 + u) * 4;
            float w = (dp == 0 ? 1 - fp : fp) * (dq == 0 ? 1 - fq : fq);
            r += t.Rgba[o] / 255f * w; g += t.Rgba[o + 1] / 255f * w; b += t.Rgba[o + 2] / 255f * w;
        }
        return (r, g, b);
    }

    static Func<double, double, int, (float, float, float, float)?> Solid(float r, float g, float b, float a) => (_, _, _) => (r, g, b, a);

    /// <summary>Forest: canopy detail normalised to its mean, plus a slight darkening/green tint.</summary>
    static Func<double, double, int, (float, float, float, float)?> Forest()
    {
        var fm = Zips.Get("forestmask.tif")!;
        var mean = fm.Mean;
        return (x, z, _) =>
        {
            var s = fm.Sample(x / ForestTile, z / ForestTile);
            float l = (s.R + s.G + s.B) / (mean.R + mean.G + mean.B);
            return (Lerp(1, l, ForestStrength) * (1 - ForestTint * 0.4f), Lerp(1, l, ForestStrength) * (1 - ForestTint * 0.2f), Lerp(1, l, ForestStrength) * (1 - ForestTint * 0.6f), -1);
        };
    }

    /// <summary>Field: crop rows from the field mask's luminance, normalised to its mean.</summary>
    static Func<double, double, int, (float, float, float, float)?> Field(Rec rec)
    {
        var masks = Enumerable.Range(1, 9).Select(i => Zips.Get($"field_{i}_mask.tif")).ToArray();
        return (x, z, t) =>
        {
            var m = masks[(int)(rec.TriMaterial[t] % 9)] ?? masks[0]!;
            var s = m.Sample(x / FieldTile, z / FieldTile);
            float l = (s.R + s.G + s.B) / (m.Mean.R + m.Mean.G + m.Mean.B);
            float k = Lerp(1, l, FieldStrength);
            return (k, k, k, -1);
        };
    }

    /// <summary>How the town photo's axes follow (u, w): bit 0 swaps them, bit 1 flips the first,
    /// bit 2 flips the second (research: picked by comparing with F10).</summary>
    public static int TownOrient = 0;
    /// <summary>Metres one village photo covers before it repeats.</summary>
    public static double TownTileM = 1024;

    /// <summary>Town: the asset's village photo placed by the node's manifold matrix
    /// (u = row 0, w = row 2 applied to the node-local position, in metres), repeating every
    /// <see cref="TownTileM"/>.</summary>
    static Func<double, double, int, (float, float, float, float)?> TownShade(Rec rec) => (x, z, t) =>
    {
        var a = rec.TriAsset[t];
        if (a < 0 || a >= Mesh.Assets.Count || rec.TriUvw[t] is not { } m) return null;
        var tex = Zips.Get(Mesh.Assets[a] + ".color.tif");
        if (tex is null) return null;
        double lx = x - rec.NodeT.X, lz = z - rec.NodeT.Z;
        double u = m[0] * lx + m[2] * lz + m[3], w = m[8] * lx + m[10] * lz + m[11];
        double s1 = u / TownTileM, s2 = w / TownTileM;
        if ((TownOrient & 1) != 0) (s1, s2) = (s2, s1);
        if ((TownOrient & 2) != 0) s1 = -s1;
        if ((TownOrient & 4) != 0) s2 = -s2;
        var s = tex.Sample(s1, s2);
        return (s.R, s.G, s.B, 1f);
    };

    static void Raster(float[] img, int n, double cx, double cz, double size, double mpp, (double X, double Z)[] tri, int t,
        Func<double, double, int, (float R, float G, float B, float A)?> shade)
    {
        // to pixel coords: col from z, row from x
        double C(double z) => (z - (cz - size / 2)) / mpp - 0.5;
        double R(double x) => (cx + size / 2 - x) / mpp - 0.5;
        double c0 = C(tri[0].Z), r0 = R(tri[0].X), c1 = C(tri[1].Z), r1 = R(tri[1].X), c2 = C(tri[2].Z), r2 = R(tri[2].X);
        int minC = Math.Max(0, (int)Math.Floor(Math.Min(c0, Math.Min(c1, c2)))), maxC = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(c0, Math.Max(c1, c2))));
        int minR = Math.Max(0, (int)Math.Floor(Math.Min(r0, Math.Min(r1, r2)))), maxR = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(r0, Math.Max(r1, r2))));
        double area = (c1 - c0) * (r2 - r0) - (c2 - c0) * (r1 - r0);
        if (Math.Abs(area) < 1e-9) return;
        for (int r = minR; r <= maxR; r++)
        for (int c = minC; c <= maxC; c++)
        {
            double w0 = ((c1 - c) * (r2 - r) - (c2 - c) * (r1 - r)) / area, w1 = ((c2 - c) * (r0 - r) - (c0 - c) * (r2 - r)) / area, w2 = 1 - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            double x = cx + size / 2 - (r + 0.5) * mpp, z = cz - size / 2 + (c + 0.5) * mpp;
            if (shade(x, z, t) is not { } s) continue;
            int o = (r * n + c) * 3;
            if (s.A == -1) { img[o] *= s.R; img[o + 1] *= s.G; img[o + 2] *= s.B; }
            else if (s.A == -2) { float k = 0.6f + 0.8f * s.R; img[o] *= k; img[o + 1] *= k; img[o + 2] *= k; }
            else { img[o] = img[o] * (1 - s.A) + s.R * s.A; img[o + 1] = img[o + 1] * (1 - s.A) + s.G * s.A; img[o + 2] = img[o + 2] * (1 - s.A) + s.B * s.A; }
        }
    }
}
