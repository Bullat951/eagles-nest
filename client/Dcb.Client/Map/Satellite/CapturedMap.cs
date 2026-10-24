using System.IO;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Dcb.Client.Map.Satellite;

/// <summary>A terrain's map captured from DCS's own Sat view (tools/MapCapture): one "&lt;terrain&gt;.ensat"
/// file (SQLite), tiles of 256 px on the DCS x/z grid at 2^L m/px, row 0 north, column 0 west —
/// the DcsMapTiles convention. Looked for in the app folder's "maps" and %LocalAppData%\DCB\maps.
/// The tile index is read once; tiles are read and decoded on the tile cache's worker.</summary>
public sealed class CapturedMap
{
    public string Path { get; }
    public int FinestLevel { get; }
    private readonly HashSet<(int, int, int)> _tiles;
    private readonly string _connection;

    private CapturedMap(string path, int finest, HashSet<(int, int, int)> tiles)
    {
        (Path, FinestLevel, _tiles) = (path, finest, tiles);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
    }

    private static readonly Dictionary<string, (CapturedMap? Map, DateTime Checked)> Opened = [];
    private static readonly TimeSpan RecheckMissing = TimeSpan.FromSeconds(10);

    /// <summary>The captured map of a terrain, or null when no file for it is installed. A missing
    /// file is looked for again every few seconds, so one installed while DCB runs is picked up.</summary>
    public static CapturedMap? For(string terrainId)
    {
        lock (Opened)
        {
            if (Opened.TryGetValue(terrainId, out var e) && (e.Map is not null || DateTime.UtcNow - e.Checked < RecheckMissing)) return e.Map;
            var map = Open(terrainId);
            Opened[terrainId] = (map, DateTime.UtcNow);
            return map;
        }
    }

    private static CapturedMap? Open(string terrainId)
    {
        var name = terrainId + ".ensat";
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "maps", name),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCB", "maps", name),
        };
        if (candidates.FirstOrDefault(File.Exists) is not { } path)
        {
            if (!Opened.ContainsKey(terrainId)) Log($"{terrainId}: no map file ({string.Join(" | ", candidates)})");
            return null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var tiles = new HashSet<(int, int, int)>();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT level, tx, tz FROM tiles";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tiles.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
            }
            var finest = tiles.Count > 0 ? tiles.Min(t => t.Item1) : 1;
            Log($"{terrainId}: opened {path}, {tiles.Count} tiles, finest level {finest}, {sw.ElapsedMilliseconds} ms");
            return new CapturedMap(path, finest, tiles);
        }
        catch (Exception e)
        {
            Log($"{terrainId}: cannot open {path}: {e.GetType().Name}: {e.Message}");
            return null;  // damaged or not a tile file: drawn without it
        }
    }

    private static readonly string LogPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCB", "captured-map.log");

    /// <summary>Diagnostics: %LocalAppData%\DCB\captured-map.log (which file was opened, or why none).</summary>
    private static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    public bool Has(int level, int tx, int tz) => _tiles.Contains((level, tx, tz));

    public string Key(int level, int tx, int tz) => $"ensat:{Path}:{level}:{tx}:{tz}";

    /// <summary>Reads and decodes one tile (called on the cache's worker).</summary>
    public SKImage Load(int level, int tx, int tz)
    {
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT data FROM tiles WHERE level = $l AND tx = $x AND tz = $z";
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$x", tx);
        cmd.Parameters.AddWithValue("$z", tz);
        var bytes = cmd.ExecuteScalar() as byte[] ?? throw new InvalidDataException("missing tile");
        using var encoded = SKImage.FromEncodedData(bytes) ?? throw new InvalidDataException("bad tile");
        return encoded.ToRasterImage(ensurePixelData: true);
    }
}
