using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace Dcb.Client.Map.Charts;

/// <summary>One DCS raster chart tile: 1024² px at <see cref="MetersPerPixel"/>, covering a
/// DCS x/z rectangle.</summary>
public sealed record ChartTile(string Name, int MetersPerPixel, double MinX, double MinZ, double MaxX, double MaxZ, string ZipPath, string EntryName);

/// <summary>Index of the raster charts a DCS install ships for one terrain, read from the
/// local install (never copied). <c>RasterCharts\rasterCharts.sup5</c> lists every tile with
/// its placement; the images are DXT5 .dds files inside the scale folders' zip archives.</summary>
public sealed class ChartIndex
{
    public IReadOnlyList<ChartTile> Tiles { get; }
    public IReadOnlyList<int> Scales { get; }

    private ChartIndex(List<ChartTile> tiles)
    {
        Tiles = tiles;
        Scales = tiles.Select(t => t.MetersPerPixel).Distinct().Order().ToList();
    }

    private static readonly Dictionary<string, ChartIndex?> Cache = [];

    /// <summary>The terrain's chart index, or null when DCS or the terrain is not installed here.</summary>
    public static ChartIndex? For(string terrainId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(terrainId, out var cached)) return cached;
            ChartIndex? index = null;
            try
            {
                if (DcsInstall.Find() is { } dcs)
                {
                    var dir = Path.Combine(dcs, "Mods", "terrains", terrainId, "RasterCharts");
                    if (File.Exists(Path.Combine(dir, "rasterCharts.sup5"))) index = Load(dir);
                }
            }
            catch (Exception)
            {
                // Unknown/changed DCS file layout: no chart map rather than a crash.
                index = null;
            }
            Cache[terrainId] = index;
            return index;
        }
    }

    public static ChartIndex Load(string rasterChartsDir)
    {
        var entries = new Dictionary<string, (string Zip, string Entry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var zip in Directory.EnumerateFiles(rasterChartsDir, "*.zip", SearchOption.AllDirectories))
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var e in archive.Entries)
                if (e.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    entries[e.Name[..^4]] = (zip, e.FullName);
        }

        var tiles = new List<ChartTile>();
        foreach (var (name, minX, minZ, maxX, maxZ) in ReadSup5(File.ReadAllBytes(Path.Combine(rasterChartsDir, "rasterCharts.sup5"))))
        {
            if (!entries.TryGetValue(name, out var e) || ScaleOf(name) is not { } mpp) continue;
            tiles.Add(new ChartTile(name, mpp, minX, minZ, maxX, maxZ, e.Zip, e.Entry));
        }
        return new ChartIndex(tiles);
    }

    /// <summary>"16m_AA00_x0_z0.tif" → 16; "256m_xAB-1_x6_z3.tif" → 256.</summary>
    public static int? ScaleOf(string name)
    {
        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && name.Length > digits.Length && char.ToLowerInvariant(name[digits.Length]) == 'm'
            ? int.Parse(digits) : null;
    }

    /// <summary>Reads (tile name, world bbox) records from a landscape5 sup5 chart file. After
    /// each length-prefixed texture name "*.tif" come "shaderDefine", "NODEFINITIONS", 8 bytes,
    /// a 4×4 float matrix (translation at 12..14) and a 6-float local bbox (min xyz, max xyz).
    /// That placement belongs to the <em>next</em> name in the file (verified on Caucasus: with
    /// this pairing every tile sits on its scale's 1024-pixel grid), so the first name has
    /// none. Pairs whose bbox is not one tile of the name's scale are dropped.</summary>
    public static List<(string Name, double MinX, double MinZ, double MaxX, double MaxZ)> ReadSup5(byte[] data)
    {
        var records = ReadRecords(data);
        var result = new List<(string, double, double, double, double)>();
        for (int k = 0; k + 1 < records.Count; k++)
        {
            var name = records[k + 1].Name;
            var (minX, minZ, maxX, maxZ) = records[k].Box;
            var size = (ScaleOf(name) ?? 0) * 1024.0;
            if (Math.Abs(maxX - minX - size) > size * 0.01 || Math.Abs(maxZ - minZ - size) > size * 0.01) continue;
            result.Add((name, minX, minZ, maxX, maxZ));
        }
        return result;
    }

    private static List<(string Name, (double, double, double, double) Box)> ReadRecords(byte[] data)
    {
        var result = new List<(string, (double, double, double, double))>();
        var marker = Encoding.ASCII.GetBytes("NODEFINITIONS");
        var span = data.AsSpan();
        for (int p = 4; p < data.Length - 8; p++)
        {
            // A length-prefixed ASCII name ending in ".tif".
            if (data[p] != (byte)'.' || data[p + 1] != (byte)'t' || data[p + 2] != (byte)'i' || data[p + 3] != (byte)'f') continue;
            int start = p;
            while (start > 0 && data[start - 1] >= 0x20 && data[start - 1] < 0x7F) start--;
            int len = p + 4 - start;
            if (start < 4 || BitConverter.ToInt32(data, start - 4) != len) continue;
            var name = Encoding.ASCII.GetString(data, start, len);

            int m = span.Slice(p, Math.Min(200, data.Length - p)).IndexOf(marker);
            if (m < 0) continue;
            int o = p + m + marker.Length + 8;
            if (o + 22 * 4 > data.Length) break;
            double tx = BitConverter.ToSingle(data, o + 4 * 12), tz = BitConverter.ToSingle(data, o + 4 * 14);
            double minX = BitConverter.ToSingle(data, o + 4 * 16) + tx, minZ = BitConverter.ToSingle(data, o + 4 * 18) + tz;
            double maxX = BitConverter.ToSingle(data, o + 4 * 19) + tx, maxZ = BitConverter.ToSingle(data, o + 4 * 21) + tz;
            result.Add((name, (minX, minZ, maxX, maxZ)));
            p = o;
        }
        return result;
    }
}

