using System.IO;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;
using Dcb.Client.Map.Satellite;
using Dcb.Client.Map.Vector;
using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>Scope base map lines (Natural Earth coast, borders, lakes) on the dark background.</summary>
public sealed class ScopeLinesSk : MapLayer
{
    private static readonly Ink.Pen Coast = new(MapStyle.Color("scope.coast.color"), MapStyle.Number("scope.coast.width"));
    private static readonly Ink.Pen Lakes = new(MapStyle.Color("scope.lakes.color"), MapStyle.Number("scope.lakes.width"));
    private static readonly Ink.Pen Borders = new(MapStyle.Color("scope.borders.color"), MapStyle.Number("scope.borders.width"), MapStyle.Dash("scope.borders.dash"));

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var data = ScopeBaseMapData.Instance;
        Lines(canvas, frame.View, data.Lakes, Lakes);
        Lines(canvas, frame.View, data.Borders, Borders);
        Lines(canvas, frame.View, data.Coast, Coast);
    }

    private static void Lines(SKCanvas canvas, MapView view, IReadOnlyList<ScopeBaseMapData.Line> lines, Ink.Pen pen)
    {
        var (minX, minY, maxX, maxY) = view.WorldBounds;
        var area = SkClip.Area(view);
        using var path = new SKPath();
        var builder = new VisibleRuns(path, area);
        foreach (var line in lines)
        {
            if (line.MaxX < minX || line.MinX > maxX || line.MaxY < minY || line.MinY > maxY) continue;
            var (px, py) = view.ToScreen(line.Xy[0], line.Xy[1]);
            builder.Start(new SKPoint((float)px, (float)py));
            for (int i = 2; i < line.Xy.Length; i += 2)
            {
                var (x, y) = view.ToScreen(line.Xy[i], line.Xy[i + 1]);
                // Skip sub-pixel steps: zoomed out, most vertices collapse together.
                if (Math.Abs(x - px) < 0.75 && Math.Abs(y - py) < 0.75 && i < line.Xy.Length - 2) continue;
                builder.To(new SKPoint((float)x, (float)y));
                (px, py) = (x, y);
            }
        }
        canvas.DrawPath(path, pen.Paint());
    }
}

/// <summary>Adds only the parts of polylines inside an area to a path, segments clipped to it.
/// A path reaching far outside the device's ±32k pixel range can be dropped whole by Skia's
/// rasteriser (a coastline is thousands of kilometres long), so nothing off-view goes in.</summary>
public struct VisibleRuns(SKPath path, SKRect area)
{
    private SKPoint _last;
    private bool _open;

    public void Start(SKPoint p)
    {
        _last = p;
        _open = false;
    }

    public void To(SKPoint p)
    {
        if (SkClip.ClipSegment(_last, p, area, out var t0, out var t1))
        {
            var d = p - _last;
            var a = _last + new SKPoint(d.X * t0, d.Y * t0);
            var b = _last + new SKPoint(d.X * t1, d.Y * t1);
            if (!_open || t0 > 0) path.MoveTo(a);
            path.LineTo(b);
            _open = t1 >= 1;  // the run continues only if the segment ended inside
        }
        else _open = false;
        _last = p;
    }
}

/// <summary>"DCS Map": the image tiles of <see cref="DcsMapTiles"/>, the level matching the
/// screen over a pre-rendered coarser one, missing tiles requested.</summary>
public sealed class DcsMapSk : MapLayer
{
    private const int MaxTiles = 200;

