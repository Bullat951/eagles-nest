using System.Text;

namespace Dcb.Client.Core;

/// <summary>Road and rail centre lines of a DCS terrain's road network
/// (<c>roads/&lt;terrain&gt;.rn4</c>, "landscape4::lRoadNetwork"), reverse engineered from a
/// local install (docs/DCS_MAP_FORMAT.md, "rn4"). Each edge is a typed polyline in DCS map
/// metres (x north, z east).</summary>
public sealed class RoadNetwork
{
    public enum Kind : byte { Track, Secondary, Primary, Motorway, Rail }

    /// <summary>An edge: its class and its points as (x, z) pairs.</summary>
    public readonly record struct Edge(Kind Kind, float[] Xz);

    public IReadOnlyList<Edge> Edges { get; }

    public RoadNetwork(IReadOnlyList<Edge> edges) => Edges = edges;

    /// <summary>DCS type name → drawing class: motorway* (incl. viaducts, tunnels, slopes),
    /// primary*, secondary* and link*, track*, rail*. A plain "road_tunnel" counts as primary.</summary>
    public static Kind Classify(string type) => type switch
    {
        _ when type.Contains("rail") => Kind.Rail,
        _ when type.Contains("motorway") => Kind.Motorway,
        _ when type.Contains("primary") || type == "road_tunnel" => Kind.Primary,
        _ when type.Contains("secondary") || type.Contains("link") => Kind.Secondary,
        _ => Kind.Track,
    };

    // ---- rn4 -------------------------------------------------------------------------------

    /// <summary>Decodes an rn4 file. Header: the class name, then u32 5, u32 count and count
    /// length-prefixed type names. An edge is `u32 6, u8 kind (1 rail, 2 road), u8 type index,
    /// u32 node, u32 node, u32 0, u32 n` followed by n × (x, y, z) float64 — found by that
    /// signature and plausible coordinates, as the file holds other tables between the edges.
    /// (The leading 6 is a constant tag, not the point count: reading it as one kept only the
    /// 6-point edges, a tenth of the network.)</summary>
    public static RoadNetwork Decode(Stream s)
    {
        var head = new byte[1 << 16];
        var got = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var types = ReadTypeNames(head.AsSpan(0, got));
        s.Position = 0;

        var edges = new List<Edge>();
        const int chunk = 64 << 20, overlap = 1 << 20;  // overlap > the longest edge we accept
        var buf = new byte[chunk + 2 * overlap];
        int carried = 0;
        while (true)
        {
            var room = buf.Length - carried;
            var read = s.ReadAtLeast(buf.AsSpan(carried, room), room, throwOnEndOfStream: false);
            var len = carried + read;
            var final = read < room;
            // Scan up to where an edge could still be cut off, unless this is the end.
            var limit = final ? len : len - overlap;
            var o = 0;
            while (o < limit - 22)
            {
                var next = TryEdge(buf, o, len, types, edges);
                o = next > o ? next : o + 1;
            }
            if (final) break;
            // Keep the unscanned tail for the next round.
            Buffer.BlockCopy(buf, o, buf, 0, len - o);
            carried = len - o;
        }
        return new RoadNetwork(edges);
    }

    private const int MaxPoints = 20000;
    private const int EdgeTag = 6;

    /// <summary>An edge at <paramref name="o"/>: adds it and returns the offset after its points;
    /// otherwise returns <paramref name="o"/>.</summary>
    private static int TryEdge(byte[] b, int o, int len, IReadOnlyList<string> types, List<Edge> edges)
    {
        if (BitConverter.ToInt32(b, o) != EdgeTag || BitConverter.ToInt32(b, o + 14) != 0) return o;
        var n = BitConverter.ToInt32(b, o + 18);
        if (n < 2 || n > MaxPoints) return o;
        int kind = b[o + 4], type = b[o + 5];
        if (kind is not (1 or 2) || type >= types.Count) return o;
        var p = o + 22;
        if (p + 24L * n > len) return o;
        var xz = new float[2 * n];
        double px = 0, pz = 0;
        for (int k = 0; k < n; k++, p += 24)
        {
            double x = BitConverter.ToDouble(b, p), y = BitConverter.ToDouble(b, p + 8), z = BitConverter.ToDouble(b, p + 16);
            if (!(Math.Abs(x) < 2e6 && Math.Abs(z) < 2e6 && y is > -1000 and < 9000)) return o;
            // Consecutive points of a road are metres to a few km apart.
            if (k > 0 && (Math.Abs(x - px) > 5000 || Math.Abs(z - pz) > 5000)) return o;
            xz[2 * k] = (float)x;
            xz[2 * k + 1] = (float)z;
            (px, pz) = (x, z);
        }
        edges.Add(new Edge(Classify(types[type]), Simplify(xz, ToleranceMeters)));
        return p;
    }

