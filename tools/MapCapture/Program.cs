// Eagle's Nest map capture (maintainer tool): DCS's own Mission Editor "Sat" map, captured step
// by step and assembled into one tile file per terrain (docs/RESEARCH-MAPCAPTURE.md).
//   MapCapture install  [dcs dir]                 add the loader line to the ME (backup kept)
//   MapCapture restore  [dcs dir]                 put me_map_window.lua back
//   MapCapture plan <landcover cache> <airfields.json> <out plan.json> [base m/px] [field m/px] [field km]
//   MapCapture run <plan.json> <capture dir>      resumable: rerun to continue
//   MapCapture assemble <capture dir> <terrain> <out.ensat>
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

var inv = CultureInfo.InvariantCulture;
string savedDcs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "DCS");
string io = Path.Combine(savedDcs, "EaglesNest", "capture");
const string Loader = "base.pcall(base.dofile, base.require('lfs').writedir() .. 'EaglesNest\\\\capture_me.lua') -- Eagle's Nest map capture";
const string Command = "command.v6.lua";  // capture_me.lua's COMMAND

if (args.Length >= 1 && args[0] == "install")
{
    var file = Path.Combine(args.Length > 1 ? args[1] : @"E:\DCS World", "MissionEditor", "modules", "me_map_window.lua");
    var backup = file + ".eaglesnest-backup";
    if (!File.ReadAllText(file).Contains("Eagle's Nest map capture"))
    {
        if (!File.Exists(backup)) File.Copy(file, backup);
        File.AppendAllText(file, "\r\n" + Loader + "\r\n");
    }
    Directory.CreateDirectory(io);
    File.Copy(Path.Combine(AppContext.BaseDirectory, "capture_me.lua"), Path.Combine(savedDcs, "EaglesNest", "capture_me.lua"), overwrite: true);
    Console.WriteLine($"Installed. Backup: {backup}. Open the Mission Editor with a mission on the terrain, then run.");
    return 0;
}

if (args.Length >= 1 && args[0] == "restore")
{
    var file = Path.Combine(args.Length > 1 ? args[1] : @"E:\DCS World", "MissionEditor", "modules", "me_map_window.lua");
    var backup = file + ".eaglesnest-backup";
    if (File.Exists(backup)) { File.Copy(backup, file, overwrite: true); File.Delete(backup); Console.WriteLine("Restored me_map_window.lua."); }
    else { File.WriteAllLines(file, File.ReadAllLines(file).Where(l => !l.Contains("Eagle's Nest map capture"))); Console.WriteLine("No backup; loader line removed if present."); }
    return 0;
}

if (args.Length >= 4 && args[0] == "plan")
{
    Plan.Build(args[1], args[2], args[3],
        args.Length > 4 ? double.Parse(args[4], inv) : 8, args.Length > 5 ? double.Parse(args[5], inv) : 2, args.Length > 6 ? double.Parse(args[6], inv) : 15);
    return 0;
}

