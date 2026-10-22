using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Dcb.Client.Core;
using Vec = System.Windows.Vector;

namespace Dcb.Client.Map.Airfields;

/// <summary>The ATC tab's carrier view: base recovery course, speed, final bearing and wind over
/// the deck, the ship's beacons, a bow-up plan of the ship (wheel zoom 1–20 NM, 10 NM at first)
/// with its landing area, the final to 10 NM, the Case I pattern and holding, range rings and the
/// traffic, the recovery case from the weather and the marshal stack.</summary>
public sealed class CarrierView : FrameworkElement
{
    private const double HeaderH = 30, Pad = 8, InfoW = 330, MinNm = 1, MaxNm = 40, FinalNm = 10;

    private static readonly Brush CaseTwo = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly Brush CaseThree = Frozen(Color.FromRgb(0xB4, 0x8C, 0xFF));
    private const double ShipMinPx = 26;  // the hull is drawn at least this long, whatever the zoom

    /// <summary>Plan range: ship to the nearer edge, NM.</summary>
    private double _rangeNm = 10;

    private const double MaxPanNm = 30;  // the Case III marshal sits 21+ NM astern
    private Vec _pan;     // plan centre off the ship, metres (forward, starboard)
    private double _scale = 1;
    private Rect _planRect;
    private Point? _dragFrom;

