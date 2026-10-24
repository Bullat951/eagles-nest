using Dcb.Client.Core;
using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>The triangulation board (AppState.Board): RWR bearing lines with their error wedges (each line's own ±σ),
/// the fix where an aircraft's lines cross (1σ ellipse), placed threats with their rings, and
/// the coalition's F10 marks.</summary>
public sealed class GciSk : MapLayer
{
    private static readonly Ink.Pen LinePen = new(MapStyle.Color("triangulation.line.color"), MapStyle.Number("triangulation.line.width"), MapStyle.Dash("triangulation.line.dash"));
    private static readonly float WedgeAlpha = MapStyle.Number("triangulation.wedgeAlpha");
    private static readonly double LineM = MapStyle.Number("triangulation.lengthNm") * Nav.MetersPerNm;
    private static readonly double FadeMinutes = MapStyle.Number("triangulation.fadeMinutes");
    private static readonly SKColor FixInk = MapStyle.Color("triangulation.fix.color");
    private static readonly float FixWidth = MapStyle.Number("triangulation.fix.width");
    private static readonly SKColor ThreatInk = MapStyle.Color("triangulation.threat.color");
    private static readonly Ink.Pen ThreatRing = new(ThreatInk, MapStyle.Number("triangulation.threat.ringWidth"), MapStyle.Dash("triangulation.threat.ringDash"));
    private static readonly float ThreatFillAlpha = MapStyle.Number("triangulation.threat.fillAlpha");
    private static readonly float ThreatSize = MapStyle.Number("triangulation.threat.size");
    private static readonly Text.Face LabelFace = Text.FaceOf(MapStyle.Text("triangulation.label.font"));
    private static readonly float LabelSize = MapStyle.Number("triangulation.label.size");
    private static readonly SKColor MarkInk = MapStyle.Color("marks.color");
    private static readonly SKColor PlayerMarkInk = MapStyle.Color("marks.player");
    private static readonly SKColor DcbMarkInk = MapStyle.Color("marks.dcb");
    private static readonly float MarkSize = MapStyle.Number("marks.size");
    private static readonly Text.Face MarkFace = Text.FaceOf(MapStyle.Text("marks.label.font"));
    private static readonly float MarkLabelSize = MapStyle.Number("marks.label.size");
    private const double MarkLabelMaxUnitsPerDip = 900;
    private const double HitRadius = 12;
    private static readonly SKColor NctrActiveInk = new(90, 170, 255);

    public SKColor Halo { get; set; } = SkPalette.HaloDark;
    public bool ShowMarks { get; set; } = true;
    public Func<MapView>? ViewProvider { get; set; }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        var board = AppState.Board;
        var area = SkClip.Area(view);
        var now = DateTime.UtcNow;
        foreach (var t in board.AllThreats(IsZoneLabel)) DrawThreat(canvas, view, area, t);
        foreach (var line in board.Lines) DrawLine(canvas, view, area, line, now);
        foreach (var (_, fix) in board.Fixes()) DrawFix(canvas, view, fix);
        DrawNctr(canvas, view, area);
        if (ShowMarks)
            foreach (var m in board.VisibleMarks(IsZoneLabel)) DrawMark(canvas, view, m);
    }

    /// <summary>A Foothold zone's own name label (Foothold puts one mark on every zone).</summary>
    private static bool IsZoneLabel(F10Mark m)
    {
        if (AppState.Store.Foothold is not { } state) return false;
        foreach (var z in state.Zones)
        {
            var r = Math.Max(z.Radius, 3000);
            if ((z.X - m.X) * (z.X - m.X) + (z.Z - m.Z) * (z.Z - m.Z) < r * r && m.Text.Contains(z.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static float Pixels(MapView view, double meters, double lat) => (float)(meters * Mercator.ScaleAt(lat) / view.UnitsPerDip);

    private void DrawLine(SKCanvas canvas, MapView view, SKRect area, BearingLine line, DateTime now)
    {
        var age = (now - line.Time).TotalMinutes;
        var opacity = age < FadeMinutes ? 1 : 0.4;
        var origin = SkDraw.ScreenLonLat(view, line.Lon, line.Lat);
        // The ±σ wedge, then the line itself (sampled so it follows the bearing on the Mercator map).
        var sigma = line.SigmaDeg;
        using (var wedge = new SKPath())
        {
            wedge.MoveTo(origin);
            for (int k = 0; k <= 8; k++)
            {
                var (la, lo) = Nav.Destination(line.Lat, line.Lon, line.BearingTrue - sigma + 2 * sigma * k / 8, LineM);
                wedge.LineTo(SkDraw.ScreenLonLat(view, lo, la));
            }
            wedge.Close();
            canvas.DrawPath(wedge, Ink.Fill(Ink.WithOpacity(LinePen.Color, WedgeAlpha * opacity)));
        }
        var pen = LinePen with { Color = Ink.WithOpacity(LinePen.Color, opacity) };
        SKPoint prev = origin;
        double along = 0;
        for (int k = 1; k <= 10; k++)
        {
            var (la, lo) = Nav.Destination(line.Lat, line.Lon, line.BearingTrue, LineM * k / 10);
            var p = SkDraw.ScreenLonLat(view, lo, la);
            SkClip.Line(canvas, pen, prev, p, area, along);
            along += (p - prev).Length;
            prev = p;
        }
        canvas.DrawCircle(origin, 3, Ink.Fill(pen.Color));
        if (!area.Contains(origin)) return;
        // "084° 6 Viper 1-1 14:02": the emitter's RWR label when it came from the scope's emitter.
        var text = $"{line.BearingTrue:000}°{(line.Emitter is { } e ? " " + e : "")} {line.Label} {line.Time.ToLocalTime():HH:mm}";
        Text.Draw(canvas, text, origin.X + 6, origin.Y + 4, pen.Color, Ink.WithOpacity(Halo, opacity), LabelFace, LabelSize);
    }

    private void DrawFix(SKCanvas canvas, MapView view, BearingFix fix)
    {
        var c = SkDraw.ScreenLonLat(view, fix.Lon, fix.Lat);
        // The ellipse traced point by point: radius r(θ) of an ellipse at angle θ from its major axis.
        using (var ellipse = new SKPath())
        {
            for (int k = 0; k <= 36; k++)
            {
                var th = k * 10 * Math.PI / 180;
                var r = fix.MajorM * fix.MinorM / Math.Sqrt(Math.Pow(fix.MinorM * Math.Cos(th), 2) + Math.Pow(fix.MajorM * Math.Sin(th), 2));
                var (la, lo) = Nav.Destination(fix.Lat, fix.Lon, fix.MajorTrueDeg + k * 10, r);
                var p = SkDraw.ScreenLonLat(view, lo, la);
                if (k == 0) ellipse.MoveTo(p); else ellipse.LineTo(p);
            }
            canvas.DrawPath(ellipse, Ink.Fill(FixInk.WithAlpha(40)));
            canvas.DrawPath(ellipse, Ink.Stroke(FixInk, FixWidth));
        }
        var pen = Ink.Stroke(FixInk, FixWidth + 0.4f);
        canvas.DrawLine(c.X - 9, c.Y - 9, c.X + 9, c.Y + 9, pen);
        canvas.DrawLine(c.X - 9, c.Y + 9, c.X + 9, c.Y - 9, pen);
        var lines = new List<string> { $"FIX ±{fix.MajorM / Nav.MetersPerNm:0.0}/{fix.MinorM / Nav.MetersPerNm:0.0} NM ({fix.Lines})" };
        if (AppState.Store.Latest?.Bullseye is { } bulls) lines.Add(Nav.BullseyeCall(bulls, fix.Lat, fix.Lon, AppState.Store.Theatre?.MagVar));
        for (int i = 0; i < lines.Count; i++)
            Text.Draw(canvas, lines[i], c.X + 12, c.Y - 6 + i * (LabelSize + 3), FixInk, Halo, LabelFace, LabelSize);
    }

    /// <summary>NCTR: a running request's area (thin) and each radar's cone, with the seconds left; under each unidentified contact the
    /// answers it got (newest request, one per method), e.g. "JEM Su-27 family · Size medium".</summary>
    private void DrawNctr(SKCanvas canvas, MapView view, SKRect area)
    {
        var store = AppState.Store;
        if (store.Nctr is not { } nctr || store.Theatre is not { } theatre) return;
        foreach (var r in nctr.Requests.Where(r => r.Running && r.Area.Count == 4))
        {
            double x1 = r.Area[0], z1 = r.Area[1], x2 = r.Area[2], z2 = r.Area[3];
            using var path = new SKPath();
            // The x/z box traced edge by edge (it is not quite a rectangle on the Mercator map).
            var corners = new[] { (x1, z1), (x2, z1), (x2, z2), (x1, z2), (x1, z1) };
            for (int c = 0; c < 4; c++)
                for (int k = 0; k <= 8; k++)
                {
                    var (ax, az) = corners[c];
                    var (bx, bz) = corners[c + 1];
                    var (lat, lon) = theatre.ToLatLon(ax + (bx - ax) * k / 8, az + (bz - az) * k / 8);
                    var p = SkDraw.ScreenLonLat(view, lon, lat);
                    if (c == 0 && k == 0) path.MoveTo(p); else path.LineTo(p);
                }
            canvas.DrawPath(path, Ink.Stroke(FixInk.WithAlpha(110), 1));
            // Each radar's cone (DCS grid frame, so traced in x/z and converted point by point).
            foreach (var cone in r.Cones ?? [])
            {
                using var wedge = new SKPath();
                var (ol, oo) = theatre.ToLatLon(cone.X, cone.Z);
                wedge.MoveTo(SkDraw.ScreenLonLat(view, oo, ol));
                for (int k = 0; k <= 12; k++)
                {
                    var b = (cone.Center - cone.Half + 2 * cone.Half * k / 12) * Math.PI / 180;
                    var (wl, wo) = theatre.ToLatLon(cone.X + Math.Cos(b) * cone.Range, cone.Z + Math.Sin(b) * cone.Range);
                    wedge.LineTo(SkDraw.ScreenLonLat(view, wo, wl));
                }
                wedge.Close();
                canvas.DrawPath(wedge, Ink.Fill(FixInk.WithAlpha(28)));
                canvas.DrawPath(wedge, Ink.Stroke(FixInk, FixWidth));
            }
            var (tl, tn) = theatre.ToLatLon(Math.Max(x1, x2), Math.Min(z1, z2));
            var label = SkDraw.ScreenLonLat(view, tn, tl);
            Text.Draw(canvas, $"NCTR {string.Join("/", r.Methods.Select(NctrMethods.NameOf))} · {r.Left:0} s · {r.Radars} radar{(r.Radars == 1 ? "" : "s")}",
                label.X + 4, label.Y - 6, FixInk, Halo, LabelFace, LabelSize);
        }
        // Methods being worked on a contact right now, in blue beside it, flashing (MapPanel
        // redraws twice a second while a request runs).
        if (DateTime.Now.Millisecond < 600)
            foreach (var g in nctr.Requests.Where(r => r.Running).SelectMany(r => r.Results).Where(x => x.Active).GroupBy(x => x.Unit))
            {
                if (store.Find(g.Key)?.Unit is not { Lat: { } alat, Lon: { } alon }) continue;
                var p = SkDraw.ScreenLonLat(view, alon, alat);
                if (!area.Contains(p)) continue;
                var methods = string.Join(" ", g.Select(x => NctrMethods.NameOf(x.Method).ToUpperInvariant()).Distinct());
                Text.Draw(canvas, methods, p.X + 14, p.Y - 10, NctrActiveInk, Halo, Text.Face.Bold, LabelSize + 1);
            }
        var shown = new HashSet<long>(nctr.Requests.SelectMany(r => r.Results).Where(x => x.Status == "ok").Select(x => x.Unit));
        foreach (var id in shown)
        {
            // An identified contact needs no answers under it (they stay in its Identify menu).
            if (store.Find(id)?.Unit is not { Lat: { } lat, Lon: { } lon, Shown: null }) continue;
            var p = SkDraw.ScreenLonLat(view, lon, lat);
            if (!area.Contains(p)) continue;
            var text = string.Join(" · ", store.NctrFor(id).Select(n => $"{NctrMethods.NameOf(n.Method)} {NctrMethods.LabelOf(n) ?? n.Result}"));
            Text.DrawCentered(canvas, text, p.X, p.Y + 34, FixInk, Halo, LabelFace, LabelSize);
        }
    }

    private void DrawThreat(SKCanvas canvas, MapView view, SKRect area, ThreatView t)
    {
        var c = SkDraw.ScreenLonLat(view, t.Lon, t.Lat);
        var r = Pixels(view, t.Type.RangeM, t.Lat);
        SkClip.Circle(canvas, ThreatInk.WithAlpha((byte)(ThreatFillAlpha * 255)), ThreatRing, c, r, area);
        if (!area.Contains(c)) return;
        var s = ThreatSize;
        using (var diamond = new SKPath())
        {
            diamond.AddPoly([new(c.X, c.Y - s), new(c.X + s, c.Y), new(c.X, c.Y + s), new(c.X - s, c.Y)], close: true);
            canvas.DrawPath(diamond, Ink.Fill(ThreatInk));
            canvas.DrawPath(diamond, Ink.Stroke(Halo, 1));
        }
        // "· F10" when it lives as an F10 mark; unsent ones are the controller's own notes.
        var tag = t.Type.Name + (t.MarkId is not null ? " · F10" : "");
        Text.DrawCentered(canvas, tag, c.X, c.Y + s + 2, ThreatInk, Halo, Text.Face.Bold, LabelSize + 1);
    }

    private void DrawMark(SKCanvas canvas, MapView view, F10Mark m)
    {
        var p = SkDraw.ScreenLonLat(view, m.Lon, m.Lat);
        if (p.X < -50 || p.Y < -50 || p.X > view.Width + 50 || p.Y > view.Height + 50) return;
        var ink = m.Dcb ? DcbMarkInk : m.ByMission ? MarkInk : PlayerMarkInk;
        // A pin: a small flag on a staff, the mark's point at its foot.
        var s = MarkSize;
        canvas.DrawLine(p.X, p.Y, p.X, p.Y - 2.6f * s, Ink.Stroke(Halo, 3));
        canvas.DrawLine(p.X, p.Y, p.X, p.Y - 2.6f * s, Ink.Stroke(ink, 1.4f));
        using (var flag = new SKPath())
        {
            flag.AddPoly([new(p.X, p.Y - 2.6f * s), new(p.X + 1.6f * s, p.Y - 2.1f * s), new(p.X, p.Y - 1.6f * s)], close: true);
            canvas.DrawPath(flag, Ink.Fill(ink));
            canvas.DrawPath(flag, Ink.Stroke(Halo, 0.8f));
        }
        canvas.DrawCircle(p, 2, Ink.Fill(ink));
        if (view.UnitsPerDip > MarkLabelMaxUnitsPerDip) return;
        var text = FirstLine(m.Text);
        if (text.Length > 0) Text.Draw(canvas, text, p.X + 2 * s, p.Y - 2.9f * s, ink, Halo, MarkFace, MarkLabelSize);
    }

    public static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 48 ? line[..47] + "…" : line;
    }

    // ---- Hit tests for the map's context menu -------------------------------------------------

    private bool Near(double lat, double lon, double px, double py, double radius = HitRadius)
    {
        if (ViewProvider?.Invoke() is not { IsValid: true } view) return false;
        var (x, y) = view.ToScreenLonLat(lon, lat);
        return (x - px) * (x - px) + (y - py) * (y - py) <= radius * radius;
    }

    public ThreatView? HitThreat(double px, double py) =>
        AppState.Board.AllThreats(IsZoneLabel).FirstOrDefault(t => Near(t.Lat, t.Lon, px, py));

    public BearingFix? HitFix(double px, double py) =>
        AppState.Board.Fixes().Select(f => f.Fix).FirstOrDefault(f => Near(f.Lat, f.Lon, px, py, 16));

    public F10Mark? HitMark(double px, double py) =>
        !ShowMarks ? null : AppState.Board.VisibleMarks(IsZoneLabel).FirstOrDefault(m => Near(m.Lat, m.Lon, px, py, 14));
}
