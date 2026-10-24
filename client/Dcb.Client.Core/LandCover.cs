using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dcb.Client.Core;

/// <summary>Land-cover grid of a DCS terrain: the area classes of the F10 map (water, built-up,
/// forest, fields, beach) rasterised from the terrain mesh <c>surface/&lt;id&gt;.surface5</c>
/// onto a regular DCS x/z grid. Row i at x = X0 + (i + ½)·Cell, column j at z = Z0 + (j + ½)·Cell.</summary>
public sealed class LandCover
{
    /// <summary>Higher wins where classes overlap. <see cref="Sea"/> is DCS's sea mesh, which
    /// runs on under the coastal land (DCS draws the terrain over it: ~34 000 km² of Syria's
    /// land lies on it), so it is the lowest and not a coastline; <see cref="Water"/> is lakes.</summary>
    public enum Class : byte { None = 0, Sea = 1, Field = 2, Beach = 3, Trees = 4, Town = 5, Water = 6 }

    public double X0 { get; }
    public double Z0 { get; }
    public double Cell { get; }
    public int Nx { get; }
    public int Nz { get; }
    public byte[] Cells { get; }

    public LandCover(double x0, double z0, double cell, int nx, int nz, byte[] cells)
    {
        (X0, Z0, Cell, Nx, Nz, Cells) = (x0, z0, cell, nx, nz, cells);
        if (cells.Length != nx * nz) throw new ArgumentException("cell count");
    }

    public Class At(int i, int j) => (Class)Cells[i * Nz + j];

    /// <summary>Class of the cell holding DCS point (x, z); None outside the grid.</summary>
    public Class At(double x, double z)
    {
        int i = (int)Math.Floor((x - X0) / Cell), j = (int)Math.Floor((z - Z0) / Cell);
        return i < 0 || j < 0 || i >= Nx || j >= Nz ? Class.None : (Class)Cells[i * Nz + j];
    }

    /// <summary>Most common class among the cells whose centres fall in the square of side
    /// <paramref name="size"/> centred on (x, z) — for drawing the grid coarser than its cells.</summary>
    public Class MajorityIn(double x, double z, double size)
    {
        Span<int> votes = stackalloc int[8];
        int i0 = (int)Math.Floor((x - size / 2 - X0) / Cell), j0 = (int)Math.Floor((z - size / 2 - Z0) / Cell);
        var n = Math.Max(1, (int)Math.Round(size / Cell));
        for (int i = Math.Max(0, i0); i < Math.Min(Nx, i0 + n); i++)
        for (int j = Math.Max(0, j0); j < Math.Min(Nz, j0 + n); j++)
            votes[Cells[i * Nz + j]]++;
        var best = 0;
        for (int k = 1; k < votes.Length; k++) if (votes[k] > votes[best]) best = k;
        return (Class)best;
    }

    /// <summary>surface5 class name → land-cover class (roads and rivers come from the vector
    /// layers; plain terrain is "no class").</summary>
    public static Class ClassOf(string name) => name switch
    {
        "Sea" => Class.Sea,
        "lake" or "Lake" => Class.Water,
        "Town" => Class.Town,
        "Trees" or "Forest" => Class.Trees,  // Syria, Caucasus
        "field" or "Field" => Class.Field,
        "Beach" => Class.Beach,
        _ => Class.None,
    };

    /// <summary>Every (shader, class) pair of a surface5 file with its record count and how many
    /// of those records carry triangles (research aid: which shaders a terrain uses for a class).</summary>
    public static List<(string Shader, string Class, int Records, int WithTriangles)> Census(string path)
    {
        var (handle, _, stream) = Surface5.Open(path);
        using var _h = handle;
        var spans = Surface5.Spans(stream, null);
        // A record's arrays sit in the span before the next record, as in Extract.
        return spans.Select((s, k) => (s.Shader, s.Class, Tri: k > 0 && spans[k - 1].Tri is not null))
            .GroupBy(s => (s.Shader, s.Class))
            .Select(g => (g.Key.Shader, g.Key.Class, g.Count(), g.Count(s => s.Tri)))
            .OrderBy(g => g.Class).ThenByDescending(g => g.Item3).ToList();
    }

    // ---- Extraction from surface5 ------------------------------------------------------------

