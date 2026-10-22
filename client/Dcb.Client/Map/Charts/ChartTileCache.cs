using System.IO;
using System.IO.Compression;
using SkiaSharp;

namespace Dcb.Client.Map.Charts;

/// <summary>Decoded map tiles (DCS Map, satellite, charts, web tiles, diagram pages) shared by
/// every map window: decoded on worker threads into raster Skia images, least-recently-used
/// tiles evicted beyond a memory budget. Requests are served newest first, and a request no
/// map has repeated for <see cref="StaleAfter"/> is dropped: after a zoom, pan or base-map
/// switch the tiles on screen now come first instead of queueing behind ones no longer shown.</summary>
public static class TileCache
{
    private const long BudgetBytes = 512L * 1024 * 1024;
    /// <summary>Maps ask again for every missing tile on each frame they draw.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(1.5);
    private static readonly Dictionary<string, LinkedListNode<Entry>> Map = [];
    private static readonly LinkedList<Entry> Lru = [];
    private static long _bytes;

    private sealed record Entry(string Key, SKImage Image, long Bytes);

    /// <summary>A tile waiting to be loaded; <see cref="Asked"/> is refreshed by every request.</summary>
    private sealed class Job(string key, Func<SKImage> load)
    {
        public string Key { get; } = key;
        public Func<SKImage> Load { get; } = load;
        public DateTime Asked = DateTime.UtcNow;
        public LinkedListNode<Job>? Node;
    }

    private static readonly Dictionary<string, Job> Pending = [];
    private static readonly LinkedList<Job> Queue = [];  // newest request first

    static TileCache()
    {
        for (int k = 0; k < Math.Clamp(Environment.ProcessorCount / 2, 2, 8); k++)
            new Thread(Work) { IsBackground = true, Name = $"TileCache {k}" }.Start();
    }

    /// <summary>Raised on a worker thread when a requested tile becomes available, or when stale
    /// requests were dropped (maps redraw and ask again for what they still show).</summary>
    public static event Action? TileLoaded;

    /// <summary>The tile for <paramref name="key"/> if it is loaded; never loads.</summary>
    public static SKImage? Peek(string key)
    {
        lock (Map) return Map.TryGetValue(key, out var node) ? node.Value.Image : null;
    }

    /// <summary>The tile for <paramref name="key"/>, or null after queueing <paramref name="load"/>
    /// (run on a worker; it returns a decoded raster image, or throws to leave the tile blank).
    /// Asking again for a waiting tile moves it to the front.</summary>
    public static SKImage? Get(string key, Func<SKImage> load)
    {
        lock (Map)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                return node.Value.Image;
            }
            if (Pending.TryGetValue(key, out var job))
            {
                job.Asked = DateTime.UtcNow;
                if (job.Node is { } n && n != Queue.First)
                {
                    Queue.Remove(n);
                    Queue.AddFirst(n);
                }
                return null;
            }
            job = new Job(key, load);
            job.Node = Queue.AddFirst(job);
            Pending[key] = job;
            Monitor.Pulse(Map);
        }
        return null;
    }

    private static void Work()
    {
        while (true)
        {
            Job? job = null;
            var dropped = false;
            lock (Map)
            {
                // The newest request still wanted; stale ones on the way are dropped.
                while (job is null)
                {
                    if (Queue.First is not { } first)
                    {
                        if (dropped) break;
                        Monitor.Wait(Map);
                        continue;
                    }
                    Queue.RemoveFirst();
                    first.Value.Node = null;
                    if (DateTime.UtcNow - first.Value.Asked > StaleAfter)
                    {
                        Pending.Remove(first.Value.Key);  // no map has asked for it lately
                        dropped = true;
                        continue;
                    }
                    job = first.Value;
                }
            }
            // Dropping may have removed a tile a map still shows but has not redrawn since:
            // a redraw asks again for whatever is still on screen.
            if (dropped) TileLoaded?.Invoke();
            if (job is null) continue;
            try
            {
                var image = job.Load();
                long bytes = (long)image.Width * image.Height * 4;
                lock (Map)
                {
                    Map[job.Key] = Lru.AddFirst(new Entry(job.Key, image, bytes));
                    _bytes += bytes;
                    while (_bytes > BudgetBytes && Lru.Last is { } last)
                    {
                        Lru.RemoveLast();
                        Map.Remove(last.Value.Key);
                        _bytes -= last.Value.Bytes;
                    }
                }
            }
            catch (Exception)
            {
                // A damaged or unexpected tile is left blank; the rest of the map still draws.
            }
            finally
            {
                lock (Map) Pending.Remove(job.Key);
            }
            TileLoaded?.Invoke();
        }
    }
}

/// <summary>DCS raster chart tiles read from the local install.</summary>
public static class ChartTileCache
{
    public static SkiaSharp.SKImage? Get(ChartTile tile, int mip) =>
        TileCache.Get($"chart:{tile.Name}:{mip}", () =>
        {
            var (bgra, w, h) = Decode(tile, mip);
            return Gpu.SkDraw.FromBgra(bgra, w, h);
        });

    private static (byte[] Bgra, int W, int H) Decode(ChartTile tile, int mip)
    {
        byte[] file;
        using (var archive = ZipFile.OpenRead(tile.ZipPath))
        {
            var entry = archive.GetEntry(tile.EntryName) ?? throw new FileNotFoundException(tile.EntryName);
            using var s = entry.Open();
            using var ms = new MemoryStream((int)entry.Length);
            s.CopyTo(ms);
            file = ms.ToArray();
        }
        return Dds.DecodeDxt5(file, mip);
    }
}
