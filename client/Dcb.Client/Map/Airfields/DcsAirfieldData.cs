using System.IO;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Map.Airfields;

/// <summary>Airfield data read from the local DCS install for the ATC diagram: pavement from the
/// terrain mesh (cached on disk, ~1 s to extract for Syria) and the taxiway signs DCS places
/// (scene file, indexed in memory). Loaded in the background on first use per terrain; local
/// only (ED data, never exported).</summary>
public static class DcsAirfieldData
{
    private sealed class Terrain
    {
        public Pavement? Pavement;
        public AirfieldSigns? Signs;
        public IReadOnlyList<Beacon> Beacons = [];
        public IReadOnlyList<IcaoMatch.Airport> Airports = [];
        public readonly Dictionary<string, TaxiwayGraph?> Graphs = new(StringComparer.OrdinalIgnoreCase);
        public string? TaxiwayDir;
        /// <summary>Each rn4's taxiway extent (DCS metres), for fields not named like their file.</summary>
        public List<(string File, double MinX, double MaxX, double MinZ, double MaxZ)>? Extents;
        public bool Done;
    }

    /// <summary>The navaids DCS lists for an airfield (beacons.lua), empty while loading.</summary>
    public static IReadOnlyList<Beacon> BeaconsFor(string terrainId, int airfieldId) =>
        Get(terrainId)?.Beacons.Where(b => b.AirfieldId == airfieldId).ToList() ?? [];

    /// <summary>The field's taxiway network (its rn4), or null without one. The file is named after
    /// the field (Syria); where it is not (Caucasus: "Senaki" for Senaki-Kolkhi, "Lochini",
    /// "TbilisiMilitary" for Soganlug…), the file whose network spans the field's runway centre.</summary>
    public static TaxiwayGraph? GraphFor(string terrainId, string airfieldName, double x, double z)
    {
        if (Get(terrainId) is not { TaxiwayDir: { } dir } t) return null;
        lock (t.Graphs)
        {
            if (t.Graphs.TryGetValue(airfieldName, out var g)) return g;
            var file = Path.Combine(dir, airfieldName.Replace(' ', '_') + ".rn4");
            if (!File.Exists(file)) file = NetworkAt(t, dir, x, z) ?? file;
            try { g = File.Exists(file) ? TaxiwayGraph.Decode(File.ReadAllBytes(file)) : null; }
            catch (Exception) { g = null; }
            t.Graphs[airfieldName] = g;
            return g;
        }
    }

    /// <summary>The rn4 whose taxiway lines' extent holds the point (the nearest centre if several).</summary>
    private static string? NetworkAt(Terrain t, string dir, double x, double z)
    {
        if (t.Extents is null)
        {
            t.Extents = [];
            foreach (var f in Directory.EnumerateFiles(dir, "*.rn4"))
            {
                try
                {
                    var pts = Taxiways.NearField(Taxiways.Polylines(File.ReadAllBytes(f))).SelectMany(l => l).ToList();
                    if (pts.Count > 0) t.Extents.Add((f, pts.Min(p => p.X), pts.Max(p => p.X), pts.Min(p => p.Z), pts.Max(p => p.Z)));
                }
                catch (Exception)
                {
                    // An unreadable file: that field has no network.
                }
            }
        }
        return t.Extents.Where(e => x >= e.MinX && x <= e.MaxX && z >= e.MinZ && z <= e.MaxZ)
            .OrderBy(e => Math.Pow((e.MinX + e.MaxX) / 2 - x, 2) + Math.Pow((e.MinZ + e.MaxZ) / 2 - z, 2))
            .Select(e => e.File).FirstOrDefault();
    }

    /// <summary>The control tower nearest to a point within 3 km (scene objects), if any.</summary>
    public static (double X, double Z)? TowerNear(string terrainId, double x, double z)
    {
        if (Get(terrainId)?.Signs is not { } signs) return null;
        lock (signs)
        {
            var towers = signs.Near(x, z, 3000).Where(s => s.Kind == AirfieldSigns.Kind.Tower).ToList();
            if (towers.Count == 0) return null;
            var t = towers.MinBy(s => (s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z));
            return (t.X, t.Z);
        }
    }