    /// <summary>Points closer than this to the simplified line are dropped: road samples are
    /// dense and mostly collinear; 2.5 m is below a pixel at the closest map zoom.</summary>
    public const double ToleranceMeters = 2.5;

    /// <summary>Douglas–Peucker simplification of an (x, z) polyline.</summary>
    public static float[] Simplify(float[] xz, double tolerance)
    {
        var n = xz.Length / 2;
        if (n <= 2) return xz;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, n - 1));
        var tol2 = tolerance * tolerance;
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double ax = xz[2 * a], az = xz[2 * a + 1], dx = xz[2 * b] - ax, dz = xz[2 * b + 1] - az;
            var len2 = dx * dx + dz * dz;
            int worst = -1;
            double worstD2 = tol2;
            for (int k = a + 1; k < b; k++)
            {
                double px = xz[2 * k] - ax, pz = xz[2 * k + 1] - az;
                double d2;
                if (len2 < 1e-9) d2 = px * px + pz * pz;
                else
                {
                    var cross = px * dz - pz * dx;
                    d2 = cross * cross / len2;
                }
                if (d2 > worstD2) (worst, worstD2) = (k, d2);
            }
            if (worst < 0) continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
        var result = new float[2 * keep.Count(k => k)];
        for (int k = 0, j = 0; k < n; k++)
        {
            if (!keep[k]) continue;
            result[j++] = xz[2 * k];
            result[j++] = xz[2 * k + 1];
        }
        return result;
    }

    internal static List<string> ReadTypeNames(ReadOnlySpan<byte> head)
    {
        var marker = "lRoadNetwork"u8;
        var i = head.IndexOf(marker);
        if (i < 0) throw new InvalidDataException("not a DCS road network (rn4)");
        i += marker.Length + 4;  // u32 5
        var count = BitConverter.ToInt32(head[i..]);
        i += 4;
        var names = new List<string>(count);
        for (int k = 0; k < count; k++)
        {
            var n = BitConverter.ToInt32(head[i..]);
            names.Add(Encoding.ASCII.GetString(head.Slice(i + 4, n)));
            i += 4 + n;
        }
        return names;
    }

    // ---- Cache -------------------------------------------------------------------------------
    // "DCBRN4 1", u32 edge count, then per edge u8 kind, u32 point count, float32 (x, z) pairs.

    private static readonly byte[] CacheMagic = "DCBRN4 1"u8.ToArray();

    public void Save(Stream s)
    {
        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        w.Write(CacheMagic);
        w.Write(Edges.Count);
        foreach (var e in Edges)
        {
            w.Write((byte)e.Kind);
            w.Write(e.Xz.Length / 2);
            foreach (var v in e.Xz) w.Write(v);
        }
    }

    public static RoadNetwork Load(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (!r.ReadBytes(CacheMagic.Length).AsSpan().SequenceEqual(CacheMagic)) throw new InvalidDataException("not a road cache");
        var count = r.ReadInt32();
        var edges = new List<Edge>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = (Kind)r.ReadByte();
            var n = r.ReadInt32();
            var xz = new float[2 * n];
            for (int k = 0; k < xz.Length; k++) xz[k] = r.ReadSingle();
            edges.Add(new Edge(kind, xz));
        }
        return new RoadNetwork(edges);
    }
}
