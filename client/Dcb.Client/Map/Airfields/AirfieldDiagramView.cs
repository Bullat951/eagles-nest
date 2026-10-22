using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Dcb.Client.Core;
using Vec = System.Windows.Vector;

namespace Dcb.Client.Map.Airfields;

/// <summary>DCB's airport diagram for one airfield as a document page in the style of printed
/// airport diagrams: header (field, ICAO, position, elevation, variation), the plan (pavement,
/// runways with heading arrows and ILS feathers, one taxiway letter per leg, stand groups,
/// tower and navaids, live traffic), an information block (frequencies, navaids, runways with
/// ILS) and a footer. All geometry and numbers are DCS data — pavement from the terrain mesh,
/// taxiway network and letters from the field's rn4 and its signs, navaids from beacons.lua,
/// runways and stands from the server; ICAO codes from the C-130J's nav database; area names and
/// letters for unsigned taxiways from placed charts (local only). Imperial units. True north up;
/// pan/zoom inside the plan frame, clamped to the field.</summary>
public sealed class AirfieldDiagramView : FrameworkElement
{
    private const double MarginM = 250;           // around the field's extent
    private const double MaxScale = 6;            // DIP per metre, closest zoom
    private const double LiveRadiusM = 6000;      // traffic drawn within this distance of the field
    private const double SignRadiusM = 4500;      // DCS signs looked up around the field
    private const double StandNumbersScale = 0.9; // DIP per metre from which single stand numbers replace groups
    private const double StandClusterM = 150;     // stands this close (chained) form one group
    private const double LabelEveryM = 1500;      // a long taxiway leg repeats its letter
    private const double FeetPerMeter = 3.28084;
    private const double MetersPerNm = 1852;

    // Page layout (DIP).
    private const double PageInset = 8, HeaderH = 54, FooterH = 20, InfoW = 270, InfoH = 150, Pad = 8;

