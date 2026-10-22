using System.IO;
using System.Text;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Map.Vector;

/// <summary>DCS F10 vector lines read from the local install's <c>Map/&lt;terrain&gt;.sup5</c>
/// (format: docs/DCS_MAP_FORMAT.md). The DCS Map's roads, rivers and power lines are baked
/// into its tiles (DcsMapTiles), which read the lines as DCS x/z segments
/// (<see cref="ReadXz"/>); what stays a live layer is the MGRS grid (<see cref="For"/>),
/// converted to Web Mercator once and grouped per DCS tile for culling.</summary>
public sealed class DcsVectorMap
{
    /// <summary>Line segments of one class inside one DCS tile, as Mercator (x0, y0, x1, y1)
    /// offsets from <see cref="OriginX"/>/<see cref="OriginY"/> (floats keep memory down).</summary>
    public sealed record Bucket(string Class, double OriginX, double OriginY, double MinX, double MinY, double MaxX, double MaxY, float[] Segments);

    public IReadOnlyList<Bucket> Buckets { get; }

    private DcsVectorMap(IReadOnlyList<Bucket> buckets) => Buckets = buckets;

    // ---- MGRS grid (background load, once per theatre) --------------------------------------

    private static readonly Dictionary<string, Task<DcsVectorMap?>> Loads = [];

    /// <summary>Raised on a worker thread when a map finished loading.</summary>
    public static event Action? Loaded;