    public DcsMapSk()
    {
        TileCache.TileLoaded += Invalidate;
        DcsMapTiles.TileReady += Invalidate;
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var store = AppState.Store;
        var view = frame.View;
        if (store.Theatre is not { } theatre) return;
        if (store.Terrain is not { } grid || DcsMapTiles.For(theatre, grid) is not { } tiles)
        {
            Text.Note(canvas, view, store.Terrain is null ? "Waiting for the server's terrain…" : $"No DCS install with {theatre.Id} on this PC: no DCS Map.");
            return;
        }
        var groundPerPx = SkDraw.MetersPerDip(view, SkDraw.CenterLat(view)) / frame.PixelsPerDip;
        var level = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, groundPerPx))), DcsMapTiles.FinestLevel, DcsMapTiles.CoarsestLevel);
        var area = DcsDraw.ViewArea(view, theatre);
        // Two levels before tiles are rendered on demand, have their data ready.
        if (level < DcsMapTiles.PrerenderFinestLevel + 2) tiles.Warm();
        var under = Math.Clamp(level + 3, DcsMapTiles.PrerenderFinestLevel, DcsMapTiles.CoarsestLevel);
        if (under > level) DrawLevel(canvas, view, theatre, tiles, under, area, request: false);
        // While the wanted level loads, the nearest levels already in memory fill in, coarse to
        // fine — a zoom never drops to the blurry underlay for tiles it has just shown.
        for (int l = Math.Min(under - 1, level + 2); l > level; l--) DrawLevel(canvas, view, theatre, tiles, l, area, request: false, cachedOnly: true);
        if (_missingLast && level > DcsMapTiles.FinestLevel)
            DrawLevel(canvas, view, theatre, tiles, level - 1, area, request: false, cachedOnly: true);  // zooming out
        var missing = DrawLevel(canvas, view, theatre, tiles, level, area, request: true);
        _missingLast = missing > 0;
        LastMissing = missing;
        if (missing > 0) RenderStats.Count("DcsMap frames with tiles missing");
        var (done, total) = tiles.Progress;
        if (done < total) Text.Note(canvas, view, $"Rendering DCS map overview: {done * 100 / Math.Max(1, total)} % (one-time)");
    }

    private bool _missingLast;

    /// <summary>Tiles of the wanted level not yet ready in the last frame drawn (tests).</summary>
    public static volatile int LastMissing;

    /// <summary>Draws a level's tiles over the view; returns how many were not ready.
    /// <paramref name="cachedOnly"/>: only tiles already decoded, nothing queued.</summary>
    private static int DrawLevel(SKCanvas canvas, MapView view, Theatre theatre, DcsMapTiles tiles, int level,
        (double MinX, double MinZ, double MaxX, double MaxZ) area, bool request, bool cachedOnly = false)
    {
        var s = DcsMapTiles.TileMeters(level);
        int tx0 = (int)Math.Floor(area.MinX / s), tx1 = (int)Math.Floor(area.MaxX / s);
        int tz0 = (int)Math.Floor(area.MinZ / s), tz1 = (int)Math.Floor(area.MaxZ / s);
        int drawn = 0, missing = 0;
        for (int tx = tx1; tx >= tx0; tx--)
        for (int tz = tz0; tz <= tz1; tz++)
        {
            if (++drawn > MaxTiles) return missing;
            if (cachedOnly)
            {
                if (TileCache.Peek("dcsmap:" + tiles.PathOf(level, tx, tz)) is { } cached)
                    SkDraw.Warped(canvas, view, theatre, cached, (tx + 1) * s, tz * s, s, s);
                continue;
            }
            if (!tiles.Has(level, tx, tz))
            {
                if (request) tiles.Request(level, tx, tz);
                missing++;
                continue;
            }
            var path = tiles.PathOf(level, tx, tz);
            if (TileCache.Get("dcsmap:" + path, () => Decode(path)) is { } image)
                SkDraw.Warped(canvas, view, theatre, image, (tx + 1) * s, tz * s, s, s);
            else missing++;
        }
        return missing;
    }

    /// <summary>Decoded here, on the cache's worker: a lazily decoded image would decode on the
    /// UI thread at its first draw.</summary>
    public static SKImage Decode(string path)
    {
        using var encoded = SKImage.FromEncodedData(path) ?? throw new InvalidDataException(path);
        return encoded.ToRasterImage(ensurePixelData: true);
    }
}