if (args.Length >= 3 && args[0] == "run")
{
    var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(args[1]))!;
    var outDir = args[2];
    Directory.CreateDirectory(io);
    Directory.CreateDirectory(outDir);
    Runner.Reload(io, savedDcs);
    var started = DateTime.UtcNow;
    int done = 0;
    foreach (var area in plan.Areas)
    {
        var areaDir = Path.Combine(outDir, area.Name);
        Directory.CreateDirectory(areaDir);
        var job = Runner.Setup(io, area);
        File.WriteAllText(Path.Combine(areaDir, "job.lua"), job.Text);
        double mpu = double.Parse(job.T["mpu"], inv), w = double.Parse(job.T["map.w"], inv), h = double.Parse(job.T["map.h"], inv);
        double spanX = h * mpu, spanZ = w * mpu, stepX = spanX * 0.7, stepZ = spanZ * 0.7;
        var blocks = area.Blocks?.Select(b => b.Split(',')).Select(b => (int.Parse(b[0]), int.Parse(b[1]))).ToHashSet();
        var cams = new List<(string Id, double X, double Z)>();
        int row = 0;
        for (double x = area.X1 - spanX / 2; x > area.X0 - stepX; x -= stepX, row++)
        {
            int col = 0;
            for (double z = area.Z0 + spanZ / 2; z < area.Z1 + stepZ; z += stepZ, col++)
            {
                if (blocks is not null && !Plan.Touches(blocks, x - spanX / 2, x + spanX / 2, z - spanZ / 2, z + spanZ / 2)) continue;
                var id = $"{area.Name}-{row:000}-{col:000}";
                if (File.Exists(Path.Combine(areaDir, $"step_{id}.png"))) continue;
                cams.Add((id, x, z));
            }
        }
        Console.WriteLine($"{area.Name}: {mpu:F2} m/px (check {job.T["check"]} m), {cams.Count} steps to capture");
        foreach (var batch in cams.Chunk(20))
        {
            Runner.Visit(io, areaDir, batch, job.T);
            done += batch.Length;
            var rate = (DateTime.UtcNow - started).TotalSeconds / done;
            Console.Write($"\r{area.Name}: {done} captured, {rate:F1} s each   ");
        }
        Console.WriteLine();
    }
    Runner.Finish(io);
    Console.WriteLine($"Capture finished in {(DateTime.UtcNow - started).TotalMinutes:F0} min.");
    return 0;
}

if (args.Length >= 4 && args[0] == "assemble")
{
    Assembler.Run(args[1], args[2], args[3]);
    return 0;
}

Console.Error.WriteLine("usage: install | restore | plan <landcover> <airfields.json> <plan.json> | run <plan.json> <dir> | assemble <dir> <terrain> <out.ensat>");
return 2;

/// <summary>What to capture: areas, each at one resolution; the base area only where its 10 km
/// blocks have detail (towns, fields, forest, beach).</summary>
sealed class Plan
{
    public List<Area> Areas { get; set; } = [];

    public sealed class Area
    {
        public string Name { get; set; } = "";
        public double Mpp { get; set; }
        public double X0 { get; set; }
        public double Z0 { get; set; }
        public double X1 { get; set; }
        public double Z1 { get; set; }
        public List<string>? Blocks { get; set; }
    }

    public const double Block = 10000;

    public static bool Touches(HashSet<(int, int)> blocks, double x0, double x1, double z0, double z1)
    {
        for (int i = (int)Math.Floor(x0 / Block); i <= (int)Math.Floor(x1 / Block); i++)
        for (int j = (int)Math.Floor(z0 / Block); j <= (int)Math.Floor(z1 / Block); j++)
            if (blocks.Contains((i, j))) return true;
        return false;
    }

    /// <summary>From the client's land-cover cache ("DCBLC 2": x0, z0, cell, nx, nz, zlib classes)
    /// and the server's airfields (name, runways[].x/z).</summary>
    public static void Build(string landcover, string airfields, string outPath, double baseMpp, double fieldMpp, double fieldKm)
    {
        var d = File.ReadAllBytes(landcover);
        if (System.Text.Encoding.ASCII.GetString(d, 0, 7) != "DCBLC 2") throw new InvalidDataException("not a land-cover cache");
        double x0 = BitConverter.ToDouble(d, 7), z0 = BitConverter.ToDouble(d, 15), cell = BitConverter.ToDouble(d, 23);
        int nx = BitConverter.ToInt32(d, 31), nz = BitConverter.ToInt32(d, 35);
        var cells = new byte[nx * nz];
        using (var z = new ZLibStream(new MemoryStream(d, 39, d.Length - 39), CompressionMode.Decompress)) z.ReadExactly(cells);
        var detail = new HashSet<(int, int)>();
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < nz; j++)
            if (cells[i * nz + j] is >= 2 and <= 5)  // field, beach, trees, town
                detail.Add(((int)Math.Floor((x0 + (i + 0.5) * cell) / Block), (int)Math.Floor((z0 + (j + 0.5) * cell) / Block)));
        // One block of margin around the detail (coasts, edges of towns).
        var blocks = detail.SelectMany(b => from di in new[] { -1, 0, 1 } from dj in new[] { -1, 0, 1 } select (b.Item1 + di, b.Item2 + dj)).ToHashSet();
        var plan = new Plan();
        plan.Areas.Add(new Area
        {
            Name = "base", Mpp = baseMpp,
            X0 = blocks.Min(b => b.Item1) * Block, X1 = (blocks.Max(b => b.Item1) + 1) * Block,
            Z0 = blocks.Min(b => b.Item2) * Block, Z1 = (blocks.Max(b => b.Item2) + 1) * Block,
            Blocks = blocks.Select(b => $"{b.Item1},{b.Item2}").ToList(),
        });
        using var doc = JsonDocument.Parse(File.ReadAllText(airfields));
        var half = fieldKm * 500;
        foreach (var f in doc.RootElement.EnumerateArray())
        {
            var runways = f.GetProperty("runways").EnumerateArray().ToList();
            if (runways.Count == 0) continue;
            double cx = runways.Average(r => r.GetProperty("x").GetDouble()), cz = runways.Average(r => r.GetProperty("z").GetDouble());
            var name = Regex.Replace(f.GetProperty("name").GetString() ?? "field", "[^A-Za-z0-9]+", "_").Trim('_');
            plan.Areas.Add(new Area { Name = name, Mpp = fieldMpp, X0 = cx - half, X1 = cx + half, Z0 = cz - half, Z1 = cz + half });
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"plan: base {blocks.Count} blocks ({blocks.Count * Block * Block / 1e6:F0} km²) at {baseMpp} m/px, {plan.Areas.Count - 1} airfields {fieldKm} km at {fieldMpp} m/px → {outPath}");
    }
}

