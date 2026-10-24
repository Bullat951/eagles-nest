// Console probe for DCB servers: logs in and prints received pictures.
// usage: Dcb.Probe <host> <password> [port] [pictures]
//        Dcb.Probe --roads <terrain.rn4>     (decode a DCS road network, print statistics)
//        Dcb.Probe --pavement <terrain.surface5> <x> <z>   (airfield pavement: count, time, cache size)
using Dcb.Client.Core;

if (args.Length == 2 && args[0] == "--roads")
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var file = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
    var net = RoadNetwork.Decode(file);
    Console.WriteLine($"{net.Edges.Count} edges, {net.Edges.Sum(e => e.Xz.Length / 2)} points in {sw.ElapsedMilliseconds} ms");
    foreach (var g in net.Edges.GroupBy(e => e.Kind).OrderBy(g => g.Key))
    {
        double km = g.Sum(e => Enumerable.Range(1, e.Xz.Length / 2 - 1)
            .Sum(k => Math.Sqrt(Math.Pow(e.Xz[2 * k] - e.Xz[2 * k - 2], 2) + Math.Pow(e.Xz[2 * k + 1] - e.Xz[2 * k - 1], 2)))) / 1000;
        Console.WriteLine($"  {g.Key,-10} {g.Count(),8} edges {km,10:F0} km");
    }
    return 0;
}

if (args.Length == 4 && args[0] == "--pavement")
{
    // --pavement <terrain.surface5> <x> <z>: extract all airfield pavement, time it, count near a point.
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var pavement = Pavement.Extract(args[1]);
    Console.WriteLine($"{pavement.TriangleCount} triangles in {sw.Elapsed.TotalSeconds:F1} s");
    double x = double.Parse(args[2], inv), z = double.Parse(args[3], inv);
    Console.WriteLine($"near ({x}, {z}) ±3 km: {pavement.Near(x, z, 3000).Sum(b => b.Length / 6)} triangles");
    using var ms = new MemoryStream();
    pavement.Save(ms);
    Console.WriteLine($"cache {ms.Length / 1024} KB");
    return 0;
}

if (args.Length == 3 && args[0] == "--legs")
{
    // --legs <field.rn4> <terrain.scn5>: taxiway legs of a field and the names its signs give them.
    var graph = TaxiwayGraph.Decode(File.ReadAllBytes(args[1]));
    var legs = graph.Legs();
    Console.WriteLine($"{graph.Edges.Count} edges ({graph.Edges.Count(e => e.IsRunway)} runway), types {string.Join(" ", graph.Edges.Select(e => e.Type).Distinct())}");
    Console.WriteLine($"{legs.Count} legs, {legs.Sum(l => l.Length) / 1000:F1} km");
    double cx = graph.Edges.Average(e => e.Xz[0]), cz = graph.Edges.Average(e => e.Xz[1]);
    using var scene = AirfieldSigns.Open(args[2]);
    var signs = scene.Near(cx, cz, 4500).Where(s => s.Kind == AirfieldSigns.Kind.Location).ToList();
    // Letter+digit boards joined, but each board votes (not merged pairs).
    var boards = AirfieldSigns.TaxiwayLetters(signs, mergeM: 0.1);
    var named = TaxiwayGraph.Name(legs, boards.Select(b => (b.Text, b.X, b.Z)));
    Console.WriteLine($"{boards.Count} sign boards → {named.Count} named legs");
    foreach (var g in named.GroupBy(n => n.Name).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key}: {g.Count()} leg(s), {string.Join(", ", g.Select(n => $"{n.Leg.Length:F0} m"))}");
    return 0;
}