    /// <summary>Raw location-sign boards near a point (letter+digit panels joined, not merged
    /// across the taxiway): each votes for its taxiway leg.</summary>
    public static IReadOnlyList<AirfieldSigns.Sign>? SignBoardsNear(string terrainId, double x, double z, double radius)
    {
        if (Get(terrainId)?.Signs is not { } signs) return null;
        lock (signs) return AirfieldSigns.TaxiwayLetters(signs.Near(x, z, radius), mergeM: 0.1);
    }

    /// <summary>The real-world identifier for a field from the C-130J's nav database (the codes
    /// its FMS uses), or null when the module is not installed or nothing matches.</summary>
    public static string? IcaoFor(string terrainId, double lat, double lon, IReadOnlyList<double> runwayHeadingsTrue) =>
        Get(terrainId) is { Airports.Count: > 0 } t ? IcaoMatch.For(lat, lon, runwayHeadingsTrue, t.Airports) : null;

    private static readonly Dictionary<string, Terrain> Terrains = [];

    /// <summary>Raised on a worker thread when a terrain's data finished loading.</summary>
    public static event Action? Loaded;

    /// <summary>The terrain's pavement, or null while loading (starts on first call) or unavailable.</summary>
    public static Pavement? PavementFor(string terrainId) => Get(terrainId)?.Pavement;

    /// <summary>DCS's taxiway letters near a point (location signs, facing pairs merged), or
    /// null while loading or when this PC has no scene file for the terrain.</summary>
    public static IReadOnlyList<AirfieldSigns.Sign>? TaxiwayLettersNear(string terrainId, double x, double z, double radius)
    {
        if (Get(terrainId)?.Signs is not { } signs) return null;
        lock (signs) return AirfieldSigns.TaxiwayLetters(signs.Near(x, z, radius));
    }

    /// <summary>True once loading finished (with or without data).</summary>
    public static bool IsLoaded(string terrainId) => Get(terrainId)?.Done == true;

    private static Terrain? Get(string id)
    {
        lock (Terrains)
        {
            if (Terrains.TryGetValue(id, out var t)) return t.Done ? t : null;
            Terrains[id] = t = new Terrain();
            Task.Run(() =>
            {
                t.Pavement = LoadPavement(id);
                var dcs = DcsInstall.Find();
                var terrainDir = dcs is null ? null : Path.Combine(dcs, "Mods", "terrains", id);
                try
                {
                    if (terrainDir is not null && Path.Combine(terrainDir, "Scenes", id + ".scn5") is var scene && File.Exists(scene))
                        t.Signs = AirfieldSigns.Open(scene);
                }
                catch (Exception)
                {
                    // Unknown scene layout: chart letters only.
                }
                try
                {
                    if (terrainDir is not null && Path.Combine(terrainDir, "beacons.lua") is var lua && File.Exists(lua))
                        t.Beacons = Beacon.Parse(File.ReadAllText(lua));
                }
                catch (Exception)
                {
                    // No navaids on the diagram.
                }
                if (terrainDir is not null && Directory.Exists(Path.Combine(terrainDir, "AirfieldsTaxiways"))) t.TaxiwayDir = Path.Combine(terrainDir, "AirfieldsTaxiways");
                if (dcs is not null) t.Airports = NavDatabase.Airports(dcs);
                t.Done = true;
                Loaded?.Invoke();
            });
            return null;
        }
    }

    private static Pavement? LoadPavement(string id)
    {
        try
        {
            if (LandCoverCache.SurfacePath(id) is not { } surface) return null;
            var info = new FileInfo(surface);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCB", "cache");
            // v2: Caucasus pavement (OldRunway5.1, 16 km finest nodes) was extracted empty in v1.
            var cache = Path.Combine(dir, $"pavement_{id}_{info.Length}_{info.LastWriteTimeUtc.Ticks}_v2.bin");
            if (File.Exists(cache))
            {
                try
                {
                    using var c = File.OpenRead(cache);
                    return Pavement.Load(c);
                }
                catch (Exception)
                {
                    // Damaged cache: extract again below.
                }
            }
            var p = Pavement.Extract(surface);
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.EnumerateFiles(dir, $"pavement_{id}_*.bin")) File.Delete(old);
            using (var c = File.Create(cache)) p.Save(c);
            return p;
        }
        catch (Exception)
        {
            return null;  // unknown terrain file layout: the diagram without pavement
        }
    }
}
