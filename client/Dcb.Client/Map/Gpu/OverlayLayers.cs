using System.Globalization;
using Dcb.Client.Core;
using Dcb.Client.Map.Airfields;
using Dcb.Client.Map.Charts;
using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>Colours of the shared style for identities and coalitions.</summary>
public static class SkPalette
{
    public static readonly SKColor Friendly = MapStyle.Color("identity.friendly");
    public static readonly SKColor Hostile = MapStyle.Color("identity.hostile");
    public static readonly SKColor Unknown = MapStyle.Color("identity.unknown");
    public static readonly SKColor Selection = MapStyle.Color("identity.selection");
    public static readonly SKColor HaloDark = MapStyle.Color("halo.dark");
    public static readonly SKColor HaloLight = MapStyle.Color("halo.light");

    public static SKColor For(Identity id) => id switch
    {
        Identity.Friendly => Friendly,
        Identity.Hostile => Hostile,
        _ => Unknown,
    };

    public static SKColor Coalition(int side) => side switch
    {
        1 => MapStyle.Color("coalition.red"),
        2 => MapStyle.Color("coalition.blue"),
        _ => MapStyle.Color("coalition.neutral"),
    };
}

/// <summary>Theatre border with the outside dimmed, plus optional town names.</summary>
public sealed class TheatreSk : MapLayer
{
    private const int MaxTowns = 400;
    private static readonly double TownsMaxUnitsPerDip = MapStyle.Number("theatre.town.maxMetersPerDip");
    private static readonly Ink.Pen BorderPen = new(MapStyle.Color("theatre.border.color"), MapStyle.Number("theatre.border.width"), MapStyle.Dash("theatre.border.dash"));
    private static readonly SKColor OutsideDark = MapStyle.Color("theatre.outsideDark");
    private static readonly SKColor OutsideLight = MapStyle.Color("theatre.outsideLight");
    private static readonly SKColor TownDark = MapStyle.Color("theatre.town.dark");
    private static readonly SKColor TownLight = MapStyle.Color("theatre.town.light");
    private static readonly float TownSize = MapStyle.Number("theatre.town.size");

    public bool ShowTowns { get; set; }
    public bool LightBaseMap { get; set; }

    private Theatre? _theatre;
    private List<(double X, double Y)> _borderMerc = [];
    private readonly Dictionary<string, string> _titles = [];

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre) return;
        if (!ReferenceEquals(theatre, _theatre))
        {
            _theatre = theatre;
            _borderMerc = theatre.Border().Select(p => Mercator.FromLonLat(p.Lon, p.Lat)).ToList();
        }
        var area = SkClip.Area(view);
        var border = _borderMerc.Select(p => { var (x, y) = view.ToScreen(p.X, p.Y); return new SKPoint((float)x, (float)y); }).ToList();
        // Outside dimmed: the view rectangle and the clipped border filled even-odd.
        var inside = SkClip.ClipPolygon(border, area);
        using (var dim = new SKPath { FillType = SKPathFillType.EvenOdd })
        {
            dim.AddRect(area);
            if (inside.Count >= 3) dim.AddPoly(inside.ToArray(), close: true);
            canvas.DrawPath(dim, Ink.Fill(LightBaseMap ? OutsideLight : OutsideDark));
        }
        var along = 0.0;
        for (int i = 0; i < border.Count; i++)
        {
            SKPoint a = border[i], b = border[(i + 1) % border.Count];
            SkClip.Line(canvas, BorderPen, a, b, area, along);
            along += (b - a).Length;
        }
        if (ShowTowns && view.UnitsPerDip <= TownsMaxUnitsPerDip) DrawTowns(canvas, view, theatre);
    }

    private void DrawTowns(SKCanvas canvas, MapView view, Theatre theatre)
    {
        var ink = LightBaseMap ? TownLight : TownDark;
        // Declutter: one town per cell of at least 110×22 DIPs. The cells are fixed in map
        // metres for each doubling of the zoom, so which names show only changes when the zoom
        // crosses a doubling — not on every frame of a zoom, where names would flicker.
        var q = Math.Pow(2, Math.Ceiling(Math.Log2(view.UnitsPerDip)));
        double cellW = 110 * q, cellH = 22 * q;
        var taken = new HashSet<(long, long)>();
        var drawn = 0;
        foreach (var town in theatre.Towns)
        {
            var (mx, my) = Mercator.FromLonLat(town.Lon, town.Lat);
            var (sx, sy) = view.ToScreen(mx, my);
            var p = new SKPoint((float)sx, (float)sy);
            if (p.X < -50 || p.Y < -20 || p.X > view.Width + 50 || p.Y > view.Height + 20) continue;
            if (!taken.Add(((long)Math.Floor(mx / cellW), (long)Math.Floor(my / cellH)))) continue;
            canvas.DrawCircle(p, 1.6f, Ink.Fill(ink));
            if (!_titles.TryGetValue(town.Name, out var title))
                _titles[town.Name] = title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(town.Name.ToLowerInvariant());
            Text.Draw(canvas, title, p.X + 3, p.Y - 7, ink, null, Text.Face.Regular, TownSize);
            if (++drawn >= MaxTowns) break;
        }
    }
}

