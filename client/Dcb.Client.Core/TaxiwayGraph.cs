using System.Globalization;
using System.Text.RegularExpressions;

namespace Dcb.Client.Core;

/// <summary>An airfield's taxiway network (<c>AirfieldsTaxiways/&lt;field&gt;.rn4</c>, the rn4
/// road-network format with types such as <c>taxiway_23m</c>, <c>runway_65m</c>) as a graph,
/// and its <see cref="Leg"/>s: stretches between turns, the unit a taxiway letter names on a
/// diagram. Coordinates in DCS map metres (x north, z east).</summary>
public sealed partial class TaxiwayGraph
{
    /// <summary>An edge between two network nodes; <c>WidthM</c> from its type name.</summary>
    public sealed record Edge(string Type, double WidthM, bool IsRunway, uint A, uint B, double[] Xz);

    /// <summary>A taxiway stretch between turns: its polyline and length.</summary>
    public sealed record Leg(double[] Xz, double Length)
    {
        /// <summary>The point halfway along the leg.</summary>
        public (double X, double Z) Middle => PointAt(Xz, Length / 2);
    }

    public IReadOnlyList<Edge> Edges { get; }

    public TaxiwayGraph(IReadOnlyList<Edge> edges) => Edges = edges;

    /// <summary>Decodes an airfield rn4 (edge layout in <see cref="RoadNetwork.Decode"/>).</summary>
    public static TaxiwayGraph Decode(byte[] data)
    {
        var types = RoadNetwork.ReadTypeNames(data.AsSpan(0, Math.Min(data.Length, 1 << 16)));
        var edges = new List<Edge>();
        for (int o = 0; o + 22 < data.Length;)
        {
            if (BitConverter.ToInt32(data, o) != 6 || BitConverter.ToInt32(data, o + 14) != 0) { o++; continue; }
            int kind = data[o + 4], type = data[o + 5], n = BitConverter.ToInt32(data, o + 18);
            // kind is 0 in airfield files (1 rail, 2 road in the terrain's road network).
            if (kind > 2 || type >= types.Count || n is < 2 or > 5000 || o + 22 + 24L * n > data.Length) { o++; continue; }
            var xz = new double[2 * n];
            var ok = true;
            for (int k = 0, p = o + 22; k < n && ok; k++, p += 24)
            {
                double x = BitConverter.ToDouble(data, p), y = BitConverter.ToDouble(data, p + 8), z = BitConverter.ToDouble(data, p + 16);
                ok = Math.Abs(x) < 2e6 && Math.Abs(z) < 2e6 && y is > -1000 and < 9000
                     && (k == 0 || (Math.Abs(x - xz[2 * k - 2]) < 3000 && Math.Abs(z - xz[2 * k - 1]) < 3000));
                xz[2 * k] = x;
                xz[2 * k + 1] = z;
            }
            if (!ok) { o++; continue; }
            var name = types[type];
            edges.Add(new Edge(name, WidthOf(name), name.StartsWith("runway", StringComparison.OrdinalIgnoreCase),
                BitConverter.ToUInt32(data, o + 6), BitConverter.ToUInt32(data, o + 10), xz));
            o += 22 + 24 * n;
        }
        // The files also hold stand and marking templates in local coordinates around (0, 0).
        var far = edges.Where(e => Enumerable.Range(0, e.Xz.Length / 2).All(k => Math.Abs(e.Xz[2 * k]) > 3000 || Math.Abs(e.Xz[2 * k + 1]) > 3000)).ToList();
        return new TaxiwayGraph(far.Count > 0 ? far : edges);
    }

    /// <summary>"taxiway_23m" → 23; "taxiway_23md" → 23; unknown → 20.</summary>
    public static double WidthOf(string type) =>
        Width().Match(type) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 20;

    [GeneratedRegex(@"_(\d+(?:\.\d+)?)m")]
    private static partial Regex Width();

    // ---- Legs ----------------------------------------------------------------------------------

    private const double BendDeg = 35;       // a sharper change of direction ends a leg
    private const double StraightDeg = 20;   // through a junction only this straight