/// <summary>Locates the DCS World install: settings override, then the registry.</summary>
/// <summary>The DCS World install the client reads maps, charts and airfield data from:
/// Settings.DcsPath when the controller chose one ("" = chose to run without DCS), else the
/// first install the search finds (ED's registry entries, Steam libraries, usual folders).</summary>
public static class DcsInstall
{
    private static readonly object Lock = new();
    private static IReadOnlyList<Core.DcsCandidate>? _found;

    public static string? Find()
    {
        var configured = AppState.Settings.DcsPath;
        if (configured is not null)
            return configured.Length > 0 && Directory.Exists(configured) ? configured : null;
        return Search().FirstOrDefault()?.Path;
    }

    /// <summary>Every DCS install on this PC, best guess first (searched once, then remembered).</summary>
    public static IReadOnlyList<Core.DcsCandidate> Search(bool again = false)
    {
        lock (Lock)
        {
            if (_found is null || again) _found = Core.DcsLocator.Verify(Guesses());
            return _found;
        }
    }

    private static IEnumerable<(string, string)> Guesses()
    {
        // ED's own installer (standalone, and the old open beta) records its folder per user.
        foreach (var key in new[] { @"Software\Eagle Dynamics\DCS World", @"Software\Eagle Dynamics\DCS World OpenBeta" })
            if (Read(Registry.CurrentUser, key, "Path") is { } p) yield return (p, "ED installer");
        // Steam: its library list names every drive folder games are installed in.
        var steamRoots = new[]
        {
            Read(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            Read(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };
        foreach (var steam in steamRoots.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = steam.Replace('/', '\\');
            yield return (Core.DcsLocator.InSteamLibrary(root), "Steam");
            string? vdf = null;
            try { vdf = File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (vdf is not null)
                foreach (var lib in Core.DcsLocator.SteamLibraries(vdf)) yield return (Core.DcsLocator.InSteamLibrary(lib), "Steam");
        }
        // The usual folders on every local disk.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            foreach (var p in Core.DcsLocator.UsualFolders(drive.RootDirectory.FullName)) yield return (p, "found on " + drive.Name.TrimEnd('\\'));
    }

    private static string? Read(RegistryKey hive, string key, string name)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(name) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