/// <summary>Foothold zones coloured by owner, links between them, names and strength bars.</summary>
public sealed class FootholdSk : MapLayer
{
    private static readonly float MinRadius = MapStyle.Number("zones.minRadius");
    private static readonly double LabelMaxUnitsPerDip = MapStyle.Number("zones.label.maxMetersPerDip");
    private static readonly Ink.Pen LinkPen = new(MapStyle.Color("zones.link.color"), MapStyle.Number("zones.link.width"), MapStyle.Dash("zones.link.dash"));
    private static readonly float Width = MapStyle.Number("zones.width"), SelectedWidth = MapStyle.Number("zones.selectedWidth");
    private static readonly float FillAlpha = MapStyle.Number("zones.fillAlpha"), SuspendedFillAlpha = MapStyle.Number("zones.suspendedFillAlpha");
    private static readonly float BigFactor = MapStyle.Number("zones.bigZoneFillFactor");
    private static readonly float[] SuspendedDash = MapStyle.Dash("zones.suspendedDash");
    private static readonly Text.Face LabelFace = Text.FaceOf(MapStyle.Text("zones.label.font"));
    private static readonly float LabelSize = MapStyle.Number("zones.label.size");

    public SKColor Halo { get; set; } = SkPalette.HaloDark;
    public string? SelectedZone { get; set; }
    public Func<MapView>? ViewProvider { get; set; }

    /// <summary>Zone under a screen point, if any.</summary>
    public string? HitTest(double x, double y)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre || store.Foothold is not { } state || ViewProvider?.Invoke() is not { IsValid: true } view) return null;
        foreach (var z in state.Zones)
        {
            var (c, r) = Circle(view, theatre, z);
            if (SKPoint.Distance(c, new SKPoint((float)x, (float)y)) <= Math.Max(r, 10)) return z.Name;
        }
        return null;
    }

    private static (SKPoint Center, float Radius) Circle(MapView view, Theatre theatre, FootholdZone z)
    {
        var c = SkDraw.Screen(view, theatre, z.X, z.Z);
        var (lat, _) = theatre.ToLatLon(z.X, z.Z);
        return (c, (float)Math.Max(MinRadius, z.Radius * Mercator.ScaleAt(lat) / view.UnitsPerDip));
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre || AppState.Store.Foothold is not { } state) return;
        var circles = state.Zones.ToDictionary(z => z.Name, z => Circle(view, theatre, z));
        var area = SkClip.Area(view);
        foreach (var link in state.Links)
        {
            if (link.Length < 2 || !circles.TryGetValue(link[0], out var a) || !circles.TryGetValue(link[1], out var b)) continue;
            var d = b.Center - a.Center;
            var len = d.Length;
            if (len <= a.Radius + b.Radius) continue;
            var u = new SKPoint(d.X / len, d.Y / len);
            SkClip.Line(canvas, LinkPen, a.Center + new SKPoint(u.X * a.Radius, u.Y * a.Radius), b.Center - new SKPoint(u.X * b.Radius, u.Y * b.Radius), area);
        }
        var labels = view.UnitsPerDip <= LabelMaxUnitsPerDip;
        foreach (var z in state.Zones)
        {
            var (c, r) = circles[z.Name];
            if (c.X < -r - 100 || c.Y < -r - 40 || c.X > view.Width + r + 100 || c.Y > view.Height + r + 40) continue;
            // Suspended (idle) zones fainter; big zones (naval areas) only a faint fill.
            var solid = SkPalette.Coalition(z.Side);
            var alpha = (z.Suspended ? SuspendedFillAlpha : FillAlpha) * (r > 60 ? BigFactor : 1);
            var pen = new Ink.Pen(solid, z.Name == SelectedZone ? SelectedWidth : Width, z.Suspended ? SuspendedDash : null);
            SkClip.Circle(canvas, solid.WithAlpha((byte)(alpha * 255)), pen, c, r, area);
            if (z.Logistic) canvas.DrawRect(c.X - 3, c.Y - 3, 6, 6, Ink.Fill(solid));
            if (!labels) continue;
            var title = z.Waypoint is { } w ? $"{z.Name} · {w}" : z.Name;
            var (_, th) = Text.DrawCentered(canvas, title, c.X, c.Y + r + 3, solid, Halo, LabelFace, LabelSize);
            if (z.Strength is { } s)
            {
                var bar = SKRect.Create(c.X - 18, c.Y + r + 3 + th + 1, 36, 4);
                canvas.DrawRect(bar, Ink.Fill(Halo));
                canvas.DrawRect(SKRect.Create(bar.Left, bar.Top, bar.Width * (float)s, bar.Height), Ink.Fill(solid));
            }
        }
    }
}