/// <summary>MGRS grid from DCS's F10 vector file over the DCS Map. Each bucket's segments are a
/// cached path in bucket-local metres, drawn through the view transform.</summary>
public sealed class MgrsGridSk : MapLayer
{
    private const double MaxUnitsPerDip = 220;
    private static readonly SKColor GridColor = MapStyle.Color("baseMaps.mgrs.color");
    private static readonly float GridWidth = MapStyle.Number("baseMaps.mgrs.width");
    private readonly Dictionary<object, SKPath> _paths = new(ReferenceEqualityComparer.Instance);
    private DcsVectorMap? _map;
    private readonly SKPaint _paint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, Color = GridColor };

    public MgrsGridSk() => DcsVectorMap.Loaded += Invalidate;

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre || view.UnitsPerDip > MaxUnitsPerDip) return;
        if (DcsVectorMap.For(theatre, out _) is not { } map) return;
        if (!ReferenceEquals(map, _map))
        {
            foreach (var p in _paths.Values) p.Dispose();
            _paths.Clear();
            _map = map;
        }
        var (minX, minY, maxX, maxY) = view.WorldBounds;
        var upd = (float)view.UnitsPerDip;
        _paint.StrokeWidth = GridWidth * upd;  // in metres: drawn through the world transform
        var area = SkClip.Area(view);
        foreach (var b in map.Buckets)
        {
            if (b.MaxX < minX || b.MinX > maxX || b.MaxY < minY || b.MinY > maxY) continue;
            if (Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) / upd > 8000)
            {
                // Zoomed far in: the bucket would reach past Skia's ±32k pixel range; clip
                // its segments to the view instead.
                using var near = new SKPath();
                var s = b.Segments;
                for (int i = 0; i + 3 < s.Length; i += 4)
                {
                    var (ax, ay) = view.ToScreen(b.OriginX + s[i], b.OriginY + s[i + 1]);
                    var (bx, by) = view.ToScreen(b.OriginX + s[i + 2], b.OriginY + s[i + 3]);
                    SKPoint a = new((float)ax, (float)ay), c = new((float)bx, (float)by);
                    if (!SkClip.ClipSegment(a, c, area, out var t0, out var t1)) continue;
                    var d = c - a;
                    near.MoveTo(a + new SKPoint(d.X * t0, d.Y * t0));
                    near.LineTo(a + new SKPoint(d.X * t1, d.Y * t1));
                }
                canvas.DrawPath(near, Ink.Stroke(GridColor, GridWidth));
                continue;
            }
            if (!_paths.TryGetValue(b, out var path))
            {
                path = new SKPath();
                var s = b.Segments;
                for (int i = 0; i + 3 < s.Length; i += 4)
                {
                    path.MoveTo(s[i], -s[i + 1]);
                    path.LineTo(s[i + 2], -s[i + 3]);
                }
                _paths[b] = path;
            }
            var (ox, oy) = view.ToScreen(b.OriginX, b.OriginY);
            canvas.Save();
            canvas.Translate((float)ox, (float)oy);
            canvas.Scale(1 / upd);
            canvas.DrawPath(path, _paint);
            canvas.Restore();
        }
    }
}

/// <summary>"DCS Charts": DCS's raster charts, scale and mip following the zoom, coarsest
/// scale underneath.</summary>
public sealed class DcsChartsSk : MapLayer
{
    private const int MaxTilesPerScale = 120;

    public DcsChartsSk() => TileCache.TileLoaded += Invalidate;

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre) return;
        var index = ChartIndex.For(theatre.Id);
        if (index is null || index.Scales.Count == 0)
        {
            Text.Note(canvas, view, $"No DCS charts for {theatre.Id} on this PC (DCS install with this terrain needed).");
            return;
        }
        var groundPerPx = SkDraw.MetersPerDip(view, SkDraw.CenterLat(view)) / frame.PixelsPerDip;
        var fine = index.Scales.LastOrDefault(s => s <= groundPerPx, index.Scales[0]);
        var coarse = index.Scales[^1];
        if (coarse != fine) DrawScale(canvas, view, theatre, index, coarse, groundPerPx);
        DrawScale(canvas, view, theatre, index, fine, groundPerPx);
    }

    private static void DrawScale(SKCanvas canvas, MapView view, Theatre theatre, ChartIndex index, int scale, double groundPerPx)
    {
        var mip = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, groundPerPx / scale))), 0, 10);
        var drawn = 0;
        foreach (var tile in index.Tiles)
        {
            if (tile.MetersPerPixel != scale || !OnScreen(view, theatre, tile)) continue;
            if (++drawn > MaxTilesPerScale) break;
            if (ChartTileCache.Get(tile, mip) is { } image)
                SkDraw.Warped(canvas, view, theatre, image, tile.MaxX, tile.MinZ, tile.MaxZ - tile.MinZ, tile.MaxX - tile.MinX);
        }
    }

    private static bool OnScreen(MapView view, Theatre theatre, ChartTile t)
    {
        SKPoint a = SkDraw.Screen(view, theatre, t.MinX, t.MinZ), b = SkDraw.Screen(view, theatre, t.MaxX, t.MaxZ);
        SKPoint c = SkDraw.Screen(view, theatre, t.MinX, t.MaxZ), d = SkDraw.Screen(view, theatre, t.MaxX, t.MinZ);
        float minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        float minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)), maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
        return maxX >= 0 && maxY >= 0 && minX <= view.Width && minY <= view.Height;
    }
}