/// <summary>The conversation with capture_me.lua through files in Saved Games\DCS\EaglesNest\capture.</summary>
static class Runner
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Loads the current capture_me.lua into the open editor (copies from before the
    /// generation counter read command.lua; newer copies retire older ones).</summary>
    public static void Reload(string io, string savedDcs)
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "capture_me.lua"), Path.Combine(savedDcs, "EaglesNest", "capture_me.lua"), overwrite: true);
        File.Delete(Path.Combine(io, "loaded.txt"));
        File.WriteAllText(Path.Combine(io, "command.lua"), "dofile(require('lfs').writedir() .. 'EaglesNest\\\\capture_me.lua') return nil");
        File.WriteAllText(Path.Combine(io, "command.v5.lua"), "dofile(require('lfs').writedir() .. 'EaglesNest\\\\capture_me.lua') return nil");
        var t0 = DateTime.UtcNow;
        while (!File.Exists(Path.Combine(io, "loaded.txt")) && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(30)) Thread.Sleep(300);
        Console.WriteLine(File.Exists(Path.Combine(io, "loaded.txt")) ? "Capture script loaded in the editor." : "No reload answer: is the Mission Editor open?");
        foreach (var f in new[] { "command.lua", "command.v5.lua" }) File.Delete(Path.Combine(io, f));
        Thread.Sleep(1000);
    }

    public sealed record Job(string Text, Dictionary<string, string> T);

    public static Job Setup(string io, Plan.Area area)
    {
        var jobFile = Path.Combine(io, "job.lua");
        File.Delete(jobFile);
        WriteShared(Path.Combine(io, "command.v6.lua"), string.Create(Inv,
            // settle: frames the editor waits after a move; the helper's matching shots catch
            // imagery still streaming in, so this need not be long.
            $"return {{ op = 'setup', mpp = {area.Mpp}, settle = 20, x = {(area.X0 + area.X1) / 2}, z = {(area.Z0 + area.Z1) / 2} }}"));
        var t0 = DateTime.UtcNow;
        while (!File.Exists(jobFile))
        {
            if (DateTime.UtcNow - t0 > TimeSpan.FromMinutes(1)) throw new TimeoutException("no answer to setup (Mission Editor open?)");
            Thread.Sleep(200);
        }
        Thread.Sleep(200);
        var text = File.ReadAllText(jobFile);
        var t = Lua.Table(text);
        if (t.ContainsKey("error")) throw new InvalidOperationException("setup failed, see capture.log");
        return new Job(text, t);
    }

    public static void Visit(string io, string areaDir, (string Id, double X, double Z)[] batch, Dictionary<string, string> job)
    {
        File.Delete(Path.Combine(io, "ready.txt"));
        var cams = string.Join(", ", batch.Select(c => string.Create(Inv, $"{{ id = '{c.Id}', x = {c.X:F1}, z = {c.Z:F1} }}")));
        WriteShared(Path.Combine(io, "command.v6.lua"), $"return {{ op = 'visit', cams = {{ {cams} }} }}");
        var last = "";
        var t0 = DateTime.UtcNow;
        while (true)
        {
            var ready = Lua.ReadShared(Path.Combine(io, "ready.txt"));
            if (ready is null || ready == last)
            {
                if (DateTime.UtcNow - t0 > TimeSpan.FromMinutes(3)) throw new TimeoutException("the editor stopped answering");
                Thread.Sleep(50);
                continue;
            }
            if (ready == "batch-done")
            {
                Task.WaitAll([.. saves]);  // every PNG of the batch on disk before the next batch
                saves.Clear();
                return;
            }
            last = ready;
            t0 = DateTime.UtcNow;
            var stepLua = Path.Combine(io, $"step_{ready}.lua");
            var shot = Capture.MapView(job);
            File.WriteAllText(Path.Combine(areaDir, $"step_{ready}.scale"), shot.UnitsToPixels.ToString(Inv));
            File.Move(stepLua, Path.Combine(areaDir, $"step_{ready}.lua"), overwrite: true);
            // The editor moves on while the PNG is written. The PNG is the resume marker, so it
            // only appears complete (written under a temporary name, then renamed).
            WriteShared(Path.Combine(io, "done.txt"), ready);
            var png = Path.Combine(areaDir, $"step_{ready}.png");
            Saving.Wait();
            saves.Add(Task.Run(() =>
            {
                try
                {
                    shot.Image.Save(png + ".tmp", System.Drawing.Imaging.ImageFormat.Png);
                    File.Move(png + ".tmp", png, overwrite: true);
                }
                finally
                {
                    shot.Image.Dispose();
                    Saving.Release();
                }
            }));
        }
    }

    static readonly SemaphoreSlim Saving = new(4);
    static readonly List<Task> saves = [];

    /// <summary>Writes a handshake file the editor reads every frame: its read can hold the file
    /// for a moment (sharing violation), so try again shortly.</summary>
    static void WriteShared(string path, string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(path, text);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 100)
            {
                Thread.Sleep(20);
            }
        }
    }

    public static void Finish(string io) => WriteShared(Path.Combine(io, "command.v6.lua"), "return { op = 'finish' }");
}

