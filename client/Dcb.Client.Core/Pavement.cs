using System.IO.Compression;
using System.Text;

namespace Dcb.Client.Core;

/// <summary>Airfield pavement of a DCS terrain — runways, taxiways, aprons, shelters as DCS
/// itself lays them — from the terrain mesh's <c>Runway</c> class (shader <c>Runway5.1</c>,
/// finest level of detail; docs/DCS_MAP_FORMAT.md, "Airfield pavement"). Triangles in DCS map
/// metres (x north, z east), bucketed on a 2 km grid by their centroid.</summary>
public sealed class Pavement
{
    public const double Bucket = 2000;
    private const string Class = "Runway";

    /// <summary>The pavement geometry's shaders: Runway5.1 (Syria); OldRunway5.1 and Runway5.0
    /// (Caucasus). Onlays (painted markings) and the coarse CaucasusSimple5.1 are left out.</summary>
    private static readonly HashSet<string> Shaders = ["Runway5.1", "OldRunway5.1", "Runway5.0"];

    private readonly Dictionary<(int I, int J), float[]> _buckets;

    public Pavement(Dictionary<(int I, int J), float[]> buckets) => _buckets = buckets;

    /// <summary>Triangles in all buckets.</summary>
    public int TriangleCount => _buckets.Values.Sum(b => b.Length / 6);

    /// <summary>The triangles within <paramref name="radius"/> m (by bucket) of a point, each as
    /// x0, z0, x1, z1, x2, z2.</summary>
    public IEnumerable<float[]> Near(double x, double z, double radius)
    {
        int i0 = (int)Math.Floor((x - radius) / Bucket), i1 = (int)Math.Floor((x + radius) / Bucket);
        int j0 = (int)Math.Floor((z - radius) / Bucket), j1 = (int)Math.Floor((z + radius) / Bucket);
        for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++)
            if (_buckets.TryGetValue((i, j), out var b)) yield return b;
    }

    /// <summary>Reads every pavement mesh of the finest nodes (records and pools as in
    /// <see cref="Surface5"/>; the edge strips around the pavement are part of it).</summary>
    public static Pavement Extract(string path, IProgress<double>? progress = null)
    {
        var (handle, dataBase, stream) = Surface5.Open(path);
        using var _ = handle;
        var spans = Surface5.Spans(stream, null);
        var finest = Surface5.FinestWidth(stream);
        var buckets = new Dictionary<(int, int), List<float>>();
        var pending = new List<(Surface5.Attr BaseIndex, Surface5.Attr Tri)>();
        (double X, double Z)? translation = null;
        for (int k = 0; k < spans.Count; k++)
        {
            var s = spans[k];
            var next = k + 1 < spans.Count ? spans[k + 1] : null;
            if (s.BaseIndex is { } bi && s.Tri is { } tri && next is { Class: Class } && Shaders.Contains(next.Shader)) pending.Add((bi, tri));
            if (s.Pbase is not { } pb) continue;
            if (pending.Count > 0 && translation is { } t)
            {
                var pool = Surface5.ReadFloats(handle, dataBase, pb);
                if (Surface5.IsFinest(pool, finest))
                    foreach (var (b, tr) in pending) Add(handle, dataBase, b, tr, pool, t, buckets);
            }
            pending.Clear();
            translation = s.Translation;
            progress?.Report((double)k / spans.Count);
        }
        return new Pavement(buckets.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    private static void Add(Microsoft.Win32.SafeHandles.SafeFileHandle h, long dataBase, Surface5.Attr baseIndex, Surface5.Attr tri,
        float[] pool, (double X, double Z) t, Dictionary<(int, int), List<float>> buckets)
    {
        var idx = Surface5.ReadUInts(h, dataBase, baseIndex);
        var tris = Surface5.ReadUInts(h, dataBase, tri);
        var nv = pool.Length / 3;
        Span<float> v = stackalloc float[6];
        for (int k = 0; k + 2 < tris.Length; k += 3)
        {
            var ok = true;
            for (int c = 0; c < 3 && ok; c++)
            {
                var a = tris[k + c];
                if (a >= idx.Length || idx[a] >= nv) { ok = false; break; }
                var p = idx[a];
                v[2 * c] = (float)(pool[3 * p] + t.X);
                v[2 * c + 1] = (float)(pool[3 * p + 2] + t.Z);
            }
            if (!ok) continue;
            AddTriangle(buckets, v);
        }
    }

    internal static void AddTriangle(Dictionary<(int, int), List<float>> buckets, ReadOnlySpan<float> v)
    {
        var key = ((int)Math.Floor((v[0] + v[2] + v[4]) / 3 / Bucket), (int)Math.Floor((v[1] + v[3] + v[5]) / 3 / Bucket));
        if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
        foreach (var f in v) list.Add(f);
    }

    // ---- Cache: "DCBPV 1", i32 bucket count, per bucket i32 i, j, n floats, floats; zlib ---------

    private static readonly byte[] Magic = "DCBPV 1"u8.ToArray();

    public void Save(Stream s)
    {
        s.Write(Magic);
        using var z = new ZLibStream(s, CompressionLevel.Optimal, leaveOpen: true);
        using var w = new BinaryWriter(z, Encoding.ASCII, leaveOpen: true);
        w.Write(_buckets.Count);
        foreach (var ((i, j), b) in _buckets)
        {
            w.Write(i); w.Write(j); w.Write(b.Length);
            foreach (var f in b) w.Write(f);
        }
    }

    public static Pavement Load(Stream s)
    {
        var magic = new byte[Magic.Length];
        s.ReadExactly(magic);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("not a pavement cache");
        using var z = new ZLibStream(s, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(z, Encoding.ASCII, leaveOpen: true);
        var n = r.ReadInt32();
        var buckets = new Dictionary<(int, int), float[]>(n);
        for (int k = 0; k < n; k++)
        {
            int i = r.ReadInt32(), j = r.ReadInt32(), count = r.ReadInt32();
            var b = new float[count];
            for (int q = 0; q < count; q++) b[q] = r.ReadSingle();
            buckets[(i, j)] = b;
        }
        return new Pavement(buckets);
    }
}
