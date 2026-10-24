using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Media = System.Windows.Media;
using Dcb.Client.Core;
using Dcb.Client.Map;
using Dcb.Client.Map.Gpu;

namespace Dcb.Client.Panels;

/// <summary>One map window. Each has its own base map, label level and view; any number can
/// be open, docked or floating. Everything on it is drawn by Skia into one surface (Map/Gpu).</summary>
public partial class MapPanel : UserControl
{
    private const double CaucasusX = 4_680_000, CaucasusY = 5_220_000;
    private const double DefaultResolution = 1200;

    private readonly MapSettings _settings;
    private bool _viewPlaced;
    private Point? _pressAt;
    private bool _initializing = true;

    public string ContentId { get; }

    /// <summary>The ATC tab's airfield map: airfields drawn in full (diagram, taxiways,
    /// parking); positioned by the ATC airfield list rather than fitting the whole picture.</summary>
    public bool IsAirfieldView { get; }

    // Layers, bottom-up: base maps, the dimming, then overlays (static first, tracks, ruler).
    private readonly ScopeLinesSk _scope = new();
    private readonly WebTilesSk _esri = WebTilesSk.EsriImagery();
    private readonly DcsMapSk _dcsMap = new();
    private readonly MgrsGridSk _mgrs = new();
    private readonly DcsChartsSk _charts = new();
    private readonly DcsSatelliteSk _satellite = new();
    private readonly DimSk _dim = new();
    private readonly TheatreSk _theatre = new();
    private readonly AirfieldsSk _airfields = new();
    private readonly FootholdSk _foothold = new();
    private readonly GciSk _gci = new();
    private readonly SymbolsSk _symbols = new();
    private readonly MeasureSk _measure = new();

    private MapNavigator Nav => Surface.Navigator;

