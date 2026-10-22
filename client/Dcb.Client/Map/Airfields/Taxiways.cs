using System.IO;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Map.Airfields;

/// <summary>Taxiway and runway centre lines of every airfield in a theatre, read from the local
/// DCS install's <c>Mods/terrains/&lt;id&gt;/AirfieldsTaxiways/*.rn4</c> (format notes in
/// docs/DCS_MAP_FORMAT.md; Python reference: tools/dcs_taxiways.py). Polylines are converted
/// to Web Mercator once; one bucket per airfield file for culling.</summary>
public sealed class Taxiways
{
    /// <summary>One airfield file: <see cref="Lines"/> in Web Mercator (x, y pairs) for maps,
    /// <see cref="LinesXz"/> in DCS map metres (x north, z east pairs) for the diagram view.</summary>
    public sealed record Field(string Name, double MinX, double MinY, double MaxX, double MaxY, IReadOnlyList<double[]> Lines, IReadOnlyList<double[]> LinesXz);

    public IReadOnlyList<Field> Fields { get; }

    private Taxiways(IReadOnlyList<Field> fields) => Fields = fields;

    /// <summary>The taxiway file of an airfield: DCS names them after the field with
    /// underscores for spaces.</summary>
    public Field? ForAirfield(string airfieldName)
    {
        var file = airfieldName.Replace(' ', '_');
        return Fields.FirstOrDefault(f => f.Name.Equals(file, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Dictionary<string, Task<Taxiways?>> Loads = [];

    /// <summary>Raised on a worker thread when a theatre's taxiways finished loading.</summary>
    public static event Action? Loaded;

    /// <summary>The theatre's taxiways, or null while loading (starts on first call) or when
    /// this PC has no DCS install with the terrain.</summary>
    public static Taxiways? For(Theatre theatre)
    {
        Task<Taxiways?> task;
        lock (Loads)
        {
            if (!Loads.TryGetValue(theatre.Id, out task!))
            {
                task = Task.Run(() => TryLoad(theatre));
                task.ContinueWith(_ => Loaded?.Invoke());
                Loads[theatre.Id] = task;
            }
        }
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private static Taxiways? TryLoad(Theatre theatre)
    {
        try
        {
            if (DcsInstall.Find() is not { } dcs) return null;
            var dir = Path.Combine(dcs, "Mods", "terrains", theatre.Id, "AirfieldsTaxiways");
            if (!Directory.Exists(dir)) return null;
            var fields = new List<Field>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.rn4"))
            {
                var lines = NearField(Polylines(File.ReadAllBytes(file)));
                if (lines.Count == 0) continue;
                fields.Add(ToMercator(Path.GetFileNameWithoutExtension(file), lines, theatre));
            }
            return new Taxiways(fields);
        }
        catch (Exception)
        {
            return null;  // unexpected layout (DCS update): no taxiways rather than a crash
        }
    }

    /// <summary>Runs of "u32 count + count × (x, y, z) float64" with plausible, closely spaced
    /// points: the edges of the airfield road network (DCS map metres, x north, z east).</summary>
    public static List<List<(double X, double Z)>> Polylines(byte[] data)
    {
        var result = new List<List<(double, double)>>();
        int o = 0;
        while (o + 4 + 48 <= data.Length)
        {
            var n = BitConverter.ToUInt32(data, o);
            if (n is >= 2 and <= 5000 && o + 4 + 24L * n <= data.Length)
            {
                var pts = new List<(double, double)>((int)n);
                bool ok = true;
                for (int k = 0; k < n && ok; k++)
                {
                    var p = o + 4 + 24 * k;
                    double x = BitConverter.ToDouble(data, p), y = BitConverter.ToDouble(data, p + 8), z = BitConverter.ToDouble(data, p + 16);
                    ok = x is > -2e6 and < 2e6 && z is > -2e6 and < 2e6 && y is > -500 and < 6000 && !(x == 0 && z == 0);
                    if (ok && pts.Count > 0)
                        ok = Math.Abs(x - pts[^1].Item1) <= 3000 && Math.Abs(z - pts[^1].Item2) <= 3000;
                    if (ok) pts.Add((x, z));
                }
                if (ok)
                {
                    result.Add(pts);
                    o += 4 + 24 * (int)n;
                    continue;
                }
            }
            o++;
        }
        return result;
    }

    /// <summary>The files also hold shapes in local coordinates around (0, 0) (stand/marking
    /// templates) and stray look-alike byte runs: keep the field's own lines only.</summary>
    public static List<List<(double X, double Z)>> NearField(List<List<(double X, double Z)>> lines, double radius = 20000, double local = 3000)
    {
        var far = lines.Where(l => l.Any(p => Math.Abs(p.X) > local || Math.Abs(p.Z) > local)).ToList();
        if (far.Count > 0) lines = far;
        if (lines.Count == 0) return lines;
        var xs = lines.SelectMany(l => l.Select(p => p.X)).Order().ToList();
        var zs = lines.SelectMany(l => l.Select(p => p.Z)).Order().ToList();
        double mx = xs[xs.Count / 2], mz = zs[zs.Count / 2];
        return lines.Where(l => l.All(p => Math.Abs(p.X - mx) < radius && Math.Abs(p.Z - mz) < radius)).ToList();
    }

    private static Field ToMercator(string name, List<List<(double X, double Z)>> lines, Theatre theatre)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var merc = new List<double[]>(lines.Count);
        var xz = new List<double[]>(lines.Count);
        foreach (var l in lines)
        {
            var arr = new double[l.Count * 2];
            var raw = new double[l.Count * 2];
            for (int i = 0; i < l.Count; i++)
            {
                var (lat, lon) = theatre.ToLatLon(l[i].X, l[i].Z);
                var (mx, my) = Mercator.FromLonLat(lon, lat);
                arr[2 * i] = mx;
                arr[2 * i + 1] = my;
                raw[2 * i] = l[i].X;
                raw[2 * i + 1] = l[i].Z;
                minX = Math.Min(minX, mx); maxX = Math.Max(maxX, mx);
                minY = Math.Min(minY, my); maxY = Math.Max(maxY, my);
            }
            merc.Add(arr);
            xz.Add(raw);
        }
        return new Field(name, minX, minY, maxX, maxY, merc, xz);
    }
}
