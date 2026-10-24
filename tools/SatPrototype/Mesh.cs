using System.Text;

/// <summary>Caucasus.surface5 land-cover records around a point (finest level), with the same
/// record/pool rules as the client's Core/Surface5.cs: arrays after record k belong to record k+1;
/// a pool's node translation is the matrix in the previous pool span. Per record: triangles in
/// world x/z, per-vertex manifold index (parametricUVW as u32), per-triangle assetIndex, and the
/// xyz2uvw matrices found with the arrays (and with the record's own span, for comparison).</summary>
public sealed class Rec
{
    public string Shader = "", Class = "";
    public List<(double X, double Z)[]> Tris = [];
    public List<uint[]> TriManifold = [];   // manifold index per corner
    public List<int> TriAsset = [];          // asset per triangle (-1 none)
    public List<uint> TriMaterial = [];      // materialParamsIndex of the first corner
    public float[][] MatsArrays = [];        // xyz2uvw found with the arrays
    public float[][] MatsOwn = [];           // xyz2uvw in the record's own span
    public (double X, double Z) NodeT;
    /// <summary>Per triangle: the node's xyz2uvw matrix chosen by the first corner's manifold
    /// index (row-major, applied to node-local x, y, z), or null.</summary>
    public List<float[]?> TriUvw = [];
}

public static class Mesh
{
    public static string Path = @"E:\DCS World\Mods\terrains\Caucasus\Surface\Caucasus.surface5";
    public static List<string> Assets = [];

    private record Attr(int Type, int Size, long Bytes, long Off);

    public static List<Rec> Load(double cx, double cz, double radius)
    {
        using var fs = File.OpenRead(Path);
        var head = new byte[16];
        fs.ReadExactly(head);
        long dataBase = BitConverter.ToInt64(head, 8);
        var d = new byte[dataBase];
        fs.Position = 0;
        fs.ReadExactly(d);
        Assets = AssetNames(d);

        // Record starts: u32 2, len, shader, len, class.
        var starts = new List<(int Pos, string Sh, string Cl)>();
        for (int p = 0; p + 16 < d.Length; p++)
        {
            if (d[p] != 2 || d[p + 1] != 0 || d[p + 2] != 0 || d[p + 3] != 0) continue;
            int n = BitConverter.ToInt32(d, p + 4);
            if (n is < 5 or > 32 || p + 8 + n + 4 > d.Length) continue;
            if (!IsShader(d.AsSpan(p + 8, n))) continue;
            int q = p + 8 + n, cn = BitConverter.ToInt32(d, q);
            if (cn is <= 0 or >= 40 || q + 4 + cn > d.Length) continue;
            var cls = d.AsSpan(q + 4, cn);
            bool ok = true;
            foreach (var c in cls) if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_')) { ok = false; break; }
            if (!ok) continue;
            starts.Add((p, Encoding.ASCII.GetString(d, p + 8, n), Encoding.ASCII.GetString(cls)));
        }
        Console.WriteLine($"records: {starts.Count}, assets: {Assets.Count}");

        var spans = new List<(string Sh, string Cl, Dictionary<string, Attr> A, (float X, float Z)? T, float[][] Mats)>();
        for (int k = 0; k < starts.Count; k++)
        {
            int a = starts[k].Pos, b = k + 1 < starts.Count ? starts[k + 1].Pos : d.Length;
            var region = d.AsSpan(a, b - a);
            var attrs = Attrs(region);
            (float, float)? t = null;
            int m = region.IndexOf(Ident);
            if (m >= 0 && m + 64 <= region.Length) t = (BitConverter.ToSingle(region[(m + 48)..]), BitConverter.ToSingle(region[(m + 56)..]));
            float[][] mats = attrs.TryGetValue("xyz2uvw", out var xa) ? ReadMats(fs, dataBase, xa) : [];
            spans.Add((starts[k].Sh, starts[k].Cl, attrs, t, mats));
        }

        var recs = new List<Rec>();
        var pending = new List<(string Sh, string Cl, Dictionary<string, Attr> A, float[][] MatsArr, float[][] MatsOwn)>();
        (float X, float Z)? translation = null;
        for (int k = 0; k < spans.Count; k++)
        {
            var s = spans[k];
            if (s.A.ContainsKey("baseIndex") && s.A.ContainsKey("TRI") && k + 1 < spans.Count)
                pending.Add((spans[k + 1].Sh, spans[k + 1].Cl, s.A, s.Mats, spans[k + 1].Mats));
            if (!s.A.TryGetValue("Pbase", out var pb)) continue;
            if (pending.Count > 0 && translation is { } t && Math.Abs(t.X - cx) < radius + 13000 && Math.Abs(t.Z - cz) < radius + 13000)
            {
                var pool = Floats(fs, dataBase, pb);
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (int i = 0; i + 2 < pool.Length; i += 3) { x0 = Math.Min(x0, pool[i]); x1 = Math.Max(x1, pool[i]); z0 = Math.Min(z0, pool[i + 2]); z1 = Math.Max(z1, pool[i + 2]); }
                if (Math.Max(x1 - x0, z1 - z0) < 27500)   // finest Caucasus level
                    foreach (var (sh, cl, A, matsArr, matsOwn) in pending)
                        // The node's manifold matrices sit in its pool span (indexed by parametricUVW).
                        if (Build(fs, dataBase, sh, cl, A, matsArr, matsOwn, pool, t, cx, cz, radius, s.Mats) is { } r) recs.Add(r);
            }
            pending.Clear();
            translation = s.T;
        }
        return recs;
    }