/// <summary>Airfields: runways (lines when zoomed out, true size with designators zoomed in),
/// approach paths for the ATC tab's airfield, and in the ATC airfield view also placed
/// diagrams, taxiways and parking.</summary>
public sealed class AirfieldsSk : MapLayer
{
    private const double OverviewMaxUnitsPerDip = 400;
    private const double DetailMaxUnitsPerDip = 12;
    private const double ParkingMaxUnitsPerDip = 6;
    private const double TaxiwayWidthM = 20;
    private const double ApproachMaxUnitsPerDip = 1500;
    private const double Nm = 1852;
    private static readonly double[] ApproachTicksNm = MapStyle.Numbers("airfields.approach.ticksNm");
    private static readonly SKColor LabelInk = MapStyle.Color("airfields.label");
    private static readonly SKColor RunwayFill = MapStyle.Color("airfields.runway");
    private static readonly SKColor TaxiFill = MapStyle.Color("airfields.taxiway");
    private static readonly SKColor Marking = MapStyle.Color("airfields.marking");
    private static readonly SKColor TaxiLine = MapStyle.Color("airfields.taxiLine");
    private static readonly SKColor Parking = MapStyle.Color("airfields.parking");
    private static readonly SKColor SelectedInk = MapStyle.Color("airfields.selected");
    private static readonly SKColor Outline = MapStyle.Color("airfields.chartOutline");
    private static readonly SKColor ApproachInk = MapStyle.Color("airfields.approach.color");
    private static readonly Ink.Pen ApproachPen = new(ApproachInk, MapStyle.Number("airfields.approach.width"));
    private static readonly Ink.Pen ApproachTick = new(ApproachInk, MapStyle.Number("airfields.approach.tickWidth"), Round: true);

    public bool FullDetail { get; set; }
    public SKColor Halo { get; set; } = SkPalette.HaloDark;
    public int? SelectedId { get; set; }
    public bool ShowDiagrams { get; set; } = true;
    public bool NightDiagrams { get; set; }