    private static readonly Brush Panel = Frozen(Color.FromRgb(0x18, 0x20, 0x28));
    private static readonly Brush Scope = Frozen(Color.FromRgb(0x10, 0x16, 0x1C));
    private static readonly Brush Grid = Frozen(Color.FromRgb(0x3A, 0x4A, 0x58));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xDC, 0xE4, 0xEA));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x9A, 0xA8));
    private static readonly Brush Hull = Frozen(Color.FromRgb(0x5A, 0x64, 0x6E));
    private static readonly Brush Deck = Frozen(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly Brush Path = Frozen(Color.FromRgb(0x4C, 0xD9, 0x64));
    private static readonly Brush Own = Frozen(Color.FromRgb(0x5A, 0xA8, 0xFF));
    private static readonly Brush Warn = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));

    private long? _carrier;

    public CarrierView()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => InvalidateVisual();
        // A guidance-line chip toggled: redraw now rather than at the next picture.
        Loaded += (_, _) => CarrierCases.Changed += InvalidateVisual;
        Unloaded += (_, _) => CarrierCases.Changed -= InvalidateVisual;
    }

    /// <summary>Plan range in NM (1–40), as the wheel sets it.</summary>
    public double RangeNm
    {
        get => _rangeNm;
        set
        {
            _rangeNm = Math.Clamp(value, MinNm, MaxNm);
            InvalidateVisual();
        }
    }

    public void Show(long carrierId)
    {
        if (_carrier != carrierId) _pan = default;
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
            Map.Gpu.MapSurface.Log($"carrier view: {ex}");
            dc.DrawRectangle(Panel, null, Box(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Panel, null, Box(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var store = AppState.Store;
        if (ActualWidth < 420 || ActualHeight < 300)
        {
            Centered(dc, "Enlarge the pane to see the carrier", new Point(ActualWidth / 2, ActualHeight / 2), Dim, 12, dpi);
            return;
        }
        if (_carrier is not { } id || store.Carriers.FirstOrDefault(c => c.Id == id) is not { } info
            || CarrierGeometry.For(info.Type) is not { } g || CarrierState.Ship(id) is not { } ship)
        {
            Centered(dc, "Select a carrier", new Point(ActualWidth / 2, ActualHeight / 2), Dim, 14, dpi);
            return;
        }

        var wod = CarrierState.WindOverDeck(ship, info, g);
        var speedKt = Math.Sqrt(ship.Vx * ship.Vx + ship.Vz * ship.Vz) * Groove.KnotsPerMs;
        // The case DCS runs: weather, and Case III whenever the sun is down at the ship.
        double? sun = null;
        if (store.Theatre is { } theatre && Sun.MissionUtc(store.Weather, store.AbsTimeNow, theatre.Id) is { } utc)
        {
            var (lat, lon) = theatre.ToLatLon(ship.X, ship.Z);
            sun = Sun.ElevationDeg(utc, lat, lon);
        }
        var rcase = Marshal.CaseFor(store.Weather, sun);
        CarrierCases.SetCurrent(rcase);
        var header = new List<string> { CarrierGeometry.Label(info.Type).ToUpperInvariant(), $"BRC {CarrierState.Mag(ship, ship.Hdg)}", $"{speedKt:0} KT" };
        if (g.Arrested) header.Add($"FB {CarrierState.Mag(ship, Groove.FinalBearingGrid(ship, g))}");
        header.Add($"WOD {wod.Knots:0} KT ({wod.AxialKt:0} axial, {Math.Abs(wod.CrossKt):0} {(wod.CrossKt >= 0 ? "stbd" : "port")})");
        header.Add($"CASE {Roman(rcase)}");
        Label(dc, string.Join("   ", header), new Point(Pad, 7), Text, 12, dpi, bold: true);

        var aside = ActualWidth >= 760;
        var plan = Box(Pad, HeaderH, ActualWidth - 2 * Pad - (aside ? InfoW + Pad : 0), ActualHeight - HeaderH - Pad - (aside ? 0 : 230));
        var info2 = aside ? Box(ActualWidth - InfoW - Pad, HeaderH, InfoW, ActualHeight - HeaderH - Pad) : Box(Pad, ActualHeight - 230, ActualWidth - 2 * Pad, 222);
        DrawPlan(dc, plan, ship, g, rcase, dpi);
        DrawInfo(dc, info2, ship, info, g, wod, rcase, dpi);
    }

    /// <summary>Bow-up plan around the ship: range rings, the Case I holding circle and pattern
    /// (dimmed when the weather calls for Case II/III), the hull and its angled landing area, the
    /// final astern to 10 NM with distance ticks, and own aircraft (those parked on the deck are
    /// only counted).</summary>
    private void DrawPlan(DrawingContext dc, Rect r, LsoShip ship, CarrierGeometry g, int rcase, double dpi)
    {
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        dc.PushClip(new RectangleGeometry(r));
        const double nmM = Groove.MetersPerNm;
        var scale = _scale = Math.Min(r.Width, r.Height) / 2 / (_rangeNm * nmM);
        _planRect = r;
        var mid = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        var h = ship.Hdg * Math.PI / 180;
        // Ship frame (forward, starboard; metres) → screen, bow up, panned.
        Point S(double fwd, double stbd) => new(mid.X + (stbd - _pan.Y) * scale, mid.Y - (fwd - _pan.X) * scale);
        var c = S(0, 0);
        Point N(double fwdNm, double stbdNm) => S(fwdNm * nmM, stbdNm * nmM);
        Point FromWorld(double x, double z)
        {
            double dx = x - ship.X, dz = z - ship.Z;
            var fwd = dx * Math.Cos(h) + dz * Math.Sin(h);
            var stbd = -dx * Math.Sin(h) + dz * Math.Cos(h);
            return S(fwd, stbd);
        }

        // Range rings at round distances for the zoom.
        var ringStep = _rangeNm switch { <= 2 => 0.5, <= 5 => 1, <= 12 => 2, <= 25 => 5, _ => 10 };
        for (var nm = ringStep; nm <= _rangeNm * 1.45; nm += ringStep)
        {
            dc.DrawEllipse(null, new Pen(Grid, 0.7), c, nm * nmM * scale, nm * nmM * scale);
            Label(dc, $"{nm:0.#}", new Point(c.X + nm * nmM * scale * 0.707 + 3, c.Y - nm * nmM * scale * 0.707 - 12), Grid, 9, dpi);
        }

        var a = g.DeckAngleDeg * Math.PI / 180;
        Point Along(double m) => S(g.TouchdownForward + Math.Cos(a) * m, g.TouchdownStarboard + Math.Sin(a) * m);
        if (g.Arrested)
        {
            // Guidance lines: each recovery case when shown (the current one by default).
            // Positions along the final, in NM astern, and sideways (left of the inbound course +).
            var inF = Math.Cos(a);
            var inS = Math.Sin(a);
            // Left of the inbound course (towards the ship) is to port when it points at the bow.
            Point P(double asternNm, double leftNm) => S(g.TouchdownForward - inF * asternNm * nmM + inS * leftNm * nmM,
                g.TouchdownStarboard - inS * asternNm * nmM - inF * leftNm * nmM);
            if (CarrierCases.Shown(3)) DrawCaseThree(dc, P, dpi);
            if (CarrierCases.Shown(2)) DrawCaseTwo(dc, P, Along(-0.75 * nmM), N, scale, withPattern: !CarrierCases.Shown(1), dpi);
            if (CarrierCases.Shown(1)) DrawCaseOne(dc, Own, holding: true, Along(-0.75 * nmM), N, scale, dpi);
            // The final astern to 10 NM, ticked every 1/4 NM close in, else every NM.
            dc.DrawLine(new Pen(Path, 1.2) { DashStyle = new DashStyle([6, 4], 0) }, Along(0), Along(-FinalNm * nmM));
            var tick = 1 * nmM * scale >= 70 ? 0.25 : 1.0;
            var labelEvery = 1 * nmM * scale >= 22 ? 1 : 2;
            for (var nm = tick; nm <= FinalNm + 1e-9; nm += tick)
            {
                var p = Along(-nm * nmM);
                var whole = Math.Abs(nm - Math.Round(nm)) < 1e-9;
                dc.DrawEllipse(null, new Pen(Path, 1), p, whole ? 2.5 : 1.8, whole ? 2.5 : 1.8);
                if (!whole || (int)Math.Round(nm) % labelEvery == 0 || nm == FinalNm)
                    Label(dc, $"{nm:0.##}", p + new Vec(6, -6), Path, 9, dpi);
            }
        }

        // The hull, never smaller than ShipMinPx; the landing area drawn on it at the same scale.
        var hs = Math.Max(scale, ShipMinPx / g.LengthM);
        Point H(double fwd, double stbd) => new(c.X + stbd * hs, c.Y - fwd * hs);
        double hl = g.LengthM / 2, hb = g.BeamM / 2;
        dc.DrawGeometry(Hull, new Pen(Deck, 1), Poly(H(hl, 0), H(hl * 0.8, hb), H(-hl, hb * 0.8), H(-hl, -hb * 0.8), H(hl * 0.8, -hb)));
        Point HAlong(double m) => H(g.TouchdownForward + Math.Cos(a) * m, g.TouchdownStarboard + Math.Sin(a) * m);
        if (g.Arrested)
        {
            dc.DrawLine(new Pen(Deck, 1.5) { DashStyle = new DashStyle([4, 3], 0) }, HAlong(-25), HAlong(190));
            dc.DrawEllipse(Path, null, HAlong(0), 3, 3);
        }
        else dc.DrawLine(new Pen(Deck, 1.5) { DashStyle = new DashStyle([4, 3], 0) }, H(-hl * 0.9, 0), H(hl * 0.9, 0));

        // Own aircraft (fast feed when running, else the picture); parked ones only counted.
        var store = AppState.Store;
        var queue = AppState.Queues.For(RunwayQueues.CarrierKey(ship.Id));
        var parked = 0;
        foreach (var t in store.Tracks)
        {
            var u = t.Unit;
            if (u.Detected || !Symbology.IsAir(Symbology.KindOf(u)) || u.Lat is not { } lat || u.Lon is not { } lon || store.Theatre is not { } theatre) continue;
            var (x, z) = theatre.FromLatLon(lat, lon);
            if (store.Lso?.Aircraft.FirstOrDefault(f => f.Id == u.Id) is { } fast) (x, z) = (fast.X, fast.Z);
            double dx = x - ship.X, dz = z - ship.Z;
            if (t.OnGround)
            {
                // Landed (here or elsewhere) is not drawn; this ship's deck is counted.
                if (dx * dx + dz * dz < g.LengthM * g.LengthM) parked++;
                continue;
            }
            if ((u.Spd ?? 0) < 40 && (u.Alt ?? 0) < 60 && dx * dx + dz * dz < g.LengthM * g.LengthM)
            {
                parked++;
                continue;
            }
            var p = FromWorld(x, z);
            if (!r.Contains(p)) continue;
            // The runway queue: the place beside the aircraft, opposite its callsign, in the strip's colour.
            if (queue.Place(u.Id) is var place and > 0)
                LabelLeftOf(dc, $"#{place}", p, QueueBase.BrushOf(queue.Entries[place - 1].Kind), 15, dpi, bold: true);
            dc.DrawEllipse(Own, null, p, 4, 4);
            Label(dc, $"{Symbology.NameOf(u) ?? "?"} {(u.AltFt ?? 0) / 100:000}", p + new Vec(7, -7), Own, 10, dpi, bold: true);
        }
        if (parked > 0) LabelLeftOf(dc, $"{parked} on deck", H(-hl * 0.4, -hb), Dim, 9, dpi);
        Label(dc, $"bow up · {_rangeNm:0.#} NM · wheel to zoom · drag to pan{(_pan.Length > 1 ? " · double-click to centre" : "")}", new Point(r.Left + 6, r.Bottom - 18), Dim, 9, dpi);
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_planRect.Contains(e.GetPosition(this))) return;
        if (e.ClickCount == 2) _pan = default;
        else
        {
            _dragFrom = e.GetPosition(this);
            CaptureMouse();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var at = e.GetPosition(this);
        // Screen up = forward (X), screen right = starboard (Y).
        var pan = _pan + new Vec(at.Y - from.Y, -(at.X - from.X)) / _scale;
        var max = MaxPanNm * Groove.MetersPerNm;
        if (pan.Length > max) pan *= max / pan.Length;
        _pan = pan;
        _dragFrom = at;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Case III, schematic, along the final extended (left-hand marshal on the
    /// reciprocal of the final bearing, standard figures): marshal at DME = angels + 15 (first
    /// slot angels 6, 21 DME; +1 NM and +1000 ft per slot), push at 250 kt descending 4000 ft/min
    /// to the platform at 5000 ft, then 2000 ft/min to 1200 ft, level by 12 NM, dirty up at
    /// 10 NM, the 6-mile gate at 1200 ft, glideslope about 3 NM, call the ball at ¾ NM.</summary>
    private static void DrawCaseThree(DrawingContext dc, Func<double, double, Point> p, double dpi)
    {
        var brush = CaseThree;
        var pen = new Pen(brush, 1.2) { DashStyle = new DashStyle([7, 4], 0) };
        const double fix = 21, leg = 6, width = 2.5;
        // The approach from the marshal fix down the final to the ramp.
        dc.DrawLine(pen, p(fix, 0), p(0.75, 0));
        // Marshal: left-hand racetrack, inbound leg ending at the fix.
        var track = new StreamGeometry();
        using (var ctx = track.Open())
        {
            ctx.BeginFigure(p(fix + leg, 0), false, false);
            ctx.LineTo(p(fix, 0), true, false);
            for (var t = 0.0; t <= Math.PI + 1e-9; t += Math.PI / 18)      // left turn at the fix
                ctx.LineTo(p(fix - width / 2 * Math.Sin(t), width / 2 - width / 2 * Math.Cos(t)), true, true);
            ctx.LineTo(p(fix + leg, width), true, false);                 // outbound leg
            for (var t = 0.0; t <= Math.PI + 1e-9; t += Math.PI / 18)      // left turn back inbound
                ctx.LineTo(p(fix + leg + width / 2 * Math.Sin(t), width / 2 + width / 2 * Math.Cos(t)), true, true);
        }
        track.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 1) { DashStyle = new DashStyle([3, 3], 0) }, track);
        void Mark(double nm, string text, bool left = false)
        {
            var at = p(nm, 0);
            dc.DrawEllipse(brush, null, at, 2.5, 2.5);
            if (left) LabelLeftOf(dc, text, at, brush, 9, dpi);
            else Label(dc, text, at + new Vec(8, -6), brush, 9, dpi);
        }
        Mark(fix, "CASE III MARSHAL · A6 · 21 DME");
        Label(dc, "+1 NM, +1000 FT per slot · push 250 KT, 4000 FT/MIN", p(fix, 0) + new Vec(8, 7), brush, 9, dpi);
        Mark(20, "PLATFORM 5000 FT → 2000 FT/MIN");
        Mark(12, "12 NM · LEVEL 1200 FT");
        Mark(10, "10 NM · 1200 FT · DIRTY UP");
        Mark(6, "6 NM · 1200 FT");
        Mark(3, "3 NM · GLIDESLOPE ~1100 FT");
        Mark(0.75, "¾ NM · CALL THE BALL");
    }

    /// <summary>Case II: depart marshal and descend as Case III to 10 NM at 1200 ft, then
    /// visually to 800 ft up the initial and a Case I break (drawn here when Case I is not).</summary>
    private static void DrawCaseTwo(DrawingContext dc, Func<double, double, Point> p, Point x, Func<double, double, Point> n,
        double scale, bool withPattern, double dpi)
    {
        var brush = CaseTwo;
        var pen = new Pen(brush, 1.2) { DashStyle = new DashStyle([7, 4], 0) };
        dc.DrawLine(pen, p(21, -0.15), p(10, -0.15));
        dc.DrawLine(pen, p(10, -0.15), n(-3, 0));                        // visual: to the initial
        void Mark(Point at, string text)
        {
            dc.DrawEllipse(brush, null, at, 2.5, 2.5);
            LabelLeftOf(dc, text, at, brush, 9, dpi);
        }
        Mark(p(21, -0.15), "CASE II · DEPART MARSHAL, DESCEND AS CASE III");
        Mark(p(10, -0.15), "10 NM · 1200 FT · VISUAL → 800 FT, CASE I BREAK");
        if (withPattern) DrawCaseOne(dc, brush, holding: false, x, n, scale, dpi);
    }

    /// <summary>Case I, schematic and relative to the ship: the holding circle (left-hand, 5 NM
    /// across, tangent to the BRC line with the ship off the right wing), the initial from 3 NM
    /// astern at 800 ft, the left break ahead of the ship, downwind ~1.2 NM to port at 600 ft,
    /// the 180, the 90 at 450 ft and the turn into the groove at the X.</summary>
    private static void DrawCaseOne(DrawingContext dc, Brush brush, bool holding, Point x, Func<double, double, Point> n, double scale, double dpi)
    {
        var pen = new Pen(brush, 1.1) { DashStyle = new DashStyle([5, 4], 0) };
        const double nmM = Groove.MetersPerNm;
        if (holding)
        {
            dc.DrawEllipse(null, new Pen(brush, 0.9) { DashStyle = new DashStyle([2, 4], 0) }, n(0, -2.5), 2.5 * nmM * scale, 2.5 * nmM * scale);
            Label(dc, "CASE I HOLD · left · angels 2+", n(2.5, -4.6), brush, 9, dpi);
        }

        const double breakFwd = 0.3, downwind = -1.2;
        // The final turn ends at the X, on the angled final: its centre is midway across.
        var xOff = (x.X - n(0, 0).X) / (nmM * scale);                  // the X's starboard offset, NM
        var xFwd = -(x.Y - n(0, 0).Y) / (nmM * scale);                 // and its forward one, NM
        var turnR = (xOff - downwind) / 2;
        var turnC = (Fwd: xFwd, Stbd: downwind + turnR);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(n(-3, 0), false, false);                     // initial
            ctx.LineTo(n(breakFwd, 0), true, false);
            for (var t = 0.0; t <= Math.PI + 1e-9; t += Math.PI / 24)     // left break
                ctx.LineTo(n(breakFwd + 0.6 * Math.Sin(t), -0.6 + 0.6 * Math.Cos(t)), true, true);
            ctx.LineTo(n(turnC.Fwd, downwind), true, false);             // downwind to the 180
            for (var t = 0.0; t <= Math.PI + 1e-9; t += Math.PI / 24)     // the 90 and into the groove
                ctx.LineTo(n(turnC.Fwd - turnR * Math.Sin(t), turnC.Stbd - turnR * Math.Cos(t)), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);

        void Mark(Point p, string text)
        {
            dc.DrawEllipse(brush, null, p, 2.5, 2.5);
            LabelLeftOf(dc, text, p, brush, 9, dpi);
        }
        Mark(n(-3, 0), "INITIAL 800 FT");
        Mark(n(breakFwd + 0.6, -0.6), "BREAK");
        Mark(n(turnC.Fwd, downwind), "180 · 600 FT");
        Mark(n(turnC.Fwd - turnR, turnC.Stbd), "90 · 450 FT");
        dc.DrawEllipse(brush, null, x, 2.5, 2.5);
        Label(dc, "X", x + new Vec(-12, 2), brush, 9, dpi);
    }

    protected override void OnMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        RangeNm = _rangeNm * Math.Pow(1.25, -e.Delta / 120.0);
        e.Handled = true;
    }

    private void DrawInfo(DrawingContext dc, Rect r, LsoShip ship, CarrierInfo info, CarrierGeometry g,
        (double Knots, double AxialKt, double CrossKt) wod, int rcase, double dpi)
    {
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        var x = r.Left + 8;
        var y = r.Top + 6;
        double Line(string text, Brush brush, bool bold = false, double size = 11)
        {
            Label(dc, text, new Point(x, y), brush, size, dpi, bold);
            return y += size + 5;
        }
        Line("BEACONS", Text, bold: true);
        Line($"TACAN {info.Tacan ?? "—"}   ICLS {(info.Icls is { } i ? i.ToString() : "—")}", Text);
        Line($"Link 4 {(info.Link4 is { } l ? l.ToString("0.000", CultureInfo.InvariantCulture) : "—")}   ACLS {(info.Acls ? "on" : "—")}   radio {(info.Radio is { } rr ? rr.ToString("0.000", CultureInfo.InvariantCulture) : "—")}", Text);
        y += 4;
        Line("DECK", Text, bold: true);
        Line(g.Arrested ? $"Angled deck {Math.Abs(g.DeckAngleDeg):0}° port · glideslope {g.GlideslopeDeg:0.0}°" : "No arrested recovery (STOVL / helicopters)", Text);
        var wodLow = g.Arrested && wod.AxialKt < 20;
        Line($"Wind over deck {wod.Knots:0} kt, {wod.AxialKt:0} down the angle", wodLow ? Warn : Text);
        if (wodLow) Line("Below ~25 kt axial: ask the ship for more speed into the wind", Warn, size: 10);
        y += 4;
        Line($"MARSHAL — CASE {Roman(rcase)}", Text, bold: true);
        Line(rcase == 1 ? "Overhead, left-hand 5 NM circle, angels by slot"
            : $"Radial {CarrierState.Mag(ship, Nav.Normalize(Groove.FinalBearingGrid(ship, g) + 180))} · angels 6+ · DME angels+15 · push 1 min apart", Dim, size: 10);
        var slots = Marshal.Stack(rcase, Inbound(ship), AppState.Store.AbsTimeNow ?? 0);
        if (slots.Count == 0) Line("No inbound carrier aircraft within 60 NM", Dim, size: 10);
        var theatreId = AppState.Store.Theatre?.Id ?? "";
        foreach (var s in slots)
        {
            if (y > r.Bottom - 30) break;
            var name = Symbology.NameOf(s.Unit) ?? "?";
            var type = s.Unit.Type is null ? "" : Symbology.ShortType(s.Unit.Type);
            var where = s.Dme is { } dme ? $"A{s.Angels} {dme} NM" : $"A{s.Angels}";
            var push = s.PushTime is { } pt ? "  push " + Clock(pt, theatreId) : "";
            Line($"{s.Number}. {name} {type}  {where}{push}", Own, bold: true, size: 10.5);
            Line($"    now {s.RangeNm:0} NM  {(s.Unit.AltFt ?? 0):0} FT  {(s.Unit.SpdKt ?? 0):0} KT", Dim, size: 10);
        }
    }

    /// <summary>Own carrier-capable aircraft in the air, with their range to the ship.</summary>
    private static IEnumerable<(Unit Unit, double RangeNm)> Inbound(LsoShip ship)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre) yield break;
        var (slat, slon) = theatre.ToLatLon(ship.X, ship.Z);
        foreach (var t in store.Tracks)
        {
            var u = t.Unit;
            if (u.Detected || !Marshal.CarrierCapable(u.Type) || u.Lat is not { } lat || u.Lon is not { } lon) continue;
            if ((u.Spd ?? 0) < 40 && (u.Alt ?? 0) < 60) continue;  // on deck
            yield return (u, Nav.BearingRange(slat, slon, lat, lon).Meters / Groove.MetersPerNm);
        }
    }

    private static string Clock(double absSeconds, string theatreId)
    {
        var (time, zulu) = AtisText.Clock(absSeconds, theatreId);
        return $"{time:hh\\:mm}{(zulu ? "Z" : "L")}";
    }

    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", _ => "III" };

    private static Geometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            c.PolyLineTo(pts[1..], true, false);
        }
        g.Freeze();
        return g;
    }

    private static void Label(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi, bool bold = false) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, brush, dpi), at);

    /// <summary>Text ending just left of a point, vertically centred on it.</summary>
    private static void LabelLeftOf(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi, bool bold = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, brush, dpi);
        dc.DrawText(ft, new Point(at.X - ft.Width - 6, at.Y - ft.Height / 2));
    }

    private static void Centered(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