    public MapPanel(string contentId, bool airfieldView = false)
    {
        ContentId = contentId;
        IsAirfieldView = airfieldView;
        _settings = AppState.Settings.MapFor(contentId);
        InitializeComponent();
        DiagramsBox.Visibility = airfieldView ? Visibility.Visible : Visibility.Collapsed;

        foreach (var layer in new MapLayer[] { _scope, _esri, _dcsMap, _mgrs, _charts, _satellite,
                     _dim, _theatre, _airfields, _foothold, _gci, _symbols, _measure })
            Surface.Add(layer);
        _gci.ShowMarks = _settings.ShowMarks;
        _gci.ViewProvider = () => Nav.View;
        _dim.Opacity = 1 - _settings.Brightness;
        _airfields.FullDetail = airfieldView;
        _airfields.ShowDiagrams = _settings.ShowDiagrams;
        _airfields.Visible = _settings.ShowAirfields;
        _foothold.Visible = _settings.FootholdZones;
        _foothold.ViewProvider = () => Nav.View;
        _symbols.Settings = _settings;
        _symbols.ViewProvider = () => Nav.View;
        Nav.Changed += OnViewChanged;

        MapArea.SizeChanged += (_, _) =>
        {
            ApplyTheatreBounds();
            if (_pendingAirfield is { } p && DateTime.UtcNow - p.At < TimeSpan.FromSeconds(3))
                Dispatcher.BeginInvoke(() => CenterAirfield(p.Id), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        BaseMapBox.ItemsSource = Enum.GetValues<BaseMap>();
        LabelsBox.ItemsSource = Enum.GetValues<LabelLevel>();
        BaseMapBox.SelectedItem = _settings.BaseMap;
        LabelsBox.SelectedItem = _settings.Labels;
        ThreatRingsBox.IsChecked = _settings.ThreatRings;
        MarksBox.IsChecked = _settings.ShowMarks;
        FootholdBox.IsChecked = _settings.FootholdZones;
        AirfieldsBox.IsChecked = _settings.ShowAirfields;
        DiagramsBox.IsChecked = _settings.ShowDiagrams;
        GridBox.IsChecked = _settings.ShowGrid;
        BrightnessSlider.Value = _settings.Brightness;
        _initializing = false;
        ApplyBaseMap();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Centre (Mercator) and resolution (Mercator metres per DIP) of the view.</summary>
    public (double X, double Y, double Resolution) ViewCenter => (Nav.CenterX, Nav.CenterY, Nav.Resolution);

    private void OnViewChanged()
    {
        // The bullseye call follows the ground under a still pointer while the map moves.
        if (_measure.BullsCursor is null) return;
        var p = Mouse.GetPosition(MapArea);
        _measure.BullsCursor = (p.X, p.Y);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppState.Store.Changed += Surface.Invalidate;
        Srs.SrsListen.Instance.TalkChanged += Surface.Invalidate;
        AppState.Store.Changed += OnPictureChanged;
        AppState.Store.SelectionChanged += Surface.Invalidate;
        AppState.Store.TheatreChanged += ApplyTheatreBounds;
        AppState.Store.FootholdChanged += Surface.Invalidate;
        AppState.Store.AirfieldsChanged += Surface.Invalidate;
        AppState.ZoneSelectionChanged += OnZoneSelectionChanged;
        AppState.AirfieldSelectionChanged += OnAirfieldSelectionChanged;
        AppState.BullsKeyChanged += OnBullsKeyChanged;
        AppState.Packages.Changed += Surface.Invalidate;
        AppState.Board.Changed += Surface.Invalidate;
        AppState.RwrClockRequested += OnRwrKey;
        AppState.NctrRequested += OnNctrKey;
        AppState.Store.NctrChanged += Surface.Invalidate;
        // The methods being worked flash beside their contacts while an NCTR request runs.
        _nctrFlash ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { if (AppState.Store.Nctr?.Requests.Any(r => r.Running) == true) Surface.Invalidate(); }, Dispatcher);
        _nctrFlash.Start();
        OnAirfieldSelectionChanged();  // picked before this map loaded
        ApplyTrackFilters();
        PlaceInitialView();
        ApplyTheatreBounds();
    }

    /// <summary>The workspace window holding this map (a floating map's window is owned by it).</summary>
    private MainWindow? Workspace
    {
        get
        {
            var w = Window.GetWindow(this);
            while (w is not null and not MainWindow) w = w.Owner;
            return w as MainWindow;
        }
    }

    /// <summary>Ground units and landed aircraft as the map's settings say, or by its window:
    /// an ATC window hides both, other windows hide landed aircraft only.</summary>
    private void ApplyTrackFilters()
    {
        var atc = Workspace?.Preset == MainWindow.AtcPreset;
        _symbols.ShowGroundUnits = _settings.ShowGroundUnits ?? !atc;
        _symbols.ShowLanded = _settings.ShowLanded ?? false;
        var was = _initializing;
        _initializing = true;
        GroundUnitsBox.IsChecked = _symbols.ShowGroundUnits;
        LandedBox.IsChecked = _symbols.ShowLanded;
        _initializing = was;
        Surface.Invalidate();
    }

    private void TrackFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowGroundUnits = GroundUnitsBox.IsChecked == true;
        _settings.ShowLanded = LandedBox.IsChecked == true;
        ApplyTrackFilters();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Docking moves the panel between windows (Unloaded then Loaded); resubscribe on load.
        AppState.Store.Changed -= Surface.Invalidate;
        Srs.SrsListen.Instance.TalkChanged -= Surface.Invalidate;
        AppState.Store.Changed -= OnPictureChanged;
        AppState.Store.SelectionChanged -= Surface.Invalidate;
        AppState.Store.TheatreChanged -= ApplyTheatreBounds;
        AppState.Store.FootholdChanged -= Surface.Invalidate;
        AppState.Store.AirfieldsChanged -= Surface.Invalidate;
        AppState.ZoneSelectionChanged -= OnZoneSelectionChanged;
        AppState.AirfieldSelectionChanged -= OnAirfieldSelectionChanged;
        AppState.BullsKeyChanged -= OnBullsKeyChanged;
        AppState.Packages.Changed -= Surface.Invalidate;
        AppState.Board.Changed -= Surface.Invalidate;
        AppState.RwrClockRequested -= OnRwrKey;
        AppState.NctrRequested -= OnNctrKey;
        AppState.Store.NctrChanged -= Surface.Invalidate;
        _nctrFlash?.Stop();
        CloseRwrClock();
        CloseNctr();
    }

    private void OnAirfieldSelectionChanged()
    {
        _airfields.SelectedId = AppState.SelectedAirfieldId;
        // Approach range marks belong to ATC: an ATC window or the ATC tab's own map.
        _airfields.ShowApproaches = IsAirfieldView || Workspace?.Preset == MainWindow.AtcPreset;
        _airfields.Invalidate();
    }

    /// <summary>Centres on an airfield and zooms so its runways fill the view (ATC view).</summary>
    public void ShowAirfield(int id)
    {
        // Centre after layout, and again if the panel is resized right after (a floated or
        // freshly docked panel is still settling): otherwise the map centres on a stale size.
        _pendingAirfield = (id, DateTime.UtcNow);
        Dispatcher.BeginInvoke(() => CenterAirfield(id), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private (int Id, DateTime At)? _pendingAirfield;

    private void CenterAirfield(int id)
    {
        if (AppState.Store.Theatre is not { } theatre || AppState.Store.Airfields.FirstOrDefault(a => a.Id == id) is not { } field) return;
        // Centre on the runways: DCS's airfield reference point can sit at one end of the field.
        var (cx, cz) = field.Runways.Count > 0 ? (field.Runways.Average(r => r.X), field.Runways.Average(r => r.Z)) : (field.X, field.Z);
        var (lat, lon) = theatre.ToLatLon(cx, cz);
        var longest = field.Runways.Count > 0 ? field.Runways.Max(r => r.Length) : 1500;
        var res = Math.Clamp(longest * 1.4 * Mercator.ScaleAt(lat) / Math.Max(300, Nav.Width), 0.6, 12);
        ShowAt(lat, lon, res);
    }

    private void OnZoneSelectionChanged()
    {
        _foothold.SelectedZone = AppState.SelectedZone;
        _foothold.Invalidate();
    }

    /// <summary>Centres on a zone (or, <paramref name="westEdge"/>, on the west point of its circle).</summary>
    public void CenterOnZone(string name, bool westEdge = false)
    {
        if (AppState.Store.Theatre is { } theatre && AppState.Store.Foothold?.Find(name) is { } zone)
        {
            var (lat, lon) = theatre.ToLatLon(zone.X, westEdge ? zone.Z - zone.Radius : zone.Z);
            var (x, y) = Mercator.FromLonLat(lon, lat);
            Nav.CenterOn(x, y);
        }
    }

    /// <summary>Keeps the view on the DCS theatre: the centre cannot leave it and zooming out
    /// stops once the whole theatre is in view.</summary>
    private void ApplyTheatreBounds()
    {
        if (AppState.Store.Theatre is not { } theatre) return;
        var pts = theatre.Border().Select(p => Mercator.FromLonLat(p.Lon, p.Lat)).ToList();
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        Nav.PanBounds = (minX, minY, maxX, maxY);
        if (Nav.Width > 0 && Nav.Height > 0)
            Nav.ZoomBounds = (0.5, Math.Max((maxX - minX) / Nav.Width, (maxY - minY) / Nav.Height) * 1.05);
        Nav.CenterOnAndZoomTo(Nav.CenterX, Nav.CenterY, Nav.Resolution);  // re-clamp
    }

    private void PlaceInitialView()
    {
        if (_viewPlaced) return;
        if (_settings.CenterX is { } x && _settings.CenterY is { } y && _settings.Resolution is { } res)
        {
            Nav.CenterOnAndZoomTo(x, y, res);
            _viewPlaced = true;
        }
        else if (IsAirfieldView || !FitPicture())
        {
            // The ATC view waits for an airfield; others show the Caucasus and fit on the first picture.
            Nav.CenterOnAndZoomTo(CaucasusX, CaucasusY, DefaultResolution);
        }
    }

    private void OnPictureChanged()
    {
        if (!_viewPlaced && !IsAirfieldView) FitPicture();
    }

    /// <summary>Zooms to all current tracks. Returns false when there are none.</summary>
    public bool FitPicture()
    {
        var points = AppState.Store.Tracks
            .Where(t => !t.Lost && t.Unit.Lat is not null && t.Unit.Lon is not null)
            .Select(t => Mercator.FromLonLat(t.Unit.Lon!.Value, t.Unit.Lat!.Value))
            .ToList();
        if (points.Count == 0) return false;
        var pad = 20_000;
        Nav.ZoomToBox(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad, points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad);
        _viewPlaced = true;
        return true;
    }

    public void CenterOn(long trackId)
    {
        if (AppState.Store.Find(trackId)?.Unit is { Lat: { } lat, Lon: { } lon })
        {
            var (x, y) = Mercator.FromLonLat(lon, lat);
            Nav.CenterOn(x, y);
        }
    }

    /// <summary>Centres on a point at a resolution (Mercator metres per DIP).</summary>
    public void ShowAt(double lat, double lon, double resolution)
    {
        var (x, y) = Mercator.FromLonLat(lon, lat);
        Nav.CenterOnAndZoomTo(x, y, resolution);
        _viewPlaced = true;
    }

    public void SetGrid(bool on) => GridBox.IsChecked = on;

    /// <summary>Switches the base map as the toolbar does (tests).</summary>
    public void SetBaseMap(BaseMap map) => BaseMapBox.SelectedItem = map;

    public void SetBrightness(double value) => BrightnessSlider.Value = Math.Clamp(value, BrightnessSlider.Minimum, 1);

    /// <summary>Debug aid: Natural Earth coastline over any base map, to check alignment.</summary>
    public void ShowCoastlineOverlay() => _scope.Visible = true;

    public void SaveView()
    {
        if (Nav.Resolution <= 0) return;
        _settings.CenterX = Nav.CenterX;
        _settings.CenterY = Nav.CenterY;
        _settings.Resolution = Nav.Resolution;
    }

    private void ApplyBaseMap()
    {
        var b = _settings.BaseMap;
        var scope = b == BaseMap.Scope;
        var dcsMap = b == BaseMap.DcsMap;
        var dcsCharts = b == BaseMap.DcsCharts;
        var dcsSatellite = b == BaseMap.DcsSatellite;
        GridBox.Visibility = dcsMap ? Visibility.Visible : Visibility.Collapsed;
        Surface.BackColor = dcsSatellite ? DcsSatelliteSk.Sea
            : scope ? MapStyle.Color("scope.background")
            : dcsMap || dcsCharts ? MapStyle.Color("baseMaps.dcsBackground")
            : MapStyle.Color("baseMaps.webBackground");
        _scope.Visible = scope;
        _esri.Visible = b == BaseMap.Satellite;
        _dcsMap.Visible = dcsMap;
        _mgrs.Visible = dcsMap && _settings.ShowGrid;
        _charts.Visible = dcsCharts;
        _satellite.Visible = dcsSatellite;
        // A dimmed light map reads as dark: label halos follow what the controller sees.
        var light = (dcsMap || dcsCharts) && _settings.Brightness >= 0.65;
        _theatre.LightBaseMap = light;
        _theatre.ShowTowns = scope || dcsMap || dcsSatellite;  // charts and web tiles print their own names
        var halo = light ? SkPalette.HaloLight : SkPalette.HaloDark;
        _symbols.Halo = halo;
        _foothold.Halo = halo;
        _gci.Halo = halo;
        _airfields.Halo = SkPalette.HaloDark;  // airfield labels are light: always a dark halo
        _airfields.NightDiagrams = !light;     // NVG-green pages on dark maps
        var attribution = TileBaseMaps.AttributionFor(b);
        AttributionText.Text = attribution;
        AttributionText.Visibility = attribution.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Surface.Invalidate();
    }

    // ---- Toolbar ------------------------------------------------------------------------

    private void BaseMapBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || BaseMapBox.SelectedItem is not BaseMap b) return;
        _settings.BaseMap = b;
        ApplyBaseMap();
    }

    private void LabelsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LabelsBox.SelectedItem is not LabelLevel l) return;
        _settings.Labels = l;
        Surface.Invalidate();
    }

    private void ThreatRingsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ThreatRings = ThreatRingsBox.IsChecked == true;
        Surface.Invalidate();
    }

    private void MarksBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowMarks = MarksBox.IsChecked == true;
        _gci.ShowMarks = _settings.ShowMarks;
        Surface.Invalidate();
    }

    private void FootholdBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.FootholdZones = FootholdBox.IsChecked == true;
        _foothold.Visible = _settings.FootholdZones;
    }

    private void AirfieldsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowAirfields = AirfieldsBox.IsChecked == true;
        _airfields.Visible = _settings.ShowAirfields;
    }

    private void DiagramsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowDiagrams = DiagramsBox.IsChecked == true;
        _airfields.ShowDiagrams = _settings.ShowDiagrams;
        _airfields.Invalidate();
    }

    private void GridBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowGrid = GridBox.IsChecked == true;
        _mgrs.Visible = _settings.ShowGrid && _settings.BaseMap == BaseMap.DcsMap;
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var wasLight = _settings.Brightness >= 0.65;
        _settings.Brightness = Math.Round(e.NewValue, 2);
        _dim.Opacity = 1 - _settings.Brightness;
        _dim.Invalidate();
        if (wasLight != _settings.Brightness >= 0.65) ApplyBaseMap();  // halos switch light/dark
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitPicture();

    // ---- Ruler (right-drag) and bullseye readout (hold the bullseye key) -----------------

    /// <summary>A ruler end at a screen point: on the track under it if there is one.</summary>
    private MeasureSk.End? EndAt(Point p)
    {
        if (!Nav.IsValid) return null;
        if (_symbols.HitTest(p.X, p.Y) is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } tl, Lon: { } tn })
            return new(id, tl, tn);
        var (lat, lon) = Nav.View.ToLatLon(p.X, p.Y);
        return new(null, lat, lon);
    }

    private void SetRuler(MeasureSk.End? from, MeasureSk.End? to)
    {
        _measure.RulerFrom = from;
        _measure.RulerTo = to;
        _measure.Invalidate();
    }

    private void SetBullsCursor(Point? p)
    {
        _measure.BullsCursor = p is { } q ? (q.X, q.Y) : null;
        _measure.Invalidate();
    }

    private Point _rightDownAt;

    private void MapArea_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RwrCanvas.IsVisible) return;  // the clock takes the clicks while open
        _rightDownAt = e.GetPosition(MapArea);
        var from = EndAt(_rightDownAt);
        SetRuler(from, from);
        MapArea.CaptureMouse();
        e.Handled = true;  // the map itself does nothing with the right button here
    }

    private void MapArea_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_measure.RulerFrom is null) return;
        // Measuring only while dragging (user's choice): the line goes on release.
        SetRuler(null, null);
        MapArea.ReleaseMouseCapture();
        e.Handled = true;
        // A right-click without dragging on one of our players: what can be sent to them.
        var at = e.GetPosition(MapArea);
        if ((at - _rightDownAt).Length < 4) ShowMapMenu(at);
    }

    /// <summary>The right-click menu for what is under the pointer: an own aircraft, a placed
    /// threat, an F10 mark, or the map itself (place a threat, at a fix when on one).</summary>
    private void ShowMapMenu(Point at)
    {
        if (!Nav.IsValid) return;
        var menu = new ContextMenu();
        var store = AppState.Store;
        if (_symbols.HitTest(at.X, at.Y) is { } id && store.Find(id)?.Unit is { } unit && PackageMenu.Packable(unit))
            AddTrackItems(menu, unit);
        else if (_symbols.HitTest(at.X, at.Y) is { } cid && store.Find(cid)?.Unit is { Detected: true } contact)
            AddContactItems(menu, contact);
        else if (_gci.HitThreat(at.X, at.Y) is { } threat)
            AddThreatItems(menu, threat);
        else if (_gci.HitMark(at.X, at.Y) is { } mark)
            AddMarkItems(menu, mark);
        else
            AddMapItems(menu, at);
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = MapArea;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    private void AddTrackItems(ContextMenu menu, Unit unit)
    {
        var board = AppState.Board;
        var key = AppState.Settings.RwrKey;
        menu.Items.Add(Item($"RWR… ({key})", () => OpenRwrScope(unit.Id)));
        if (board.Lines.Any(l => l.TrackId == unit.Id))
            menu.Items.Add(Item($"Clear bearing lines of {LineLabel(unit)}", () => board.ClearLines(unit.Id)));
        menu.Items.Add(new Separator());
        foreach (var item in PackageMenu.Items(unit)) menu.Items.Add(item);
        menu.Items.Add(new Separator());
        // What can be sent to a player.
        if (unit.Player is not null && AirfieldFor(unit) is { } field)
        {
            var name = field.Name.Replace('_', ' ');
            void Add(string header, bool atis)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => new Panels.SendMessageWindow(field, unit.Id, atis) { Owner = Window.GetWindow(this) }.Show();
                menu.Items.Add(item);
            }
            Add($"Send ATIS: {name}…", true);
            Add($"Send airfield info: {name}…", false);
        }
        // The player's SRS radios are shown in the SRS tab.
        if (PackageMenu.ListenItem(unit, this) is { } listen) menu.Items.Add(listen);
    }

    /// <summary>A radar contact: identify it for the coalition — first the RWR emitters whose
    /// bearings pass through it, then any label or a typed name — or clear its identity.</summary>
    private void AddContactItems(ContextMenu menu, Unit contact)
    {
        var store = AppState.Store;
        var connected = AppState.Connection.IsConnected;
        var what = contact.Type ?? (contact.Assigned is { } a ? $"identified as {a}" : "unidentified");
        menu.Items.Add(new MenuItem { Header = $"Contact: {what}", IsEnabled = false });
        var identify = new MenuItem { Header = "Identify as", IsEnabled = connected };
        // NCTR answers first (newest request), then RWR emitters whose bearings pass through it.
        foreach (var n in store.NctrFor(contact.Id))
        {
            var label = NctrMethods.LabelOf(n);
            var header = $"{n.Result}   NCTR {NctrMethods.NameOf(n.Method)}" + (n.Platform is { } p ? $" by {p}" : "");
            identify.Items.Add(label is null
                ? new MenuItem { Header = header, IsEnabled = false }  // a size class or raid count is a hint, not an identity
                : Item(header, () => _ = IdentifyAsync(contact.Id, label)));
        }
        var candidates = RwrLabels.Candidates(contact, store.Rwr, id => store.Find(id)?.Unit, GridToTrue);
        foreach (var c in candidates.Take(6))
        {
            var heard = c.Receivers == 1 ? "1 receiver" : $"{c.Receivers} receivers";
            identify.Items.Add(Item($"{c.Label}   RWR: {heard}, {c.Off:0}° off", () => _ = IdentifyAsync(contact.Id, c.Label)));
        }
        if (candidates.Count == 0) identify.Items.Add(new MenuItem { Header = "(no RWR bearing passes through it)", IsEnabled = false });
        identify.Items.Add(new Separator());
        foreach (var (group, labels) in RwrLabels.Groups)
        {
            var sub = new MenuItem { Header = group };
            foreach (var l in labels)
            {
                var label = l;
                sub.Items.Add(Item(label, () => _ = IdentifyAsync(contact.Id, label)));
            }
            identify.Items.Add(sub);
        }
        identify.Items.Add(Item("Type a name…", () =>
        {
            if (PromptText("Identify contact", "Identity for your coalition (up to 24 letters, digits, '-', '/', spaces), e.g. \"Su-34\" or \"SA-11\":") is { } text)
                _ = IdentifyAsync(contact.Id, text);
        }));
        menu.Items.Add(identify);
        if (contact.Assigned is { } current)
            menu.Items.Add(Item($"Clear identity ({current})", () => _ = IdentifyAsync(contact.Id, ""), connected));
    }

    /// <summary>Sends an identity (empty: clears it); the ids channel brings it back to every
    /// controller of the coalition.</summary>
    private async Task IdentifyAsync(long unitId, string label)
    {
        label = label.Trim();
        if (label.Length > 24 || !System.Text.RegularExpressions.Regex.IsMatch(label, @"^[A-Za-z0-9 /\-]*$"))
        {
            MessageBox.Show(Window.GetWindow(this), "Up to 24 letters, digits, '-', '/' and spaces.", "Identify", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var result = await AppState.Connection.SendCommandAsync("identify", new Dictionary<string, object> { ["unit"] = unitId, ["label"] = label });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"Not identified: {result.Error}", "Identify", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AddMapItems(ContextMenu menu, Point at)
    {
        var board = AppState.Board;
        // On a fix the threat goes on the fix and that aircraft's lines have done their job.
        var fixHit = board.Fixes().FirstOrDefault(f => _gci.HitFix(at.X, at.Y) == f.Fix);
        var (lat, lon) = fixHit.Fix is { } fix ? (fix.Lat, fix.Lon) : Nav.View.ToLatLon(at.X, at.Y);
        var place = new MenuItem { Header = fixHit.Fix is null ? "Place threat here" : "Place threat at fix" };
        foreach (var group in ThreatType.All.GroupBy(t => t.Group))
        {
            if (place.Items.Count > 0) place.Items.Add(new Separator());
            foreach (var type in group)
            {
                var t = type;
                place.Items.Add(Item($"{t.Name}  ({t.RangeKm / 1.852:0} NM)", () =>
                {
                    // Connected: shared with the coalition's controllers; offline: a note here.
                    if (AppState.Connection.IsConnected) _ = ShareThreatAsync(t, lat, lon);
                    else board.AddThreat(t, lat, lon, DateTime.UtcNow);
                    if (fixHit.Fix is not null) board.ClearLines(fixHit.TrackId);
                }));
            }
        }
        menu.Items.Add(place);
        menu.Items.Add(Item("Add F10 mark here…", () => _ = AddMarkAsync(lat, lon), AppState.Connection.IsConnected));
        menu.Items.Add(Item("Coordinates…", () => ShowPointAt(lat, lon)));
        if (board.Lines.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Clear all bearing lines ({board.Lines.Count})", () => board.ClearLines()));
        }
    }

    private void AddThreatItems(ContextMenu menu, ThreatView threat)
    {
        var board = AppState.Board;
        var connected = AppState.Connection.IsConnected;
        var mark = board.Marks.FirstOrDefault(m => m.Id == threat.MarkId);
        var source = threat.SharedId is not null ? "controllers only"
            : threat.MarkId is null ? "not sent"
            : mark is null ? "F10 mark" : mark.Dcb ? "F10 mark by DCB" : mark.ByMission ? "F10 mark by the mission" : $"F10 mark by {mark.Author}";
        menu.Items.Add(new MenuItem { Header = $"{threat.Type.Name} ({source})", IsEnabled = false });
        if (threat.SharedId is { } sharedId)
        {
            // To the pilots: the F10 mark takes over, so the shared one goes.
            menu.Items.Add(Item("Send to F10 map", async () =>
            {
                if (await SendMarkAsync(threat.Lat, threat.Lon, $"{threat.Type.Name} (GCI)", threat.Type.Name) is not null)
                    await RemoveSharedThreatAsync(sharedId);
            }, connected));
            menu.Items.Add(Item("Coordinates…", () => ShowPointAt(threat.Lat, threat.Lon)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete threat (for every controller)", () => _ = RemoveSharedThreatAsync(sharedId), connected));
            return;
        }
        if (threat.Local is { MarkId: null } local)
            menu.Items.Add(Item("Send to F10 map", () => _ = SendThreatAsync(local), connected));
        // The mark is the threat: removing it removes the threat for everyone.
        if (threat.MarkId is { } markId && threat.Removable)
            menu.Items.Add(Item("Remove from F10 map", () => _ = RemoveMarkAsync(markId, threat.Local), connected));
        menu.Items.Add(Item("Coordinates…", () => ShowPointAt(threat.Lat, threat.Lon, mark?.Text.Trim())));
        if (threat.Local is { MarkId: null } note)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete threat", () => board.RemoveThreat(note)));
        }
    }

    private void AddMarkItems(ContextMenu menu, F10Mark mark)
    {
        var by = mark.Dcb ? "DCB" : mark.ByMission ? "mission" : mark.Author;
        menu.Items.Add(new MenuItem { Header = $"F10 mark by {by}: {GciSk.FirstLine(mark.Text)}", IsEnabled = false });
        menu.Items.Add(Item("Coordinates…", () => ShowPointAt(mark.Lat, mark.Lon, mark.Text.Trim())));
        if (mark.Dcb) menu.Items.Add(Item("Remove from F10 map", () => _ = RemoveMarkAsync(mark.Id, null), AppState.Connection.IsConnected));
    }

    private void ShowPointAt(double lat, double lon, string? title = null)
    {
        var (x, y) = Nav.View.ToScreenLonLat(lon, lat);
        ShowPoint(new Point(x, y), title);
    }

    // ---- F10 marks for the own coalition (its players can delete them in the game) ------------

    /// <summary>Sends a mark; returns its id, or null (the reason shown) when it was not made.</summary>
    private async Task<long?> SendMarkAsync(double lat, double lon, string text, string? threat)
    {
        if (AppState.Store.Theatre is not { } theatre) return null;
        var (x, z) = theatre.FromLatLon(lat, lon);
        var fields = new Dictionary<string, object> { ["x"] = (long)Math.Round(x), ["z"] = (long)Math.Round(z), ["text"] = text };
        if (threat is not null) fields["threat"] = threat;
        var result = await AppState.Connection.SendCommandAsync("mark_add", fields);
        if (result.Ok && long.TryParse(result.Value, out var id)) return id;
        MessageBox.Show(Window.GetWindow(this), $"Not sent: {result.Error ?? "no mark id"}", "F10 mark", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>A threat for the coalition's controllers (not the pilots): the threats channel
    /// brings it to every one of them, this client included.</summary>
    private async Task ShareThreatAsync(ThreatType type, double lat, double lon)
    {
        if (AppState.Store.Theatre is not { } theatre) return;
        var (x, z) = theatre.FromLatLon(lat, lon);
        var result = await AppState.Connection.SendCommandAsync("threat_add", new Dictionary<string, object>
            { ["x"] = (long)Math.Round(x), ["z"] = (long)Math.Round(z), ["threat"] = type.Name });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"Not shared: {result.Error}", "Threat", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task RemoveSharedThreatAsync(long id)
    {
        var result = await AppState.Connection.SendCommandAsync("threat_remove", new Dictionary<string, object> { ["threat_id"] = id });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"Not removed: {result.Error}", "Threat", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task SendThreatAsync(ThreatMarker threat)
    {
        if (await SendMarkAsync(threat.Lat, threat.Lon, $"{threat.Type.Name} (GCI)", threat.Type.Name) is { } id)
            AppState.Board.SetMarkId(threat, id, DateTime.UtcNow);
    }

    /// <summary>A plain mark with the controller's text. Text that starts with a threat type
    /// ("SA-11 …") makes it a threat with its ring, like a player's mark would.</summary>
    private async Task AddMarkAsync(double lat, double lon)
    {
        var text = PromptText("F10 mark", "Text of the mark (your coalition sees it on the F10 map; " +
            "start with a type such as \"SA-11\" to mark a threat):");
        if (text is null) return;
        await SendMarkAsync(lat, lon, text, ThreatType.Parse(text)?.Name);
    }

    private async Task RemoveMarkAsync(long markId, ThreatMarker? local)
    {
        var result = await AppState.Connection.SendCommandAsync("mark_remove", new Dictionary<string, object> { ["mark"] = markId });
        if (result.Ok)
        {
            if (local is not null) AppState.Board.RemoveThreat(local);
        }
        else MessageBox.Show(Window.GetWindow(this), $"Not removed: {result.Error}", "F10 mark", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>A one-line text from the controller (≤ 200 bytes), or null when cancelled or empty.</summary>
    private string? PromptText(string title, string prompt)
    {
        // 95 characters stay within the server's 200 bytes even in a two-byte script (Cyrillic).
        var box = new TextBox { MinWidth = 360, Margin = new Thickness(0, 6, 0, 10), MaxLength = 95 };
        var ok = new Button { Content = "Send", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        if (dialog.ShowDialog() != true) return null;
        var text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    // ---- RWR scope: what an own unit's receiver hears, and bearing lines from it -------------
    // The server's RWR picture (rwr channel) around the own unit under the pointer or the selected
    // one: each emitter at its bearing, nearer the centre the stronger it is, coloured by mode
    // (search, track, launch flashing), a bar over airborne ones. A click on an emitter draws its
    // bearing line (wedge = its error); a click anywhere else on the scope draws a line at that
    // azimuth (a pilot's call on voice). Live once a second until Esc or a click outside it.
    // The mouse wheel over it sizes it (kept for the session); symbols that would overlap are
    // pushed apart, with a leader line to where their bearing and strength put them.

    private const double ScopeMinRadius = 70;
    private double _scopeRadius = 150;
    private double ScopeRadius => Math.Clamp(_scopeRadius, ScopeMinRadius, Math.Max(ScopeMinRadius, Math.Min(MapArea.ActualWidth, MapArea.ActualHeight) / 2 - 24));
    private long? _rwrTrack;
    private Point? _scopeMouse;

    private static Media.SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Media.SolidColorBrush ScopeInk = Brush(190, 255, 179, 71);
    private static readonly Media.SolidColorBrush ScopeFaint = Brush(70, 255, 179, 71);
    private static readonly Media.SolidColorBrush ScopeFill = Brush(150, 8, 12, 10);
    private static readonly Media.SolidColorBrush SearchInk = Brush(255, 120, 230, 120);
    private static readonly Media.SolidColorBrush TrackInk = Brush(255, 255, 214, 64);
    private static readonly Media.SolidColorBrush LaunchInk = Brush(255, 255, 72, 72);

    private static string LineLabel(Unit u) => Symbology.NameOf(u) ?? u.Callsign ?? $"#{u.Id}";

    /// <summary>The RWR key: the scope for the own unit under the pointer, else the selected one.</summary>
    private bool OnRwrKey()
    {
        if (!MapArea.IsMouseOver || !Nav.IsValid) return false;
        var p = Mouse.GetPosition(MapArea);
        var store = AppState.Store;
        static bool Own(Unit? u) => u is { Detected: false, Lat: not null, Lon: not null };
        long? id = _symbols.HitTest(p.X, p.Y) is { } hit && Own(store.Find(hit)?.Unit) ? hit
            : store.SelectedId is { } sel && Own(store.Find(sel)?.Unit) ? sel : null;
        if (id is null) return false;
        OpenRwrScope(id.Value);
        return true;
    }

    private void OpenRwrScope(long trackId)
    {
        _rwrTrack = trackId;
        _scopeMouse = null;
        AppState.Store.RwrChanged -= BuildRwrScope;
        AppState.Store.RwrChanged += BuildRwrScope;
        BuildRwrScope();
        if (_rwrTrack is null) return;
        RwrCanvas.Focusable = true;
        RwrCanvas.Visibility = Visibility.Visible;
        RwrCanvas.Focus();
        RwrCanvas.KeyDown -= RwrCanvas_KeyDown;
        RwrCanvas.KeyDown += RwrCanvas_KeyDown;
        RwrCanvas.MouseMove -= RwrCanvas_MouseMove;
        RwrCanvas.MouseMove += RwrCanvas_MouseMove;
        RwrCanvas.PreviewMouseWheel -= RwrCanvas_MouseWheel;
        RwrCanvas.PreviewMouseWheel += RwrCanvas_MouseWheel;
    }

    /// <summary>The wheel sizes the scope (not the map's zoom while it is open).</summary>
    private void RwrCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _scopeRadius = Math.Clamp(ScopeRadius * Math.Pow(1.15, e.Delta / 120.0), ScopeMinRadius, 2000);
        BuildRwrScope();
        e.Handled = true;
    }

    /// <summary>Grid → true at a point (RWR bearings and headings come from DCS grid north).</summary>
    private static double GridToTrue(double lat, double lon) => AppState.Store.Theatre?.GridNorthAt(lat, lon) ?? 0;

    private (double X, double Y, Unit Unit)? ScopeCentre() =>
        _rwrTrack is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } lat, Lon: { } lon } u && Nav.IsValid
            ? (Nav.View.ToScreenLonLat(lon, lat).X, Nav.View.ToScreenLonLat(lon, lat).Y, u) : null;

    private void BuildRwrScope()
    {
        if (ScopeCentre() is not var (cx, cy, unit))
        {
            CloseRwrClock();
            return;
        }
        var R = ScopeRadius;
        var kids = RwrCanvas.Children;
        kids.Clear();
        void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); kids.Add(e); }

        // The disc takes the azimuth clicks; rings at a third and two thirds of the signal.
        var disc = new System.Windows.Shapes.Ellipse { Width = 2 * R, Height = 2 * R, Stroke = ScopeInk, StrokeThickness = 1.4, Fill = ScopeFill, Cursor = Cursors.Cross };
        disc.MouseLeftButtonDown += (_, e) =>
        {
            var at = e.GetPosition(RwrCanvas);
            AddRwrLine(Core.Nav.Normalize(Math.Atan2(at.X - cx, cy - at.Y) * 180 / Math.PI), Core.Triangulation.BearingSigmaDeg, null);
            e.Handled = true;
        };
        Place(disc, cx - R, cy - R);
        foreach (var f in new[] { 1 / 3.0, 2 / 3.0 })
            Place(new System.Windows.Shapes.Ellipse { Width = 2 * R * f, Height = 2 * R * f, Stroke = ScopeFaint, StrokeThickness = 1, IsHitTestVisible = false }, cx - R * f, cy - R * f);

        // Ticks every 30° from the nose (clock positions); the nose a longer tick.
        if (unit.Hdg is { } hdg)
        {
            var nose = hdg + GridToTrue(unit.Lat!.Value, unit.Lon!.Value);
            for (int clock = 0; clock < 12; clock++)
            {
                var a = (nose + clock * 30) * Math.PI / 180;
                var inner = clock == 0 ? R - 16 : clock % 3 == 0 ? R - 10 : R - 6;
                kids.Add(new System.Windows.Shapes.Line
                {
                    X1 = cx + Math.Sin(a) * inner, Y1 = cy - Math.Cos(a) * inner, X2 = cx + Math.Sin(a) * R, Y2 = cy - Math.Cos(a) * R,
                    Stroke = ScopeInk, StrokeThickness = clock == 0 ? 2.4 : 1.2, IsHitTestVisible = false,
                });
            }
        }

        var store = AppState.Store;
        var emitters = store.RwrFor(unit.Id);
        var gridToTrue = GridToTrue(unit.Lat!.Value, unit.Lon!.Value);
        // Launch, then track, then the strongest keep their own spots; drawn the other way round
        // so the most important ends on top.
        var ordered = emitters.OrderByDescending(e => e.Mode == "launch" ? 2 : e.Mode == "track" ? 1 : 0).ThenByDescending(e => e.Level).ToList();
        var symbols = new List<(FrameworkElement Symbol, double X, double Y, Media.Brush Ink)>();
        foreach (var e in ordered)
        {
            var brg = Core.Nav.Normalize(e.Bearing + gridToTrue);
            var r = R * (0.22 + 0.68 * (1 - Math.Clamp(e.Level, 0, 1)));
            var a = brg * Math.PI / 180;
            var ink = e.Mode switch { "launch" => LaunchInk, "track" => TrackInk, _ => SearchInk };
            var air = e.Category is "airplane" or "helicopter";
            var symbol = new Border
            {
                Child = new TextBlock { Text = e.Label, Foreground = ink, FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(3, 0, 3, 0) },
                BorderBrush = ink, BorderThickness = air ? new Thickness(0, 2.5, 0, 0) : new Thickness(0),
                Background = Brush(200, 0, 0, 0), Cursor = Cursors.Hand,
                ToolTip = $"{e.Label}  {e.Category}, {e.Mode}\nbearing {brg:000}° ±{e.Err:0}° from {LineLabel(unit)}\nclick: bearing line",
            };
            var emitter = e;
            var bearing = brg;
            symbol.MouseLeftButtonDown += (_, ev) =>
            {
                AddRwrLine(bearing, emitter.Err, emitter.Label);
                ev.Handled = true;
            };
            if (e.Mode == "launch")
            {
                var flash = new System.Windows.Media.Animation.DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(350)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
                symbol.BeginAnimation(OpacityProperty, flash);
            }
            symbol.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            symbols.Add((symbol, cx + Math.Sin(a) * r, cy - Math.Cos(a) * r, ink));
        }
        var spots = ScopeLayout.Declutter(symbols.Select(s => (s.X, s.Y, s.Symbol.DesiredSize.Width, s.Symbol.DesiredSize.Height)).ToList(), cx, cy, R);
        for (int i = symbols.Count - 1; i >= 0; i--)
        {
            var (symbol, x, y, ink) = symbols[i];
            var (sx, sy) = spots[i];
            if (Math.Abs(sx - x) + Math.Abs(sy - y) > 3)
            {
                // Moved aside: a dot where it belongs, and a leader to it.
                kids.Add(new System.Windows.Shapes.Line { X1 = x, Y1 = y, X2 = sx, Y2 = sy, Stroke = ink, StrokeThickness = 0.8, Opacity = 0.7, IsHitTestVisible = false });
                Place(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = ink, IsHitTestVisible = false }, x - 2, y - 2);
            }
            Place(symbol, sx - symbol.DesiredSize.Width / 2, sy - symbol.DesiredSize.Height / 2);
        }

        // Centre: the receiver, and what the scope has to say about it.
        var status = store.Rwr is null ? "no RWR data" : emitters.Count == 0 ? "nothing heard" : $"{emitters.Count} heard";
        var title = new TextBlock
        {
            Text = $"{LineLabel(unit)}\n{status} · wheel: size", Foreground = Media.Brushes.White, FontSize = 10.5,
            TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
        };
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(title, cx - title.DesiredSize.Width / 2, cy + R + 4);
        kids.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = ScopeInk, IsHitTestVisible = false });
        Canvas.SetLeft(kids[^1], cx - 2.5);
        Canvas.SetTop(kids[^1], cy - 2.5);
        UpdateScopeCursor();
    }

    /// <summary>The azimuth under the pointer: a line from the centre and its bearing.</summary>
    private void RwrCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        _scopeMouse = e.GetPosition(RwrCanvas);
        UpdateScopeCursor();
    }

    private System.Windows.Shapes.Line? _scopeLine;
    private TextBlock? _scopeText;

    private void UpdateScopeCursor()
    {
        if (_scopeLine is not null) RwrCanvas.Children.Remove(_scopeLine);
        if (_scopeText is not null) RwrCanvas.Children.Remove(_scopeText);
        _scopeLine = null;
        _scopeText = null;
        if (_scopeMouse is not { } m || ScopeCentre() is not var (cx, cy, _)) return;
        double dx = m.X - cx, dy = m.Y - cy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d > ScopeRadius || d < 8) return;
        var brg = Core.Nav.Normalize(Math.Atan2(dx, -dy) * 180 / Math.PI);
        _scopeLine = new System.Windows.Shapes.Line
        {
            X1 = cx, Y1 = cy, X2 = cx + dx / d * ScopeRadius, Y2 = cy + dy / d * ScopeRadius,
            Stroke = ScopeInk, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false,
        };
        _scopeText = new TextBlock { Text = $"{brg:000}°", Foreground = ScopeInk, FontSize = 11, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
        Canvas.SetLeft(_scopeText, cx + dx / d * (ScopeRadius + 6) - 12);
        Canvas.SetTop(_scopeText, cy + dy / d * (ScopeRadius + 6) - 8);
        // Under the emitter symbols, over the disc.
        RwrCanvas.Children.Insert(Math.Min(1, RwrCanvas.Children.Count), _scopeLine);
        RwrCanvas.Children.Add(_scopeText);
    }

    /// <summary>A bearing line from where the unit is now, true degrees; the scope stays open.</summary>
    private void AddRwrLine(double bearingTrue, double sigmaDeg, string? emitter)
    {
        if (_rwrTrack is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } lat, Lon: { } lon } u)
            AppState.Board.AddLine(id, LineLabel(u), lat, lon, bearingTrue, DateTime.UtcNow, sigmaDeg, emitter);
    }

    private void CloseRwrClock()
    {
        _rwrTrack = null;
        _scopeMouse = null;
        _scopeLine = null;
        _scopeText = null;
        AppState.Store.RwrChanged -= BuildRwrScope;
        RwrCanvas.Visibility = Visibility.Collapsed;
        RwrCanvas.Children.Clear();
    }

    private void RwrCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A click outside the scope closes it (and does nothing else on the map).
        if (ReferenceEquals(e.OriginalSource, RwrCanvas))
        {
            CloseRwrClock();
            e.Handled = true;
        }
    }

    private void RwrCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRwrClock();
            e.Handled = true;
        }
    }

    // ---- NCTR: the NCTR key, a box dragged on the map, the methods, Run ----------------------
    // The server's AI radars holding the contacts in the box try each method (docs/
    // RESEARCH-IDENTIFICATION.md, phase 4); results come back on the nctr channel and show beside
    // the contacts and first in their Identify menu.

    private Point? _nctrFrom;
    private System.Windows.Threading.DispatcherTimer? _nctrFlash;
    private System.Windows.Shapes.Rectangle? _nctrBox;
    private static readonly HashSet<string> NctrChosen = ["jem", "hrrp", "isar", "size", "rotor", "raid"];  // remembered for the session

    private bool OnNctrKey()
    {
        if (!MapArea.IsMouseOver || !Nav.IsValid || !AppState.Connection.IsConnected) return false;
        CloseRwrClock();
        CloseNctr();
        NctrCanvas.Visibility = Visibility.Visible;
        NctrCanvas.Focusable = true;
        NctrCanvas.Focus();
        NctrCanvas.MouseLeftButtonDown += NctrCanvas_Down;
        NctrCanvas.MouseMove += NctrCanvas_Move;
        NctrCanvas.MouseLeftButtonUp += NctrCanvas_Up;
        NctrCanvas.KeyDown += NctrCanvas_Key;
        var hint = new TextBlock { Text = "NCTR: drag a box around the contacts (Esc cancels)", Foreground = ScopeInk, Background = Brush(170, 0, 0, 0),
            Padding = new Thickness(6, 2, 6, 2), IsHitTestVisible = false };
        Canvas.SetLeft(hint, 10);
        Canvas.SetTop(hint, 36);
        NctrCanvas.Children.Add(hint);
        return true;
    }

    private void NctrCanvas_Down(object sender, MouseButtonEventArgs e)
    {
        if (_nctrBox is not null && !ReferenceEquals(e.OriginalSource, NctrCanvas)) return;  // the method picker
        foreach (var card in NctrCanvas.Children.OfType<Border>().ToList()) NctrCanvas.Children.Remove(card);  // a new box
        _nctrFrom = e.GetPosition(NctrCanvas);
        if (_nctrBox is null)
        {
            _nctrBox = new System.Windows.Shapes.Rectangle { Stroke = ScopeInk, StrokeThickness = 1.5, StrokeDashArray = [5, 3], Fill = Brush(30, 255, 179, 71), IsHitTestVisible = false };
            NctrCanvas.Children.Add(_nctrBox);
        }
        NctrCanvas.CaptureMouse();
        NctrCanvas_Move(sender, e);
        e.Handled = true;
    }

    private void NctrCanvas_Move(object sender, MouseEventArgs e)
    {
        if (_nctrFrom is not { } a || _nctrBox is null || !NctrCanvas.IsMouseCaptured) return;
        var b = e.GetPosition(NctrCanvas);
        Canvas.SetLeft(_nctrBox, Math.Min(a.X, b.X));
        Canvas.SetTop(_nctrBox, Math.Min(a.Y, b.Y));
        _nctrBox.Width = Math.Abs(b.X - a.X);
        _nctrBox.Height = Math.Abs(b.Y - a.Y);
    }

    private void NctrCanvas_Up(object sender, MouseButtonEventArgs e)
    {
        if (_nctrFrom is not { } a || !NctrCanvas.IsMouseCaptured) return;
        NctrCanvas.ReleaseMouseCapture();
        var b = e.GetPosition(NctrCanvas);
        e.Handled = true;
        if (Math.Abs(b.X - a.X) < 8 || Math.Abs(b.Y - a.Y) < 8)
        {
            CloseNctr();  // a click, not a box
            return;
        }
        ShowNctrMethods(a, b);
    }

    private void NctrCanvas_Key(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseNctr();
        e.Handled = true;
    }

    /// <summary>The methods to try (each with its rule as a tooltip), then Run.</summary>
    private void ShowNctrMethods(Point a, Point b)
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = "NCTR methods", FontWeight = FontWeights.Bold, Foreground = Media.Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
        var boxes = new List<(string Id, CheckBox Box)>();
        foreach (var (id, name, hint) in NctrMethods.All)
        {
            var box = new CheckBox { Content = name, IsChecked = NctrChosen.Contains(id), ToolTip = hint, Foreground = Media.Brushes.White, Margin = new Thickness(0, 1, 0, 1) };
            boxes.Add((id, box));
            panel.Children.Add(box);
        }
        var run = new Button { Content = "Run", IsDefault = true, MinWidth = 64, Margin = new Thickness(0, 6, 6, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 64, Margin = new Thickness(0, 6, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(run);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var card = new Border { Child = panel, Background = Brush(235, 22, 26, 30), BorderBrush = ScopeInk, BorderThickness = new Thickness(1) };
        cancel.Click += (_, _) => CloseNctr();
        run.Click += async (_, _) =>
        {
            var methods = boxes.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToList();
            NctrChosen.Clear();
            foreach (var m in methods) NctrChosen.Add(m);
            CloseNctr();
            if (methods.Count > 0) await RunNctrAsync(a, b, methods);
        };
        Canvas.SetLeft(card, Math.Min(Math.Max(a.X, b.X) + 6, Math.Max(0, NctrCanvas.ActualWidth - 150)));
        Canvas.SetTop(card, Math.Min(Math.Min(a.Y, b.Y), Math.Max(0, NctrCanvas.ActualHeight - 200)));
        NctrCanvas.Children.Add(card);
    }

    private async Task RunNctrAsync(Point a, Point b, IReadOnlyList<string> methods)
    {
        if (AppState.Store.Theatre is not { } theatre) return;
        var (la, loa) = Nav.View.ToLatLon(a.X, a.Y);
        var (lb, lob) = Nav.View.ToLatLon(b.X, b.Y);
        var (x1, z1) = theatre.FromLatLon(la, loa);
        var (x2, z2) = theatre.FromLatLon(lb, lob);
        var result = await AppState.Connection.SendCommandAsync("nctr", new Dictionary<string, object>
        {
            ["x"] = (long)Math.Round(x1), ["z"] = (long)Math.Round(z1), ["x2"] = (long)Math.Round(x2), ["z2"] = (long)Math.Round(z2),
            ["methods"] = string.Join(",", methods),
        });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"NCTR not started: {result.Error}", "NCTR", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CloseNctr()
    {
        NctrCanvas.MouseLeftButtonDown -= NctrCanvas_Down;
        NctrCanvas.MouseMove -= NctrCanvas_Move;
        NctrCanvas.MouseLeftButtonUp -= NctrCanvas_Up;
        NctrCanvas.KeyDown -= NctrCanvas_Key;
        if (NctrCanvas.IsMouseCaptured) NctrCanvas.ReleaseMouseCapture();
        NctrCanvas.Children.Clear();
        NctrCanvas.Visibility = Visibility.Collapsed;
        _nctrFrom = null;
        _nctrBox = null;
    }

    /// <summary>Snapshot aid: the RWR scope on the own unit hearing the most emitters (server
    /// picture), with the bearing line of its first emitter drawn as a click would.</summary>
    public async Task<string> DebugRwr()
    {
        var store = AppState.Store;
        for (int i = 0; i < 10 && store.Rwr is null; i++) await Task.Delay(500);
        var best = store.Rwr?.Receivers.Where(r => r.Emitters.Count > 0 && store.Find(r.Id)?.Unit is { Lat: not null, Lon: not null })
            .MaxBy(r => r.Emitters.Count);
        if (best is null) return $"rwr: {(store.Rwr is null ? "no rwr message" : "no receiver hears anything")}";
        var u = store.Find(best.Id)!.Unit;
        double lat = u.Lat!.Value, lon = u.Lon!.Value;
        ShowAt(lat, lon, 80 * Core.Nav.MetersPerNm * Mercator.ScaleAt(lat) / Math.Max(300, Math.Min(Nav.Width, Nav.Height)));
        await Task.Delay(500);
        OpenRwrScope(best.Id);
        var e = best.Emitters[0];
        var brg = Core.Nav.Normalize(e.Bearing + GridToTrue(lat, lon));
        AddRwrLine(brg, e.Err, e.Label);
        await Task.Delay(1500);
        var scopeState = RwrCanvas.IsVisible ? "scope open" : "scope closed";
        // What the Identify menu would suggest for each contact (RWR bearings through it).
        var contacts = store.Tracks.Where(t => t.Unit.Detected && !t.Lost).Select(t => t.Unit).Take(8)
            .Select(c => $"{c.Id} {c.Category} shown={c.Shown ?? "-"} suggests " +
                string.Join("/", RwrLabels.Candidates(c, store.Rwr, id => store.Find(id)?.Unit, GridToTrue).Select(k => $"{k.Label}({k.Receivers}rx,{k.Off:0}°)")));
        return $"rwr: {LineLabel(u)} ({u.Type}) hears {best.Emitters.Count}: "
            + string.Join(", ", best.Emitters.Select(x => $"{x.Label}/{x.Category} grid {x.Bearing:000} {x.Mode} lvl {x.Level:0.00}"))
            + $"; grid→true {GridToTrue(lat, lon):+0.0;-0.0}°, line {brg:000}° ±{e.Err:0}°"
            + $"\nids: mode {store.IdentificationMode}, {store.Identities.Count} identified\n" + string.Join("\n", contacts)
            + "\n" + scopeState;
    }

    /// <summary>Snapshot aid: three bearing lines from an own aircraft's track towards a made-up
    /// emitter 35 NM off its right wing (2–4° of RWR error), the fix, an SA-6 placed at it (sent
    /// as an F10 mark when <paramref name="send"/>), and the RWR clock open on the aircraft.</summary>
    public async Task<string> DebugGci(long trackId, bool send)
    {
        if (AppState.Store.Find(trackId)?.Unit is not { Lat: { } lat, Lon: { } lon, Hdg: { } hdg } u) return "gci: no aircraft";
        var board = AppState.Board;
        var (eLat, eLon) = Core.Nav.Destination(lat, lon, hdg + 90, 35 * Core.Nav.MetersPerNm);
        var origins = new[] { (lat, lon), Core.Nav.Destination(lat, lon, hdg + 180, 18 * Core.Nav.MetersPerNm), Core.Nav.Destination(lat, lon, hdg + 130, 22 * Core.Nav.MetersPerNm) };
        var errors = new[] { 3.0, -2.0, 4.0 };
        var clocks = new[] { 3, 2, 4 };
        for (int i = 0; i < origins.Length; i++)
        {
            var brg = Core.Nav.BearingRange(origins[i].Item1, origins[i].Item2, eLat, eLon).TrueDeg + errors[i];
            board.AddLine(trackId, LineLabel(u), origins[i].Item1, origins[i].Item2, brg, DateTime.UtcNow.AddMinutes(-6 + 3 * i));
        }
        var report = $"gci: emitter {eLat:F4},{eLon:F4}";
        if (board.Fixes().FirstOrDefault(f => f.TrackId == trackId).Fix is { } fix)
        {
            report += $"; fix {fix.Lat:F4},{fix.Lon:F4} off by {Core.Nav.BearingRange(fix.Lat, fix.Lon, eLat, eLon).Meters / Core.Nav.MetersPerNm:0.0} NM, ±{fix.MajorM / Core.Nav.MetersPerNm:0.0}/{fix.MinorM / Core.Nav.MetersPerNm:0.0} NM";
            var threat = board.AddThreat(ThreatType.Find("SA-6")!, fix.Lat, fix.Lon, DateTime.UtcNow);
            if (send)
            {
                await SendThreatAsync(threat);
                report += $"; F10 mark {threat.MarkId?.ToString() ?? "not sent"}";
            }
        }
        else report += "; no fix";
        ShowAt((lat + eLat) / 2, (lon + eLon) / 2, 70 * Core.Nav.MetersPerNm * Mercator.ScaleAt(lat) / Math.Max(300, Math.Min(Nav.Width, Nav.Height)));
        await Task.Delay(500);
        OpenRwrScope(trackId);
        return report;
    }

    /// <summary>The ATC tab's airfield, or else the airdrome nearest to the unit.</summary>
    private static Airfield? AirfieldFor(Unit unit)
    {
        var store = AppState.Store;
        if (AppState.SelectedAirfieldId is { } id && store.Airfields.FirstOrDefault(a => a.Id == id) is { } picked) return picked;
        if (store.Theatre is not { } theatre || unit.Lat is not { } lat || unit.Lon is not { } lon) return null;
        var (x, z) = theatre.FromLatLon(lat, lon);
        return store.Airfields.Where(a => !a.IsHelipad).MinBy(a => (a.X - x) * (a.X - x) + (a.Z - z) * (a.Z - z));
    }

    private void MapArea_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(MapArea);
        if (_measure.RulerFrom is { } from && e.RightButton == MouseButtonState.Pressed) SetRuler(from, EndAt(p));
        if (AppState.BullsKeyHeld) SetBullsCursor(p);
    }

    private void MapArea_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_measure.BullsCursor is not null) SetBullsCursor(null);
    }

    /// <summary>Snapshot aid: a ruler between two screen points (snapping like a right-drag)
    /// and the bullseye readout at a third.</summary>
    public void DebugMeasure(Point from, Point to, Point? bullsCursor)
    {
        SetRuler(EndAt(from), EndAt(to));
        SetBullsCursor(bullsCursor);
    }

    public (double X, double Y) DebugScreen(double lat, double lon) => Nav.View.ToScreenLonLat(lon, lat);

    private void OnBullsKeyChanged() =>
        SetBullsCursor(AppState.BullsKeyHeld && MapArea.IsMouseOver ? Mouse.GetPosition(MapArea) : null);

    // ---- Selection: a click (not a drag) on the map selects the nearest track -------------

    private void MapArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RwrCanvas.IsVisible) return;  // the clock takes the clicks while open
        if (!IsAirfieldView) AppState.ActiveMap = this;  // the ATC view is driven by its airfield list
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            ShowPoint(e.GetPosition(MapArea));
            AppState.AltClicked = true;  // releasing Alt must not open the menu bar
            e.Handled = true;            // no pan, no selection
            return;
        }
        _pressAt = e.GetPosition(MapArea);
    }

    // ---- Alt+click: the point's coordinates ----------------------------------------------

    /// <summary>Shows a point's position in every format a controller passes on: DDM (as DCS
    /// shows it), DMS, decimal, MGRS, bullseye call and ground elevation.</summary>
    public void ShowPoint(Point p, string? title = null)
    {
        if (!Nav.IsValid) return;
        var (lat, lon) = Nav.View.ToLatLon(p.X, p.Y);
        var store = AppState.Store;
        var lines = new List<string>();
        if (title is { Length: > 0 }) lines.Add(title);
        lines.AddRange(new[]
        {
            Coordinates.Ddm(lat, lon),
            Coordinates.Dms(lat, lon),
            $"{lat.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}, {lon.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}",
            "MGRS " + Coordinates.Mgrs(lat, lon),
        });
        if (store.Latest?.Bullseye is { } bulls)
        {
            var magvar = store.Theatre?.MagVar;
            lines.Add(Core.Nav.BullseyeCall(bulls, lat, lon, magvar) + (magvar is null ? " (true)" : ""));
        }
        if (store.Theatre is { } theatre && store.Terrain is { } grid)
        {
            var (x, z) = theatre.FromLatLon(lat, lon);
            if (LineOfSight.HeightAt(grid, x, z) is { } h)
                lines.Add($"Elevation {Math.Max(0, h):0} m / {Math.Max(0, h) / 0.3048:0} ft");
        }
        PointText.Text = string.Join(Environment.NewLine, lines);
        _measure.Pin = (lat, lon);
        _measure.Invalidate();
        PointPopup.HorizontalOffset = p.X + 14;
        PointPopup.VerticalOffset = p.Y + 10;
        PointPopup.IsOpen = false;  // re-place when already open
        PointPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => PointText.Focus(), System.Windows.Threading.DispatcherPriority.Input);  // for Esc
    }

    private void PointPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        PointPopup.IsOpen = false;
        e.Handled = true;
    }

    private void PointCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PointText.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; the text can still be selected by hand.
        }
    }

    private void PointClose_Click(object sender, RoutedEventArgs e) => PointPopup.IsOpen = false;

    private void PointPopup_Closed(object? sender, EventArgs e)
    {
        _measure.Pin = null;
        _measure.Invalidate();
    }

    private void MapArea_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(MapArea);
        if (RwrCanvas.IsVisible) return;
        if (_pressAt is { } p && (at - p).Length < 4)
        {
            // A track wins over the zone around it.
            var track = _symbols.HitTest(at.X, at.Y);
            AppState.Store.Select(track);
            if (track is null && _foothold.Visible) AppState.SelectZone(_foothold.HitTest(at.X, at.Y));
        }
        _pressAt = null;
    }
}
