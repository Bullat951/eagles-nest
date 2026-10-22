using System.IO;
using System.Net.Http;
using Dcb.Client.Map.Charts;
using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>Online web-Mercator tiles (Esri World Imagery), cached on disk under
/// %AppData%\DCB\tiles\&lt;name&gt;\z\x\y. At most two downloads at a time and an identifying
/// User-Agent. While a tile loads, the nearest cached ancestor is drawn scaled up in its place.</summary>
public sealed class WebTilesSk : MapLayer
{
    private const double WorldHalf = 20037508.342789244;
    private const int TilePixels = 256;
    private const int MaxTiles = 200;
    private static readonly HttpClient Http = CreateClient();

    private readonly string _name, _url, _ext;
    private readonly int _maxZoom;

    public static WebTilesSk EsriImagery() =>
        new("web-esri", "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", "jpg", 19);

    private WebTilesSk(string name, string url, string ext, int maxZoom)
    {
        _name = name;
        _url = url;
        _ext = ext;
        _maxZoom = maxZoom;
        TileCache.TileLoaded += Invalidate;
        Downloader.Downloaded += Invalidate;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(TileBaseMaps.UserAgent);
        return c;
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        var unitsPerPx = view.UnitsPerDip / frame.PixelsPerDip;
        var z = Math.Clamp((int)Math.Round(Math.Log2(2 * WorldHalf / TilePixels / unitsPerPx)), 0, _maxZoom);
        var n = 1 << z;
        var size = 2 * WorldHalf / n;
        var (minX, minY, maxX, maxY) = view.WorldBounds;
        int x0 = Math.Max(0, (int)Math.Floor((minX + WorldHalf) / size)), x1 = Math.Min(n - 1, (int)Math.Floor((maxX + WorldHalf) / size));
        int y0 = Math.Max(0, (int)Math.Floor((WorldHalf - maxY) / size)), y1 = Math.Min(n - 1, (int)Math.Floor((WorldHalf - minY) / size));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxTiles) return;
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            var (ax, ay) = view.ToScreen(x * size - WorldHalf, WorldHalf - y * size);
            var (bx, by) = view.ToScreen((x + 1) * size - WorldHalf, WorldHalf - (y + 1) * size);
            var dest = new SKRect((float)ax, (float)ay, (float)bx, (float)by);
            if (Tile(z, x, y, request: true) is { } image)
            {
                canvas.DrawImage(image, dest, Ink.Smooth, Ink.Image());
                continue;
            }
            // Still loading: the closest cached ancestor's matching part, scaled up.
            for (int up = 1; up <= 4 && z - up >= 0; up++)
            {
                if (Tile(z - up, x >> up, y >> up, request: false) is not { } parent) continue;
                var part = TilePixels >> up;
                var src = SKRect.Create((x & ((1 << up) - 1)) * part, (y & ((1 << up) - 1)) * part, part, part);
                canvas.DrawImage(parent, src, dest, Ink.Smooth, Ink.Image());
                break;
            }
        }
    }

    private SKImage? Tile(int z, int x, int y, bool request)
    {
        var key = $"{_name}:{z}:{x}:{y}";
        if (TileCache.Peek(key) is { } ready) return ready;
        var path = Path.Combine(AppSettings.Folder, "tiles", _name, z.ToString(), x.ToString(), $"{y}.{_ext}");
        if (Downloader.IsOnDisk(path)) return TileCache.Get(key, () => DcsMapSk.Decode(path));
        if (request) Downloader.Want(_url.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString()), path);
        return null;
    }

    /// <summary>Downloads on their own two async lanes, never holding a tile-decoding thread:
    /// newest request first, a request not repeated for a few seconds dropped (the view moved
    /// on), a failed tile left alone for 30 s.</summary>
    private static class Downloader
    {
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
        private static readonly object Lock = new();
        private static readonly Dictionary<string, (string Url, DateTime Asked)> Wanted = [];
        private static readonly HashSet<string> Running = [];
        private static readonly HashSet<string> OnDisk = [];
        private static readonly Dictionary<string, DateTime> Failed = [];
        private static int _lanes;

        public static event Action? Downloaded;

        public static bool IsOnDisk(string path)
        {
            lock (Lock)
                if (OnDisk.Contains(path)) return true;
            if (!File.Exists(path)) return false;
            lock (Lock) OnDisk.Add(path);
            return true;
        }

        public static void Want(string url, string path)
        {
            lock (Lock)
            {
                if (Running.Contains(path)) return;
                if (Failed.TryGetValue(path, out var at) && DateTime.UtcNow - at < RetryAfter) return;
                Wanted[path] = (url, DateTime.UtcNow);
                if (_lanes >= 2) return;
                _lanes++;
            }
            _ = Task.Run(Lane);
        }

        private static async Task Lane()
        {
            while (true)
            {
                string path, url;
                lock (Lock)
                {
                    // Drop what no map asked for lately, then take the newest request.
                    foreach (var stale in Wanted.Where(w => DateTime.UtcNow - w.Value.Asked > StaleAfter).Select(w => w.Key).ToList())
                        Wanted.Remove(stale);
                    if (Wanted.Count == 0)
                    {
                        _lanes--;
                        return;
                    }
                    var next = Wanted.MaxBy(w => w.Value.Asked);
                    (path, url) = (next.Key, next.Value.Url);
                    Wanted.Remove(path);
                    Running.Add(path);
                }
                try
                {
                    var bytes = await Http.GetByteArrayAsync(url);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes);
                    lock (Lock) OnDisk.Add(path);
                    Downloaded?.Invoke();
                }
                catch (Exception)
                {
                    lock (Lock) Failed[path] = DateTime.UtcNow;  // offline or refused: try again later
                }
                finally
                {
                    lock (Lock) Running.Remove(path);
                }
            }
        }
    }
}
