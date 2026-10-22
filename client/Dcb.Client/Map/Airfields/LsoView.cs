using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Dcb.Client.Core;
using Vec = System.Windows.Vector;

namespace Dcb.Client.Map.Airfields;

/// <summary>The ATC tab's LSO view for a carrier, from the fast feed (10 a second): the aircraft
/// in the groove on a PLAT-style crosshair (glideslope and lineup), its numbers (range, height,
/// high/low and lineup in feet, closure, estimated AoA, gear and hook), the groove from the side
/// and from above with the X/IM/IC/AR marks, and the pass log with results and grades (a click
/// on a grade cycles it).</summary>
public sealed class LsoView : FrameworkElement
{
    private const double Pad = 8, GrooveNm = 1.25;
    private static readonly string[] Grades = ["OK", "(OK)", "---", "C", "B", "WO"];

    private static readonly Brush Panel = Frozen(Color.FromRgb(0x18, 0x20, 0x28));
    private static readonly Brush Scope = Frozen(Color.FromRgb(0x10, 0x16, 0x1C));
    private static readonly Brush Grid = Frozen(Color.FromRgb(0x3A, 0x4A, 0x58));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xDC, 0xE4, 0xEA));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x9A, 0xA8));
    private static readonly Brush Path = Frozen(Color.FromRgb(0x4C, 0xD9, 0x64));
    private static readonly Brush Plane = Frozen(Color.FromRgb(0xFF, 0xE0, 0x40));
    private static readonly Brush Warn = Frozen(Color.FromRgb(0xFF, 0x70, 0x50));
    private static readonly Brush Good = Frozen(Color.FromRgb(0x70, 0xE0, 0x80));

    private long? _carrier;
    private readonly List<(Rect Row, Pass Pass)> _gradeCells = [];

    public LsoView()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public void Show(long carrierId)
    {
        _carrier = carrierId;
        InvalidateVisual();
    }

    private static Rect Box(double x, double y, double w, double h) => new(x, y, Math.Max(0, w), Math.Max(0, h));

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            Render(dc);
        }
        catch (Exception ex)
        {
            Map.Gpu.MapSurface.Log($"LSO view: {ex}");
            dc.DrawRectangle(Panel, null, Box(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Panel, null, Box(0, 0, ActualWidth, ActualHeight));
        _gradeCells.Clear();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (ActualWidth < 520 || ActualHeight < 360)
        {
            Centered(dc, "Enlarge the pane to see the LSO view", new Point(ActualWidth / 2, ActualHeight / 2), Dim, 12, dpi);
            return;
        }
        var store = AppState.Store;
        if (_carrier is not { } id || store.Carriers.FirstOrDefault(c => c.Id == id) is not { } info || CarrierGeometry.For(info.Type) is not { } g)
        {
            Centered(dc, "Select a carrier", new Point(ActualWidth / 2, ActualHeight / 2), Dim, 14, dpi);
            return;
        }
        if (!g.Arrested)
        {
            Centered(dc, $"{CarrierGeometry.Label(info.Type)}: no arrested recovery — LSO tools are for angled-deck carriers", new Point(ActualWidth / 2, ActualHeight / 2), Dim, 12, dpi);
            return;
        }

        // The aircraft to watch: nearest astern in or near the groove of this ship.
        var ship = store.Lso?.Carriers.FirstOrDefault(c => c.Id == id);
        var wind = store.Passes.WindFor(id);  // the same AoA estimate the pass log grades with
        (LsoAircraft A, Groove G)? watched = null;
        if (ship is not null)
            watched = store.Lso!.Aircraft.Select(a => (A: a, G: Groove.Of(ship, g, a, wind)))
                // In or near the groove and flying (not parked on the deck after a trap).
                .Where(x => x.G.AsternNm is > -0.1 and < 2 && Math.Abs(x.G.CrossM) < 1500 && x.G.HeightM < 1000 && (x.G.RelSpeedMs > 10 || x.G.HeightM > 5))
                .OrderBy(x => x.G.AsternM).Cast<(LsoAircraft, Groove)?>().FirstOrDefault();

        double w = ActualWidth - 2 * Pad, h = ActualHeight - 2 * Pad;
        var plat = Box(Pad, Pad, Math.Min(h * 0.45, w * 0.35), Math.Min(h * 0.45, w * 0.35));
        var readout = Box(plat.Right + Pad, Pad, w * 0.62 - plat.Width - Pad, plat.Height);
        var log = Box(Pad + w * 0.62 + Pad, Pad, w * 0.38 - Pad, h);
        var profile = Box(Pad, plat.Bottom + Pad, w * 0.62, (h - plat.Height - 2 * Pad) * 0.55);
        var top = Box(Pad, profile.Bottom + Pad, w * 0.62, h - plat.Height - profile.Height - 2 * Pad);

        var name = watched is { } wa && store.Find(wa.A.Id)?.Unit is { } unit ? (Symbology.NameOf(unit) ?? "?", unit.Type) : ("", (string?)null);
        var trail = watched is { } wt && store.LsoHistory.TryGetValue(wt.A.Id, out var hist)
            ? hist.Select(f => Groove.Of(f.Ship, g, f.Aircraft, wind)).ToList() : [];

        DrawPlat(dc, plat, watched?.G, trail, dpi);
        DrawReadout(dc, readout, watched, name, g, info, dpi);
        DrawProfile(dc, profile, g, watched?.G, trail, dpi);
        DrawTop(dc, top, watched?.G, trail, dpi);
        DrawLog(dc, log, id, dpi);
    }

    /// <summary>PLAT crosshair: horizontal line = glideslope, vertical = lineup; the aircraft by
    /// its angular deviations (±1.5° vertical, ±4° horizontal across the box).</summary>
    private void DrawPlat(DrawingContext dc, Rect r, Groove? gr, List<Groove> trail, double dpi)
    {
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        dc.DrawLine(new Pen(Path, 1.2), new Point(r.Left, c.Y), new Point(r.Right, c.Y));
        dc.DrawLine(new Pen(Path, 1.2), new Point(c.X, r.Top), new Point(c.X, r.Bottom));
        foreach (var k in new[] { -0.5, 0.5 })
        {
            dc.DrawLine(new Pen(Grid, 0.8), new Point(r.Left, c.Y + k * r.Height / 2), new Point(r.Right, c.Y + k * r.Height / 2));
            dc.DrawLine(new Pen(Grid, 0.8), new Point(c.X + k * r.Width / 2, r.Top), new Point(c.X + k * r.Width / 2, r.Bottom));
        }
        Point At(Groove x)
        {
            var vert = Math.Atan2(x.GlideM, Math.Max(30, x.AsternM)) * 180 / Math.PI;   // above the slope +
            var horiz = x.LineupDeg;                                                     // right of the line +
            return new Point(c.X + Math.Clamp(horiz / 4, -1, 1) * r.Width / 2, c.Y - Math.Clamp(vert / 1.5, -1, 1) * r.Height / 2);
        }
        foreach (var t in trail.Where(t => t.AsternNm is > 0 and < GrooveNm).TakeLast(100))
            dc.DrawEllipse(Dim, null, At(t), 1.2, 1.2);
        if (gr is { } now && now.AsternNm > -0.05)
        {
            var p = At(now);
            dc.DrawEllipse(null, new Pen(Plane, 2), p, 7, 7);
            dc.DrawLine(new Pen(Plane, 2), p - new Vec(12, 0), p + new Vec(12, 0));
        }
        Label(dc, "PLAT  ±1.5° slope · ±4° line", new Point(r.Left + 4, r.Bottom - 16), Dim, 9, dpi);
    }

    private void DrawReadout(DrawingContext dc, Rect r, (LsoAircraft A, Groove G)? watched, (string Name, string? Type) who,
        CarrierGeometry g, CarrierInfo info, double dpi)
    {
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        var x = r.Left + 10;
        var y = r.Top + 8;
        void Line(string text, Brush brush, double size = 13, bool bold = true)
        {
            Label(dc, text, new Point(x, y), brush, size, dpi, bold);
            y += size + 7;
        }
        if (watched is not { } w)
        {
            Line(CarrierGeometry.Label(info.Type).ToUpperInvariant(), Text);
            Line(AppState.Store.Lso is null ? "No fast feed: no aircraft within 10 NM of the ship" : "No aircraft in the groove", Dim, 12, false);
            return;
        }
        var gr = w.G;
        var type = who.Type is null ? "" : Symbology.ShortType(who.Type);
        Line($"{who.Name}  {type}", Plane, 15);
        Line($"{gr.AsternNm:0.00} NM   {gr.HeightM * Groove.FeetPerMeter:0} FT above deck   closure {gr.ClosureMs * Groove.KnotsPerMs:0} KT", Text, 12, false);
        var glide = gr.GlideFt;
        Line(Math.Abs(glide) < 25 ? $"ON GLIDESLOPE ({glide:+0;-0} FT)" : glide > 0 ? $"HIGH {glide:0} FT" : $"LOW {-glide:0} FT",
            Math.Abs(glide) < 25 ? Good : Math.Abs(glide) < 60 ? Plane : Warn);
        var cross = gr.CrossFt;
        Line(Math.Abs(cross) < 12 ? $"ON CENTRELINE ({cross:+0;-0} FT)" : cross < 0 ? $"LINED UP LEFT {-cross:0} FT" : $"LINED UP RIGHT {cross:0} FT",
            Math.Abs(cross) < 12 ? Good : Math.Abs(cross) < 30 ? Plane : Warn);
        if (Groove.OnSpeedAoa(who.Type) is { } onSpeed)
        {
            var d = gr.AoaDeg - onSpeed;
            Line(Math.Abs(d) < 0.8 ? $"ON SPEED  AoA {gr.AoaDeg:0.0}° (est.)" : d < 0 ? $"FAST  AoA {gr.AoaDeg:0.0}° (est.)" : $"SLOW  AoA {gr.AoaDeg:0.0}° (est.)",
                Math.Abs(d) < 0.8 ? Good : Plane, 12);
        }
        else Line($"AoA {gr.AoaDeg:0.0}° (est.)", Text, 12, false);
        // Gear from the model's draw argument 0. The hook has no common argument (25 read 0 on a
        // trapped F/A-18C), so it is not shown rather than shown wrong.
        Line(w.A.Gear is { } ge ? (ge > 0.9 ? "GEAR DOWN" : "GEAR UP") : "gear ?", w.A.Gear is > 0.9 ? Good : Warn, 12);
        if (gr.ClosureMs > 5 && gr.AsternM > 0) Line($"{gr.AsternM / gr.ClosureMs:0} s to the wires", Dim, 11, false);
    }

    /// <summary>The groove from the side: distance astern (0–1.25 NM) against height above the
    /// deck, the glideslope, X/IM/IC/AR marks, the aircraft and its trail.</summary>
    private void DrawProfile(DrawingContext dc, Rect r, CarrierGeometry g, Groove? gr, List<Groove> trail, double dpi)
    {
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        var plot = Box(r.Left + 40, r.Top + 18, r.Width - 50, r.Height - 36);
        const double maxFt = 600;
        Point P(double nm, double ft) => new(plot.Right - nm / GrooveNm * plot.Width, plot.Bottom - Math.Clamp(ft, -50, maxFt) / maxFt * plot.Height);
        foreach (var ft in new[] { 200, 400, 600 })
        {
            dc.DrawLine(new Pen(Grid, 0.6), P(GrooveNm, ft), P(0, ft));
            Label(dc, $"{ft}", new Point(r.Left + 6, P(0, ft).Y - 7), Dim, 9, dpi);
        }
        dc.DrawLine(new Pen(Dim, 1), P(GrooveNm, 0), P(0, 0));
        var slopeFtPerNm = Math.Tan(g.GlideslopeDeg * Math.PI / 180) * Groove.MetersPerNm * Groove.FeetPerMeter;
        dc.DrawLine(new Pen(Path, 1.6), P(0, 0), P(GrooveNm, GrooveNm * slopeFtPerNm));
        foreach (var (mark, nm) in PassTracker.Marks)
        {
            dc.DrawLine(new Pen(Grid, 0.8), P(nm, 0), P(nm, maxFt));
            Label(dc, mark, new Point(P(nm, 0).X - 6, plot.Bottom + 2), Dim, 9, dpi);
        }
        var pts = trail.Where(t => t.AsternNm is > -0.05 and < GrooveNm).Select(t => P(t.AsternNm, t.HeightM * Groove.FeetPerMeter)).ToList();
        for (int i = 1; i < pts.Count; i++) dc.DrawLine(new Pen(Dim, 1.2), pts[i - 1], pts[i]);
        if (gr is { } now && now.AsternNm is > -0.05 and < GrooveNm)
            dc.DrawEllipse(Plane, null, P(now.AsternNm, now.HeightM * Groove.FeetPerMeter), 5, 5);
        Label(dc, $"SIDE · ft above deck · {g.GlideslopeDeg:0.0}° slope", new Point(r.Left + 6, r.Top + 2), Dim, 9, dpi);
    }

    /// <summary>The groove from above: distance astern against lineup (±300 ft).</summary>
    private void DrawTop(DrawingContext dc, Rect r, Groove? gr, List<Groove> trail, double dpi)
    {
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        var plot = Box(r.Left + 40, r.Top + 16, r.Width - 50, r.Height - 32);
        const double maxFt = 300;
        Point P(double nm, double ft) => new(plot.Right - nm / GrooveNm * plot.Width, plot.Top + plot.Height / 2 - Math.Clamp(ft, -maxFt, maxFt) / maxFt * plot.Height / 2);
        dc.DrawLine(new Pen(Path, 1.4), P(GrooveNm, 0), P(0, 0));
        foreach (var ft in new[] { -200, 200 })
        {
            dc.DrawLine(new Pen(Grid, 0.6), P(GrooveNm, ft), P(0, ft));
            Label(dc, ft > 0 ? "R200" : "L200", new Point(r.Left + 4, P(0, ft).Y - 7), Dim, 9, dpi);
        }
        foreach (var (_, nm) in PassTracker.Marks) dc.DrawLine(new Pen(Grid, 0.8), P(nm, -maxFt), P(nm, maxFt));
        var pts = trail.Where(t => t.AsternNm is > -0.05 and < GrooveNm).Select(t => P(t.AsternNm, t.CrossFt)).ToList();
        for (int i = 1; i < pts.Count; i++) dc.DrawLine(new Pen(Dim, 1.2), pts[i - 1], pts[i]);
        if (gr is { } now && now.AsternNm is > -0.05 and < GrooveNm)
            dc.DrawEllipse(Plane, null, P(now.AsternNm, now.CrossFt), 5, 5);
        Label(dc, "TOP · lineup ft (right of the angled centreline +)", new Point(r.Left + 6, r.Top + 1), Dim, 9, dpi);
    }

    /// <summary>The pass log, newest first: time, pilot, type, result, grade (click to change), comments.</summary>
    private void DrawLog(DrawingContext dc, Rect r, long carrierId, double dpi)
    {
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        Label(dc, "PASS LOG", new Point(r.Left + 8, r.Top + 6), Text, 12, dpi, bold: true);
        Label(dc, "click a grade to change it", new Point(r.Left + 90, r.Top + 8), Dim, 9, dpi);
        var y = r.Top + 28;
        var theatreId = AppState.Store.Theatre?.Id ?? "";
        var passes = AppState.Store.Passes.Log.Where(p => p.Carrier == carrierId).Reverse().ToList();
        if (passes.Count == 0) Label(dc, "No passes yet", new Point(r.Left + 8, y), Dim, 10, dpi);
        foreach (var p in passes)
        {
            if (y > r.Bottom - 34) break;
            var type = p.Type is null ? "" : Symbology.ShortType(p.Type);
            var result = p.Final ? p.Result : "IN THE GROOVE";
            Label(dc, $"{p.Name} {type}  {result}", new Point(r.Left + 8, y), p.Final ? Text : Plane, 11, dpi, bold: true);
            if (p.Final)
            {
                var cell = new Rect(r.Right - 58, y - 1, 50, 18);
                dc.DrawRoundedRectangle(GradeBrush(p.Grade), null, cell, 4, 4);
                Centered(dc, p.Grade, new Point(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2), Scope, 11, dpi);
                _gradeCells.Add((cell, p));
            }
            y += 17;
            Label(dc, $"  {(p.Comments.Length > 0 ? p.Comments : p.Final ? "no deviations noted" : "")}", new Point(r.Left + 8, y), Dim, 10, dpi);
            y += 19;
        }
    }

    private static Brush GradeBrush(string grade) => grade switch
    {
        "OK" => Good,
        "(OK)" => Frozen(Color.FromRgb(0xB8, 0xE0, 0x70)),
        "---" or "B" => Plane,
        _ => Warn,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at = e.GetPosition(this);
        foreach (var (cell, pass) in _gradeCells)
        {
            if (!cell.Contains(at)) continue;
            var i = Array.IndexOf(Grades, pass.Grade);
            AppState.Store.Passes.SetGrade(pass, Grades[(i + 1) % Grades.Length]);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
    }

    private static void Label(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi, bool bold = false) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, brush, dpi), at);

    private static void Centered(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, brush, dpi);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