    /// <summary>The taxiway legs: edges cut at sharp bends, then joined end to end where they
    /// continue — through a plain joint when the turn is under 35°, through a junction only
    /// along its straightest pair (under 20°). Runway edges are not taxiways.</summary>
    public IReadOnlyList<Leg> Legs()
    {
        // Pieces: edges split at bends, with the network node (or none) at each end.
        var pieces = new List<(double[] Xz, long A, long B)>();
        long nextFree = -1;  // bends get their own node ids, below the network's
        foreach (var e in Edges.Where(e => !e.IsRunway))
        {
            var parts = SplitAtBends(e.Xz);
            long from = e.A;
            for (int i = 0; i < parts.Count; i++)
            {
                long to = i == parts.Count - 1 ? e.B : nextFree--;
                pieces.Add((parts[i], from, to));
                from = to;
            }
        }

        var at = new Dictionary<long, List<int>>();
        for (int i = 0; i < pieces.Count; i++)
        {
            Add(at, pieces[i].A, i);
            Add(at, pieces[i].B, i);
        }

        // At each node, pair the two pieces that continue each other best.
        var next = new Dictionary<(int Piece, long Node), int>();
        foreach (var (node, list) in at)
        {
            var limit = list.Count == 2 ? BendDeg : StraightDeg;
            var pairs = new List<(double Turn, int P, int Q)>();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var turn = Turn(pieces[list[i]], pieces[list[j]], node);
                    if (turn < limit) pairs.Add((turn, list[i], list[j]));
                }
            var used = new HashSet<int>();
            foreach (var (_, p, q) in pairs.OrderBy(t => t.Turn))
            {
                if (p == q || used.Contains(p) || used.Contains(q)) continue;
                used.Add(p);
                used.Add(q);
                next[(p, node)] = q;
                next[(q, node)] = p;
            }
        }

