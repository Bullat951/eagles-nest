using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Dcb.Client.Core;
using Vec = System.Windows.Vector;

namespace Dcb.Client.Map.Airfields;

/// <summary>The ATC tab's approach view: 10 NM around an airfield, true north up, for ordering
/// traffic — range rings at 2/5/10 NM from the landing threshold, the extended centreline with
/// inbound and outbound arrows, 3° glidepath altitudes at 2/5/10 NM, the field's ILS and TACAN,
/// live traffic with altitude, groundspeed, range to the threshold and height against the
/// glidepath, lines from the field to the aircraft in the runway queue (place, bearing/range)
/// and a side-view profile of the final approach. The runway end in use follows the wind (as
/// the ATIS) until the controller picks another by clicking the header. Dragging pans the scope
/// up to 10 NM off the threshold; a double-click centres it again.</summary>
public sealed class ApproachView : FrameworkElement
{
    private const double MetersPerNm = 1852;
    private const double FeetPerMeter = 3.28084;
    private const double RangeNm = 10;  // the final drawn, the glidepath marks and the profile
    private const double MinZoomNm = 2, MaxZoomNm = 20;
    private double _zoomNm = RangeNm;   // threshold to the scope's nearer edge (wheel)
    private const double GlideDeg = 3;
    private static readonly double FeetPerNmOnPath = Math.Tan(GlideDeg * Math.PI / 180) * MetersPerNm * FeetPerMeter;  // ≈ 318
    private static readonly int[] Marks = [2, 5, 10];
    private const double HeaderH = 30, ProfileH = 150, Pad = 8;
    private const double MaxPanNm = 10, ContactNm = 32;