    public AirfieldsSk()
    {
        Taxiways.Loaded += () => { if (FullDetail) Invalidate(); };
        TileCache.TileLoaded += () => { if (FullDetail && ShowDiagrams) Invalidate(); };
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        var store = AppState.Store;
        if (store.Theatre is not { } theatre || store.Airfields.Count == 0) return;
        var upd = view.UnitsPerDip;
        if (!FullDetail && upd <= ApproachMaxUnitsPerDip && store.Airfields.FirstOrDefault(f => f.Id == SelectedId) is { } selected)
            foreach (var rw in selected.Runways) DrawApproaches(canvas, view, theatre, rw);
        if (upd > OverviewMaxUnitsPerDip) return;

        var detail = upd <= DetailMaxUnitsPerDip;
        var diagrams = detail && FullDetail && ShowDiagrams ? AirfieldDiagrams.For(theatre) : null;
        var visible = store.Airfields.Where(f => InView(view, theatre, f, upd)).ToList();
        var charted = new HashSet<int>();
        if (diagrams is not null)
        {
            foreach (var field in visible)
            {
                if (diagrams.For(field.Id, NightDiagrams) is not { } entry) continue;
                diagrams.Draw(canvas, view, theatre, entry, 0.95f);
                charted.Add(field.Id);
            }
        }
        if (detail && FullDetail && Taxiways.For(theatre) is { } taxi)
        {
            var skip = store.Airfields.Where(f => charted.Contains(f.Id)).Select(f => f.Name.Replace(' ', '_')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            DrawTaxiways(canvas, view, taxi, upd, skip);
        }
        foreach (var field in visible)
        {
            var c = SkDraw.Screen(view, theatre, field.X, field.Z);
            foreach (var rw in field.Runways) DrawRunway(canvas, view, theatre, rw, detail, charted.Contains(field.Id));
            if (FullDetail && upd <= ParkingMaxUnitsPerDip) DrawParking(canvas, view, theatre, field);
            if (field.Id == SelectedId) canvas.DrawCircle(c, 22, Ink.Stroke(SelectedInk, 2));
            if (upd > DetailMaxUnitsPerDip || field.Runways.Count == 0)
                Text.Draw(canvas, field.IsHelipad ? $"H {field.Name}" : field.Name, c.X + 8, c.Y + 6, LabelInk, Halo, Text.Face.Bold, 10);
        }
    }

    private static bool InView(MapView view, Theatre theatre, Airfield field, double upd)
    {
        var c = SkDraw.Screen(view, theatre, field.X, field.Z);
        var extent = 400 + (field.Runways.Count > 0 ? field.Runways.Max(r => r.Length) : 1000) * 1.5 / upd;
        return c.X >= -extent && c.Y >= -extent && c.X <= view.Width + extent && c.Y <= view.Height + extent;
    }

    private static void DrawTaxiways(SKCanvas canvas, MapView view, Taxiways taxi, double upd, HashSet<string> skip)
    {
        var (minX, minY, maxX, maxY) = view.WorldBounds;
        var widthDip = (float)Math.Max(1.5, TaxiwayWidthM / upd);
        using var path = new SKPath();
        var area = SkClip.Area(view);
        area.Inflate(widthDip, widthDip);
        var runs = new VisibleRuns(path, area);
        foreach (var f in taxi.Fields)
        {
            if (skip.Contains(f.Name) || f.MaxX < minX || f.MinX > maxX || f.MaxY < minY || f.MinY > maxY) continue;
            foreach (var line in f.Lines)
            {
                var (x0, y0) = view.ToScreen(line[0], line[1]);
                runs.Start(new SKPoint((float)x0, (float)y0));
                for (int i = 2; i + 1 < line.Length; i += 2)
                {
                    var (x, y) = view.ToScreen(line[i], line[i + 1]);
                    runs.To(new SKPoint((float)x, (float)y));
                }
            }
        }
        canvas.DrawPath(path, Ink.Stroke(TaxiFill, widthDip, round: true));
        if (widthDip >= 6) canvas.DrawPath(path, Ink.Stroke(TaxiLine, Math.Max(0.6f, widthDip / 14)));
    }

    private static void DrawRunway(SKCanvas canvas, MapView view, Theatre theatre, Runway rw, bool detail, bool outlineOnly)
    {
        var h = rw.HeadingTrue * Math.PI / 180;
        double ax = Math.Cos(h), az = Math.Sin(h), cx = -Math.Sin(h), cz = Math.Cos(h);
        double hl = rw.Length / 2, hw = rw.Width / 2;
        SKPoint P(double along, double across) => SkDraw.Screen(view, theatre, rw.X + ax * along + cx * across, rw.Z + az * along + cz * across);
        if (!detail)
        {
            canvas.DrawLine(P(-hl, 0), P(hl, 0), Ink.Stroke(LabelInk, 2.5f, round: true));
            return;
        }
        SKPoint[] corners = [P(-hl, -hw), P(hl, -hw), P(hl, hw), P(-hl, hw)];
        using var outline = new SKPath();
        outline.AddPoly(corners, close: true);
        if (outlineOnly)
        {
            canvas.DrawPath(outline, Ink.Stroke(Outline, 1.2f));  // the chart draws the runway
            return;
        }
        canvas.DrawPath(outline, Ink.Fill(RunwayFill));
        canvas.DrawPath(outline, Ink.Stroke(Marking, 1));
        canvas.DrawLine(P(-hl + 120, 0), P(hl - 120, 0), new Ink.Pen(Marking, 1, [6, 6]).Paint());
        var (near, far) = rw.Designators;
        Centered(canvas, near, P(-hl + 70, 0));
        Centered(canvas, far, P(hl - 70, 0));
    }

    private static void Centered(SKCanvas canvas, string text, SKPoint at)
    {
        if (text.Length == 0) return;
        var (w, h) = Text.Measure(text, Text.Face.Bold, 11);
        Text.Draw(canvas, text, at.X - w / 2, at.Y - h / 2, Marking, SkPalette.HaloDark, Text.Face.Bold, 11);
    }

    /// <summary>Extended centre line out to 10 NM per runway end, ticks at 2/5/10 NM, "RWY xx"
    /// beyond the outer end; clipped to the view.</summary>
    private static void DrawApproaches(SKCanvas canvas, MapView view, Theatre theatre, Runway rw)
    {
        var h = rw.HeadingTrue * Math.PI / 180;
        double ax = Math.Cos(h), az = Math.Sin(h), hl = rw.Length / 2;
        var area = SkClip.Area(view);
        var (nearName, farName) = rw.Designators;
        foreach (var (sign, name) in new[] { (-1, nearName), (1, farName) })
        {
            double tx = rw.X + sign * ax * hl, tz = rw.Z + sign * az * hl;
            var threshold = SkDraw.Screen(view, theatre, tx, tz);
            var outer = SkDraw.Screen(view, theatre, tx + sign * ax * 10 * Nm, tz + sign * az * 10 * Nm);
            SkClip.Line(canvas, ApproachPen, threshold, outer, area);
            var d = outer - threshold;
            var len = d.Length;
            if (len < 1) continue;
            var dir = new SKPoint(d.X / len, d.Y / len);
            var across = new SKPoint(-dir.Y, dir.X);
            foreach (var nm in ApproachTicksNm)
            {
                var at = threshold + new SKPoint(d.X * (float)(nm / 10), d.Y * (float)(nm / 10));
                var half = nm == 10 ? 9f : 6f;
                if (!area.Contains(at)) continue;
                canvas.DrawLine(at - new SKPoint(across.X * half, across.Y * half), at + new SKPoint(across.X * half, across.Y * half), ApproachTick.Paint());
                var lp = at + new SKPoint(across.X * (half + 3), across.Y * (half + 3) - 7);
                Text.Draw(canvas, nm.ToString("0"), lp.X, lp.Y, ApproachInk, SkPalette.HaloDark, Text.Face.Bold, 10);
            }
            var tag = outer + new SKPoint(dir.X * 14, dir.Y * 14);
            if (name.Length > 0 && area.Contains(tag))
            {
                var text = $"RWY {name}";
                var (w, th) = Text.Measure(text, Text.Face.Bold, 10);
                Text.Draw(canvas, text, tag.X - w / 2, tag.Y - th / 2, ApproachInk, SkPalette.HaloDark, Text.Face.Bold, 10);
            }
        }
    }

    private void DrawParking(SKCanvas canvas, MapView view, Theatre theatre, Airfield field)
    {
        foreach (var spot in field.Parking)
        {
            var p = SkDraw.Screen(view, theatre, spot.X, spot.Z);
            if (p.X < -20 || p.Y < -20 || p.X > view.Width + 20 || p.Y > view.Height + 20) continue;
            canvas.DrawCircle(p, 4, Ink.Stroke(Parking, 1));
            Text.Draw(canvas, spot.Number.ToString(), p.X + 5, p.Y - 6, Parking, Halo, Text.Face.Regular, 9);
        }
    }
}

/// <summary>Track symbols, style B (docs/PLAN.md phase 2): identity shapes, speed vectors,
/// data blocks, threat rings, selection.</summary>
public sealed class SymbolsSk : MapLayer
{
    private static readonly float R = MapStyle.Number("symbols.halfSize");
    private static readonly float SymbolWidth = MapStyle.Number("symbols.width");
    private static readonly float ThinWidth = MapStyle.Number("symbols.thinWidth");
    private static readonly float LeaderWidth = MapStyle.Number("symbols.leaderWidth");
    private static readonly double VectorSeconds = MapStyle.Number("symbols.vectorSeconds");
    private static readonly double GroundLabelMaxUnitsPerDip = MapStyle.Number("symbols.groundLabelsMaxMetersPerDip");
    private static readonly Text.Face LabelFace = Text.FaceOf(MapStyle.Text("symbols.label.font"));
    private static readonly float LabelSize = MapStyle.Number("symbols.label.size");
    private static readonly float LineHeight = MapStyle.Number("symbols.label.lineHeight");
    private static readonly Ink.Pen SelectionPen = new(SkPalette.Selection, MapStyle.Number("symbols.selection.width"), MapStyle.Dash("symbols.selection.dash"));
    private static readonly float SelectionRadius = MapStyle.Number("symbols.selection.radius");
    private static readonly Ink.Pen RingPen = new(MapStyle.Color("threatRing.color"), MapStyle.Number("threatRing.width"), MapStyle.Dash("threatRing.dash"));
    private const double HitRadius = 12;

