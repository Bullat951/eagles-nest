using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dcb.Client.Core;
using Dcb.Client.Map.Vector;

namespace Dcb.Client.Map;

/// <summary>The DCS Map as image tiles on disk, rendered from DCS's own data: the server's
/// terrain grid (relief, hill shading) coloured with DCS's F10 palette, the terrain's area
/// fills (surface5 land cover), and its road network, railways, rivers and power lines.
/// Tiles are 256 px on a DCS x/z grid at 2^L m/px (L = 1…10), north up; tile (tx, tz) of level
/// L covers x ∈ [tx·S, (tx+1)·S], z ∈ [tz·S, (tz+1)·S], S = 256·2^L. Stored as PNG under
/// %LocalAppData%\DCB\tiles\&lt;terrain&gt;\&lt;key&gt;\&lt;L&gt;\, the key covering every input, so a
/// DCS update or another terrain grid starts a fresh set. The overview levels (≥ 32 m/px)
/// are pre-rendered in the background once; finer tiles are rendered the first time they
/// are viewed. Road and area data are loaded only while tiles are being rendered.</summary>
public sealed class DcsMapTiles
{
    public const int TilePixels = 256;
    public const int FinestLevel = 1, CoarsestLevel = 10;
    /// <summary>Levels from here up are rendered in advance; finer ones on first view.</summary>
    public const int PrerenderFinestLevel = 5;
    /// <summary>Bump when the rendering changes, so old tiles are not reused.</summary>
    // r2: soft land-cover edges below 100 m/px. r3: sea from DCS's surface grid + height.
    // r4: land cover from the finest surface5 nodes only (no shifted coarse copies).
    // r5/r6: lake fixes. r7: surface5 nodes placed with their own header's translation
    // (were one node, mostly 8 km east, off).
    private const string RenderVersion = "r7";
    // Reloading the road and area data costs ~0.75 s before the first close-up tile; at 30 s
    // every zoom after a short pause paid it.
    private static readonly TimeSpan ReleaseAfterIdle = TimeSpan.FromMinutes(5);

    public static double MetersPerPixel(int level) => Math.Pow(2, level);
    public static double TileMeters(int level) => TilePixels * MetersPerPixel(level);

    public string Folder { get; }
    private readonly Theatre _theatre;
    private readonly TerrainGrid _grid;
    private readonly MapModePalette _palette;

