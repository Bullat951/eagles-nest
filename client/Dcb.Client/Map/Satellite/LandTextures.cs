using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Map.Satellite;

/// <summary>The ground colour texture of terrains without satellite clipmaps (Caucasus): what
/// DCS's F10 "Sat" view shows, <c>vfstextures/LandTextures&lt;Season&gt;.zip</c>, tiles
/// <c>land_39_&lt;i&gt;_&lt;j&gt;.png.dds</c> (256 px, DXT5) and a 4× coarser <c>land_156_*</c>
/// level (docs/DCS_MAP_FORMAT.md, "Land textures"). DCS maps world position straight to the
/// texture (the mesh's <c>xyz2uvw</c> is the node translation): fine pixel P = x / SX + OX
/// (columns, along x north), Q = z / SZ + OZ (rows, along z east), tile (i, j) = pixels
/// [256·i, 256·i + 256) × [256·j, …). Measured by registering DCS's F10 river lines to the
/// texture in 8 regions across the map (0.65 px rms, ~25 m); the 21 runways and the coastline
/// agree. Read in place, never copied or exported.</summary>
public sealed partial class LandTextures
{
    /// <summary>Fine-level metres per pixel along x and z, and the pixel offsets.</summary>
    public const double SX = 38.398, SZ = 38.3929, OX = -0.64, OZ = 1.10;
    public const int TilePixels = 256;

    /// <summary>A level: <see cref="Factor"/> fine pixels per pixel (1 for "39", 4 for "156").</summary>
    public sealed record Level(string Name, int Factor, int MinI, int MaxI, int MinJ, int MaxJ)
    {
        /// <summary>The tile's south-west corner (DCS metres) and its extent along x and z.</summary>
        public (double X, double Z, double Width, double Height) Bounds(int i, int j) =>
            ((i * TilePixels * Factor - OX) * SX, (j * TilePixels * Factor - OZ) * SZ, TilePixels * Factor * SX, TilePixels * Factor * SZ);

        public int TileAtX(double x) => (int)Math.Floor((x / SX + OX) / (TilePixels * Factor));
        public int TileAtZ(double z) => (int)Math.Floor((z / SZ + OZ) / (TilePixels * Factor));
        public double MetersPerPixel => Factor * (SX + SZ) / 2;
    }

    public string Path { get; }
    public string Season { get; }
    /// <summary>Coarsest first.</summary>
    public IReadOnlyList<Level> Levels { get; }

    private readonly ZipArchive _zip;
    private readonly HashSet<(string Level, int I, int J)> _tiles;
    private readonly object _lock = new();

    private LandTextures(string path, string season, ZipArchive zip, HashSet<(string, int, int)> tiles, List<Level> levels) =>
        (Path, Season, _zip, _tiles, Levels) = (path, season, zip, tiles, levels);

    public bool Has(Level level, int i, int j) => _tiles.Contains((level.Name, i, j));

    /// <summary>A tile as an image with pixel columns along x (north) and rows along z (east).</summary>
    public SkiaSharp.SKImage ReadTile(Level level, int i, int j)
    {
        byte[] dds;
        lock (_lock)  // ZipArchive is not thread-safe; tiles load on several workers
        {
            using var s = _zip.GetEntry($"land_{level.Name}_{i}_{j}.png.dds")!.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            dds = ms.ToArray();
        }
        var (w, h, bgra) = Dxt5.Decode(dds);
        for (int k = 3; k < bgra.Length; k += 4) bgra[k] = 255;  // colour only: the alpha channel is not coverage
        return Gpu.SkDraw.FromBgra(bgra, w, h);
    }

    /// <summary>"Winter" December–February, "Spring" March–May, "Summer" June–August,
    /// "Autumn" September–November; Summer without a date.</summary>
    public static string SeasonOf(string? missionDate) =>
        DateTime.TryParseExact(missionDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
            ? d.Month switch { 12 or 1 or 2 => "Winter", >= 3 and <= 5 => "Spring", >= 6 and <= 8 => "Summer", _ => "Autumn" }
            : "Summer";

    private static readonly Dictionary<(string, string), LandTextures?> Cache = [];

    [GeneratedRegex(@"^land_(\d+)_(-?\d+)_(-?\d+)\.png\.dds$", RegexOptions.IgnoreCase)]
    private static partial Regex TileName();

    /// <summary>The terrain's land texture for a season (falls back to Summer), or null when the
    /// install has none.</summary>
    public static LandTextures? For(string terrainId, string season)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((terrainId, season), out var cached)) return cached;
            LandTextures? set = null;
            try
            {
                if (DcsInstall.Find() is { } dcs)
                {
                    var dir = System.IO.Path.Combine(dcs, "Mods", "terrains", terrainId, "vfstextures");
                    var path = new[] { season, "Summer" }.Select(s => System.IO.Path.Combine(dir, $"LandTextures{s}.zip")).FirstOrDefault(File.Exists);
                    if (path is not null) set = Open(path);
                }
            }
            catch (Exception)
            {
                set = null;  // unknown layout: no land texture rather than a crash
            }
            Cache[(terrainId, season)] = set;
            return set;
        }
    }

    private static LandTextures? Open(string path)
    {
        var zip = ZipFile.OpenRead(path);
        var tiles = new HashSet<(string, int, int)>();
        foreach (var e in zip.Entries)
            if (TileName().Match(e.Name) is { Success: true } m)
                tiles.Add((m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
        if (tiles.Count == 0)
        {
            zip.Dispose();
            return null;
        }
        // The level names are nominal metres per pixel ("39", "156"): their ratio is the factor.
        var names = tiles.Select(t => t.Item1).Distinct().OrderByDescending(int.Parse).ToList();
        var finest = int.Parse(names[^1]);
        var levels = names.Select(n =>
        {
            var mine = tiles.Where(t => t.Item1 == n).ToList();
            return new Level(n, (int)Math.Round(int.Parse(n) / (double)finest),
                mine.Min(t => t.Item2), mine.Max(t => t.Item2), mine.Min(t => t.Item3), mine.Max(t => t.Item3));
        }).ToList();
        return new LandTextures(path, System.IO.Path.GetFileNameWithoutExtension(path)["LandTextures".Length..], zip, tiles, levels);
    }
}