    private static readonly Brush Desk = Frozen(Color.FromRgb(0xD4, 0xD4, 0xD0));
    private static readonly Brush Paper = Frozen(Colors.White);
    private static readonly Brush Pavement = Frozen(Color.FromRgb(0xA9, 0xA5, 0x9A));
    private static readonly Brush RunwayFill = Frozen(Colors.Black);
    private static readonly Brush Marking = Frozen(Colors.White);
    private static readonly Brush Ink = Frozen(Colors.Black);
    private static readonly Brush StandBadge = Frozen(Color.FromRgb(0x2E, 0x5C, 0x9A));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x5A, 0x5A, 0x5A));
    private static readonly Brush NavaidInk = Frozen(Color.FromRgb(0x6A, 0x2C, 0x8A));
    private static readonly Brush Warning = Frozen(Color.FromRgb(0xC0, 0x10, 0x10));
    private static readonly Brush HaloLight = Frozen(Colors.White);

    /// <summary>A taxiway name placed on the plan; <c>FromChart</c> when DCS has no sign for it.</summary>
    private sealed record TaxiLabel(string Text, double X, double Z, bool FromChart, double LegLength);

    private Airfield? _field;
    private Theatre? _theatre;
    private LocalFrame? _frame;
    private (double MinE, double MinN, double MaxE, double MaxN) _extent;
    private double _scale;            // DIP per metre
    private double _centerE, _centerN; // view centre in local metres (east, north of origin)
    private Point? _dragFrom;
    private bool _userView;            // zoomed or panned by the controller since the last fit
    private Geometry? _pavement;       // local metres (east, −north), frozen
    private List<(double WidthM, Geometry Lines)> _network = [];  // taxiway network at its widths
    private IReadOnlyList<(IReadOnlyList<int> Numbers, double X, double Z)> _stands = [];
    private readonly List<Rect> _placed = [];  // labels drawn this frame: nothing is drawn over them
    private IReadOnlyList<TaxiLabel> _taxiLabels = [];
    private IReadOnlyList<DiagramLabel> _areas = [];
    private IReadOnlyList<Beacon> _beacons = [];
    private (double X, double Z)? _tower;
    private string? _icao;

    public IReadOnlyList<DiagramLabel> Labels { get; set; } = [];

    /// <summary>Where the taxiway letters come from: "DCS signs", "DCS signs + chart", "chart" or "".</summary>
    public string LetterSource { get; private set; } = "";

    public AirfieldDiagramView()
    {
        ClipToBounds = true;
        Focusable = true;
        Taxiways.Loaded += () => Dispatcher.BeginInvoke(() => { if (_field is not null) { Show(_field); } });
        DcsAirfieldData.Loaded += () => Dispatcher.BeginInvoke(() => { if (_field is not null) { Show(_field); } });
        // Until the controller zooms or pans, a resize (also the first layout) refits the field.
        SizeChanged += (_, _) =>
        {
            if (_field is not null && !_userView) Fit();
            else ClampView();
            InvalidateVisual();
        };
    }

    public Airfield? Field => _field;

    /// <summary>Snapshot aid: field, extent, view and scale.</summary>
    public string DebugState =>
        $"{_field?.Name} ({_icao}) extent E {_extent.MinE:F0}..{_extent.MaxE:F0} N {_extent.MinN:F0}..{_extent.MaxN:F0} plan {Plan.Width:F0}x{Plan.Height:F0} " +
        $"scale {_scale:F3} fit {FitScale:F3} userView {_userView} letters {LetterSource} ({_taxiLabels.Count}) stands {_stands.Count} " +
        $"pavement {_pavement is not null} network {_network.Count} beacons {_beacons.Count} tower {_tower is not null}";

    /// <summary>Shows an airfield, fitted to the plan frame.</summary>
    public void Show(Airfield field, IReadOnlyList<DiagramLabel>? labels = null)
    {
        _field = field;
        _theatre = AppState.Store.Theatre;
        if (labels is not null) Labels = labels;
        if (_theatre is null) return;
        var id = _theatre.Id;
        var (ox, oz) = field.Runways.Count > 0 ? (field.Runways.Average(r => r.X), field.Runways.Average(r => r.Z)) : (field.X, field.Z);
        _frame = new LocalFrame(_theatre, ox, oz);
        var graph = DcsAirfieldData.GraphFor(id, field.Name);
        _extent = Extent(field, graph);
        _pavement = BuildPavement(id, ox, oz);
        _network = BuildNetwork(graph);
        _stands = StandGroups(field.Parking);
        _areas = Labels.Where(l => l.Kind == DiagramLabel.Area).ToList();
        _taxiLabels = TaxiLabels(field, graph, ox, oz);
        _beacons = DcsAirfieldData.BeaconsFor(id, field.Id);
        // The tower: a tower model DCS places, else the chart's "TOWER" label (local only).
        _tower = DcsAirfieldData.TowerNear(id, ox, oz)
            ?? _areas.Where(a => a.Text.Contains("TOWER", StringComparison.OrdinalIgnoreCase)).Select(a => ((double X, double Z)?)(a.X, a.Z)).FirstOrDefault();
        var (lat, lon) = _theatre.ToLatLon(ox, oz);
        _icao = DcsAirfieldData.IcaoFor(id, lat, lon, field.Runways.Select(TrueHeading).ToList()) ?? ChartLabels.IcaoFor(field);
        Fit();
    }

    // ---- Content -----------------------------------------------------------------------------

    /// <summary>One label per taxiway leg (between turns): legs named by DCS's location signs
    /// (each sign board votes for its nearest leg), legs DCS has no sign on named by the chart's
    /// letters (grey); long legs repeat the letter every ~1.5 km. Without the field's network,
    /// the chart's letters where printed.</summary>
    private List<TaxiLabel> TaxiLabels(Airfield field, TaxiwayGraph? graph, double ox, double oz)
    {
        var chart = Labels.Where(l => l.Kind == DiagramLabel.Taxiway).ToList();
        if (graph is null)
        {
            LetterSource = chart.Count > 0 ? "chart" : "";
            return chart.Select(l => new TaxiLabel(l.Text, l.X, l.Z, false, 0)).ToList();
        }
        var others = AppState.Store.Airfields.Where(a => a.Id != field.Id && !a.IsHelipad).ToList();
        var boards = (DcsAirfieldData.SignBoardsNear(_theatre!.Id, ox, oz, SignRadiusM) ?? [])
            .Where(s =>
            {
                // Only this field's signs: closer to it than to any other airdrome.
                var d = (s.X - ox) * (s.X - ox) + (s.Z - oz) * (s.Z - oz);
                return others.All(a => (s.X - a.X) * (s.X - a.X) + (s.Z - a.Z) * (s.Z - a.Z) > d);
            })
            .ToList();
        var legs = graph.Legs();
        var fromSigns = TaxiwayGraph.Name(legs, boards.Select(b => (b.Text, b.X, b.Z)));
        var named = fromSigns.Select(n => n.Leg).ToHashSet();
        var signNames = fromSigns.Select(n => n.Name).ToHashSet();
        var fromChart = TaxiwayGraph.Name(legs.Where(l => !named.Contains(l)).ToList(),
            chart.Where(l => !signNames.Contains(l.Text)).Select(l => (l.Text, l.X, l.Z)), reachM: 80);
        var mixed = fromSigns.Count > 0 && fromChart.Count > 0;
        LetterSource = fromSigns.Count > 0 ? (mixed ? "DCS signs + chart" : "DCS signs") : fromChart.Count > 0 ? "chart" : "";

        var labels = new List<TaxiLabel>();
        void Place(TaxiwayGraph.Leg leg, string name, bool grey)
        {
            var count = Math.Max(1, (int)Math.Round(leg.Length / LabelEveryM));
            for (int i = 0; i < count; i++)
            {
                var (x, z) = TaxiwayGraph.PointAt(leg.Xz, leg.Length * (i + 0.5) / count);
                labels.Add(new TaxiLabel(name, x, z, grey, leg.Length));
            }
        }
        foreach (var (leg, name) in fromSigns) Place(leg, name, false);
        foreach (var (leg, name) in fromChart) Place(leg, name, mixed);
        return labels;
    }

    /// <summary>DCS's pavement around the field as one frozen geometry in local metres (east,
    /// −north), so a transform maps it to the screen for any zoom; null without terrain data.</summary>
    private Geometry? BuildPavement(string terrainId, double ox, double oz)
    {
        if (DcsAirfieldData.PavementFor(terrainId) is not { } pavement) return null;
        double minE = _extent.MinE - 500, maxE = _extent.MaxE + 500, minN = _extent.MinN - 500, maxN = _extent.MaxN + 500;
        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
        var any = false;
        using (var c = g.Open())
        {
            var reach = Math.Max(maxE - minE, maxN - minN);
            foreach (var bucket in pavement.Near(ox, oz, reach))
                for (int i = 0; i + 5 < bucket.Length; i += 6)
                {
                    var (e0, n0) = _frame!.Local(bucket[i], bucket[i + 1]);
                    if (e0 < minE || e0 > maxE || n0 < minN || n0 > maxN) continue;
                    var (e1, n1) = _frame.Local(bucket[i + 2], bucket[i + 3]);
                    var (e2, n2) = _frame.Local(bucket[i + 4], bucket[i + 5]);
                    c.BeginFigure(new Point(e0, -n0), true, true);
                    c.LineTo(new Point(e1, -n1), false, false);
                    c.LineTo(new Point(e2, -n2), false, false);
                    any = true;
                }
        }
        g.Freeze();
        return any ? g : null;
    }

    /// <summary>The taxiway network at the widths DCS gives its edges (taxiway_23m…), grouped by
    /// width — drawn under the mesh pavement, it fills taxiways the mesh leaves out.</summary>
    private List<(double, Geometry)> BuildNetwork(TaxiwayGraph? graph)
    {
        var result = new List<(double, Geometry)>();
        if (graph is null) return result;
        foreach (var group in graph.Edges.Where(e => !e.IsRunway).GroupBy(e => e.WidthM))
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
                foreach (var e in group)
                {
                    var (e0, n0) = _frame!.Local(e.Xz[0], e.Xz[1]);
                    c.BeginFigure(new Point(e0, -n0), false, false);
                    for (int i = 2; i + 1 < e.Xz.Length; i += 2)
                    {
                        var (ei, ni) = _frame.Local(e.Xz[i], e.Xz[i + 1]);
                        c.LineTo(new Point(ei, -ni), true, true);
                    }
                }
            g.Freeze();
            result.Add((group.Key, g));
        }
        return result;
    }

    /// <summary>Stand groups as on printed diagrams: stands standing within 150 m of each other
    /// (chained) form one group, labelled with its number ranges ("21-38", "2-4, 9") at its middle;
    /// groups whose badges would touch at the current zoom are merged when drawn.</summary>
    public static IReadOnlyList<(IReadOnlyList<int> Numbers, double X, double Z)> StandGroups(IReadOnlyList<ParkingSpot> spots)
    {
        var parent = Enumerable.Range(0, spots.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < spots.Count; i++)
            for (int j = i + 1; j < spots.Count; j++)
            {
                double dx = spots[i].X - spots[j].X, dz = spots[i].Z - spots[j].Z;
                if (dx * dx + dz * dz < StandClusterM * StandClusterM) parent[Find(i)] = Find(j);
            }
        return Enumerable.Range(0, spots.Count).GroupBy(Find)
            .Select(g => g.Select(i => spots[i]).ToList())
            .Select(g => ((IReadOnlyList<int>)g.Select(s => s.Number).ToList(), g.Average(s => s.X), g.Average(s => s.Z)))
            .ToList();
    }

    /// <summary>"1 2 3 7 9 10" → "1-3, 7, 9-10"; more than three runs → "1-10".</summary>
    public static string Ranges(IEnumerable<int> numbers)
    {
        var n = numbers.Distinct().Order().ToList();
        var runs = new List<(int A, int B)>();
        foreach (var v in n)
            if (runs.Count > 0 && runs[^1].B + 1 == v) runs[^1] = (runs[^1].A, v);
            else runs.Add((v, v));
        if (runs.Count > 3) return $"{n[0]}-{n[^1]}";
        return string.Join(", ", runs.Select(r => r.A == r.B ? $"{r.A}" : $"{r.A}-{r.B}"));
    }

    // ---- Page layout ---------------------------------------------------------------------------

    private Rect Page => Box(PageInset, PageInset, Math.Max(1, ActualWidth - 2 * PageInset), Math.Max(1, ActualHeight - 2 * PageInset));
    private bool InfoAside => Page.Width >= 900;

    /// <summary>The plan frame: below the header, beside or above the information block.</summary>
    private Rect Plan
    {
        get
        {
            var p = Page;
            double top = p.Top + HeaderH, bottom = p.Bottom - FooterH - (InfoAside ? 0 : InfoH);
            double right = p.Right - (InfoAside ? InfoW : 0);
            return Box(p.Left + Pad, top, Math.Max(1, right - p.Left - 2 * Pad), Math.Max(1, bottom - top - Pad));
        }
    }

    private Rect Info
    {
        get
        {
            var p = Page;
            return InfoAside
                ? Box(p.Right - InfoW, p.Top + HeaderH, InfoW - Pad, p.Height - HeaderH - FooterH - Pad)
                : Box(p.Left + Pad, p.Bottom - FooterH - InfoH, p.Width - 2 * Pad, InfoH - Pad);
        }
    }

    /// <summary>A rectangle that is never negative: docking panes pass through tiny sizes while
    /// they are dragged, and WPF's Rect throws on a negative size.</summary>
    private static Rect Box(double x, double y, double w, double h) => new(x, y, Math.Max(0, w), Math.Max(0, h));

    /// <summary>Below this the page layout does not fit; a note is drawn instead.</summary>
    private bool TooSmall => ActualWidth < 420 || ActualHeight < 300;

    // ---- View ----------------------------------------------------------------------------------

    /// <summary>Whole field in the plan frame.</summary>
    public void Fit()
    {
        _userView = false;
        _scale = 0;
        ClampView();
        _centerE = (_extent.MinE + _extent.MaxE) / 2;
        _centerN = (_extent.MinN + _extent.MaxN) / 2;
        InvalidateVisual();
    }

    private double FitScale =>
        ActualWidth < 1 || ActualHeight < 1 ? 1
        : Math.Min(Plan.Width / (_extent.MaxE - _extent.MinE), Plan.Height / (_extent.MaxN - _extent.MinN));

    private void ClampView()
    {
        var min = FitScale;
        _scale = Math.Clamp(_scale, min, Math.Max(min, MaxScale));
        double halfE = Plan.Width / 2 / _scale, halfN = Plan.Height / 2 / _scale;
        double loE = _extent.MinE + halfE, hiE = _extent.MaxE - halfE;
        double loN = _extent.MinN + halfN, hiN = _extent.MaxN - halfN;
        _centerE = loE <= hiE ? Math.Clamp(_centerE, loE, hiE) : (_extent.MinE + _extent.MaxE) / 2;
        _centerN = loN <= hiN ? Math.Clamp(_centerN, loN, hiN) : (_extent.MinN + _extent.MaxN) / 2;
    }

    private (double MinE, double MinN, double MaxE, double MaxN) Extent(Airfield field, TaxiwayGraph? graph)
    {
        var pts = new List<(double E, double N)>();
        foreach (var rw in field.Runways)
        {
            var h = rw.HeadingTrue * Math.PI / 180;
            double dx = Math.Cos(h) * rw.Length / 2, dz = Math.Sin(h) * rw.Length / 2;
            pts.Add(_frame!.Local(rw.X + dx, rw.Z + dz));
            pts.Add(_frame.Local(rw.X - dx, rw.Z - dz));
        }
        foreach (var p in field.Parking) pts.Add(_frame!.Local(p.X, p.Z));
        if (graph is not null)
            foreach (var e in graph.Edges)
                for (int i = 0; i + 1 < e.Xz.Length; i += 2) pts.Add(_frame!.Local(e.Xz[i], e.Xz[i + 1]));
        if (pts.Count == 0) pts.Add((0, 0));
        return (pts.Min(p => p.E) - MarginM, pts.Min(p => p.N) - MarginM, pts.Max(p => p.E) + MarginM, pts.Max(p => p.N) + MarginM);
    }

    private Point Screen(double e, double n)
    {
        var plan = Plan;
        return new(plan.Left + plan.Width / 2 + (e - _centerE) * _scale, plan.Top + plan.Height / 2 - (n - _centerN) * _scale);
    }

    private Point ScreenXz(double x, double z)
    {
        var (e, n) = _frame!.Local(x, z);
        return Screen(e, n);
    }

    /// <summary>A runway's heading from true north (DCS gives it from grid north).</summary>
    private double TrueHeading(Runway rw) => Nav.Normalize(rw.HeadingTrue + (_frame?.GridNorthBearing ?? 0));

    private double? Variation(double x, double z)
    {
        if (_theatre?.MagVar is not { } mv) return null;
        var (lat, lon) = _theatre.ToLatLon(x, z);
        return mv.At(lat, lon);
    }

    private string MagText(double trueDeg, double x, double z) =>
        Variation(x, z) is { } v ? $"{Nav.Bearing3(Nav.Magnetic(trueDeg, v))}°" : $"{Nav.Bearing3(trueDeg)}°T";

    private double? ElevationFt(double x, double z) =>
        AppState.Store.Terrain is { } grid && LineOfSight.HeightAt(grid, x, z) is { } h ? h * FeetPerMeter : null;

    // ---- Rendering -----------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            Render(dc);
        }
        catch (Exception ex)
        {
            // A drawing fault must never take the app down: log it, show a note.
            Map.Gpu.MapSurface.Log($"airfield diagram: {ex}");
            dc.DrawRectangle(Desk, null, Box(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Desk, null, Box(0, 0, ActualWidth, ActualHeight));
        if (TooSmall)
        {
            DrawCentered(dc, "Enlarge the pane to see the diagram", new Point(ActualWidth / 2, ActualHeight / 2), Muted, 12, false);
            return;
        }
        var page = Page;
        dc.DrawRectangle(Paper, new Pen(Muted, 0.6), page);
        if (_field is not { } field || _frame is null || _theatre is not { } theatre)
        {
            DrawCentered(dc, "Select an airfield", new Point(ActualWidth / 2, ActualHeight / 2), Muted, 14, true);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var plan = Plan;
        dc.PushClip(new RectangleGeometry(plan));
        var toScreen = new MatrixTransform(_scale, 0, 0, _scale, plan.Left + plan.Width / 2 - _centerE * _scale, plan.Top + plan.Height / 2 + _centerN * _scale);
        dc.PushTransform(toScreen);
        foreach (var (width, lines) in _network)
            dc.DrawGeometry(null, new Pen(Pavement, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, lines);
        if (_pavement is not null) dc.DrawGeometry(Pavement, null, _pavement);
        dc.Pop();

        // Labels in order of importance; each takes a free spot or gives way (_placed).
        _placed.Clear();
        // The north arrow and the scale bar own their corners.
        Take(new Rect(plan.Right - 62, plan.Top + 4, 58, 70));
        Take(new Rect(plan.Left + 4, plan.Bottom - 26, 230, 24));
        foreach (var rw in field.Runways) DrawRunway(dc, field, rw, dpi);
        foreach (var b in _beacons.Where(b => b.IsLocalizer)) DrawIlsFeather(dc, field, b, dpi);
        DrawNavaids(dc, dpi);
        DrawTaxiLabels(dc, dpi);
        DrawStands(dc, field, dpi);
        DrawAreas(dc, dpi);
        DrawTraffic(dc, dpi);
        DrawNorthArrow(dc, field, dpi);
        DrawScaleBar(dc, dpi);
        dc.Pop();
        dc.DrawRectangle(null, new Pen(Ink, 1.2), plan);

        DrawHeader(dc, field, theatre, dpi);
        DrawInfo(dc, field, theatre, dpi);
        DrawFooter(dc, theatre, dpi);
    }

    /// <summary>Black runway with a white edge, designators beyond each end, heading arrows at
    /// the thresholds (magnetic) and "length X width" in feet.</summary>
    private void DrawRunway(DrawingContext dc, Airfield field, Runway rw, double dpi)
    {
        var h = rw.HeadingTrue * Math.PI / 180;
        double ax = Math.Cos(h), az = Math.Sin(h), cx = -Math.Sin(h), cz = Math.Cos(h);
        double hl = rw.Length / 2, hw = rw.Width / 2;
        Point P(double along, double across) => ScreenXz(rw.X + ax * along + cx * across, rw.Z + az * along + cz * across);
        var outline = Poly(P(-hl, -hw), P(hl, -hw), P(hl, hw), P(-hl, hw));
        dc.DrawGeometry(RunwayFill, new Pen(Ink, 1), outline);
        var inset = Math.Min(hw * 0.25, 1.5 / Math.Max(_scale, 1e-6));
        dc.DrawGeometry(null, new Pen(Marking, 0.8), Poly(P(-hl + inset, -hw + inset), P(hl - inset, -hw + inset), P(hl - inset, hw - inset), P(-hl + inset, hw - inset)));

        Point a = P(-hl, 0), b = P(hl, 0);
        var screenHeading = Math.Atan2(b.X - a.X, -(b.Y - a.Y)) * 180 / Math.PI;
        var (near, far) = Names(field, rw);
        var off = 12 / _scale + 10;
        var size = Math.Clamp(14 * Math.Sqrt(_scale), 10, 22);
        DrawRotated(dc, near, P(-hl - off, 0), screenHeading, Ink, size, dpi);
        DrawRotated(dc, far, P(hl + off, 0), screenHeading + 180, Ink, size, dpi);
        foreach (var end in new[] { P(-hl - off, 0), P(hl + off, 0) })
            Take(new Rect(end.X - size * 1.4, end.Y - size * 1.4, size * 2.8, size * 2.8));

        // Heading arrows beside each threshold, pointing the landing direction (magnetic).
        var side = hw + 14 / _scale;
        var len = 46 / _scale;
        var trueHdg = TrueHeading(rw);
        DrawHeadingArrow(dc, P(-hl + 30 / _scale, -side), P(-hl + 30 / _scale + len, -side), MagText(trueHdg, rw.X, rw.Z), dpi);
        DrawHeadingArrow(dc, P(hl - 30 / _scale, side), P(hl - 30 / _scale - len, side), MagText(Nav.Normalize(trueHdg + 180), rw.X, rw.Z), dpi);

        var dims = $"{rw.Length * FeetPerMeter:0} X {rw.Width * FeetPerMeter:0}";
        var angle = Upright(screenHeading - 90);
        var at = P(0, -(hw + 12 / _scale + 4));
        var ft = Format(dims, Ink, 10, bold: true, dpi);
        var bounds = new RotateTransform(angle, at.X, at.Y).TransformBounds(new Rect(at.X - ft.Width / 2, at.Y - ft.Height / 2, ft.Width, ft.Height));
        Take(bounds);
        dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    private static double Upright(double angle)
    {
        while (angle > 90) angle -= 180;
        while (angle < -90) angle += 180;
        return angle;
    }

    private void DrawHeadingArrow(DrawingContext dc, Point from, Point to, string text, double dpi)
    {
        var pen = new Pen(Ink, 1.2);
        dc.DrawLine(pen, from, to);
        var d = to - from;
        if (d.Length < 1) return;
        d.Normalize();
        var n = new Vec(-d.Y, d.X);
        dc.DrawGeometry(Ink, null, Poly(to, to - d * 7 + n * 3.5, to - d * 7 - n * 3.5));
        var mid = from + (to - from) / 2;
        var angle = Upright(Math.Atan2(d.Y, d.X) * 180 / Math.PI);
        var ft = Format(text, Ink, 9, bold: true, dpi);
        var lift = n * 8 * (Vec.CrossProduct(new Vec(1, 0), d) >= 0 ? -1 : 1);
        var at = mid + lift;
        dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    /// <summary>ILS feather: an arrow on the extended centreline ending at the threshold of the
    /// runway it serves, with ident, frequency and inbound course (magnetic).</summary>
    private void DrawIlsFeather(DrawingContext dc, Airfield field, Beacon loc, double dpi)
    {
        var inbound = loc.InboundCourseTrue;  // grid-relative like the runway courses
        if (RunwayFor(field, loc) is null) return;
        var rw = RunwayFor(field, loc)!;
        // Threshold of the landing end: the end you reach first flying the inbound course.
        var h = inbound * Math.PI / 180;
        double ux = Math.Cos(h), uz = Math.Sin(h);
        double tx = rw.X - ux * rw.Length / 2, tz = rw.Z - uz * rw.Length / 2;
        var start = ScreenXz(tx - ux * 1400, tz - uz * 1400);
        var end = ScreenXz(tx - ux * 120, tz - uz * 120);
        var pen = new Pen(NavaidInk, 1.4);
        dc.DrawLine(pen, start, end);
        var d = end - start;
        if (d.Length < 4) return;
        d.Normalize();
        var n = new Vec(-d.Y, d.X);
        // Feather: an arrowhead at the runway end, a half-feather at the tail.
        dc.DrawGeometry(NavaidInk, null, Poly(end, end - d * 9 + n * 4, end - d * 9 - n * 4));
        dc.DrawLine(pen, start, start - d * 6 + n * 7);
        var angle = Upright(Math.Atan2(d.Y, d.X) * 180 / Math.PI);
        var text = $"ILS {loc.Callsign} {loc.FrequencyText}  {MagText(Nav.Normalize(inbound + (_frame?.GridNorthBearing ?? 0)), loc.X, loc.Z)}";
        var ft = Format(text, NavaidInk, 9, bold: true, dpi);
        var at = start + (end - start) / 2 + n * 9;
        var textBox = new RotateTransform(angle, at.X, at.Y).TransformBounds(new Rect(at.X - ft.Width / 2, at.Y - ft.Height / 2, ft.Width, ft.Height));
        if (!Free(textBox)) return;  // the feather stays; its text gives way (see the info block)
        Take(textBox);
        dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    /// <summary>The runway a localizer serves: on its course axis (±10°), and of parallel ones
    /// the one whose centreline passes nearest the antenna.</summary>
    internal static Runway? RunwayFor(Airfield field, Beacon loc) =>
        field.Runways.Where(r => IcaoMatch.AxisDiff(r.HeadingTrue, loc.InboundCourseTrue) <= 10)
            .MinBy(r => Math.Abs(Lateral(r, loc.X, loc.Z)));

    /// <summary>Signed distance of a point from a runway's centreline (right of its heading positive).</summary>
    private static double Lateral(Runway r, double x, double z)
    {
        var h = r.HeadingTrue * Math.PI / 180;
        return -(x - r.X) * Math.Sin(h) + (z - r.Z) * Math.Cos(h);
    }

    /// <summary>Runway designators as printed: parallel runways DCS names alike get L/R (left or
    /// right looking along each end's heading), e.g. 05L/23R and 05R/23L.</summary>
    internal static (string Near, string Far) Names(Airfield field, Runway rw)
    {
        var (near, far) = Oriented(rw);
        var twin = field.Runways.FirstOrDefault(o => !ReferenceEquals(o, rw) && IcaoMatch.AxisDiff(o.HeadingTrue, rw.HeadingTrue) <= 10
            && Oriented(o) == (near, far));
        if (twin is null || near.EndsWith('L') || near.EndsWith('R') || near.EndsWith('C')) return (near, far);
        // This runway lies left of the twin, looking along its heading → its near end is L.
        var left = Lateral(rw, twin.X, twin.Z) > 0;
        return left ? (near + "L", far + "R") : (near + "R", far + "L");
    }

    /// <summary>Designators ordered by the runway's course: near = the end landed on flying
    /// <c>HeadingTrue</c>. DCS sometimes names a runway after its other end (Damascus "23" with a
    /// 050° course), so the name alone does not say which end is which.</summary>
    internal static (string Near, string Far) Oriented(Runway rw)
    {
        var (near, far) = rw.Designators;
        var digits = new string(near.TakeWhile(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var number) && Math.Abs(Nav.Normalize(number * 10 - rw.HeadingTrue + 180) - 180) > 90)
            (near, far) = (far, near);
        return (near, far);
    }

    /// <summary>The designator of the end an ILS lands on (its inbound course along or against the runway).</summary>
    private static string LandingEnd(Airfield field, Runway rw, Beacon loc)
    {
        var d = Nav.Normalize(loc.InboundCourseTrue - rw.HeadingTrue);
        var (near, far) = Names(field, rw);
        return d < 90 || d > 270 ? near : far;
    }

    // ---- Labels: each takes a free spot near its place, or gives way ----------------------------

    private bool Free(Rect r) => !_placed.Any(p => p.IntersectsWith(r));

    private void Take(Rect r) => _placed.Add(r);

    /// <summary>The first free spot among the label's place and small shifts around it.</summary>
    private Rect? Spot(Rect r, params Vec[] shifts)
    {
        if (Free(r)) return r;
        foreach (var s in shifts)
        {
            var moved = Rect.Offset(r, s);
            if (Free(moved)) return moved;
        }
        return null;
    }

    private void DrawAreas(DrawingContext dc, double dpi)
    {
        foreach (var label in _areas)
        {
            var p = ScreenXz(label.X, label.Z);
            var text = label.Text.ToUpperInvariant();
            var size = Map.Labels.Measure(text, Ink, HaloLight, 9.5, Map.Labels.Font.Regular, dpi);
            var spot = Spot(new Rect(p.X - size.Width / 2, p.Y - size.Height / 2, size.Width, size.Height), new Vec(0, -14), new Vec(0, 14), new Vec(0, -26), new Vec(0, 26));
            if (spot is not { } r) continue;  // no room at this zoom: zoom in to read it
            Take(r);
            Map.Labels.Draw(dc, text, r.TopLeft, Ink, HaloLight, 9.5, Map.Labels.Font.Regular, dpi);
        }
    }

    /// <summary>Stand groups (blue badges) zoomed out — badges that would touch merge into one
    /// ("13-25" + "26-30" → "13-30") — and each stand with its number closer in.</summary>
    private void DrawStands(DrawingContext dc, Airfield field, double dpi)
    {
        if (_scale < StandNumbersScale)
        {
            var groups = _stands.Select(g => (Numbers: g.Numbers.ToList(), P: ScreenXz(g.X, g.Z))).ToList();
            Rect Badge((List<int> Numbers, Point P) g, double pad)
            {
                var w = Format(Ranges(g.Numbers), Marking, 9, bold: true, dpi).Width + 9;
                return new Rect(g.P.X - w / 2 - pad, g.P.Y - 7.5 - pad, w + 2 * pad, 15 + 2 * pad);
            }
            for (var merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < groups.Count && !merged; i++)
                    for (int j = i + 1; j < groups.Count && !merged; j++)
                    {
                        if (!Badge(groups[i], 3).IntersectsWith(Badge(groups[j], 3))) continue;
                        int ni = groups[i].Numbers.Count, nj = groups[j].Numbers.Count;
                        var at = new Point((groups[i].P.X * ni + groups[j].P.X * nj) / (ni + nj), (groups[i].P.Y * ni + groups[j].P.Y * nj) / (ni + nj));
                        groups[i] = ([.. groups[i].Numbers, .. groups[j].Numbers], at);
                        groups.RemoveAt(j);
                        merged = true;
                    }
            }
            foreach (var g in groups)
            {
                var text = Ranges(g.Numbers);
                var ft = Format(text, Marking, 9, bold: true, dpi);
                var w = ft.Width + 9;
                // Stands are worth a longer look for room than area names: two rings of shifts.
                var spot = Spot(new Rect(g.P.X - w / 2, g.P.Y - 7.5, w, 15),
                    new Vec(0, -17), new Vec(0, 17), new Vec(w * 0.7, 0), new Vec(-w * 0.7, 0),
                    new Vec(w * 0.6, -17), new Vec(-w * 0.6, -17), new Vec(w * 0.6, 17), new Vec(-w * 0.6, 17),
                    new Vec(0, -34), new Vec(0, 34), new Vec(w * 1.3, 0), new Vec(-w * 1.3, 0));
                if (spot is not { } r) continue;
                Take(r);
                dc.DrawRoundedRectangle(StandBadge, null, r, 6, 6);
                dc.DrawText(ft, new Point(r.Left + 4.5, r.Top + 7.5 - ft.Height / 2));
            }
            return;
        }
        var pen = new Pen(StandBadge, 1.2);
        foreach (var spot in field.Parking)
        {
            var p = ScreenXz(spot.X, spot.Z);
            dc.DrawEllipse(Paper, pen, p, 3.5, 3.5);
            var text = spot.Number.ToString();
            var size = Map.Labels.Measure(text, StandBadge, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            if (Spot(new Rect(p.X + 4, p.Y - 6, size.Width, size.Height), new Vec(-size.Width - 8, 0), new Vec(0, 8)) is not { } r) continue;
            Take(r);
            Map.Labels.Draw(dc, text, r.TopLeft, StandBadge, HaloLight, 9, Map.Labels.Font.Bold, dpi);
        }
    }

    /// <summary>Taxiway letters: white in a black oval (grey: from the chart, no DCS sign). Long
    /// legs first; a letter with no room nearby gives way (zoom in to see it).</summary>
    private void DrawTaxiLabels(DrawingContext dc, double dpi)
    {
        foreach (var l in _taxiLabels.OrderBy(l => l.FromChart).ThenByDescending(l => l.LegLength))
        {
            var p = ScreenXz(l.X, l.Z);
            var ft = Format(l.Text, Marking, 10, bold: true, dpi);
            var w = Math.Max(16, ft.Width + 9);
            var spot = Spot(new Rect(p.X - w / 2, p.Y - 7.5, w, 15), new Vec(0, -16), new Vec(0, 16), new Vec(w, 0), new Vec(-w, 0));
            if (spot is not { } r) continue;
            Take(r);
            var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            dc.DrawEllipse(l.FromChart ? Muted : Ink, null, c, w / 2, 7.5);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }
    }
    /// <summary>Tower (star), TACAN/VOR (hexagon), NDB (dotted circle), localizer (bar) and
    /// glideslope (triangle) where DCS places them, with their idents.</summary>
    private void DrawNavaids(DrawingContext dc, double dpi)
    {
        var pen = new Pen(NavaidInk, 1.3);
        foreach (var b in _beacons)
        {
            var p = ScreenXz(b.X, b.Z);
            string? label = null;
            if (b.IsTacan || b.Type.StartsWith("VOR"))
            {
                dc.DrawGeometry(Paper, pen, Poly(Enumerable.Range(0, 6).Select(i => p + new Vec(Math.Cos(i * Math.PI / 3), Math.Sin(i * Math.PI / 3)) * 6).ToArray()));
                dc.DrawEllipse(NavaidInk, null, p, 1.5, 1.5);
                label = b.IsTacan ? $"{b.Callsign} {b.ChannelText}" : $"{b.Callsign} {b.FrequencyText}";
            }
            else if (b.Kind == "NDB")
            {
                dc.DrawEllipse(null, new Pen(NavaidInk, 1.2) { DashStyle = new DashStyle([1, 2], 0) }, p, 7, 7);
                dc.DrawEllipse(NavaidInk, null, p, 2, 2);
                label = $"{b.Callsign} {b.FrequencyText}";
            }
            else if (b.IsLocalizer)
            {
                dc.DrawRectangle(NavaidInk, null, new Rect(p.X - 4, p.Y - 2, 8, 4));
                label = "LOC";
            }
            else if (b.IsGlideslope)
            {
                dc.DrawGeometry(NavaidInk, null, Poly(new Point(p.X, p.Y - 5), new Point(p.X + 4.5, p.Y + 3), new Point(p.X - 4.5, p.Y + 3)));
                label = "GS";
            }
            Take(new Rect(p.X - 8, p.Y - 8, 16, 16));
            if (label is null) continue;
            var ls = Map.Labels.Measure(label, NavaidInk, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            if (Spot(new Rect(p.X + 9, p.Y - 7, ls.Width, ls.Height), new Vec(-ls.Width - 18, 0), new Vec(0, 12), new Vec(0, -12)) is not { } lr) continue;
            Take(lr);
            Map.Labels.Draw(dc, label, lr.TopLeft, NavaidInk, HaloLight, 9, Map.Labels.Font.Bold, dpi);
        }
        if (_tower is { } t)
        {
            var p = ScreenXz(t.X, t.Z);
            var star = Enumerable.Range(0, 10).Select(i =>
            {
                var r = i % 2 == 0 ? 8.0 : 3.4;
                var a = -Math.PI / 2 + i * Math.PI / 5;
                return p + new Vec(Math.Cos(a), Math.Sin(a)) * r;
            }).ToArray();
            dc.DrawGeometry(Paper, new Pen(Ink, 1.2), Poly(star));
            Take(new Rect(p.X - 9, p.Y - 9, 18, 18));
            var ts = Map.Labels.Measure("TWR", Ink, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            if (Spot(new Rect(p.X + 10, p.Y - 7, ts.Width, ts.Height), new Vec(-ts.Width - 20, 0)) is { } tr)
            {
                Take(tr);
                Map.Labels.Draw(dc, "TWR", tr.TopLeft, Ink, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            }
        }
    }

    private void DrawTraffic(DrawingContext dc, double dpi)
    {
        var store = AppState.Store;
        if (store.Coalition is not { } own) return;
        var now = DateTime.UtcNow;
        foreach (var t in store.Tracks)
        {
            var u = t.Unit;
            if (u.Lat is not { } lat || u.Lon is not { } lon) continue;
            var (x, z) = _frame!.FromLatLon(lat, lon);
            var (e, n) = _frame.Local(x, z);
            if (Math.Abs(e) > LiveRadiusM || Math.Abs(n) > LiveRadiusM) continue;
            var p = Screen(e, n);
            var identity = Symbology.IdentityOf(u, own);
            var brush = identity switch { Identity.Friendly => Brushes.RoyalBlue, Identity.Hostile => Brushes.Firebrick, _ => Brushes.DarkGoldenrod };
            var air = Symbology.IsAir(Symbology.KindOf(u));
            dc.PushOpacity(t.Freshness(now));
            if (air)
            {
                if (u.Hdg is { } hdg)
                {
                    // Unit headings are relative to DCS grid north; the plan is true north up.
                    var rad = (hdg + _frame.GridNorthBearing) * Math.PI / 180;
                    dc.DrawLine(new Pen(brush, 1.5), p, new Point(p.X + Math.Sin(rad) * 16, p.Y - Math.Cos(rad) * 16));
                }
                dc.DrawEllipse(brush, new Pen(Paper, 1), p, 5, 5);
                var name = Symbology.NameOf(u) ?? (u.Type is null ? "UNK" : Symbology.ShortType(u.Type));
                var line2 = $"{(u.Type is null ? "" : Symbology.ShortType(u.Type))} {u.SpdKt ?? 0:0}KT";
                Map.Labels.Draw(dc, name, new Point(p.X + 8, p.Y - 14), brush, HaloLight, 11, Map.Labels.Font.Bold, dpi);
                Map.Labels.Draw(dc, line2, new Point(p.X + 8, p.Y - 1), brush, HaloLight, 10, Map.Labels.Font.Regular, dpi);
            }
            else
            {
                dc.DrawRectangle(brush, null, new Rect(p.X - 3, p.Y - 3, 6, 6));
            }
            dc.Pop();
        }
    }

    private void DrawNorthArrow(DrawingContext dc, Airfield field, double dpi)
    {
        var plan = Plan;
        var at = new Point(plan.Right - 26, plan.Top + 28);
        dc.DrawGeometry(Ink, null, Poly(new Point(at.X, at.Y - 18), new Point(at.X + 7, at.Y + 8), new Point(at.X, at.Y + 3), new Point(at.X - 7, at.Y + 8)));
        DrawCentered(dc, "N", new Point(at.X, at.Y + 17), Ink, 11, true);
        if (Variation(field.X, field.Z) is { } v)
            DrawCentered(dc, $"VAR {Math.Abs(v):0.0}°{(v >= 0 ? "E" : "W")}", new Point(at.X - 6, at.Y + 31), Ink, 9, false);
    }

    private void DrawScaleBar(DrawingContext dc, double dpi)
    {
        var plan = Plan;
        // Feet: the largest of 500/1000/2000/5000 ft that fits in ~130 DIP.
        var feet = new[] { 5000, 2000, 1000, 500 }.FirstOrDefault(f => f / FeetPerMeter * _scale <= 130, 500);
        var len = feet / FeetPerMeter * _scale;
        var y = plan.Bottom - 14;
        var x0 = plan.Left + 12;
        var pen = new Pen(Ink, 2);
        dc.DrawLine(pen, new Point(x0, y), new Point(x0 + len, y));
        dc.DrawLine(pen, new Point(x0, y - 4), new Point(x0, y + 1));
        dc.DrawLine(pen, new Point(x0 + len, y - 4), new Point(x0 + len, y + 1));
        dc.DrawText(Format($"{feet} FT", Ink, 9, bold: false, dpi), new Point(x0 + len + 5, y - 7));
    }

    private void DrawHeader(DrawingContext dc, Airfield field, Theatre theatre, double dpi)
    {
        var p = Page;
        dc.DrawText(Format("AIRPORT DIAGRAM", Ink, 19, bold: true, dpi), new Point(p.Left + Pad, p.Top + 5));
        dc.DrawText(Format($"DCS {theatre.Id.ToUpperInvariant()}", Muted, 9.5, bold: false, dpi), new Point(p.Left + Pad, p.Top + 33));
        var name = field.Name.Replace('_', ' ').ToUpperInvariant() + (_icao is null ? "" : $" ({_icao})");
        var title = Format(name, Ink, 15, bold: true, dpi);
        dc.DrawText(title, new Point(p.Right - Pad - title.Width, p.Top + 6));
        var (lat, lon) = theatre.ToLatLon(field.X, field.Z);
        var parts = new List<string> { Coordinates.Ddm(lat, lon) };
        if (FieldElevationFt(field) is { } elev) parts.Add($"ELEV {elev:0} FT");
        if (Variation(field.X, field.Z) is { } v) parts.Add($"VAR {Math.Abs(v):0.0}°{(v >= 0 ? "E" : "W")}");
        var line = Format(string.Join("   ", parts), Ink, 10, bold: false, dpi);
        dc.DrawText(line, new Point(p.Right - Pad - line.Width, p.Top + 31));
        dc.DrawLine(new Pen(Ink, 1.5), new Point(p.Left + Pad, p.Top + HeaderH - 6), new Point(p.Right - Pad, p.Top + HeaderH - 6));
    }

    private double? FieldElevationFt(Airfield field) =>
        AppState.Store.Weather?.Airfields.FirstOrDefault(a => a.Id == field.Id) is { } w ? w.Elevation * FeetPerMeter : ElevationFt(field.X, field.Z);

    /// <summary>Information block: communications (radio.lua), navaids (beacons.lua), runways
    /// with magnetic headings, dimensions, threshold elevations and their ILS.</summary>
    private void DrawInfo(DrawingContext dc, Airfield field, Theatre theatre, double dpi)
    {
        var r = Info;
        dc.DrawRectangle(null, new Pen(Ink, 1), r);
        var x = r.Left + 8;
        var y = r.Top + 6;
        var col2 = InfoAside ? r.Left + 8 : r.Left + r.Width / 2 + 8;
        double Heading(string text, double at, double yy)
        {
            dc.DrawText(Format(text, Ink, 10, bold: true, dpi), new Point(at, yy));
            return yy + 16;
        }
        double Line(string text, double at, double yy, Brush? brush = null)
        {
            dc.DrawText(Format(text, brush ?? Ink, 9.5, bold: false, dpi), new Point(at, yy));
            return yy + 14;
        }

        y = Heading("COMMUNICATIONS", x, y);
        var freqs = Srs.SrsBridge.FrequenciesOf(theatre.Id, field.Id);
        y = freqs.Count == 0 ? Line("No frequencies in radio.lua", x, y, Muted)
            : Line("TOWER  " + string.Join("  ", freqs.Select(f => $"{f.Mhz:0.000}")), x, y);

        y += 4;
        y = Heading("NAVAIDS", x, y);
        var navaids = _beacons.Where(b => !b.IsLocalizer && !b.IsGlideslope).ToList();
        if (navaids.Count == 0) y = Line("None listed", x, y, Muted);
        foreach (var b in navaids)
            y = Line($"{b.Kind,-7} {b.Callsign,-4} {(b.IsTacan ? b.ChannelText + "  " : "")}{b.FrequencyText}", x, y);

        var y2 = InfoAside ? y + 4 : r.Top + 6;
        y2 = Heading("RUNWAYS", col2, y2);
        foreach (var rw in field.Runways)
        {
            var (near, far) = Names(field, rw);
            var trueHdg = TrueHeading(rw);
            var h = rw.HeadingTrue * Math.PI / 180;
            double ux = Math.Cos(h) * rw.Length / 2, uz = Math.Sin(h) * rw.Length / 2;
            string End(string des, double hdg, double ex, double ez) =>
                $"RWY {des,-4} {MagText(hdg, rw.X, rw.Z)}  ELEV {(ElevationFt(ex, ez) is { } e ? $"{e:0}" : "—")}";
            y2 = Line($"{near}/{far}  {rw.Length * FeetPerMeter:0} X {rw.Width * FeetPerMeter:0} FT", col2, y2);
            y2 = Line("  " + End(near, trueHdg, rw.X - ux, rw.Z - uz), col2, y2);
            y2 = Line("  " + End(far, Nav.Normalize(trueHdg + 180), rw.X + ux, rw.Z + uz), col2, y2);
        }
        foreach (var loc in _beacons.Where(b => b.IsLocalizer))
        {
            var landing = RunwayFor(field, loc) is { } rw ? LandingEnd(field, rw, loc) : "";
            var crs = MagText(Nav.Normalize(loc.InboundCourseTrue + (_frame?.GridNorthBearing ?? 0)), loc.X, loc.Z);
            y2 = Line($"ILS {landing,-4} {loc.Callsign,-5} {loc.FrequencyText}  CRS {crs}", col2, y2, NavaidInk);
        }
        // Letters' source, small, at the bottom of the block.
        if (LetterSource.Length > 0)
            dc.DrawText(Format($"Taxiway letters: {LetterSource}", Muted, 8.5, bold: false, dpi), new Point(x, r.Bottom - 15));
    }

    private void DrawFooter(DrawingContext dc, Theatre theatre, double dpi)
    {
        var p = Page;
        var y = p.Bottom - FooterH + 3;
        dc.DrawText(Format($"DCB · DCS {theatre.Id.ToUpperInvariant()} DATA", Muted, 9, bold: false, dpi), new Point(p.Left + Pad, y));
        var warn = Format("NOT FOR REAL NAVIGATION", Warning, 10, bold: true, dpi);
        dc.DrawText(warn, new Point(p.Left + p.Width / 2 - warn.Width / 2, y - 1));
        var date = Format(DateTime.Now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant(), Muted, 9, bold: false, dpi);
        dc.DrawText(date, new Point(p.Right - Pad - date.Width, y));
    }

    // ---- Drawing helpers -----------------------------------------------------------------------

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

    private static void DrawRotated(DrawingContext dc, string text, Point at, double screenHeading, Brush brush, double size, double dpi)
    {
        if (text.Length == 0) return;
        var ft = Format(text, brush, size, bold: true, dpi);
        dc.PushTransform(new RotateTransform(screenHeading, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    private void DrawCentered(DrawingContext dc, string text, Point at, Brush brush, double size, bool bold)
    {
        var ft = Format(text, brush, size, bold, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static FormattedText Format(string text, Brush brush, double size, bool bold, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);

    // ---- Interaction: wheel zooms at the cursor, drag pans, double-click fits (plan frame) -------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_field is null) return;
        var at = e.GetPosition(this);
        var plan = Plan;
        if (!plan.Contains(at)) return;
        double cx = plan.Left + plan.Width / 2, cy = plan.Top + plan.Height / 2;
        var pe = _centerE + (at.X - cx) / _scale;
        var pn = _centerN + (cy - at.Y) / _scale;
        _scale *= e.Delta > 0 ? 1.25 : 0.8;
        _userView = true;
        var min = FitScale;
        _scale = Math.Clamp(_scale, min, Math.Max(min, MaxScale));
        _centerE = pe - (at.X - cx) / _scale;
        _centerN = pn - (cy - at.Y) / _scale;
        ClampView();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (!Plan.Contains(e.GetPosition(this))) return;
        if (e.ClickCount == 2)
        {
            Fit();
            return;
        }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(this);
        _centerE -= (at.X - from.X) / _scale;
        _centerN += (at.Y - from.Y) / _scale;
        _dragFrom = at;
        _userView = true;
        ClampView();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>A name placed on the diagram, in DCS map metres.</summary>
public sealed record DiagramLabel(string Text, string Kind, double X, double Z)
{
    public const string Taxiway = "taxiway";
    public const string Area = "area";
}

/// <summary>True-north local frame around a DCS point: DCS x/z ↔ local east/north metres,
/// and lat/lon → DCS x/z by a linear fit around the origin (exact to well under a metre over
/// an airfield's few kilometres).</summary>
public sealed class LocalFrame
{
    private readonly double _ox, _oz, _lat0, _lon0;
    private readonly double _sin, _cos;                 // rotation grid → true north
    private readonly double _a, _b, _c, _d;             // inverse Jacobian (lat, lon) → (x, z)

    /// <summary>Bearing of DCS grid north from true north, degrees (meridian convergence).</summary>
    public double GridNorthBearing { get; }

    public LocalFrame(Theatre theatre, double ox, double oz)
    {
        _ox = ox;
        _oz = oz;
        (_lat0, _lon0) = theatre.ToLatLon(ox, oz);
        const double step = 500;
        var (latX, lonX) = theatre.ToLatLon(ox + step, oz);
        var (latZ, lonZ) = theatre.ToLatLon(ox, oz + step);
        // Bearing of the +x (grid north) axis from true north.
        var cosLat = Math.Cos(_lat0 * Math.PI / 180);
        var bearing = Math.Atan2((lonX - _lon0) * cosLat, latX - _lat0);
        GridNorthBearing = bearing * 180 / Math.PI;
        _sin = Math.Sin(bearing);
        _cos = Math.Cos(bearing);
        // Jacobian d(lat, lon)/d(x, z), inverted.
        double j11 = (latX - _lat0) / step, j12 = (latZ - _lat0) / step;
        double j21 = (lonX - _lon0) / step, j22 = (lonZ - _lon0) / step;
        var det = j11 * j22 - j12 * j21;
        _a = j22 / det; _b = -j12 / det; _c = -j21 / det; _d = j11 / det;
    }

    /// <summary>DCS x/z → (east, north) metres from the origin, true north.</summary>
    public (double E, double N) Local(double x, double z)
    {
        double dx = x - _ox, dz = z - _oz;
        // Grid x points at bearing β, grid z at β + 90°.
        return (dx * _sin + dz * _cos, dx * _cos - dz * _sin);
    }

    public (double X, double Z) FromLatLon(double lat, double lon)
    {
        double dLat = lat - _lat0, dLon = lon - _lon0;
        return (_ox + _a * dLat + _b * dLon, _oz + _c * dLat + _d * dLon);
    }
}