    private static readonly Brush Scope = Frozen(Color.FromRgb(0x10, 0x16, 0x1C));
    private static readonly Brush Panel = Frozen(Color.FromRgb(0x18, 0x20, 0x28));
    private static readonly Brush Grid = Frozen(Color.FromRgb(0x3A, 0x4A, 0x58));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xDC, 0xE4, 0xEA));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x9A, 0xA8));
    private static readonly Brush RunwayInk = Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush Path = Frozen(Color.FromRgb(0x4C, 0xD9, 0x64));
    private static readonly Brush Outbound = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly Brush Friendly = Frozen(Color.FromRgb(0x5A, 0xA8, 0xFF));
    private static readonly Brush Hostile = Frozen(Color.FromRgb(0xFF, 0x5A, 0x5A));
    private static readonly Brush Unknown = Frozen(Color.FromRgb(0xF0, 0xD0, 0x50));

    private Airfield? _field;
    private Theatre? _theatre;
    private LocalFrame? _frame;
    private IReadOnlyList<Beacon> _beacons = [];
    private int? _pickedEnd;  // index into Ends() chosen by the controller; null = by the wind
    private Vec _pan;         // scope centre off the threshold, metres east/north
    private double _scale = 1;
    private Point? _dragFrom;

    /// <summary>A runway end to land on: designator, threshold (DCS x/z), landing course from
    /// grid north and from true north, threshold elevation (ft).</summary>
    private sealed record End(string Name, Runway Runway, double X, double Z, double CourseGrid, double CourseTrue, double ElevFt);

    /// <summary>An aircraft relative to the approach in use.</summary>
    private sealed record Contact(Unit Unit, Brush Brush, double E, double N, double RangeNm, double AlongNm, double CrossNm, double AltFt, double? AboveFt, bool Inbound);

    public ApproachView()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => InvalidateVisual();
        DcsAirfieldData.Loaded += () => Dispatcher.BeginInvoke(() => { if (_field is not null) Show(_field); });
    }

    public void Show(Airfield field)
    {
        if (_field?.Id != field.Id)
        {
            _pickedEnd = null;
            _pan = default;
        }
        _field = field;
        _theatre = AppState.Store.Theatre;
        if (_theatre is null) return;
        var (ox, oz) = field.Runways.Count > 0 ? (field.Runways.Average(r => r.X), field.Runways.Average(r => r.Z)) : (field.X, field.Z);
        _frame = new LocalFrame(_theatre, ox, oz);
        _beacons = DcsAirfieldData.BeaconsFor(_theatre.Id, field.Id);
        InvalidateVisual();
    }

    private List<End> Ends()
    {
        var ends = new List<End>();
        if (_field is null || _frame is null) return ends;
        foreach (var rw in _field.Runways)
        {
            var (near, far) = AirfieldDiagramView.Names(_field, rw);
            var h = rw.HeadingTrue * Math.PI / 180;
            double ux = Math.Cos(h) * rw.Length / 2, uz = Math.Sin(h) * rw.Length / 2;
            ends.Add(MakeEnd(near, rw, rw.X - ux, rw.Z - uz, rw.HeadingTrue));
            ends.Add(MakeEnd(far, rw, rw.X + ux, rw.Z + uz, Nav.Normalize(rw.HeadingTrue + 180)));
        }
        return ends;
    }

    private End MakeEnd(string name, Runway rw, double x, double z, double courseGrid)
    {
        var elev = AppState.Store.Terrain is { } g && LineOfSight.HeightAt(g, x, z) is { } m ? m * FeetPerMeter
            : AppState.Store.Weather?.Airfields.FirstOrDefault(a => a.Id == _field!.Id) is { } w ? w.Elevation * FeetPerMeter : 0;
        return new End(name, rw, x, z, courseGrid, Nav.Normalize(courseGrid + _frame!.GridNorthBearing), elev);
    }

    /// <summary>The end in use: the controller's pick, else the ATIS's from the wind, else the first.</summary>
    private End? ActiveEnd(List<End> ends)
    {
        if (ends.Count == 0) return null;
        if (_pickedEnd is { } i && i < ends.Count) return ends[i];
        if (_field is not null && AppState.Store.Weather?.Airfields.FirstOrDefault(a => a.Id == _field.Id) is { } wx
            && AtisText.RunwayInUse(_field, wx) is { } name && ends.FirstOrDefault(e => e.Name == name) is { } byWind)
            return byWind;
        return ends[0];
    }

    // ---- Layout ----------------------------------------------------------------------------------

    private Rect ScopeRect => Box(Pad, HeaderH, Math.Max(1, ActualWidth - 2 * Pad), Math.Max(1, ActualHeight - HeaderH - ProfileH - 2 * Pad));
    private Rect ProfileRect => Box(Pad, ActualHeight - ProfileH - Pad, Math.Max(1, ScopeRect.Width), ProfileH);

    /// <summary>A rectangle that is never negative (docking panes pass through tiny sizes while dragged).</summary>
    private static Rect Box(double x, double y, double w, double h) => new(x, y, Math.Max(0, w), Math.Max(0, h));

    private bool TooSmall => ActualWidth < 420 || ActualHeight < 320;

    /// <summary>DIP per metre: the zoom range (+5 %) from the threshold to the scope's edge, every way.</summary>
    private double ScaleFor(Rect r) => Math.Min(r.Width, r.Height) / 2 / (_zoomNm * 1.08 * MetersPerNm);

    // ---- Rendering --------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            Render(dc);
        }
        catch (Exception ex)
        {
            // A drawing fault must never take the app down: log it.
            Map.Gpu.MapSurface.Log($"approach view: {ex}");
            dc.DrawRectangle(Panel, null, Box(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Panel, null, Box(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (TooSmall)
        {
            Centered(dc, "Enlarge the pane to see the approach view", new Point(ActualWidth / 2, ActualHeight / 2), Dim, 12, dpi);
            return;
        }
        var ends = Ends();
        if (_field is not { } field || _frame is null || ActiveEnd(ends) is not { } end)
        {
            Centered(dc, "Select an airfield with a runway", new Point(ActualWidth / 2, ActualHeight / 2), Dim, 14, dpi);
            return;
        }
        var scope = ScopeRect;
        dc.DrawRectangle(Scope, new Pen(Grid, 1), scope);
        var scale = _scale = ScaleFor(scope);
        var (te, tn) = _frame.Local(end.X, end.Z);
        Point S(double e, double n) => new(scope.Left + scope.Width / 2 + (e - te - _pan.X) * scale, scope.Top + scope.Height / 2 - (n - tn - _pan.Y) * scale);
        Point Sxz(double x, double z) { var (e, n) = _frame.Local(x, z); return S(e, n); }
        var thr = S(te, tn);

        var contacts = Contacts(end);

        dc.PushClip(new RectangleGeometry(scope));
        // Range rings from the threshold: 2/5/10 NM, plus 15/20 when zoomed out.
        foreach (var nm in new[] { 2, 5, 10, 15, 20 }.Where(n => n <= 10 || n <= _zoomNm * 1.5))
        {
            var r = nm * MetersPerNm * scale;
            dc.DrawEllipse(null, new Pen(Grid, 1) { DashStyle = new DashStyle([3, 4], 0) }, thr, r, r);
            Label(dc, $"{nm} NM", new Point(thr.X + r * 0.71 + 3, thr.Y - r * 0.71 - 12), Dim, 10, dpi);
        }
        // Runways.
        foreach (var rw in field.Runways)
        {
            var h = rw.HeadingTrue * Math.PI / 180;
            double ux = Math.Cos(h) * rw.Length / 2, uz = Math.Sin(h) * rw.Length / 2;
            dc.DrawLine(new Pen(RunwayInk, Math.Max(3, rw.Width * scale)), Sxz(rw.X - ux, rw.Z - uz), Sxz(rw.X + ux, rw.Z + uz));
        }
        // Final approach: extended centreline from the threshold outwards, arrow pointing in.
        var c = end.CourseGrid * Math.PI / 180;
        double cx = Math.Cos(c), cz = Math.Sin(c);  // landing direction (grid)
        Point Out(double nm) => Sxz(end.X - cx * nm * MetersPerNm, end.Z - cz * nm * MetersPerNm);
        dc.DrawLine(new Pen(Path, 1.5) { DashStyle = new DashStyle([6, 4], 0) }, thr, Out(RangeNm));
        Arrow(dc, Out(RangeNm * 0.8), Out(RangeNm * 0.62), Path);
        foreach (var nm in Marks)
        {
            var p = Out(nm);
            var d = Out(nm + 0.01) - p;
            d.Normalize();
            var n = new Vec(-d.Y, d.X);
            dc.DrawLine(new Pen(Path, 2), p - n * 7, p + n * 7);
            var alt = end.ElevFt + nm * FeetPerNmOnPath;
            Label(dc, $"{nm} NM  {Math.Round(alt / 10) * 10:0} FT", p + n * 10, Path, 10, dpi, bold: true);
        }
        // Departure: from the far end outwards along the landing direction.
        var far = Sxz(end.X + cx * end.Runway.Length, end.Z + cz * end.Runway.Length);
        var dep = Sxz(end.X + cx * (end.Runway.Length + 3 * MetersPerNm), end.Z + cz * (end.Runway.Length + 3 * MetersPerNm));
        dc.DrawLine(new Pen(Outbound, 1.5) { DashStyle = new DashStyle([2, 3], 0) }, far, dep);
        Arrow(dc, far + (dep - far) * 0.5, dep, Outbound);
        Label(dc, "DEP", dep + new Vec(6, -6), Outbound, 10, dpi, bold: true);
        // Navaids of the field.
        foreach (var b in _beacons.Where(b => b.IsTacan || b.Type.StartsWith("VOR") || b.Kind == "NDB"))
        {
            var p = Sxz(b.X, b.Z);
            dc.DrawEllipse(null, new Pen(Dim, 1), p, 4, 4);
            Label(dc, b.IsTacan ? $"{b.Callsign} {b.ChannelText}" : $"{b.Callsign}", p + new Vec(6, 2), Dim, 9, dpi);
        }
        // The runway queue: each aircraft's place, in its strip colour.
        var queue = AppState.Queues.For(RunwayQueues.AirfieldKey(field.Id));
        foreach (var k in contacts)
        {
            var place = queue.Place(k.Unit.Id);
            if (place == 0) continue;
            var brush = QueueBase.BrushOf(queue.Entries[place - 1].Kind);
            var p = S(k.E, k.N);
            // The place beside the aircraft, opposite its callsign.
            var tag = Format($"#{place}", brush, 15, true, dpi);
            dc.DrawText(tag, new Point(p.X - tag.Width - 8, p.Y - tag.Height / 2));
        }
        // Traffic.
        foreach (var k in contacts)
        {
            var p = S(k.E, k.N);
            if (k.Unit.Hdg is { } hdg)
            {
                var rad = (hdg + _frame.GridNorthBearing) * Math.PI / 180;
                var spd = k.Unit.Spd ?? 0;
                var lead = Math.Max(14, spd * 60 * scale);  // one minute ahead
                dc.DrawLine(new Pen(k.Brush, 1.3), p, new Point(p.X + Math.Sin(rad) * lead, p.Y - Math.Cos(rad) * lead));
            }
            dc.DrawEllipse(k.Brush, null, p, 4.5, 4.5);
            var name = Symbology.NameOf(k.Unit) ?? (k.Unit.Type is null ? "UNK" : Symbology.ShortType(k.Unit.Type));
            Label(dc, name, p + new Vec(8, -16), k.Brush, 11, dpi, bold: true);
            var line2 = $"{k.AltFt / 100:000}  {k.Unit.SpdKt ?? 0:0}KT  {k.RangeNm:0.0}NM";
            if (k.AboveFt is { } above) line2 += $"  {(above >= 0 ? "+" : "")}{Math.Round(above / 10) * 10:0}";
            Label(dc, line2, p + new Vec(8, -3), k.Brush, 10, dpi);
        }
        dc.Pop();

        DrawHeader(dc, field, end, ends, dpi);
        DrawProfile(dc, end, contacts, dpi);
        Label(dc, $"{_zoomNm:0.#} NM · wheel to zoom · drag to pan{(_pan.Length > 1 ? " · double-click to centre" : "")}", new Point(scope.Left + 6, scope.Bottom - 18), Dim, 9, dpi);
    }

    /// <summary>Air contacts within 12 NM of the threshold, relative to the final approach:
    /// along = distance out along the extended centreline, cross = sideways; height against the
    /// 3° path for those on final (within ±2 NM sideways, 0–12 NM out).</summary>
    private List<Contact> Contacts(End end)
    {
        var list = new List<Contact>();
        var store = AppState.Store;
        if (store.Coalition is not { } own || _frame is null) return list;
        var (te, tn) = _frame.Local(end.X, end.Z);
        var c = end.CourseTrue * Math.PI / 180;
        double le = Math.Sin(c), ln = Math.Cos(c);  // landing direction, east/north
        foreach (var t in store.Tracks)
        {
            var u = t.Unit;
            // Landed aircraft are the diagram's (ground traffic), not the approach's.
            if (!Symbology.IsAir(Symbology.KindOf(u)) || t.OnGround || u.Lat is not { } lat || u.Lon is not { } lon || u.Alt is not { } altM) continue;
            var (x, z) = _frame.FromLatLon(lat, lon);
            var (e, n) = _frame.Local(x, z);
            double de = e - te, dn = n - tn;
            var range = Math.Sqrt(de * de + dn * dn) / MetersPerNm;
            if (range > ContactNm) continue;  // the scope pans up to 10 NM off the threshold
            var alongNm = -(de * le + dn * ln) / MetersPerNm;   // positive: out on the approach side
            var crossNm = (de * ln - dn * le) / MetersPerNm;
            var altFt = altM * FeetPerMeter;
            double? above = alongNm is > 0 and < 12 && Math.Abs(crossNm) < 2 ? altFt - (end.ElevFt + alongNm * FeetPerNmOnPath) : null;
            // Inbound: airborne and its track closes on the threshold.
            var inbound = false;
            if (u.Hdg is { } hdg && (u.Spd ?? 0) > 30 && altFt - end.ElevFt > 150)
            {
                var trk = (hdg + _frame.GridNorthBearing) * Math.PI / 180;
                inbound = (Math.Sin(trk) * -de + Math.Cos(trk) * -dn) > 0.5 * Math.Sqrt(de * de + dn * dn);
            }
            var brush = Symbology.IdentityOf(u, own) switch { Identity.Friendly => Friendly, Identity.Hostile => Hostile, _ => Unknown };
            list.Add(new Contact(u, brush, e, n, range, alongNm, crossNm, altFt, above, inbound));
        }
        return list;
    }

    private void DrawHeader(DrawingContext dc, Airfield field, End end, List<End> ends, double dpi)
    {
        var parts = new List<string> { $"{field.Name.Replace('_', ' ').ToUpperInvariant()}  RWY {end.Name} ▸", $"CRS {MagText(end.CourseTrue, end.X, end.Z)}", $"THR {end.ElevFt:0} FT" };
        // This end's ILS: a localizer serving this very runway, inbound along the landing course.
        var ils = _beacons.FirstOrDefault(b => b.IsLocalizer && ReferenceEquals(AirfieldDiagramView.RunwayFor(field, b), end.Runway)
            && Math.Abs(Nav.Normalize(b.InboundCourseTrue - end.CourseGrid + 180) - 180) < 90);
        if (ils is not null) parts.Add($"ILS {ils.Callsign} {ils.FrequencyText}");
        if (_beacons.FirstOrDefault(b => b.IsTacan) is { } tcn) parts.Add($"TACAN {tcn.Callsign} {tcn.ChannelText}");
        if (AppState.Store.Weather?.Airfields.FirstOrDefault(a => a.Id == field.Id) is { } wx)
            parts.Add($"QNH {wx.QnhHpa:0} / {wx.QnhHpa * 0.02953:0.00}");
        parts.Add($"3° PATH {FeetPerNmOnPath:0} FT/NM");
        Label(dc, string.Join("   ", parts), new Point(Pad, 7), Text, 12, dpi, bold: true);
    }

    /// <summary>Side view of the final approach: distance out (0–10 NM) against height above the
    /// threshold, the 3° path with its 2/5/10 NM altitudes, and the aircraft on final.</summary>
    private void DrawProfile(DrawingContext dc, End end, List<Contact> contacts, double dpi)
    {
        var r = ProfileRect;
        dc.DrawRectangle(Scope, new Pen(Grid, 1), r);
        var plot = new Rect(r.Left + 46, r.Top + 10, r.Width - 56, r.Height - 30);
        const double maxFt = 4000;
        Point P(double nm, double ftAboveThr) => new(plot.Right - nm / RangeNm * plot.Width, plot.Bottom - Math.Clamp(ftAboveThr, -200, maxFt) / maxFt * plot.Height);
        foreach (var ft in new[] { 1000, 2000, 3000, 4000 })
        {
            dc.DrawLine(new Pen(Grid, 0.6), P(RangeNm, ft), P(0, ft));
            Label(dc, $"{ft}", new Point(r.Left + 4, P(0, ft).Y - 7), Dim, 9, dpi);
        }
        Label(dc, "FT ABOVE THR · path altitudes MSL", new Point(plot.Left + 4, r.Top + 2), Dim, 9, dpi);
        dc.DrawLine(new Pen(Dim, 1), P(RangeNm, 0), P(0, 0));
        dc.DrawLine(new Pen(Path, 1.6), P(0, 0), P(RangeNm, RangeNm * FeetPerNmOnPath));
        foreach (var nm in Marks)
        {
            dc.DrawLine(new Pen(Grid, 0.8), P(nm, 0), P(nm, maxFt));
            Label(dc, $"{nm}", new Point(P(nm, 0).X - 3, plot.Bottom + 3), Dim, 9, dpi);
            var alt = end.ElevFt + nm * FeetPerNmOnPath;
            Label(dc, $"{Math.Round(alt / 10) * 10:0}", P(nm, nm * FeetPerNmOnPath) + new Vec(4, -14), Path, 9, dpi, bold: true);
        }
        Label(dc, "NM", new Point(plot.Right - 14, plot.Bottom + 3), Dim, 9, dpi);
        Label(dc, $"RWY {end.Name}", new Point(plot.Right - 50, plot.Top), Dim, 9, dpi);
        foreach (var k in contacts.Where(k => k.AboveFt is not null && k.AlongNm <= RangeNm))
        {
            var p = P(k.AlongNm, k.AltFt - end.ElevFt);
            dc.DrawEllipse(k.Brush, null, p, 4, 4);
            var name = Symbology.NameOf(k.Unit) ?? "UNK";
            Label(dc, $"{name} {(k.AboveFt >= 0 ? "+" : "")}{Math.Round(k.AboveFt!.Value / 10) * 10:0}", p + new Vec(6, -14), k.Brush, 9, dpi, bold: true);
        }
    }

    private string MagText(double trueDeg, double x, double z)
    {
        if (_theatre?.MagVar is not { } mv) return $"{Nav.Bearing3(trueDeg)}°T";
        var (lat, lon) = _theatre.ToLatLon(x, z);
        return $"{Nav.Bearing3(Nav.Magnetic(trueDeg, mv.At(lat, lon)))}°";
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static void Arrow(DrawingContext dc, Point from, Point to, Brush brush)
    {
        dc.DrawLine(new Pen(brush, 1.8), from, to);
        var d = to - from;
        if (d.Length < 1) return;
        d.Normalize();
        var n = new Vec(-d.Y, d.X);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(to, true, true);
            c.PolyLineTo([to - d * 10 + n * 5, to - d * 10 - n * 5], true, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    private static void Label(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi, bool bold = false) =>
        dc.DrawText(Format(text, brush, size, bold, dpi), at);

    private static void Centered(DrawingContext dc, string text, Point at, Brush brush, double size, double dpi)
    {
        var ft = Format(text, brush, size, false, dpi);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static FormattedText Format(string text, Brush brush, double size, bool bold, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);

    // ---- Interaction: a click on the header picks the next runway end; the scope drags ---------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var at = e.GetPosition(this);
        if (ScopeRect.Contains(at))
        {
            if (e.ClickCount == 2) _pan = default;
            else
            {
                _dragFrom = at;
                CaptureMouse();
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (at.Y > HeaderH) return;
        var ends = Ends();
        if (ends.Count == 0) return;
        var current = ActiveEnd(ends);
        var i = current is null ? 0 : ends.IndexOf(current);
        _pickedEnd = (i + 1) % ends.Count;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var at = e.GetPosition(this);
        // Screen right = east, screen up = north.
        var pan = _pan + new Vec(-(at.X - from.X), at.Y - from.Y) / _scale;
        var max = MaxPanNm * MetersPerNm;
        if (pan.Length > max) pan *= max / pan.Length;
        _pan = pan;
        _dragFrom = at;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _zoomNm = Math.Clamp(_zoomNm * Math.Pow(1.25, -e.Delta / 120.0), MinZoomNm, MaxZoomNm);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