    /// <summary>The theatre's MGRS grid; null while loading (load starts on first call) or when
    /// this PC has no DCS install with the terrain. <paramref name="loading"/> tells which.</summary>
    public static DcsVectorMap? For(Theatre theatre, out bool loading)
    {
        Task<DcsVectorMap?> task;
        lock (Loads)
        {
            if (!Loads.TryGetValue(theatre.Id, out task!))
            {
                task = Task.Run(() => TryLoadGrid(theatre));
                task.ContinueWith(_ => Loaded?.Invoke());
                Loads[theatre.Id] = task;
            }
        }
        loading = !task.IsCompleted;
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private static DcsVectorMap? TryLoadGrid(Theatre theatre)
    {
        try
        {
            return Sup5Path(theatre.Id) is { } path ? LoadGrid(File.ReadAllBytes(path), theatre) : null;
        }
        catch (Exception)
        {
            return null;  // unexpected file layout (DCS update): no grid rather than a crash
        }
    }

    public static string? Sup5Path(string terrainId) =>
        DcsInstall.Find() is { } dcs && Path.Combine(dcs, "Mods", "terrains", terrainId, "Map", terrainId + ".sup5") is var p && File.Exists(p) ? p : null;

    private static DcsVectorMap LoadGrid(byte[] data, Theatre theatre)
    {
        var buckets = new List<Bucket>();
        foreach (var (node, xz) in ReadXzByNode(data, "MGRS"))
        {
            var merc = new double[xz.Count];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < xz.Count; i += 2)
            {
                var (lat, lon) = theatre.ToLatLon(xz[i], xz[i + 1]);
                var (x, y) = Mercator.FromLonLat(lon, lat);
                (merc[i], merc[i + 1]) = (x, y);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            var segs = new float[merc.Length];
            for (int i = 0; i < merc.Length; i += 2)
            {
                segs[i] = (float)(merc[i] - minX);
                segs[i + 1] = (float)(merc[i + 1] - minY);
            }
            buckets.Add(new Bucket("MGRS", minX, minY, minX, minY, maxX, maxY, segs));
        }
        return new DcsVectorMap(buckets);
    }

    // ---- Road network (roads/<id>.rn4, cached) -----------------------------------------------

    /// <summary>The terrain's road network, decoded once (≈2 s for Syria's 2.2 GB) and kept in a
    /// local cache keyed by the file's size and date; null when the terrain has none or it fails.</summary>
    public static RoadNetwork? LoadRoads(string terrainId)
    {
        try
        {
            if (DcsInstall.Find() is not { } dcs) return null;
            var rn4 = Path.Combine(dcs, "Mods", "terrains", terrainId, "roads", terrainId + ".rn4");
            if (!File.Exists(rn4)) return null;
            var info = new FileInfo(rn4);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCB", "cache");
            var cache = Path.Combine(dir, $"roads_{terrainId}_{info.Length}_{info.LastWriteTimeUtc.Ticks}.bin");
            if (File.Exists(cache))
            {
                try
                {
                    using var c = File.OpenRead(cache);
                    return RoadNetwork.Load(c);
                }
                catch (Exception)
                {
                    // Damaged cache: decode again below.
                }
            }
            RoadNetwork net;
            using (var s = new FileStream(rn4, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                net = RoadNetwork.Decode(s);
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.EnumerateFiles(dir, $"roads_{terrainId}_*.bin")) File.Delete(old);
            using (var c = File.Create(cache)) net.Save(c);
            return net;
        }
        catch (Exception)
        {
            return null;  // unknown layout: the F10 file's own roads are used
        }
    }

    // ---- sup5 decoding -----------------------------------------------------------------------

    private static readonly byte[] NodePattern = BuildNodePattern();
    private static readonly byte[] VertsPattern = [1, 0, 0, 0, 1, 0, 0, 0, 0x50, 0x02, 0x00, 0x0c, 0, 0, 0, 0, 0];
    private static readonly byte[] GeomPattern = [8, 0, 0, 0, .. "LINELIST"u8];

    private static byte[] BuildNodePattern()
    {
        // 4×4 float matrix with identity rotation: 1, 0×4, 1, 0×4, 1, 0 (translation follows).
        byte[] one = [0, 0, 0x80, 0x3f];
        return [.. one, .. new byte[16], .. one, .. new byte[16], .. one, 0, 0, 0, 0];
    }

    private static List<int> FindAll(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        var hits = new List<int>();
        for (int at = 0; ;)
        {
            var i = data[at..].IndexOf(pattern);
            if (i < 0) return hits;
            hits.Add(at + i);
            at += i + pattern.Length;
        }
    }

    /// <summary>Line segments of the given classes as DCS (x0, z0, x1, z1) per class.</summary>
    public static Dictionary<string, List<float>> ReadXz(byte[] data, params string[] classes)
    {
        var result = classes.ToDictionary(c => c, _ => new List<float>());
        foreach (var (cls, _, xz) in Segments(data, [.. classes]))
            result[cls].AddRange(xz);
        return result;
    }

    private static IEnumerable<(int Node, List<float> Xz)> ReadXzByNode(byte[] data, string cls)
    {
        var byNode = new Dictionary<int, List<float>>();
        foreach (var (_, node, xz) in Segments(data, [cls]))
        {
            if (!byNode.TryGetValue(node, out var list)) byNode[node] = list = [];
            list.AddRange(xz);
        }
        return byNode.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary>Per geometry of a wanted class: its class, its DCS node and its segments as
    /// (x0, z0, x1, z1) in DCS metres. Indices pair with the most recent vertices entry before
    /// the geometry; vertices are local to the most recent node (docs/DCS_MAP_FORMAT.md).</summary>
    private static IEnumerable<(string Class, int Node, List<float> Xz)> Segments(byte[] data, HashSet<string> wanted)
    {
        var dataBase = (long)BitConverter.ToUInt64(data, 8);
        var table = data.AsSpan(0, (int)Math.Min(dataBase, data.Length));
        var nodes = FindAll(table, NodePattern);
        var verts = FindAll(table, VertsPattern);
        var geoms = FindAll(table, GeomPattern);
        foreach (var g in geoms)
        {
            var nameLen = BitConverter.ToInt32(data, g + 52);
            if (nameLen is < 1 or > 64) continue;
            var cls = Encoding.ASCII.GetString(data, g + 56, nameLen);
            if (!wanted.Contains(cls)) continue;

            var ibBytes = (long)BitConverter.ToUInt64(data, g + 20);
            var ibOff = (long)BitConverter.ToUInt64(data, g + 28);
            var vi = LastBefore(verts, g);
            if (vi < 0 || ibBytes <= 0 || dataBase + ibOff + ibBytes > data.Length) continue;
            var v = verts[vi] + VertsPattern.Length;
            var vbBytes = (long)BitConverter.ToUInt64(data, v);
            var vbOff = BitConverter.ToUInt32(data, v + 8);
            if (vbBytes <= 0 || dataBase + vbOff + vbBytes > data.Length) continue;
            var ni = LastBefore(nodes, verts[vi]);
            float tx = 0, tz = 0;
            if (ni >= 0)
            {
                tx = BitConverter.ToSingle(data, nodes[ni] + 48);
                tz = BitConverter.ToSingle(data, nodes[ni] + 56);
            }

            var nVerts = (int)(vbBytes / 12);
            var vbase = (int)(dataBase + vbOff);
            var ibase = (int)(dataBase + ibOff);
            var list = new List<float>((int)(ibBytes / 2));
            for (long k = 0; k + 8 <= ibBytes; k += 8)
            {
                var a = BitConverter.ToInt32(data, ibase + (int)k);
                var b = BitConverter.ToInt32(data, ibase + (int)k + 4);
                if ((uint)a >= nVerts || (uint)b >= nVerts) continue;
                list.Add(BitConverter.ToSingle(data, vbase + a * 12) + tx);
                list.Add(BitConverter.ToSingle(data, vbase + a * 12 + 8) + tz);
                list.Add(BitConverter.ToSingle(data, vbase + b * 12) + tx);
                list.Add(BitConverter.ToSingle(data, vbase + b * 12 + 8) + tz);
            }
            yield return (cls, ni, list);
        }
    }

    private static int LastBefore(List<int> sorted, int position)
    {
        var i = sorted.BinarySearch(position);
        return (i >= 0 ? i : ~i) - 1;
    }
}