    public MapSettings Settings { get; set; } = new();
    public SKColor Halo { get; set; } = SkPalette.HaloDark;
    public Func<MapView>? ViewProvider { get; set; }

    /// <summary>Ground units (vehicles, structures) drawn; ships always are.</summary>
    public bool ShowGroundUnits { get; set; } = true;
    /// <summary>Aircraft on the ground or a deck drawn.</summary>
    public bool ShowLanded { get; set; }

    private bool Shown(Track t)
    {
        var kind = Symbology.KindOf(t.Unit);
        if (kind == SymbolKind.Ground) return ShowGroundUnits;
        return ShowLanded || !t.OnGround;
    }

    /// <summary>Track under a screen point, if any.</summary>
    public long? HitTest(double px, double py)
    {
        if (ViewProvider?.Invoke() is not { IsValid: true } view) return null;
        long? best = null;
        var bestDist = HitRadius;
        foreach (var t in AppState.Store.Tracks)
        {
            if (t.Unit.Lat is not { } lat || t.Unit.Lon is not { } lon || !Shown(t)) continue;
            var (x, y) = view.ToScreenLonLat(lon, lat);
            var d = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if (d < bestDist) (best, bestDist) = (t.Unit.Id, d);
        }
        return best;
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        var store = AppState.Store;
        if (store.Coalition is not { } own) return;
        var now = DateTime.UtcNow;
        var ordered = store.Tracks
            .Where(t => t.Unit.Lat is not null && t.Unit.Lon is not null && Shown(t))
            .OrderBy(t => Symbology.KindOf(t.Unit) switch { SymbolKind.Ground => 0, SymbolKind.Ship => 1, _ => 2 })
            .ThenBy(t => t.Unit.Id == store.SelectedId)
            .ToList();
        if (Settings.ThreatRings) DrawThreatRings(canvas, view, ordered, own, now);

        foreach (var t in ordered)
        {
            var u = t.Unit;
            var p = SkDraw.ScreenLonLat(view, u.Lon!.Value, u.Lat!.Value);
            if (p.X < -300 || p.Y < -300 || p.X > view.Width + 300 || p.Y > view.Height + 300) continue;
            var identity = Symbology.IdentityOf(u, own);
            var kind = Symbology.KindOf(u);
            var fresh = t.Freshness(now);
            // Package members in their package's colour, the tag ("P1") left of the symbol.
            var package = identity == Identity.Friendly ? AppState.Packages.PackageOf(u.Id) : null;
            var ink = Ink.WithOpacity(package is null ? SkPalette.For(identity) : PackageColors.Sk(package.Number), fresh);
            var halo = Ink.WithOpacity(Halo, fresh);
            var pen = Ink.Stroke(ink, SymbolWidth);
            switch (kind)
            {
                case SymbolKind.Aircraft:
                case SymbolKind.Helicopter:
                    DrawAir(canvas, view, u, identity, kind, p, ink, halo, pen);
                    if (package is not null)
                        Text.Draw(canvas, package.Tag, p.X - R - 24, p.Y + 5, ink, halo, Text.Face.Bold, 13);
                    break;
                case SymbolKind.Ship:
                    Shape(canvas, identity, p, pen, null);
                    using (var hull = new SKPath())
                    {
                        hull.MoveTo(p.X - 12, p.Y + 9);
                        hull.QuadTo(p.X, p.Y + 20, p.X + 12, p.Y + 9);
                        canvas.DrawPath(hull, pen);
                    }
                    if (u.Emitter) EmitterArcs(canvas, p, ink);
                    CenteredBlock(canvas, Symbology.DataBlock(u, Settings.Labels), p.X, p.Y + 20, ink, halo);
                    break;
                default:
                    Shape(canvas, identity, p, pen, ink);
                    canvas.DrawLine(p.X - 10, p.Y + 11, p.X + 10, p.Y + 11, pen);
                    if (u.Emitter) EmitterArcs(canvas, p, ink);
                    if (view.UnitsPerDip <= GroundLabelMaxUnitsPerDip)
                        CenteredBlock(canvas, Symbology.DataBlock(u, Settings.Labels), p.X, p.Y + 14, ink, halo);
                    break;
            }
            if (u.Id == store.SelectedId) canvas.DrawCircle(p, SelectionRadius, SelectionPen.Paint());
            if (u.Player is not null && Srs.SrsListen.Instance.TalkingOn(u) is { } channel) DrawTalking(canvas, p, channel, halo);
        }
        DrawMissiles(canvas, view, store, own);
    }