    /// <summary>Rasterises the area classes of a surface5 file over the x/z box (format and the
    /// record/pool rules: <see cref="Surface5"/>). Every shader's records are read: terrains
    /// draw the same class with different shaders (Caucasus: forest with CaucasusSimple5.1,
    /// fields with Blend5.1 / SimpleOnlayArray5.1, towns with HardSplatArray5.1, sea with Sea5.0).</summary>
    public static LandCover Extract(string path, double x0, double z0, double x1, double z1, double cell, IProgress<double>? progress = null)
    {
        var (handle, dataBase, stream) = Surface5.Open(path);
        using var _ = handle;
        var spans = Surface5.Spans(stream, null);
        var finest = Surface5.FinestWidth(stream);
        int nx = (int)Math.Ceiling((x1 - x0) / cell), nz = (int)Math.Ceiling((z1 - z0) / cell);
        var cells = new byte[nx * nz];
        var pending = new List<(LandCover.Class Class, Surface5.Attr BaseIndex, Surface5.Attr Tri)>();
        // A pool span's matrix (and the bounding box after it) belongs to the NEXT pool: the
        // node header precedes its data, as with the classes.
        (double X, double Z)? translation = null;
        for (int k = 0; k < spans.Count; k++)
        {
            var s = spans[k];
            var cls = k + 1 < spans.Count ? ClassOf(spans[k + 1].Class) : Class.None;
            if (s.BaseIndex is { } bi && s.Tri is { } tri && cls != Class.None) pending.Add((cls, bi, tri));
            if (s.Pbase is not { } pb) continue;
            // The node's pool: resolve everything that waited for it.
            if (pending.Count > 0 && translation is { } t)
            {
                var pool = Surface5.ReadFloats(handle, dataBase, pb);
                if (Surface5.IsFinest(pool, finest))
                    foreach (var (c, b, tr) in pending)
                        Rasterise(handle, dataBase, b, tr, pool, t, c, cells, nx, nz, x0, z0, cell);
            }
            pending.Clear();
            translation = s.Translation;
            progress?.Report((double)k / spans.Count);
        }
        return new LandCover(x0, z0, cell, nx, nz, cells);
    }
    private static void Rasterise(SafeFileHandle h, long dataBase, Surface5.Attr baseIndex, Surface5.Attr tri, float[] pool, (double X, double Z) t,
        Class cls, byte[] cells, int nx, int nz, double x0, double z0, double cell)
    {
        var idx = Surface5.ReadUInts(h, dataBase, baseIndex);
        var tris = Surface5.ReadUInts(h, dataBase, tri);
        var nv = pool.Length / 3;
        for (int k = 0; k + 2 < tris.Length; k += 3)
        {
            uint a = tris[k], b = tris[k + 1], c = tris[k + 2];
            if (a >= idx.Length || b >= idx.Length || c >= idx.Length) continue;
            uint pa = idx[a], pb = idx[b], pc = idx[c];
            if (pa >= nv || pb >= nv || pc >= nv) continue;
            FillTriangle(cells, nx, nz, cls,
                (pool[3 * pa] + t.X - x0) / cell, (pool[3 * pa + 2] + t.Z - z0) / cell,
                (pool[3 * pb] + t.X - x0) / cell, (pool[3 * pb + 2] + t.Z - z0) / cell,
                (pool[3 * pc] + t.X - x0) / cell, (pool[3 * pc + 2] + t.Z - z0) / cell);
        }
    }

    /// <summary>Marks every cell whose centre lies in the triangle (grid units, i along x, j
    /// along z), keeping the higher class where classes overlap.</summary>
    public static void FillTriangle(byte[] cells, int nx, int nz, Class cls, double ai, double aj, double bi, double bj, double ci, double cj)
    {
        var area = (bi - ai) * (cj - aj) - (bj - aj) * (ci - ai);
        if (Math.Abs(area) < 1e-12) return;
        int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ai, Math.Min(bi, ci)))), i1 = Math.Min(nx - 1, (int)Math.Ceiling(Math.Max(ai, Math.Max(bi, ci))));
        int j0 = Math.Max(0, (int)Math.Floor(Math.Min(aj, Math.Min(bj, cj)))), j1 = Math.Min(nz - 1, (int)Math.Ceiling(Math.Max(aj, Math.Max(bj, cj))));
        var v = (byte)cls;
        for (int i = i0; i <= i1; i++)
        {
            var pi = i + 0.5;
            for (int j = j0; j <= j1; j++)
            {
                var pj = j + 0.5;
                var w0 = (bi - pi) * (cj - pj) - (bj - pj) * (ci - pi);
                var w1 = (ci - pi) * (aj - pj) - (cj - pj) * (ai - pi);
                var w2 = (ai - pi) * (bj - pj) - (aj - pj) * (bi - pi);
                var inside = area > 0 ? w0 >= 0 && w1 >= 0 && w2 >= 0 : w0 <= 0 && w1 <= 0 && w2 <= 0;
                if (inside && cells[i * nz + j] < v) cells[i * nz + j] = v;
            }
        }
    }

    // ---- Cache: "DCBLC 2", f64 x0, z0, cell, i32 nx, nz, zlib cells -------------------------

    private static readonly byte[] Magic = "DCBLC 2"u8.ToArray();  // 2: Sea apart from lakes

    public void Save(Stream s)
    {
        using (var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(X0); w.Write(Z0); w.Write(Cell); w.Write(Nx); w.Write(Nz);
        }
        using var z = new ZLibStream(s, CompressionLevel.Optimal, leaveOpen: true);
        z.Write(Cells);
    }

    public static LandCover Load(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("not a land-cover cache");
        double x0 = r.ReadDouble(), z0 = r.ReadDouble(), cell = r.ReadDouble();
        int nx = r.ReadInt32(), nz = r.ReadInt32();
        var cells = new byte[nx * nz];
        using var z = new ZLibStream(s, CompressionMode.Decompress, leaveOpen: true);
        z.ReadExactly(cells);
        return new LandCover(x0, z0, cell, nx, nz, cells);
    }
}