    private readonly object _lock = new();
    private readonly LinkedList<(int L, int Tx, int Tz)> _urgent = [];
    private readonly Queue<(int L, int Tx, int Tz)> _background = new();
    private readonly HashSet<(int, int, int)> _queued = [];
    private readonly HashSet<(int, int, int)> _onDisk = [];
    /// <summary>Tiles a render thread is drawing now: asked again meanwhile, they are not queued
    /// a second time (each close-up tile was rendered ~2.4 times while the view waited).</summary>
    private readonly HashSet<(int, int, int)> _rendering = [];
    /// <summary>When each waiting close-up tile was last asked for: one no map has asked for in
    /// <see cref="StaleAfter"/> is dropped (the view moved, zoomed or switched base map).</summary>
    private readonly Dictionary<(int, int, int), DateTime> _asked = [];
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);
    private Sources? _sources;
    private DateTime _lastWork = DateTime.UtcNow;
    private int _backgroundTotal, _backgroundDone;
    private int _errors;

    /// <summary>Raised on a render thread when a tile has been written.</summary>
    public static event Action? TileReady;

    private DcsMapTiles(Theatre theatre, TerrainGrid grid, MapModePalette palette, string folder)
    {
        (_theatre, _grid, _palette, Folder) = (theatre, grid, palette, folder);
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories))
            if (Parse(file) is { } t) _onDisk.Add(t);
        QueueOverview();
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        for (int k = 0; k < threads; k++)
        {
            var t = new Thread(Work) { IsBackground = true, Name = $"DcsMapTiles {k}", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);  // WPF drawing (roads) and PNG encoding
            t.Start();
        }
    }

    // ---- Instance per theatre and inputs -------------------------------------------------------

    private static DcsMapTiles? _current;
    private static readonly object CurrentLock = new();

    /// <summary>The tile set for the theatre and the server's terrain grid; null when this PC has
    /// no DCS install with the terrain (no palette to colour with).</summary>
    public static DcsMapTiles? For(Theatre theatre, TerrainGrid grid)
    {
        lock (CurrentLock)
        {
            if (_current is { } c && ReferenceEquals(c._theatre, theatre) && ReferenceEquals(c._grid, grid)) return c;
            if (MapModePalette.For(theatre.Id) is not { } palette) return null;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCB", "tiles", theatre.Id);
            var folder = Path.Combine(root, Key(theatre, grid));
            // Older sets for this terrain (another DCS version, grid or renderer) are stale.
            if (Directory.Exists(root))
                foreach (var old in Directory.EnumerateDirectories(root))
                    if (!string.Equals(old, folder, StringComparison.OrdinalIgnoreCase))
                        try { Directory.Delete(old, recursive: true); } catch (IOException) { /* in use; next time */ }
            return _current = new DcsMapTiles(theatre, grid, palette, folder);
        }
    }

    /// <summary>Everything the pictures depend on: renderer version, terrain grid, and the DCS
    /// files read (road network, surface, F10 lines, palette) by size and date.</summary>
    private static string Key(Theatre theatre, TerrainGrid grid)
    {
        ulong h = 1469598103934665603;
        void Add(long v) { h ^= (ulong)v; h *= 1099511628211; }
        Add(grid.Nx); Add(grid.Nz); Add((long)grid.Step); Add((long)grid.X0); Add((long)grid.Z0);
        for (int i = 0; i < grid.Heights.Length; i += 97) Add(grid.Heights[i]);
        if (Charts.DcsInstall.Find() is { } dcs)
        {
            var t = Path.Combine(dcs, "Mods", "terrains", theatre.Id);
            foreach (var rel in new[] { Path.Combine("roads", theatre.Id + ".rn4"), Path.Combine("surface", theatre.Id + ".surface5"),
                                        Path.Combine("Map", theatre.Id + ".sup5"), Path.Combine("vfsTextures", "Map.zip") })
            {
                var f = new FileInfo(Path.Combine(t, rel));
                if (f.Exists) { Add(f.Length); Add(f.LastWriteTimeUtc.Ticks); }
            }
        }
        return $"{RenderVersion}_{h:x16}";
    }

    // ---- Requests ------------------------------------------------------------------------------

    public string PathOf(int level, int tx, int tz) => Path.Combine(Folder, level.ToString(), $"{tx}_{tz}.png");

    public bool Has(int level, int tx, int tz)
    {
        lock (_lock) return _onDisk.Contains((level, tx, tz));
    }

    /// <summary>Asks for a tile the view needs now; the latest requests are served first.</summary>
    public void Request(int level, int tx, int tz)
    {
        lock (_lock)
        {
            var key = (level, tx, tz);
            if (_onDisk.Contains(key) || _rendering.Contains(key)) return;  // done, or being drawn now
            _asked[key] = DateTime.UtcNow;
            if (_queued.Add(key))
            {
                _urgent.AddFirst(key);
                RenderStats.Count($"Tiles.requested L{level}");
            }
            else
            {
                // Already waiting (perhaps in the background queue): move it to the front.
                var node = _urgent.Find(key);
                if (node is not null) { _urgent.Remove(node); _urgent.AddFirst(node); }
                else _urgent.AddFirst(key);
            }
            // A view that moved on leaves old requests behind: keep the queue short.
            while (_urgent.Count > 400)
            {
                _queued.Remove(_urgent.Last!.Value);
                _asked.Remove(_urgent.Last.Value);
                _urgent.RemoveLast();
            }
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Loads the road and area data in the background if they are not loaded, so the
    /// first close-up tile does not wait for them (~0.8 s). Called while the view nears the
    /// levels rendered on demand.</summary>
    public void Warm()
    {
        lock (_lock)
        {
            if (_sources is not null || _warming) return;
            _warming = true;
            _lastWork = DateTime.UtcNow;  // not released straight away by an idle thread
        }
        Task.Run(() =>
        {
            try
            {
                LoadSources();
            }
            catch (Exception)
            {
                // The first tile render loads (and reports) them instead.
            }
            finally
            {
                lock (_lock) _warming = false;
            }
        });
    }

    private bool _warming;

    /// <summary>Pre-render progress (done, total) of the overview levels.</summary>
    public (int Done, int Total) Progress
    {
        get { lock (_lock) return (_backgroundDone, _backgroundTotal); }
    }

    private void QueueOverview()
    {
        for (int level = CoarsestLevel; level >= PrerenderFinestLevel; level--)
        {
            var s = TileMeters(level);
            for (int tx = (int)Math.Floor(_theatre.X0 / s); tx <= (int)Math.Floor(_theatre.X1 / s); tx++)
            for (int tz = (int)Math.Floor(_theatre.Z0 / s); tz <= (int)Math.Floor(_theatre.Z1 / s); tz++)
            {
                _backgroundTotal++;
                if (_onDisk.Contains((level, tx, tz))) { _backgroundDone++; continue; }
                _background.Enqueue((level, tx, tz));
            }
        }
    }

    private static (int, int, int)? Parse(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file).Split('_');
        return name.Length == 2 && int.TryParse(Path.GetFileName(Path.GetDirectoryName(file)), out var l) &&
               int.TryParse(name[0], out var tx) && int.TryParse(name[1], out var tz) ? (l, tx, tz) : null;
    }

    // ---- Render threads ------------------------------------------------------------------------

    private void Work()
    {
        while (true)
        {
            (int L, int Tx, int Tz) job;
            bool background;
            var dropped = false;
            lock (_lock)
            {
                while (true)
                {
                    if (_urgent.First is { } n)
                    {
                        _urgent.RemoveFirst();
                        if (!_asked.Remove(n.Value, out var asked) || DateTime.UtcNow - asked > StaleAfter)
                        {
                            _queued.Remove(n.Value);  // no longer on any map's screen
                            dropped = true;
                            continue;
                        }
                        job = n.Value;
                        background = false;
                        break;
                    }
                    if (_background.TryDequeue(out job))
                    {
                        if (_onDisk.Contains(job)) { _backgroundDone++; continue; }
                        background = true;
                        break;
                    }
                    if (dropped)
                    {
                        job = (-1, 0, 0);  // nothing left: first let the maps ask again (below)
                        background = false;
                        break;
                    }
                    // Nothing to do: drop the road and area data after a while.
                    if (_sources is not null && DateTime.UtcNow - _lastWork > ReleaseAfterIdle) _sources = null;
                    Monitor.Wait(_lock, 1000);
                }
                if (job.L >= 0 && (_onDisk.Contains(job) || !_rendering.Add(job))) { _queued.Remove(job); job = (-1, 0, 0); }
            }
            // Dropped requests may include tiles still on screen whose map has not redrawn since:
            // a redraw asks again for those.
            if (dropped) TileReady?.Invoke();
            if (job.L < 0) continue;
            try
            {
                Render(job.L, job.Tx, job.Tz, LoadSources());
            }
            catch (Exception e)
            {
                // A tile that cannot be rendered stays missing; the coarser level shows instead.
                // The first few failures are noted next to the tiles for diagnosis.
                if (Interlocked.Increment(ref _errors) <= 20)
                    try { File.AppendAllText(Path.Combine(Folder, "errors.log"), $"{DateTime.Now:s} L{job.L} {job.Tx}_{job.Tz}: {e}\n"); }
                    catch (IOException) { }
            }
            lock (_lock)
            {
                _queued.Remove(job);
                _rendering.Remove(job);
                _onDisk.Add(job);
                if (background) _backgroundDone++;
                _lastWork = DateTime.UtcNow;
            }
            TileReady?.Invoke();
        }
    }

    /// <summary>What a tile is drawn from, loaded on first need and released when idle.</summary>
    private sealed class Sources
    {
        public LandCover? Cover;
        public float[] Heights = [], Shade = [];
        /// <summary>Line segments (x0, z0, x1, z1) per class and 16 km DCS tile.</summary>
        public Dictionary<(string Class, int Bx, int Bz), float[]> Lines = [];
    }

    private const double BucketMeters = 16384;

    private readonly object _loadLock = new();

    private Sources LoadSources()
    {
        // One thread loads; the others wait for it rather than each building a copy.
        lock (_loadLock)
        {
            lock (_lock)
            {
                if (_sources is not null) return _sources;
            }
            var loaded = BuildSources();
            lock (_lock) return _sources = loaded;
        }
    }

    private Sources BuildSources()
    {
        using var perf = RenderStats.Measure("Tiles.Sources");
        var s = new Sources { Cover = LandCoverCache.Load(_theatre) };
        // Relief: heights and hill shade per terrain cell, bilinear between cells when drawn.
        var g = _grid;
        s.Heights = new float[g.Nx * g.Nz];
        s.Shade = new float[g.Nx * g.Nz];
        for (int i = 0; i < g.Nx; i++)
        for (int j = 0; j < g.Nz; j++)
        {
            s.Heights[i * g.Nz + j] = g.HeightAt(i, j);
            s.Shade[i * g.Nz + j] = (float)TerrainImage.ShadeAt(g, i, j);
        }
        // Lines: the road network when the terrain has one, otherwise the F10 file's roads.
        var lines = new Dictionary<(string, int, int), List<float>>();
        void Add(string cls, float x0, float z0, float x1, float z1)
        {
            var key = (cls, (int)Math.Floor(x0 / BucketMeters), (int)Math.Floor(z0 / BucketMeters));
            if (!lines.TryGetValue(key, out var list)) lines[key] = list = [];
            list.Add(x0); list.Add(z0); list.Add(x1); list.Add(z1);
        }
        var roads = DcsVectorMap.LoadRoads(_theatre.Id);
        if (roads is not null)
            foreach (var e in roads.Edges)
                for (int k = 0; k + 3 < e.Xz.Length; k += 2)
                    Add("rn4:" + e.Kind, e.Xz[k], e.Xz[k + 1], e.Xz[k + 2], e.Xz[k + 3]);
        if (DcsVectorMap.Sup5Path(_theatre.Id) is { } sup5)
        {
            string[] classes = roads is null ? ["River", "ELT", "Road", "Rail"] : ["River", "ELT"];
            foreach (var (cls, xz) in DcsVectorMap.ReadXz(File.ReadAllBytes(sup5), classes))
                for (int k = 0; k + 3 < xz.Count; k += 4) Add(cls, xz[k], xz[k + 1], xz[k + 2], xz[k + 3]);
        }
        s.Lines = lines.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        return s;
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    // Bottom → top: class, colour, width (px), dashed, coarsest ground m/px it is drawn at. Road
    // colours are DCS's F10 palette swatches (#9F2616, #DC9C48).
    private static readonly (string Class, Pen Pen, double MaxMetersPerPixel)[] LineStyles =
    [
        ("ELT", Pen(Color.FromRgb(0x9A, 0x4F, 0xB0), 0.8, dash: true), 200),
        ("River", Pen(Color.FromRgb(0x3C, 0x7F, 0xD0), 1.0, dash: false), 1000),
        ("rn4:Track", Pen(Color.FromRgb(0x8A, 0x76, 0x58), 0.7, dash: true), 75),
        ("rn4:Secondary", Pen(Color.FromRgb(0xDC, 0x9C, 0x48), 1.0, dash: false), 375),
        ("rn4:Primary", Pen(Color.FromRgb(0xDC, 0x9C, 0x48), 1.4, dash: false), 1250),
        ("rn4:Motorway", Pen(Color.FromRgb(0x9F, 0x26, 0x16), 2.2, dash: false), 2500),
        ("rn4:Rail", Pen(Color.FromRgb(0x30, 0x30, 0x30), 1.3, dash: true), 1250),
        ("Rail", Pen(Color.FromRgb(0x30, 0x30, 0x30), 1.2, dash: true), 1000),
        ("Road", Pen(Color.FromRgb(0xC8, 0x50, 0x28), 1.2, dash: false), 2000),
    ];

    private static Pen Pen(Color c, double width, bool dash)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        var p = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dash) p.DashStyle = new DashStyle([5, 3], 0);
        p.Freeze();
        return p;
    }

    private Color ClassColor(LandCover.Class cls) => cls switch
    {
        LandCover.Class.Field => WithOpacity(_palette.Classes[0], 0.55),
        LandCover.Class.Beach => Color.FromArgb(178, 0xE8, 0xD8, 0xA8),
        LandCover.Class.Trees => WithOpacity(_palette.Classes[10], 0.9),
        LandCover.Class.Town => WithOpacity(_palette.Classes[1], 0.85),
        _ => Colors.Transparent,
    };

    private static Color WithOpacity(Color c, double opacity) => Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B);

    private void Render(int level, int tx, int tz, Sources s)
    {
        using var perf = RenderStats.Measure(level < PrerenderFinestLevel ? "Tiles.Render(fine)" : "Tiles.Render(overview)");
        RenderStats.Count($"Tiles.rendered L{level}");
        var pixelsTimer = System.Diagnostics.Stopwatch.StartNew();
        var m = MetersPerPixel(level);
        var size = TileMeters(level);
        double xTop = (tx + 1) * size, zLeft = tz * size;
        var g = _grid;
        // Plain integer colours: WPF's Color recomputes float channels on every operation, and
        // that (with a closure per pixel) made a tile cost ~95 ms.
        var water = Rgb.Of(_palette.Water);
        var classColors = new Rgb[8];
        var classAlpha = new int[8];
        for (int k = 0; k < 8; k++)
        {
            var cc = ClassColor((LandCover.Class)k);
            classColors[k] = Rgb.Of(cc);
            classAlpha[k] = cc.A;
        }
        var ramp = _palette.Ramp.Select(Rgb.Of).ToArray();
        var lc = s.Cover;

        // Relief and areas, per pixel.
        var px = new byte[TilePixels * TilePixels * 4];
        for (int r = 0; r < TilePixels; r++)
        {
            var x = xTop - (r + 0.5) * m;
            var fi = (x - g.X0) / g.Step;
            for (int c = 0; c < TilePixels; c++)
            {
                var z = zLeft + (c + 0.5) * m;
                var fj = (z - g.Z0) / g.Step;
                Rgb color;
                if (fi < 0 || fj < 0 || fi > g.Nx - 1 || fj > g.Nz - 1)
                {
                    color = water;  // beyond the theatre's terrain: open sea
                }
                else
                {
                    var h = Bilinear(s.Heights, g, fi, fj);
                    var row = (int)Math.Round((ramp.Length - 1) * (1 - Math.Clamp(h / MapModePalette.RampTopMeters, 0, 1)));
                    var land = new LandColors(ramp[row], Bilinear(s.Shade, g, fi, fj),
                        // Sea: DCS's surface grid says water around here and the ground is at sea
                        // level — the coastline follows the height data smoothly and matches what
                        // DCS calls water. Lakes: the lake meshes.
                        h < 1 && NearWater(g, fi, fj), water, classColors, classAlpha);
                    if (lc is null) color = land.Of(LandCover.Class.None);
                    else if (m >= 200) color = land.Of(lc.MajorityIn(x, z, m));
                    else if (m >= lc.Cell) color = land.Of(lc.At(x, z));
                    else color = Blend(lc, x, z, land);  // finer than the cells: soft area edges
                }
                var o = (r * TilePixels + c) * 4;
                px[o] = (byte)color.B;
                px[o + 1] = (byte)color.G;
                px[o + 2] = (byte)color.R;
                px[o + 3] = 255;
            }
        }
        RenderStats.Add("Tiles.Pixels", pixelsTimer.Elapsed.TotalMilliseconds);
        var linesTimer = System.Diagnostics.Stopwatch.StartNew();
        var relief = BitmapSource.Create(TilePixels, TilePixels, 96, 96, PixelFormats.Bgra32, null, px, TilePixels * 4);

        // Lines, in tile pixels (with a margin for pen widths crossing the edge).
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(relief, new Rect(0, 0, TilePixels, TilePixels));
            var margin = 4 * m;
            int bx0 = (int)Math.Floor((xTop - size - margin) / BucketMeters), bx1 = (int)Math.Floor((xTop + margin) / BucketMeters);
            int bz0 = (int)Math.Floor((zLeft - margin) / BucketMeters), bz1 = (int)Math.Floor((zLeft + size + margin) / BucketMeters);
            foreach (var (cls, pen, maxM) in LineStyles)
            {
                if (m > maxM) continue;
                var geometry = new StreamGeometry();
                var any = false;
                using (var ctx = geometry.Open())
                {
                    // Segments start in their bucket but may reach one bucket further.
                    for (int bx = bx0 - 1; bx <= bx1; bx++)
                    for (int bz = bz0 - 1; bz <= bz1; bz++)
                    {
                        if (!s.Lines.TryGetValue((cls, bx, bz), out var seg)) continue;
                        for (int k = 0; k + 3 < seg.Length; k += 4)
                        {
                            double ac = (seg[k + 1] - zLeft) / m, ar = (xTop - seg[k]) / m;
                            double bc = (seg[k + 3] - zLeft) / m, br = (xTop - seg[k + 2]) / m;
                            if ((ac < -4 && bc < -4) || (ac > TilePixels + 4 && bc > TilePixels + 4) ||
                                (ar < -4 && br < -4) || (ar > TilePixels + 4 && br > TilePixels + 4)) continue;
                            if (Math.Abs(bc - ac) < 0.3 && Math.Abs(br - ar) < 0.3) continue;  // sub-pixel
                            ctx.BeginFigure(new Point(ac, ar), false, false);
                            ctx.LineTo(new Point(bc, br), true, false);
                            any = true;
                        }
                    }
                }
                if (!any) continue;
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }
        var bitmap = new RenderTargetBitmap(TilePixels, TilePixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        RenderStats.Add("Tiles.Lines", linesTimer.Elapsed.TotalMilliseconds);
        var saveTimer = System.Diagnostics.Stopwatch.StartNew();

        var path = PathOf(level, tx, tz);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Unique per process and thread: two clients may fill the same tile folder at once.
        var temp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        using (var f = File.Create(temp))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(f);
        }
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (UnauthorizedAccessException) when (File.Exists(path))
        {
            File.Delete(temp);  // another client wrote (and may be reading) the same tile
        }
        RenderStats.Add("Tiles.Save", saveTimer.Elapsed.TotalMilliseconds);
    }

    /// <summary>Any of the four terrain cells around a point is water (DCS's surface grid).</summary>
    private static bool NearWater(TerrainGrid g, double fi, double fj)
    {
        int i = Math.Min((int)fi, g.Nx - 2), j = Math.Min((int)fj, g.Nz - 2);
        for (int di = 0; di <= 1; di++)
        for (int dj = 0; dj <= 1; dj++)
            if (g.SurfaceAt(i + di, j + dj) is Surface.Water or Surface.ShallowWater) return true;
        return false;
    }

    /// <summary>An opaque colour as plain integers (0–255).</summary>
    private readonly record struct Rgb(int R, int G, int B)
    {
        public static Rgb Of(Color c) => new(c.R, c.G, c.B);
    }

    /// <summary>The colour of one ground point for each land-cover class: water for lakes and the
    /// sea, otherwise the class swatch laid over the height tint, then hill-shaded.</summary>
    private readonly struct LandColors(Rgb tint, double shade, bool sea, Rgb water, Rgb[] classColors, int[] classAlpha)
    {
        public Rgb Of(LandCover.Class cls)
        {
            if (cls == LandCover.Class.Water || sea) return water;
            var k = (int)cls;
            if (cls is LandCover.Class.None or LandCover.Class.Sea || classAlpha[k] == 0) return Shade(tint);
            int a = classAlpha[k], top = 255 - a;
            var over = new Rgb((classColors[k].R * a + tint.R * top) / 255, (classColors[k].G * a + tint.G * top) / 255, (classColors[k].B * a + tint.B * top) / 255);
            return Shade(over);
        }

        private Rgb Shade(Rgb c) =>
            new(Math.Min(255, (int)(c.R * shade)), Math.Min(255, (int)(c.G * shade)), Math.Min(255, (int)(c.B * shade)));
    }

    /// <summary>Colour at (x, z) blended bilinearly from the four nearest land-cover cells, each
    /// coloured by its class: area edges read soft instead of 100 m squares.</summary>
    private static Rgb Blend(LandCover lc, double x, double z, in LandColors land)
    {
        double fi = (x - lc.X0) / lc.Cell - 0.5, fj = (z - lc.Z0) / lc.Cell - 0.5;
        int i0 = (int)Math.Floor(fi), j0 = (int)Math.Floor(fj);
        double ti = fi - i0, tj = fj - j0;
        double r = 0, gr = 0, b = 0;
        for (int di = 0; di <= 1; di++)
        for (int dj = 0; dj <= 1; dj++)
        {
            int i = Math.Clamp(i0 + di, 0, lc.Nx - 1), j = Math.Clamp(j0 + dj, 0, lc.Nz - 1);
            var w = (di == 0 ? 1 - ti : ti) * (dj == 0 ? 1 - tj : tj);
            var c = land.Of(lc.At(i, j));
            r += c.R * w; gr += c.G * w; b += c.B * w;
        }
        return new Rgb((int)r, (int)gr, (int)b);
    }

    private static float Bilinear(float[] v, TerrainGrid g, double fi, double fj)
    {
        int i = Math.Min((int)fi, g.Nx - 2), j = Math.Min((int)fj, g.Nz - 2);
        double ti = fi - i, tj = fj - j;
        return (float)((v[i * g.Nz + j] * (1 - tj) + v[i * g.Nz + j + 1] * tj) * (1 - ti) +
                       (v[(i + 1) * g.Nz + j] * (1 - tj) + v[(i + 1) * g.Nz + j + 1] * tj) * ti);
    }
}