    private static readonly SKColor TalkInk = MapStyle.Color("srs.talk");
    private static readonly float TalkWidth = MapStyle.Number("srs.ringWidth");

    /// <summary>A player talking on SRS: two rings pulsing out from the symbol and the frequency
    /// under it. The map is redrawn every 100 ms while anyone talks.</summary>
    private static void DrawTalking(SKCanvas canvas, SKPoint p, Core.SrsChannel channel, SKColor halo)
    {
        var phase = (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds % 0.8 / 0.8);
        foreach (var k in new[] { phase, (phase + 0.5f) % 1 })
            canvas.DrawCircle(p, R + 6 + k * 14, Ink.Stroke(Ink.WithOpacity(TalkInk, 1 - k), TalkWidth));
        Text.DrawCentered(canvas, $"{channel.Mhz:0.000}", p.X, p.Y - R - 30, TalkInk, halo, Text.Face.Bold, 11);
    }

    private static readonly float MissileSize = MapStyle.Number("missiles.size");
    private static readonly float TrailWidth = MapStyle.Number("missiles.trailWidth");
    private static readonly float TrailAlpha = MapStyle.Number("missiles.trailAlpha");
    private static readonly double MissileVectorSeconds = MapStyle.Number("missiles.vectorSeconds");
    private static readonly Text.Face MissileFace = Text.FaceOf(MapStyle.Text("missiles.label.font"));
    private static readonly float MissileLabelSize = MapStyle.Number("missiles.label.size");
    private static readonly Ink.Pen TargetPen = new(SKColors.White, MapStyle.Number("missiles.targetLine.width"), MapStyle.Dash("missiles.targetLine.dash"));

    /// <summary>Missiles: trail, an arrowhead along the flight path with a short vector, a line to
    /// the target when it is on the map, and the type (own) or "MSL" (incoming).</summary>
    private void DrawMissiles(SKCanvas canvas, MapView view, PictureStore store, string own)
    {
        foreach (var m in store.Missiles)
        {
            var w = m.Weapon;
            var ink = SkPalette.For(w.Coalition == own ? Identity.Friendly : Identity.Hostile);
            var p = SkDraw.ScreenLonLat(view, w.Lon, w.Lat);
            if (w.Target is { } tid && store.Find(tid)?.Unit is { Lat: { } tlat, Lon: { } tlon })
                canvas.DrawLine(p, SkDraw.ScreenLonLat(view, tlon, tlat), (TargetPen with { Color = Ink.WithOpacity(ink, 0.7) }).Paint());
            if (m.Trail.Count > 1)
            {
                using var trail = new SKPath();
                trail.MoveTo(SkDraw.ScreenLonLat(view, m.Trail[0].Lon, m.Trail[0].Lat));
                for (int i = 1; i < m.Trail.Count; i++) trail.LineTo(SkDraw.ScreenLonLat(view, m.Trail[i].Lon, m.Trail[i].Lat));
                canvas.DrawPath(trail, Ink.Stroke(Ink.WithOpacity(ink, TrailAlpha), TrailWidth));
            }
            if (p.X < -100 || p.Y < -100 || p.X > view.Width + 100 || p.Y > view.Height + 100) continue;
            var rad = w.Hdg * Math.PI / 180;
            float dx = (float)Math.Sin(rad), dy = (float)-Math.Cos(rad);
            var len = (float)(w.Spd * MissileVectorSeconds * Mercator.ScaleAt(w.Lat) / view.UnitsPerDip);
            canvas.DrawLine(p.X, p.Y, p.X + dx * len, p.Y + dy * len, Ink.Stroke(ink, ThinWidth));
            using (var head = new SKPath())
            {
                var s = MissileSize;
                head.AddPoly([new(p.X + dx * s * 1.4f, p.Y + dy * s * 1.4f),
                    new(p.X - dx * s + dy * s * 0.7f, p.Y - dy * s - dx * s * 0.7f),
                    new(p.X - dx * s - dy * s * 0.7f, p.Y - dy * s + dx * s * 0.7f)], close: true);
                canvas.DrawPath(head, Ink.Fill(ink));
                canvas.DrawPath(head, Ink.Stroke(Halo, 0.8f));
            }
            var label = w.Type is { Length: > 0 } t ? t : "MSL";
            Text.Draw(canvas, $"{label} {w.Alt * Unit.FeetPerMeter / 1000:0}k", p.X + 7, p.Y + 3, ink, Halo, MissileFace, MissileLabelSize);
        }
    }