    private static Rec? Build(FileStream fs, long db, string sh, string cl, Dictionary<string, Attr> A, float[][] matsArr, float[][] matsOwn,
        float[] pool, (float X, float Z) t, double cx, double cz, double radius, float[][] nodeMats)
    {
        var idx = UInts(fs, db, A["baseIndex"]);
        var tri = UInts(fs, db, A["TRI"]);
        uint[] man = A.TryGetValue("parametricUVW", out var pu) && pu.Size == 4 ? UInts(fs, db, pu) : [];
        uint[] asset = A.TryGetValue("assetIndex", out var ai) ? UInts(fs, db, ai) : [];
        uint[] mat = A.TryGetValue("materialParamsIndex", out var mp) ? UInts(fs, db, mp) : [];
        var r = new Rec { Shader = sh, Class = cl, MatsArrays = matsArr, MatsOwn = matsOwn, NodeT = (t.X, t.Z) };
        int nv = pool.Length / 3;
        for (int k = 0; k + 2 < tri.Length; k += 3)
        {
            var c = new (double, double)[3];
            var mi = new uint[3];
            bool ok = true, near = false;
            for (int j = 0; j < 3 && ok; j++)
            {
                var a = tri[k + j];
                if (a >= idx.Length || idx[a] >= nv) { ok = false; break; }
                var p = idx[a];
                double x = pool[3 * p] + t.X, z = pool[3 * p + 2] + t.Z;
                c[j] = (x, z);
                mi[j] = a < man.Length ? man[a] : 0;
                if (Math.Abs(x - cx) < radius && Math.Abs(z - cz) < radius) near = true;
            }
            if (!ok || !near) continue;
            r.Tris.Add(c);
            r.TriManifold.Add(mi);
            r.TriAsset.Add(k / 3 < asset.Length ? (int)asset[k / 3] : -1);
            r.TriMaterial.Add(tri[k] < mat.Length ? mat[tri[k]] : 0);
            r.TriUvw.Add(mi[0] < nodeMats.Length && man.Length > 0 ? nodeMats[mi[0]] : null);
        }
        return r.Tris.Count > 0 ? r : null;
    }

    private static readonly byte[] Ident = Build16();
    private static byte[] Build16()
    {
        byte[] one = [0, 0, 0x80, 0x3f];
        return [.. one, .. new byte[16], .. one, .. new byte[16], .. one];
    }

    private static bool IsShader(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) i++;
        if (i == 0 || i == s.Length) return false;
        for (; i < s.Length; i++) if (s[i] != '.' && s[i] is < (byte)'0' or > (byte)'9') return false;
        return s[^1] != '.';
    }

    private static Dictionary<string, Attr> Attrs(ReadOnlySpan<byte> region)
    {
        var res = new Dictionary<string, Attr>();
        for (int p = 0; p + 8 < region.Length; p++)
        {
            if (region[p] != 1 || region[p + 1] != 0 || region[p + 2] != 0 || region[p + 3] != 0) continue;
            int n = BitConverter.ToInt32(region[(p + 4)..]);
            if (n is < 1 or > 32 || p + 8 + n + 22 > region.Length) continue;
            var name = region.Slice(p + 8, n);
            bool ok = true;
            foreach (var c in name) if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_')) { ok = false; break; }
            if (!ok) continue;
            int o = p + 8 + n;
            int type = BitConverter.ToUInt16(region[o..]), size = BitConverter.ToUInt16(region[(o + 2)..]);
            long bytes = BitConverter.ToInt64(region[(o + 8)..]);
            long off = 0;
            for (int b = 5; b >= 0; b--) off = (off << 8) | region[o + 16 + b];
            res.TryAdd(Encoding.ASCII.GetString(name), new Attr(type, size, bytes, off));
        }
        return res;
    }

    private static List<string> AssetNames(byte[] d)
    {
        // u32 count 476 then length-prefixed names: find "\x06\0\0\0town_1" and walk back to the count.
        var anchor = d.AsSpan().IndexOf("\u0006\0\0\0town_1"u8);
        for (int back = 4; back < 40000 && anchor - back > 4; back++)
        {
            int p = anchor - back, count = BitConverter.ToInt32(d, p - 4);
            if (count is < 100 or > 5000) continue;
            var names = new List<string>();
            int q = p;
            while (names.Count < count && q + 4 < d.Length)
            {
                int n = BitConverter.ToInt32(d, q);
                if (n is < 1 or > 80) break;
                names.Add(Encoding.ASCII.GetString(d, q + 4, n));
                q += 4 + n;
            }
            if (names.Count == count && names.Contains("town_1")) return names;
        }
        return [];
    }

    private static byte[] Read(FileStream fs, long db, Attr a)
    {
        var raw = new byte[a.Bytes];
        fs.Position = db + a.Off;
        fs.ReadExactly(raw);
        return raw;
    }

    private static float[] Floats(FileStream fs, long db, Attr a)
    {
        var raw = Read(fs, db, a);
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, f.Length * 4);
        return f;
    }

    private static uint[] UInts(FileStream fs, long db, Attr a)
    {
        var raw = Read(fs, db, a);
        var u = new uint[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, u, 0, u.Length * 4);
        return u;
    }

    private static float[][] ReadMats(FileStream fs, long db, Attr a)
    {
        var f = Floats(fs, db, a);
        var list = new List<float[]>();
        for (int i = 0; i + 16 <= f.Length; i += 16) list.Add(f[i..(i + 16)]);
        return [.. list];
    }
}