/// <summary>The small Lua tables the capture script writes ("return { a = 1, b = { c = 2 } }"),
/// flattened to dotted keys; lists as points.1.x etc.</summary>
static class Lua
{
    public static Dictionary<string, string> Table(string text)
    {
        var result = new Dictionary<string, string>();
        var path = new List<string>();
        var counters = new Stack<int>();
        int i = text.IndexOf('{');
        if (i < 0) return result;
        string? pendingKey = null;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                if (pendingKey is null && counters.Count > 0) { var k = counters.Pop() + 1; counters.Push(k); pendingKey = k.ToString(); }
                if (pendingKey is not null) path.Add(pendingKey);
                counters.Push(0);
                pendingKey = null;
            }
            else if (c == '}')
            {
                counters.Pop();
                if (path.Count > 0) path.RemoveAt(path.Count - 1);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var m = Regex.Match(text[i..], @"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*");
                if (!m.Success) continue;
                var key = m.Groups[1].Value;
                i += m.Length;
                if (i < text.Length && text[i] == '{') { pendingKey = key; i--; continue; }
                var v = Regex.Match(text[i..], @"^(""[^""]*""|'[^']*'|[^,}\s]+)");
                result[string.Join('.', path.Append(key))] = v.Value.Trim('"', '\'');
                i += v.Length - 1;
            }
        }
        return result;
    }

    public static string? ReadShared(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s);
            return r.ReadToEnd().Trim();
        }
        // The editor replaces the file by delete + rename: in between, reading can fail with a
        // sharing violation or "access denied" — treated as "not there yet".
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