    private void DrawAir(SKCanvas canvas, MapView view, Unit u, Identity identity, SymbolKind kind, SKPoint p, SKColor ink, SKColor halo, SKPaint pen)
    {
        Shape(canvas, identity, p, pen, null);
        var top = identity == Identity.Hostile ? p.Y - R - 2 : p.Y - R;
        if (kind == SymbolKind.Helicopter)
        {
            canvas.DrawLine(p.X - 8, top - 5, p.X + 8, top - 5, pen);
            canvas.DrawLine(p.X, top - 5, p.X, top, pen);
        }
        if (u.Spd is > 5 && u.Hdg is { } hdg)
        {
            var len = u.Spd.Value * VectorSeconds * Mercator.ScaleAt(u.Lat!.Value) / view.UnitsPerDip;
            var rad = hdg * Math.PI / 180;
            canvas.DrawLine(p.X, p.Y, (float)(p.X + Math.Sin(rad) * len), (float)(p.Y - Math.Cos(rad) * len), pen);
        }
        var lines = Symbology.DataBlock(u, Settings.Labels);
        if (lines.Count == 0) return;
        float lx = p.X + 14, ly = p.Y - 16;
        canvas.DrawLine(p.X + 4, p.Y - 4, lx, ly, Ink.Stroke(ink, LeaderWidth));
        for (int i = 0; i < lines.Count; i++)
            Text.Draw(canvas, lines[i], lx + 2, ly - LineHeight * (lines.Count - i) + 2, ink, halo, LabelFace, LabelSize);
    }

    private static void Shape(SKCanvas canvas, Identity identity, SKPoint p, SKPaint pen, SKColor? fill)
    {
        switch (identity)
        {
            case Identity.Friendly:
                if (fill is { } f1) canvas.DrawCircle(p, R, Ink.Fill(f1));
                canvas.DrawCircle(p, R, pen);
                break;
            case Identity.Hostile:
                var d = R + 2;
                using (var diamond = new SKPath())
                {
                    diamond.AddPoly([new(p.X, p.Y - d), new(p.X + d, p.Y), new(p.X, p.Y + d), new(p.X - d, p.Y)], close: true);
                    if (fill is { } f2) canvas.DrawPath(diamond, Ink.Fill(f2));
                    canvas.DrawPath(diamond, pen);
                }
                break;
            default:
                var rect = SKRect.Create(p.X - R, p.Y - R, 2 * R, 2 * R);
                if (fill is { } f3) canvas.DrawRect(rect, Ink.Fill(f3));
                canvas.DrawRect(rect, pen);
                break;
        }
    }

    private static void EmitterArcs(SKCanvas canvas, SKPoint p, SKColor ink)
    {
        var thin = Ink.Stroke(ink, ThinWidth);
        foreach (var r in new[] { 11f, 16f })
        {
            // Arc right of the symbol from (0.66r, −0.75r) to (0.66r, +0.75r) on a circle of radius r.
            var start = MathF.Atan2(-0.75f, 0.66f) * 180 / MathF.PI;
            using var arc = new SKPath();
            arc.AddArc(SKRect.Create(p.X + 0.66f * r - MathF.Sqrt(r * r - 0.5625f * r * r) - r, p.Y - r, 2 * r, 2 * r), start, -2 * start);
            canvas.DrawPath(arc, thin);
        }
    }

    private void DrawThreatRings(SKCanvas canvas, MapView view, List<Track> tracks, string own, DateTime now)
    {
        var area = SkClip.Area(view);
        var drawn = new HashSet<(int, int, double)>();
        foreach (var t in tracks)
        {
            var u = t.Unit;
            if (Symbology.IdentityOf(u, own) != Identity.Hostile || ThreatRanges.MetersFor(u.Type) is not { } range) continue;
            var p = SkDraw.ScreenLonLat(view, u.Lon!.Value, u.Lat!.Value);
            var r = (float)(range * Mercator.ScaleAt(u.Lat!.Value) / view.UnitsPerDip);
            if (!drawn.Add(((int)(p.X / 6), (int)(p.Y / 6), range))) continue;  // one ring per SAM site
            SkClip.Circle(canvas, null, RingPen with { Color = Ink.WithOpacity(RingPen.Color, t.Freshness(now)) }, p, r, area);
        }
    }

    private void CenteredBlock(SKCanvas canvas, IReadOnlyList<string> lines, float x, float y, SKColor ink, SKColor halo)
    {
        foreach (var line in lines)
        {
            Text.DrawCentered(canvas, line, x, y, ink, halo, LabelFace, LabelSize);
            y += LineHeight;
        }
    }
}

/// <summary>Bullseye, the right-drag ruler (magnetic bearing, NM; BRAA on an aircraft) and the
/// cursor's bullseye call while the bullseye key is held.</summary>
public sealed class MeasureSk : MapLayer
{
    public readonly record struct End(long? TrackId, double Lat, double Lon);

    private static readonly SKColor InkColor = MapStyle.Color("measure.ink");
    private static readonly SKColor BullsInk = MapStyle.Color("measure.bullseye");
    private static readonly SKColor Back = MapStyle.Color("measure.box");
    private static readonly SKColor Casing = MapStyle.Color("measure.casing");