if (args.Length == 5 && args[0] == "--signs")
{
    // --signs <terrain.scn5> <x> <z> <radius m>: airfield signs DCS placed near a point.
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var scene = AirfieldSigns.Open(args[1]);
    Console.WriteLine($"{scene.NodeCount} nodes indexed in {sw.Elapsed.TotalSeconds:F1} s");
    sw.Restart();
    var signs = scene.Near(double.Parse(args[2], inv), double.Parse(args[3], inv), double.Parse(args[4], inv));
    Console.WriteLine($"{signs.Count} signs in {sw.ElapsedMilliseconds} ms");
    foreach (var g in signs.GroupBy(s => s.Kind))
        Console.WriteLine($"  {g.Key}: {string.Join(" ", g.Select(s => s.Text).Distinct().Order())}");
    foreach (var l in AirfieldSigns.TaxiwayLetters(signs))
        Console.WriteLine($"  taxiway {l.Text} at ({l.X:F0}, {l.Z:F0})");
    return 0;
}

if (args.Length == 2 && args[0] == "--census")
{
    // --census <terrain.surface5>: every shader/class pair with its record count.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var rows = LandCover.Census(args[1]);
    Console.WriteLine($"{rows.Count} shader/class pairs in {sw.Elapsed.TotalSeconds:F1} s");
    foreach (var r in rows) Console.WriteLine($"  {r.Class,-20} {r.Shader,-26} {r.Records,8} records {r.WithTriangles,8} with triangles");
    return 0;
}

if (args.Length == 8 && args[0] == "--landcover")
{
    // --landcover <terrain.surface5> <x0> <z0> <x1> <z1> <cell m> <out.png>: extract, time, picture.
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    double[] box = [.. args[2..7].Select(a => double.Parse(a, inv))];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var lc = LandCover.Extract(args[1], box[0], box[1], box[2], box[3], box[4]);
    Console.WriteLine($"{lc.Nx}x{lc.Nz} cells in {sw.ElapsedMilliseconds} ms");
    foreach (var g in lc.Cells.GroupBy(c => (LandCover.Class)c).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-6} {g.Count() * lc.Cell * lc.Cell / 1e6,10:F0} km²");
    // North up: image row 0 = highest x.
    var rgb = new byte[lc.Nx * lc.Nz * 3];
    for (int i = 0; i < lc.Nx; i++)
    for (int j = 0; j < lc.Nz; j++)
    {
        (byte R, byte G, byte B) c = lc.At(i, j) switch
        {
            LandCover.Class.Water => (40, 70, 150), LandCover.Class.Town => (147, 144, 131), LandCover.Class.Trees => (55, 79, 21),
            LandCover.Class.Field => (204, 204, 153), LandCover.Class.Beach => (220, 156, 72), _ => (235, 225, 200),
        };
        var o = ((lc.Nx - 1 - i) * lc.Nz + j) * 3;
        (rgb[o], rgb[o + 1], rgb[o + 2]) = c;
    }
    File.WriteAllBytes(args[7], Png.Rgb(lc.Nz, lc.Nx, rgb));
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Dcb.Probe <host> <password> [port] [pictures]");
    return 2;
}
var host = args[0];
var password = args[1];
var port = args.Length > 2 ? int.Parse(args[2]) : DcbConnection.DefaultPort;
var count = args.Length > 3 ? int.Parse(args[3]) : 1;

try
{
    await using var conn = await DcbConnection.ConnectAsync(host, port, password);
    Console.WriteLine($"auth ok: {conn.Coalition}");
    var received = 0;
    await foreach (var pic in conn.ReadPicturesAsync())
    {
        var detected = pic.Units.Count(u => u.Detected);
        Console.WriteLine($"picture t={pic.Time:F0}s {pic.Theatre}: {pic.Units.Count - detected} own, {detected} detected");
        foreach (var u in pic.Units)
        {
            Console.WriteLine($"  {(u.Detected ? "CONTACT" : "own"),-7} {u.Category,-10} {u.Type ?? "?",-16} " +
                              $"{u.Callsign ?? "",-14} {u.Player ?? "",-12} {u.Lat,10:F5} {u.Lon,10:F5} " +
                              $"{u.AltFt,7:F0}ft {u.Hdg,4:F0}° {u.SpdKt,4:F0}kt");
        }
        if (++received >= count) break;
    }
    return 0;
}
catch (AuthenticationException e)
{
    Console.Error.WriteLine($"auth failed: {e.Message}");
    return 1;
}
