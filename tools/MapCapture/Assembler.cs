using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

/// <summary>Turns captured map views into one tile file ("*.ensat", SQLite): tiles of 256 px on
/// the DCS x/z grid at 2^L m/px, row 0 north, column 0 west (the client's DcsMapTiles
/// convention). Levels that captures exist for are rendered from the captures (finer captures on
/// top, box-filtered when coarser than the capture); coarser levels from the tiles below.
/// Tiles fully covered are JPEG, partly covered PNG (with alpha).
/// Tables: tiles(level, tx, tz, data), meta(key, value). Works through tens of GB of captures
/// with a small cache of decoded ones.</summary>
static class Assembler
{
    const int Tile = 256;
    // Of each capture's width/height, left out: editor panels, the scale bar, text other DCS
    // plugins draw near the edges (SRS's status line). Captures overlap 30 %, so nothing is lost.
    const double Margin = 0.10;
    const int CacheCaptures = 48;

    sealed class Step
    {
        public string Png = "";
        public int W, H;
        public double Mpp;
        public int Level;
        // pixel → map: x = a0 + a1·p + a2·q, z = b0 + b1·p + b2·q, and back
        public double[] A = new double[3], B = new double[3], IA = new double[2], IB = new double[2];
        public double MinX, MaxX, MinZ, MaxZ, CX, CZ;
    }

    sealed class Pixels(int w, int h, int stride, byte[] bgr)
    {
        public readonly int W = w, H = h, Stride = stride;
        public readonly byte[] Bgr = bgr;
    }

    static readonly ConcurrentDictionary<string, Lazy<Pixels>> Cache = new();
    static readonly ConcurrentQueue<string> CacheOrder = new();