    public End? RulerFrom { get; set; }
    public End? RulerTo { get; set; }
    public (double X, double Y)? BullsCursor { get; set; }
    /// <summary>The Alt+clicked point whose coordinates are shown; null hides it.</summary>
    public (double Lat, double Lon)? Pin { get; set; }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        var store = AppState.Store;
        var magvar = store.Theatre?.MagVar;
        var bulls = store.Latest?.Bullseye;
        if (bulls is not null) DrawBullseye(canvas, view, bulls);
        if (Pin is { } pin)
        {
            var p = SkDraw.ScreenLonLat(view, pin.Lon, pin.Lat);
            foreach (var pen in new[] { Ink.Stroke(Casing, 3.4f), Ink.Stroke(InkColor, 1.4f) })
            {
                canvas.DrawCircle(p, 6, pen);
                canvas.DrawLine(p.X - 11, p.Y, p.X - 3, p.Y, pen);
                canvas.DrawLine(p.X + 3, p.Y, p.X + 11, p.Y, pen);
                canvas.DrawLine(p.X, p.Y - 11, p.X, p.Y - 3, pen);
                canvas.DrawLine(p.X, p.Y + 3, p.X, p.Y + 11, pen);
            }
        }
        if (RulerFrom is { } from && RulerTo is { } to) DrawRuler(canvas, view, Resolve(from), Resolve(to), magvar);
        if (BullsCursor is { } c)
        {
            var text = bulls is null ? "no bullseye" : Nav.BullseyeCall(bulls, view.ToLatLon(c.X, c.Y).Lat, view.ToLatLon(c.X, c.Y).Lon, magvar) + (magvar is null ? " T" : "");
            Box(canvas, [text], (float)c.X + 16, (float)c.Y + 12);
        }
    }

    private static (End End, Unit? Unit) Resolve(End end) =>
        end.TrackId is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } lat, Lon: { } lon } u
            ? (end with { Lat = lat, Lon = lon }, u)
            : (end, null);

    private static void DrawRuler(SKCanvas canvas, MapView view, (End End, Unit? Unit) a, (End End, Unit? Unit) b, MagVarGrid? magvar)
    {
        var pa = SkDraw.ScreenLonLat(view, a.End.Lon, a.End.Lat);
        var pb = SkDraw.ScreenLonLat(view, b.End.Lon, b.End.Lat);
        foreach (var pen in new[] { Ink.Stroke(Casing, 3.6f), Ink.Stroke(InkColor, 1.6f) })
        {
            canvas.DrawLine(pa, pb, pen);
            canvas.DrawCircle(pa, 3, pen);
            canvas.DrawCircle(pb, 3, pen);
        }
        var (brgTrue, meters) = Nav.BearingRange(a.End.Lat, a.End.Lon, b.End.Lat, b.End.Lon);
        var brg = magvar is null ? Nav.Bearing3(brgTrue) + "°T" : Nav.Bearing3(Nav.Magnetic(brgTrue, magvar.At(a.End.Lat, a.End.Lon))) + "°";
        var lines = new List<string> { $"{brg}  {meters / Nav.MetersPerNm:0.0} NM" };
        if (b.Unit is { } target)
        {
            var parts = new List<string>();
            if (target.AltFt is { } ft) parts.Add($"{Math.Round(ft / 100) * 100:#,0} ft".Replace(',', ' '));
            if (target.Hdg is { } hdg && target.Spd is > 5)
                parts.Add(Nav.Aspect(hdg, Nav.BearingRange(b.End.Lat, b.End.Lon, a.End.Lat, a.End.Lon).TrueDeg));
            if (parts.Count > 0) lines.Add(string.Join("  ", parts));
        }
        Box(canvas, lines, pb.X + 12, pb.Y + 10);
    }

    private static void DrawBullseye(SKCanvas canvas, MapView view, Bullseye bulls)
    {
        var c = SkDraw.ScreenLonLat(view, bulls.Lon, bulls.Lat);
        if (c.X < -40 || c.Y < -40 || c.X > view.Width + 40 || c.Y > view.Height + 40) return;
        foreach (var pen in new[] { Ink.Stroke(Casing, 3.2f), Ink.Stroke(BullsInk, 1.4f) })
        {
            canvas.DrawCircle(c, 7, pen);
            canvas.DrawCircle(c, 13, pen);
            canvas.DrawLine(c.X - 18, c.Y, c.X + 18, c.Y, pen);
            canvas.DrawLine(c.X, c.Y - 18, c.X, c.Y + 18, pen);
        }
        Text.Draw(canvas, "BULLS", c.X + 15, c.Y + 9, BullsInk, SkPalette.HaloDark, Text.Face.Bold, 10);
    }

    private static void Box(SKCanvas canvas, IReadOnlyList<string> lines, float x, float y)
    {
        var sizes = lines.Select(l => Text.Measure(l, Text.Face.Mono, 12)).ToList();
        float w = sizes.Max(s => s.Width) + 10, h = sizes.Sum(s => s.Height) + 6;
        canvas.DrawRoundRect(SKRect.Create(x, y, w, h), 3, 3, Ink.Fill(Back));
        var ty = y + 3;
        for (int i = 0; i < lines.Count; i++)
        {
            Text.Draw(canvas, lines[i], x + 5, ty, InkColor, null, Text.Face.Mono, 12);
            ty += sizes[i].Height;
        }
    }
}