/// <summary>"DCS Satellite": the terrain's colour clipmaps, every useful resolution coarsest
/// first, and DCS's water cells painted over the seabed.</summary>
public sealed class DcsSatelliteSk : MapLayer
{
    private const int MaxTilesPerResolution = 160;
    private const byte ShallowAlpha = 150;
    private const int WaterBlock = 128;
    public static readonly SKColor Sea = MapStyle.Color("baseMaps.dcsSatelliteSea");
    private TerrainGrid? _waterGrid;
    private List<(SKImage Image, double XTop, double ZLeft, double Width, double Height)> _water = [];

    public DcsSatelliteSk()
    {
        TileCache.TileLoaded += Invalidate;
        ClipmapFile.LevelsLoaded += Invalidate;
    }

    private static (string Id, string Text)? _missing;

    /// <summary>Why there is no imagery: no install with the terrain, or neither clipmaps nor
    /// a land texture in it.</summary>
    private static string MissingNote(string terrainId)
    {
        if (_missing is { } m && m.Id == terrainId) return m.Text;
        var installed = Charts.DcsInstall.Find() is { } dcs && System.IO.Directory.Exists(System.IO.Path.Combine(dcs, "Mods", "terrains", terrainId));
        var text = installed
            ? $"No DCS satellite imagery found for {terrainId}. Try the Satellite map (online)."
            : $"No DCS satellite imagery for {terrainId} on this PC (DCS install with this terrain needed).";
        _missing = (terrainId, text);
        return text;
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre) return;
        var captured = CapturedMap.For(theatre.Id);
        if (ClipmapIndex.For(theatre.Id) is not { } index)
        {
            // Caucasus: the ground colour texture (F10 "Sat"), in the mission's season.
            if (LandTextures.For(theatre.Id, LandTextures.SeasonOf(AppState.Store.Weather?.Date)) is { } land)
                DrawLand(canvas, view, theatre, land, frame.PixelsPerDip);
            else if (captured is null)
                Text.Note(canvas, view, MissingNote(theatre.Id), light: false);
            if (captured is not null) DrawCaptured(canvas, view, theatre, captured, frame.PixelsPerDip);
            return;
        }
        var groundPerPx = SkDraw.MetersPerDip(view, SkDraw.CenterLat(view)) / frame.PixelsPerDip;
        var area = DcsDraw.ViewArea(view, theatre);
        foreach (var res in index.Resolutions)
        {
            var level = (int)Math.Floor(Math.Log2(Math.Max(1, groundPerPx / res)));
            var files = index.Files.Where(f => f.MetersPerPixel == res && Overlaps(f, area)).ToList();
            // Headers not read yet are read on a worker; those files join a later frame.
            var levelCount = files.Select(f => f.LevelsIfReady()?.Count).ToList().FirstOrDefault(c => c is not null);
            if (levelCount is not { } count || level >= count) continue;
            var drawn = 0;
            foreach (var file in files)
                drawn += DrawFile(canvas, view, theatre, file, Math.Max(0, level), area, MaxTilesPerResolution - drawn);
        }
        DrawWater(canvas, view, theatre);
        if (captured is not null) DrawCaptured(canvas, view, theatre, captured, frame.PixelsPerDip);
    }

    /// <summary>The map captured from DCS's own Sat view, over the imagery DCB reads itself (it
    /// covers what was captured; elsewhere the layers below show). A coarser level first, so a
    /// tile still loading never leaves a hole.</summary>
    private static void DrawCaptured(SKCanvas canvas, MapView view, Theatre theatre, CapturedMap map, float pixelsPerDip)
    {
        var groundPerPx = SkDraw.MetersPerDip(view, SkDraw.CenterLat(view)) / pixelsPerDip;
        var level = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, groundPerPx))), map.FinestLevel, 10);
        var area = DcsDraw.ViewArea(view, theatre);
        foreach (var l in level + 2 <= 10 ? new[] { level + 2, level } : new[] { level })
        {
            var s = DcsMapTiles.TileMeters(l);
            int tx0 = (int)Math.Floor(area.MinX / s), tx1 = (int)Math.Floor(area.MaxX / s);
            int tz0 = (int)Math.Floor(area.MinZ / s), tz1 = (int)Math.Floor(area.MaxZ / s);
            if ((long)(tx1 - tx0 + 1) * (tz1 - tz0 + 1) > 400) continue;
            for (int tx = tx1; tx >= tx0; tx--)
            for (int tz = tz0; tz <= tz1; tz++)
            {
                if (!map.Has(l, tx, tz)) continue;
                int cl = l, cx = tx, cz = tz;
                var key = map.Key(cl, cx, cz);
                var image = l == level ? TileCache.Get(key, () => map.Load(cl, cx, cz)) : TileCache.Peek(key) ?? TileCache.Get(key, () => map.Load(cl, cx, cz));
                if (image is not null) SkDraw.Warped(canvas, view, theatre, image, (tx + 1) * s, tz * s, s, s);
            }
        }
    }

    private void DrawWater(SKCanvas canvas, MapView view, Theatre theatre)
    {
        if (AppState.Store.Terrain is not { } g) return;
        if (!ReferenceEquals(g, _waterGrid))
        {
            _waterGrid = g;
            var px = new byte[g.Nx * g.Nz * 4];
            for (int r = 0; r < g.Nx; r++)
            {
                int i = g.Nx - 1 - r;  // image row 0 = northernmost grid row
                for (int j = 0; j < g.Nz; j++)
                {
                    var a = g.SurfaceAt(i, j) switch { Core.Surface.Water => 255, Core.Surface.ShallowWater => ShallowAlpha, _ => 0 };
                    if (a == 0) continue;
                    int o = (r * g.Nz + j) * 4;
                    px[o] = (byte)(Sea.Blue * a / 255);   // premultiplied
                    px[o + 1] = (byte)(Sea.Green * a / 255);
                    px[o + 2] = (byte)(Sea.Red * a / 255);
                    px[o + 3] = (byte)a;
                }
            }
            _water = Blocks(SkDraw.FromBgra(px, g.Nz, g.Nx, premultiplied: true), g, WaterBlock);
        }
        foreach (var b in _water) SkDraw.Warped(canvas, view, theatre, b.Image, b.XTop, b.ZLeft, b.Width, b.Height);
    }

    /// <summary>A grid-shaped image (row 0 = northernmost grid row) cut into blocks that are each
    /// warped on their own; one cell of overlap hides seams between neighbouring blocks.</summary>
    private static List<(SKImage, double, double, double, double)> Blocks(SKImage image, TerrainGrid g, int block)
    {
        var blocks = new List<(SKImage, double, double, double, double)>();
        for (int r0 = 0; r0 < g.Nx; r0 += block)
        for (int c0 = 0; c0 < g.Nz; c0 += block)
        {
            int h = Math.Min(block + 1, g.Nx - r0), w = Math.Min(block + 1, g.Nz - c0);
            var crop = image.Subset(SKRectI.Create(c0, r0, w, h));
            blocks.Add((crop, g.X0 + (g.Nx - 1 - r0 + 0.5) * g.Step, g.Z0 + (c0 - 0.5) * g.Step, w * g.Step, h * g.Step));
        }
        return blocks;
    }

    /// <summary>The land texture: the coarse level wherever it has tiles (also under fine tiles
    /// still loading), then the fine level once a fine pixel is under ~2 screen pixels.</summary>
    private static void DrawLand(SKCanvas canvas, MapView view, Theatre theatre, LandTextures land, double pixelsPerDip)
    {
        var groundPerPx = SkDraw.MetersPerDip(view, SkDraw.CenterLat(view)) / pixelsPerDip;
        var area = DcsDraw.ViewArea(view, theatre);
        foreach (var level in land.Levels)
        {
            if (level != land.Levels[0] && groundPerPx > 2 * level.MetersPerPixel) continue;
            int i0 = Math.Max(level.MinI, level.TileAtX(area.MinX)), i1 = Math.Min(level.MaxI, level.TileAtX(area.MaxX));
            int j0 = Math.Max(level.MinJ, level.TileAtZ(area.MinZ)), j1 = Math.Min(level.MaxJ, level.TileAtZ(area.MaxZ));
            var drawn = 0;
            for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                if (!land.Has(level, i, j)) continue;
                if (++drawn > MaxTilesPerResolution) return;
                int ti = i, tj = j;
                if (TileCache.Get($"land:{land.Path}:{level.Name}:{i}:{j}", () => land.ReadTile(level, ti, tj)) is { } image)
                {
                    var (x, z, w, h) = level.Bounds(i, j);
                    SkDraw.Axes(canvas, view, theatre, image, x, z, w, 0, 0, h);
                }
                else
                    RenderStats.Count("DcsSatellite land tiles missing");
            }
        }
    }

    private static bool Overlaps(ClipmapFile f, (double MinX, double MinZ, double MaxX, double MaxZ) a) =>
        f.SouthX <= a.MaxX && f.SouthX + f.BlockMeters >= a.MinX && f.WestZ <= a.MaxZ && f.WestZ + f.BlockMeters >= a.MinZ;

    private static int DrawFile(SKCanvas canvas, MapView view, Theatre theatre, ClipmapFile file, int level,
        (double MinX, double MinZ, double MaxX, double MaxZ) area, int budget)
    {
        if (budget <= 0 || file.LevelsIfReady() is not { } levels || level >= levels.Count) return 0;
        var lv = levels[level];
        var tileM = file.BlockMeters / lv.TilesPerSide;
        int n0 = Math.Max(0, (int)Math.Floor((area.MinX - file.SouthX) / tileM));
        int n1 = Math.Min(lv.TilesPerSide - 1, (int)Math.Floor((area.MaxX - file.SouthX) / tileM));
        int e0 = Math.Max(0, (int)Math.Floor((area.MinZ - file.WestZ) / tileM));
        int e1 = Math.Min(lv.TilesPerSide - 1, (int)Math.Floor((area.MaxZ - file.WestZ) / tileM));
        var drawn = 0;
        var ready = new List<(SKImage Image, int N, int E)>();
        var parents = new HashSet<(int N, int E)>();
        for (int n = n0; n <= n1; n++)
        for (int e = e0; e <= e1; e++)
        {
            if (lv.Offsets[n * lv.TilesPerSide + e] == 0) continue;
            if (++drawn > budget) break;
            int tn = n, te = e;
            if (TileCache.Get($"sat:{file.Path}:{level}:{n}:{e}", () => file.ReadTile(level, tn, te)) is { } image)
                ready.Add((image, n, e));
            else
            {
                parents.Add((n / 2, e / 2));
                RenderStats.Count("DcsSatellite tiles missing");
            }
        }
        // Tiles still loading: their parent (the next coarser level, one tile covering four)
        // if it is in memory, drawn first so the ready tiles cover it.
        if (parents.Count > 0 && level + 1 < levels.Count)
        {
            var pm = tileM * 2;
            foreach (var (pn, pe) in parents)
                if (TileCache.Peek($"sat:{file.Path}:{level + 1}:{pn}:{pe}") is { } parent)
                    SkDraw.Axes(canvas, view, theatre, parent, file.SouthX + pn * pm, file.WestZ + pe * pm, pm, 0, 0, pm);
        }
        // Stored with columns north and rows east: top-left = the tile's south-west corner,
        // width runs north, height east.
        foreach (var (image, n, e) in ready)
            SkDraw.Axes(canvas, view, theatre, image, file.SouthX + n * tileM, file.WestZ + e * tileM, tileM, 0, 0, tileM);
        return drawn;
    }
}

/// <summary>Dims everything below it (the base map); overlays above stay bright.</summary>
public sealed class DimSk : MapLayer
{
    public double Opacity { get; set; }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        if (Opacity <= 0) return;
        canvas.DrawRect(0, 0, (float)frame.View.Width, (float)frame.View.Height, Ink.Fill(new SKColor(0, 0, 0, (byte)(Opacity * 255))));
    }
}