        // Walk the chains.
        var done = new bool[pieces.Count];
        var legs = new List<Leg>();
        for (int s = 0; s < pieces.Count; s++)
        {
            if (done[s]) continue;
            // Back up to one end of the chain.
            int start = s;
            long startNode = pieces[s].A;
            var seen = new HashSet<int> { s };
            while (next.TryGetValue((start, startNode), out var prev) && seen.Add(prev))
            {
                startNode = Other(pieces[prev], startNode);
                start = prev;
            }
            // Walk forward from there, collecting the points.
            var pts = new List<double>();
            int cur = start;
            long entry = startNode;
            while (true)
            {
                done[cur] = true;
                var xz = pieces[cur].A == entry ? pieces[cur].Xz : Reverse(pieces[cur].Xz);
                pts.AddRange(pts.Count == 0 ? xz : xz.Skip(2));
                var exit = Other(pieces[cur], entry);
                if (!next.TryGetValue((cur, exit), out var nxt) || done[nxt]) break;
                cur = nxt;
                entry = exit;
            }
            var arr = pts.ToArray();
            legs.Add(new Leg(arr, Length(arr)));
        }
        return legs;
    }

    /// <summary>Names legs from placed names (signs or chart letters): each name votes for the
    /// nearest leg within <paramref name="reachM"/> m; a leg takes its most voted name.</summary>
    public static IReadOnlyList<(Leg Leg, string Name)> Name(IReadOnlyList<Leg> legs, IEnumerable<(string Name, double X, double Z)> names, double reachM = 50)
    {
        var votes = new Dictionary<int, Dictionary<string, int>>();
        foreach (var (name, x, z) in names)
        {
            int best = -1;
            var bestD = reachM;
            for (int i = 0; i < legs.Count; i++)
            {
                var d = Distance(legs[i].Xz, x, z);
                if (d < bestD) (best, bestD) = (i, d);
            }
            if (best < 0) continue;
            if (!votes.TryGetValue(best, out var v)) votes[best] = v = [];
            v[name] = v.GetValueOrDefault(name) + 1;
        }
        return votes.Select(kv => (legs[kv.Key], kv.Value.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key)).ToList();
    }

    // ---- Geometry -----------------------------------------------------------------------------

    private static void Add(Dictionary<long, List<int>> at, long node, int piece)
    {
        if (!at.TryGetValue(node, out var l)) at[node] = l = [];
        l.Add(piece);
    }

    private static long Other((double[] Xz, long A, long B) p, long node) => p.A == node ? p.B : p.A;

    private static double[] Reverse(double[] xz)
    {
        var r = new double[xz.Length];
        for (int i = 0; i < xz.Length; i += 2)
        {
            r[xz.Length - 2 - i] = xz[i];
            r[xz.Length - 1 - i] = xz[i + 1];
        }
        return r;
    }

    /// <summary>Change of direction (degrees) going from piece p through the node into piece q.</summary>
    private static double Turn((double[] Xz, long A, long B) p, (double[] Xz, long A, long B) q, long node)
    {
        // Arriving along p at the node, leaving along q from it.
        var (pdx, pdz) = p.B == node ? Direction(p.Xz, fromStart: false) : Negate(Direction(p.Xz, fromStart: true));
        var (qdx, qdz) = q.A == node ? Direction(q.Xz, fromStart: true) : Negate(Direction(q.Xz, fromStart: false));
        return Angle(pdx, pdz, qdx, qdz);
    }

    private static (double, double) Negate((double X, double Z) v) => (-v.X, -v.Z);

    /// <summary>Direction of a polyline over its first (or last) ~25 m, pointing along it.</summary>
    private static (double Dx, double Dz) Direction(double[] xz, bool fromStart)
    {
        var n = xz.Length / 2;
        if (fromStart)
        {
            int k = 1;
            while (k < n - 1 && Math.Sqrt(Sq(xz[2 * k] - xz[0]) + Sq(xz[2 * k + 1] - xz[1])) < 25) k++;
            return (xz[2 * k] - xz[0], xz[2 * k + 1] - xz[1]);
        }
        else
        {
            int k = n - 2;
            while (k > 0 && Math.Sqrt(Sq(xz[2 * (n - 1)] - xz[2 * k]) + Sq(xz[2 * n - 1] - xz[2 * k + 1])) < 25) k--;
            return (xz[2 * (n - 1)] - xz[2 * k], xz[2 * n - 1] - xz[2 * k + 1]);
        }
    }

    private static double Angle(double ax, double az, double bx, double bz)
    {
        var la = Math.Sqrt(ax * ax + az * az);
        var lb = Math.Sqrt(bx * bx + bz * bz);
        if (la < 1e-9 || lb < 1e-9) return 180;
        var c = Math.Clamp((ax * bx + az * bz) / (la * lb), -1, 1);
        return Math.Acos(c) * 180 / Math.PI;
    }

    /// <summary>Splits a polyline where its direction turns by more than <see cref="BendDeg"/>
    /// within ~30 m (curves made of many small steps count as one bend).</summary>
    public static List<double[]> SplitAtBends(double[] xz)
    {
        var simple = Simplify(xz, 1.0);
        var n = simple.Length / 2;
        var parts = new List<double[]>();
        int start = 0;
        for (int k = 1; k < n - 1; k++)
        {
            // Direction over ~30 m before and after point k.
            int a = k, b = k;
            while (a > start && Math.Sqrt(Sq(simple[2 * k] - simple[2 * a]) + Sq(simple[2 * k + 1] - simple[2 * a + 1])) < 30) a--;
            while (b < n - 1 && Math.Sqrt(Sq(simple[2 * b] - simple[2 * k]) + Sq(simple[2 * b + 1] - simple[2 * k + 1])) < 30) b++;
            var turn = Angle(simple[2 * k] - simple[2 * a], simple[2 * k + 1] - simple[2 * a + 1],
                             simple[2 * b] - simple[2 * k], simple[2 * b + 1] - simple[2 * k + 1]);
            if (turn > BendDeg)
            {
                parts.Add(simple[(2 * start)..(2 * k + 2)]);
                start = k;
            }
        }
        parts.Add(simple[(2 * start)..]);
        return parts;
    }

    /// <summary>Douglas–Peucker on the polyline relative to its first point (floats keep
    /// centimetres there; DCS metres far from the origin would not).</summary>
    private static double[] Simplify(double[] xz, double tol)
    {
        double ox = xz[0], oz = xz[1];
        var local = new float[xz.Length];
        for (int i = 0; i < xz.Length; i += 2)
        {
            local[i] = (float)(xz[i] - ox);
            local[i + 1] = (float)(xz[i + 1] - oz);
        }
        var f = RoadNetwork.Simplify(local, tol);
        var result = new double[f.Length];
        for (int i = 0; i < f.Length; i += 2)
        {
            result[i] = f[i] + ox;
            result[i + 1] = f[i + 1] + oz;
        }
        return result;
    }

    private static double Sq(double v) => v * v;

    public static double Length(double[] xz)
    {
        double l = 0;
        for (int i = 2; i < xz.Length; i += 2) l += Math.Sqrt(Sq(xz[i] - xz[i - 2]) + Sq(xz[i + 1] - xz[i - 1]));
        return l;
    }

    public static (double X, double Z) PointAt(double[] xz, double along)
    {
        for (int i = 2; i < xz.Length; i += 2)
        {
            var seg = Math.Sqrt(Sq(xz[i] - xz[i - 2]) + Sq(xz[i + 1] - xz[i - 1]));
            if (along <= seg && seg > 0)
            {
                var t = along / seg;
                return (xz[i - 2] + t * (xz[i] - xz[i - 2]), xz[i - 1] + t * (xz[i + 1] - xz[i - 1]));
            }
            along -= seg;
        }
        return (xz[^2], xz[^1]);
    }

    /// <summary>Distance from a point to a polyline.</summary>
    public static double Distance(double[] xz, double x, double z)
    {
        var best = double.MaxValue;
        for (int i = 2; i < xz.Length; i += 2)
        {
            double ax = xz[i - 2], az = xz[i - 1], dx = xz[i] - ax, dz = xz[i + 1] - az;
            var len2 = dx * dx + dz * dz;
            var t = len2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / len2, 0, 1);
            best = Math.Min(best, Sq(ax + t * dx - x) + Sq(az + t * dz - z));
        }
        return Math.Sqrt(best);
    }
}