    public static void Run(string dir, string terrain, string outPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var steps = new List<Step>();
        foreach (var lua in Directory.EnumerateFiles(dir, "step_*.lua", SearchOption.AllDirectories))
        {
            var png = Path.ChangeExtension(lua, ".png");
            if (!File.Exists(png)) continue;
            var scaleFile = Path.ChangeExtension(lua, ".scale");
            var scale = File.Exists(scaleFile) ? double.Parse(File.ReadAllText(scaleFile), inv) : 1;
            var t = Lua.Table(File.ReadAllText(lua));
            var pts = Enumerable.Range(1, 5).Where(k => t.ContainsKey($"points.{k}.u"))
                .Select(k => (P: double.Parse(t[$"points.{k}.u"], inv) * scale, Q: double.Parse(t[$"points.{k}.v"], inv) * scale,
                              X: double.Parse(t[$"points.{k}.x"], inv), Z: double.Parse(t[$"points.{k}.z"], inv))).ToList();
            var s = new Step { Png = png };
            using (var img = Image.FromFile(png)) (s.W, s.H) = (img.Width, img.Height);
            s.A = Fit(pts.Select(p => (p.P, p.Q, p.X)).ToList());
            s.B = Fit(pts.Select(p => (p.P, p.Q, p.Z)).ToList());
            var det = s.A[1] * s.B[2] - s.A[2] * s.B[1];
            s.IA = [s.B[2] / det, -s.A[2] / det];
            s.IB = [-s.B[1] / det, s.A[1] / det];
            s.Mpp = Math.Sqrt(Math.Abs(det));
            s.Level = Math.Clamp((int)Math.Round(Math.Log2(s.Mpp)), 0, 10);
            var corners = new[] { (Margin, Margin), (1 - Margin, Margin), (Margin, 1 - Margin), (1 - Margin, 1 - Margin) }
                .Select(c => ToMap(s, c.Item1 * s.W, c.Item2 * s.H)).ToList();
            (s.MinX, s.MaxX) = (corners.Min(c => c.X), corners.Max(c => c.X));
            (s.MinZ, s.MaxZ) = (corners.Min(c => c.Z), corners.Max(c => c.Z));
            (s.CX, s.CZ) = ToMap(s, s.W / 2.0, s.H / 2.0);
            steps.Add(s);
        }
        if (steps.Count == 0) throw new InvalidOperationException("no captures in " + dir);
        int finest = steps.Min(s => s.Level), coarsestCaptured = steps.Max(s => s.Level);
        Console.WriteLine($"{steps.Count} captures; levels {finest}–{coarsestCaptured} from captures, {coarsestCaptured + 1}–10 from tiles");

        File.Delete(outPath);
        using var db = new SqliteConnection($"Data Source={outPath}");
        db.Open();
        Exec(db, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; CREATE TABLE tiles(level INTEGER, tx INTEGER, tz INTEGER, data BLOB, PRIMARY KEY(level, tx, tz)); CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);");
        foreach (var (k, v) in new[] { ("format", "eaglesnest-ensat 1"), ("terrain", terrain), ("source", "DCS Mission Editor satellite map, captured"),
                     ("captured", DateTime.UtcNow.ToString("u", inv)), ("finestLevel", finest.ToString(inv)) })
            Exec(db, "INSERT INTO meta VALUES($k, $v)", ("$k", k), ("$v", v));

        // Spatial index of the captures, 20 km cells.
        const double Cell = 20000;
        var index = new Dictionary<(int, int), List<Step>>();
        foreach (var s in steps)
            for (int i = (int)Math.Floor(s.MinX / Cell); i <= (int)Math.Floor(s.MaxX / Cell); i++)
            for (int j = (int)Math.Floor(s.MinZ / Cell); j <= (int)Math.Floor(s.MaxZ / Cell); j++)
                (index.TryGetValue((i, j), out var l) ? l : index[(i, j)] = []).Add(s);

        var writer = new BlockingCollection<(int L, int X, int Z, byte[] Data)>(512);
        var writing = Task.Run(() =>
        {
            var tr = db.BeginTransaction();
            int n = 0;
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO tiles VALUES($l, $x, $z, $d)";
            var pl = cmd.Parameters.Add("$l", SqliteType.Integer); var px = cmd.Parameters.Add("$x", SqliteType.Integer);
            var pz = cmd.Parameters.Add("$z", SqliteType.Integer); var pd = cmd.Parameters.Add("$d", SqliteType.Blob);
            foreach (var t in writer.GetConsumingEnumerable())
            {
                cmd.Transaction = tr;
                pl.Value = t.L; px.Value = t.X; pz.Value = t.Z; pd.Value = t.Data;
                cmd.ExecuteNonQuery();
                if (++n % 2000 == 0) { tr.Commit(); tr = db.BeginTransaction(); }
            }
            tr.Commit();
        });

        // Levels with captures: rendered from them.
        for (int level = finest; level <= coarsestCaptured; level++)
        {
            var eligible = steps.Where(s => s.Level <= level).ToList();
            double m = 1 << level, size = Tile * m;
            int tx0 = (int)Math.Floor(eligible.Min(s => s.MinX) / size), tx1 = (int)Math.Floor(eligible.Max(s => s.MaxX) / size);
            int tz0 = (int)Math.Floor(eligible.Min(s => s.MinZ) / size), tz1 = (int)Math.Floor(eligible.Max(s => s.MaxZ) / size);
            int count = 0;
            var t0 = DateTime.UtcNow;
            Parallel.For(0, tx1 - tx0 + 1, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, r =>
            {
                int tx = tx1 - r;
                for (int tz = tz0; tz <= tz1; tz++)
                {
                    if (RenderTile(index, Cell, level, tx, tz) is { } data)
                    {
                        writer.Add((level, tx, tz, data));
                        Interlocked.Increment(ref count);
                    }
                }
            });
            Console.WriteLine($"level {level}: {count} tiles in {(DateTime.UtcNow - t0).TotalMinutes:F1} min");
        }
        writer.CompleteAdding();
        writing.Wait();

        // Coarser levels: 2×2 children averaged into one tile.
        for (int level = coarsestCaptured + 1; level <= 10; level++)
        {
            var children = new List<(int X, int Z)>();
            using (var q = db.CreateCommand())
            {
                q.CommandText = "SELECT tx, tz FROM tiles WHERE level = $l";
                q.Parameters.AddWithValue("$l", level - 1);
                using var rd = q.ExecuteReader();
                while (rd.Read()) children.Add((rd.GetInt32(0), rd.GetInt32(1)));
            }
            if (children.Count == 0) break;
            var parents = children.GroupBy(c => (FloorDiv(c.X, 2), FloorDiv(c.Z, 2))).ToList();
            foreach (var group in parents)
            {
                var px = new byte[Tile * Tile * 4];
                foreach (var (cx, cz) in group)
                {
                    var child = Decode(ReadTile(db, level - 1, cx, cz));
                    int rowOff = cx - group.Key.Item1 * 2 == 1 ? 0 : Tile / 2;   // the northern child (higher x) is the top half
                    int colOff = cz - group.Key.Item2 * 2 == 1 ? Tile / 2 : 0;
                    for (int r = 0; r < Tile / 2; r++)
                    for (int c = 0; c < Tile / 2; c++)
                    {
                        int sb = 0, sg = 0, sr = 0, n = 0;
                        for (int dr = 0; dr < 2; dr++)
                        for (int dc = 0; dc < 2; dc++)
                        {
                            int o = ((2 * r + dr) * Tile + 2 * c + dc) * 4;
                            if (child[o + 3] == 0) continue;
                            sb += child[o]; sg += child[o + 1]; sr += child[o + 2]; n++;
                        }
                        if (n == 0) continue;
                        int p = ((rowOff + r) * Tile + colOff + c) * 4;
                        px[p] = (byte)(sb / n); px[p + 1] = (byte)(sg / n); px[p + 2] = (byte)(sr / n); px[p + 3] = (byte)(n == 4 ? 255 : 128);
                    }
                }
                Exec(db, "INSERT OR REPLACE INTO tiles VALUES($l, $x, $z, $d)", ("$l", level), ("$x", group.Key.Item1), ("$z", group.Key.Item2), ("$d", Encode(px)));
            }
            Console.WriteLine($"level {level}: {parents.Count} tiles");
        }
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1048576} MB)");
    }

    /// <summary>One tile from the captures over it: coarse captures first, then finer ones, and
    /// among equals the one centred nearest last (it wins). Null when nothing covers it.</summary>
    static byte[]? RenderTile(Dictionary<(int, int), List<Step>> index, double cell, int level, int tx, int tz)
    {
        double m = 1 << level, size = Tile * m;
        double tMinX = tx * size, tMaxX = tMinX + size, tMinZ = tz * size, tMaxZ = tMinZ + size;
        double mx = tMinX + size / 2, mz = tMinZ + size / 2;
        var cand = new HashSet<Step>();
        for (int i = (int)Math.Floor(tMinX / cell); i <= (int)Math.Floor(tMaxX / cell); i++)
        for (int j = (int)Math.Floor(tMinZ / cell); j <= (int)Math.Floor(tMaxZ / cell); j++)
            if (index.TryGetValue((i, j), out var l))
                foreach (var s in l)
                    if (s.Level <= level && s.MaxX > tMinX && s.MinX < tMaxX && s.MaxZ > tMinZ && s.MinZ < tMaxZ) cand.Add(s);
        if (cand.Count == 0) return null;
        var order = cand.OrderByDescending(s => s.Level).ThenByDescending(s => (s.CX - mx) * (s.CX - mx) + (s.CZ - mz) * (s.CZ - mz)).ToList();
        var px = new byte[Tile * Tile * 4];
        foreach (var s in order)
        {
            var img = Get(s);
            int k = Math.Max(1, (int)Math.Round(m / s.Mpp));   // source pixels per tile pixel
            int r0 = Math.Max(0, (int)Math.Floor((tMaxX - s.MaxX) / m)), r1 = Math.Min(Tile - 1, (int)Math.Ceiling((tMaxX - s.MinX) / m));
            int c0 = Math.Max(0, (int)Math.Floor((s.MinZ - tMinZ) / m)), c1 = Math.Min(Tile - 1, (int)Math.Ceiling((s.MaxZ - tMinZ) / m));
            for (int r = r0; r <= r1; r++)
            {
                double x = tMaxX - (r + 0.5) * m;
                if (x < s.MinX || x > s.MaxX) continue;
                for (int c = c0; c <= c1; c++)
                {
                    double z = tMinZ + (c + 0.5) * m;
                    if (z < s.MinZ || z > s.MaxZ) continue;
                    double dx = x - s.A[0], dz = z - s.B[0];
                    double p = s.IA[0] * dx + s.IA[1] * dz, q = s.IB[0] * dx + s.IB[1] * dz;
                    var (b, g, rr) = k == 1 ? Bilinear(img, p, q) : Box(img, p, q, k);
                    int o = (r * Tile + c) * 4;
                    px[o] = b; px[o + 1] = g; px[o + 2] = rr; px[o + 3] = 255;
                }
            }
        }
        return Encode(px);
    }

    static Pixels Get(Step s)
    {
        var lazy = Cache.GetOrAdd(s.Png, p => new Lazy<Pixels>(() => Load(p)));
        if (lazy.IsValueCreated) return lazy.Value;
        CacheOrder.Enqueue(s.Png);
        while (Cache.Count > CacheCaptures && CacheOrder.TryDequeue(out var old) && old != s.Png) Cache.TryRemove(old, out _);
        return lazy.Value;
    }

    static Pixels Load(string png)
    {
        using var bmp = new Bitmap(png);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[data.Stride * bmp.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return new Pixels(bmp.Width, bmp.Height, data.Stride, bytes);
    }

    static (byte, byte, byte) Bilinear(Pixels s, double p, double q)
    {
        p -= 0.5; q -= 0.5;
        int x0 = Math.Clamp((int)Math.Floor(p), 0, s.W - 2), y0 = Math.Clamp((int)Math.Floor(q), 0, s.H - 2);
        double fx = Math.Clamp(p - x0, 0, 1), fy = Math.Clamp(q - y0, 0, 1);
        double b = 0, g = 0, r = 0;
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
        {
            double w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
            int o = (y0 + dy) * s.Stride + (x0 + dx) * 3;
            b += s.Bgr[o] * w; g += s.Bgr[o + 1] * w; r += s.Bgr[o + 2] * w;
        }
        return ((byte)b, (byte)g, (byte)r);
    }

    /// <summary>Mean of the k×k source pixels around (p, q): a capture finer than the tile level.</summary>
    static (byte, byte, byte) Box(Pixels s, double p, double q, int k)
    {
        int x0 = Math.Clamp((int)Math.Floor(p - k / 2.0), 0, s.W - k), y0 = Math.Clamp((int)Math.Floor(q - k / 2.0), 0, s.H - k);
        int b = 0, g = 0, r = 0;
        for (int y = y0; y < y0 + k; y++)
        for (int x = x0; x < x0 + k; x++)
        {
            int o = y * s.Stride + x * 3;
            b += s.Bgr[o]; g += s.Bgr[o + 1]; r += s.Bgr[o + 2];
        }
        int n = k * k;
        return ((byte)(b / n), (byte)(g / n), (byte)(r / n));
    }

    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>JPEG (quality 85) when every pixel is covered, else PNG keeping the alpha.</summary>
    static byte[] Encode(byte[] bgra)
    {
        bool full = true;
        for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] != 255) { full = false; break; }
        using var bmp = new Bitmap(Tile, Tile, full ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, Tile, Tile), ImageLockMode.WriteOnly, bmp.PixelFormat);
        if (full)
        {
            var row = new byte[Tile * 3];
            for (int r = 0; r < Tile; r++)
            {
                for (int c = 0; c < Tile; c++) { int o = (r * Tile + c) * 4; row[c * 3] = bgra[o]; row[c * 3 + 1] = bgra[o + 1]; row[c * 3 + 2] = bgra[o + 2]; }
                Marshal.Copy(row, 0, data.Scan0 + r * data.Stride, row.Length);
            }
        }
        else
            for (int r = 0; r < Tile; r++) Marshal.Copy(bgra, r * Tile * 4, data.Scan0 + r * data.Stride, Tile * 4);
        bmp.UnlockBits(data);
        using var ms = new MemoryStream();
        if (full)
        {
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            bmp.Save(ms, Jpeg, ps);
        }
        else bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static byte[] Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var img = new Bitmap(ms);
        using var argb = img.Clone(new Rectangle(0, 0, Tile, Tile), PixelFormat.Format32bppArgb);
        var d = argb.LockBits(new Rectangle(0, 0, Tile, Tile), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[Tile * Tile * 4];
        for (int r = 0; r < Tile; r++) Marshal.Copy(d.Scan0 + r * d.Stride, px, r * Tile * 4, Tile * 4);
        argb.UnlockBits(d);
        return px;
    }

    static byte[] ReadTile(SqliteConnection db, int level, int tx, int tz)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT data FROM tiles WHERE level = $l AND tx = $x AND tz = $z";
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$x", tx);
        cmd.Parameters.AddWithValue("$z", tz);
        return (byte[])cmd.ExecuteScalar()!;
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    static void Exec(SqliteConnection db, string sql, params (string, object)[] ps)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    static double[] Fit(List<(double P, double Q, double V)> pts)
    {
        double s1 = 0, sp = 0, sq = 0, spp = 0, spq = 0, sqq = 0, sv = 0, spv = 0, sqv = 0;
        foreach (var (p, q, v) in pts) { s1++; sp += p; sq += q; spp += p * p; spq += p * q; sqq += q * q; sv += v; spv += p * v; sqv += q * v; }
        double[,] m = { { s1, sp, sq }, { sp, spp, spq }, { sq, spq, sqq } };
        double Det(double[,] a) => a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        var d = Det(m);
        double[] b = [sv, spv, sqv];
        var r = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var c = (double[,])m.Clone();
            for (int i = 0; i < 3; i++) c[i, k] = b[i];
            r[k] = Det(c) / d;
        }
        return r;
    }

    static (double X, double Z) ToMap(Step s, double p, double q) => (s.A[0] + s.A[1] * p + s.A[2] * q, s.B[0] + s.B[1] * p + s.B[2] * q);
}
