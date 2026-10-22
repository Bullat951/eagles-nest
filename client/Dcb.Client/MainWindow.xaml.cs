using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using Dcb.Client.Panels;

namespace Dcb.Client;

/// <summary>A workspace window: menu, status bar and a docking area where every panel can be
/// docked, tabbed or floated. The main window is one; Window → New window opens more (GCI, ATC)
/// to spread over screens, all sharing the connection, picture, selection and settings. The
/// main window runs the app-wide jobs and closing it closes the app; on exit every open
/// window's layout and screen placement is saved to %AppData%\DCB and restored on start.</summary>
public partial class MainWindow : Window
{
    public const string MainId = "main";
    private const string ObjectsId = "objects";
    private const string ZonesId = "zones";
    private const string SrsId = "srs";
    private const string AtcId = "atc";
    private const string PackagesId = "packages";
    private const string MapPrefix = "map:";
    public const string GciPreset = "GCI";
    public const string AtcPreset = "ATC";

    private readonly DispatcherTimer? _expiry;
    private readonly Action _statusChanged;

    public string WorkspaceId { get; }
    /// <summary>What an extra window was opened as (GCI, ATC); empty for the main window.</summary>
    public string Preset { get; }
    public bool IsMain => WorkspaceId == MainId;

    /// <summary>The main window (App.xaml starts it).</summary>
    public MainWindow() : this(MainId, "", AppState.Settings.MainPlacement) { }

    private MainWindow(string workspaceId, string preset, WindowPlacement? placement)
    {
        WorkspaceId = workspaceId;
        Preset = preset;
        InitializeComponent();
        if (!IsMain) Title = $"Eagle's Nest — {preset}";
        _statusChanged = () => Dispatcher.BeginInvoke(() => ConnectionText.Text = AppState.Connection.Status);
        AppState.Connection.StatusChanged += _statusChanged;
        AppState.Store.Changed += UpdatePictureText;
        ConnectionText.Text = AppState.Connection.Status;
        UpdatePictureText();
        Place(placement);
        Loaded += (_, _) =>
        {
            RestoreLayout();
            if (IsMain) OpenSavedWorkspaces();
        };
        if (IsMain)
        {
            _expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _expiry.Tick += (_, _) => AppState.Store.Expire(DateTime.UtcNow);
            _expiry.Start();
            // Developer aid, off unless the client is started with --snapshot. First: the SRS
            // services below check DebugSnapshot.Active to leave the controller's SRS app alone.
            DebugSnapshot.TryStart(this, Environment.GetCommandLineArgs().Skip(1).ToArray());
            Srs.SrsBridge.Instance.Start();  // talks to the controller's own SRS client (local UDP)
            Srs.SrsListen.Instance.Start();  // DCB's own listen-only SRS connection, if it was on
            Srs.SrsAppListener.Instance.Start();  // the controller's SRS app's radios (local UDP 7082)
        }
    }

    // A snapshot run must not overwrite the controller's saved layout (unless DCB_HOME points elsewhere).
    private static bool PersistOnExit => !DebugSnapshot.Active || DebugSnapshot.Persist;

    private void UpdatePictureText()
    {
        var s = AppState.Store;
        if (s.Latest is not { } p)
        {
            PictureText.Text = "";
            return;
        }
        var contacts = s.Tracks.Count(t => t.Unit.Detected);
        PictureText.Text = $"{s.Coalition} · {p.Theatre} · mission time {TimeSpan.FromSeconds(p.Time):hh\\:mm\\:ss} · " +
                           $"{s.Tracks.Count - contacts} own, {contacts} contacts";
    }

    // ---- Workspace windows ------------------------------------------------------------------

    private static IEnumerable<MainWindow> OpenWorkspaces => Application.Current.Windows.OfType<MainWindow>();

    /// <summary>Opens a new workspace window (GCI or ATC layout).</summary>
    public static MainWindow OpenWorkspace(string preset)
    {
        var main = OpenWorkspaces.FirstOrDefault(w => w.IsMain);
        // Cascade from the main window; the controller drags it to the other screen.
        WindowPlacement? at = main is null ? null : new WindowPlacement
        {
            Left = main.Left + 40, Top = main.Top + 40,
            Width = Math.Max(900, main.ActualWidth - 80), Height = Math.Max(600, main.ActualHeight - 80),
        };
        var window = new MainWindow("ws" + Guid.NewGuid().ToString("N")[..8], preset, at);
        window.Show();
        return window;
    }

    private void OpenSavedWorkspaces()
    {
        foreach (var p in AppState.Settings.Workspaces.ToList())
            new MainWindow(p.Id, p.Preset, p).Show();
    }

    /// <summary>Puts the window where it was, if that is still on a screen.</summary>
    private void Place(WindowPlacement? p)
    {
        if (p is null || p.Width < 200 || p.Height < 150) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var window = new Rect(p.Left, p.Top, p.Width, p.Height);
        // At least a 100 DIP strip must be visible (a screen may have been unplugged).
        var visible = Rect.Intersect(screen, window);
        if (visible.IsEmpty || visible.Width < 100 || visible.Height < 100) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (p.Left, p.Top, p.Width, p.Height);
        // Maximise once the window exists, so it maximises on its own screen.
        if (p.Maximized) SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
    }

    private WindowPlacement Placement()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowPlacement
        {
            Id = WorkspaceId, Preset = Preset,
            Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    // ---- Layout -------------------------------------------------------------------------

    private void RestoreLayout()
    {
        var path = AppSettings.LayoutPathFor(WorkspaceId);
        if (File.Exists(path))
        {
            try
            {
                var serializer = new XmlLayoutSerializer(Dock);
                // Unknown ids get no content and are dropped by the serializer's default handling.
                serializer.LayoutSerializationCallback += (_, e) => e.Content = CreateContent(e.Model.ContentId);
                serializer.Deserialize(path);
                if (Dock.Layout.Descendents().OfType<LayoutContent>().Any()) return;
            }
            catch (Exception)
            {
                // Unreadable or incompatible layout file: fall through to the default layout.
            }
        }
        BuildDefaultLayout();
    }

    private static object? CreateContent(string? contentId) => contentId switch
    {
        ObjectsId => new ObjectsPanel(),
        ZonesId => new ZonesPanel(),
        SrsId => new SrsPanel(),
        AtcId => new AtcPanel(),
        PackagesId => new PackagesPanel(),
        AtcViewPanel.Diagram or AtcViewPanel.Approach or AtcViewPanel.Carrier or AtcViewPanel.Lso => new AtcViewPanel(contentId),
        not null when contentId.StartsWith(MapPrefix) => new MapPanel(contentId),
        _ => null,
    };

    private void BuildDefaultLayout()
    {
        switch (Preset)
        {
            case AtcPreset:
            {
                // ATC tab large on the left with SRS under it; a DCS Map beside them.
                var atc = new LayoutAnchorablePane(Anchorable(AtcId, "ATC")) { DockHeight = new GridLength(3, GridUnitType.Star) };
                var srs = new LayoutAnchorablePane(Anchorable(SrsId, "SRS")) { DockHeight = new GridLength(1, GridUnitType.Star) };
                srs.Children.Add(Anchorable(PackagesId, "Packages"));
                // A fixed width: AvalonDock lets the document area take all star space.
                var left = new LayoutAnchorablePaneGroup(atc) { Orientation = System.Windows.Controls.Orientation.Vertical, DockWidth = new GridLength(700) };
                left.Children.Add(srs);
                var map = new LayoutDocumentPaneGroup(new LayoutDocumentPane(MapDocument(BaseMap.DcsMap, 1)));
                Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel(left) };
                Dock.Layout.RootPanel.Children.Add(map);
                break;
            }
            case GciPreset:
            {
                // A large map with the track and zone lists beside it.
                var documents = new LayoutDocumentPane(MapDocument(null, 1));
                var lists = new LayoutAnchorablePane(Anchorable(ObjectsId, "Objects"));
                lists.Children.Add(Anchorable(ZonesId, "Zones"));
                lists.Children.Add(Anchorable(PackagesId, "Packages"));
                var side = new LayoutAnchorablePaneGroup(lists) { DockWidth = new GridLength(380) };
                Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel(new LayoutDocumentPaneGroup(documents)) };
                Dock.Layout.RootPanel.Children.Add(side);
                break;
            }
            default:
            {
                var documents = new LayoutDocumentPane(MapDocument(null, 1));
                var objects = new LayoutAnchorablePane(Anchorable(ObjectsId, "Objects")) { DockHeight = new GridLength(1, GridUnitType.Star) };
                objects.Children.Add(Anchorable(ZonesId, "Zones"));
                objects.Children.Add(Anchorable(PackagesId, "Packages"));
                // ATC holds its own airfield map: give it the larger share of the side column.
                var tools = new LayoutAnchorablePane(Anchorable(AtcId, "ATC")) { DockHeight = new GridLength(1.6, GridUnitType.Star) };
                tools.Children.Add(Anchorable(SrsId, "SRS"));
                var side = new LayoutAnchorablePaneGroup(objects) { Orientation = System.Windows.Controls.Orientation.Vertical, DockWidth = new GridLength(420) };
                side.Children.Add(tools);
                Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel(new LayoutDocumentPaneGroup(documents)) };
                Dock.Layout.RootPanel.Children.Add(side);
                break;
            }
        }
    }

    private static LayoutDocument MapDocument(BaseMap? baseMap, int number)
    {
        var id = NewMapId();
        if (baseMap is { } b) AppState.Settings.MapFor(id).BaseMap = b;
        return new LayoutDocument { Title = $"Map {number}", ContentId = id, Content = CreateContent(id) };
    }

    private static LayoutAnchorable Anchorable(string id, string title) => new()
    {
        ContentId = id,
        Title = title,
        Content = CreateContent(id),
        CanClose = false,
        // A floated panel takes its docked size; the ATC tab needs room for its airfield map.
        FloatingWidth = id == AtcId ? 1000 : 500,
        FloatingHeight = id == AtcId ? 850 : 500,
    };

    private static string NewMapId() => MapPrefix + Guid.NewGuid().ToString("N");

    private void ShowAnchorable(string id, string title)
    {
        var existing = Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id);
        if (existing is null)
        {
            existing = Anchorable(id, title);
            existing.AddToLayout(Dock, AnchorableShowStrategy.Right);
        }
        existing.Show();
        existing.IsActive = true;
    }

    private IEnumerable<MapPanel> Maps => Dock.Layout.Descendents().OfType<LayoutContent>().Select(c => c.Content).OfType<MapPanel>();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsMain) SaveAll();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        AppState.Connection.StatusChanged -= _statusChanged;
        AppState.Store.Changed -= UpdatePictureText;
        base.OnClosed(e);
    }

    /// <summary>Main window closing: saves every open window's maps, layout and placement, then
    /// disconnects; the other windows close with the app.</summary>
    private void SaveAll()
    {
        SaveLayouts();
        AppState.Connection.Disconnect();
    }

    /// <summary>Saves every open window's maps, layout and placement (Window → Save layout, and on exit).</summary>
    private static void SaveLayouts()
    {
        var windows = OpenWorkspaces.ToList();
        var maps = windows.SelectMany(w => w.Maps).ToList();
        foreach (var map in maps) map.SaveView();
        // Settings of closed map windows are dropped here, not on close events: AvalonDock
        // also raises those while moving a map into a floating window.
        foreach (var id in AppState.Settings.Maps.Keys.Except(maps.Select(m => m.ContentId)).ToList())
            AppState.Settings.Maps.Remove(id);
        if (PersistOnExit)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                foreach (var w in windows) new XmlLayoutSerializer(w.Dock).Serialize(AppSettings.LayoutPathFor(w.WorkspaceId));
                if (windows.FirstOrDefault(w => w.IsMain) is { } main) AppState.Settings.MainPlacement = main.Placement();
                AppState.Settings.Workspaces = windows.Where(w => !w.IsMain).Select(w => w.Placement()).ToList();
                // Layouts of windows closed during the session are not needed any more.
                var keep = windows.Select(w => Path.GetFileName(AppSettings.LayoutPathFor(w.WorkspaceId))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var old in Directory.EnumerateFiles(AppSettings.Folder, "layout-*.xml"))
                    if (!keep.Contains(Path.GetFileName(old))) File.Delete(old);
                AppState.Settings.Save();
            }
            catch (IOException)
            {
                // Losing the saved layout is not worth blocking exit.
            }
        }
    }

    // ---- Menu ---------------------------------------------------------------------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        var listen = Srs.SrsListen.Instance;
        var dialog = new ConnectDialog(s.Host, s.Port, s.RememberLogin ? s.RememberedPassword : null, s.RememberLogin,
            s.Srs.ListenPort, s.Srs.EamPassword ?? listen.SessionPassword) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        s.Host = dialog.Host;
        s.Port = dialog.Port;
        s.Srs.ListenPort = dialog.SrsPort;
        // Unticking forgets the stored passwords at once; unticked, the SRS one lasts the session.
        s.RememberLogin = dialog.Remember;
        s.RememberedPassword = dialog.Remember ? dialog.Password : null;
        var eam = dialog.EamPassword.Length > 0 ? dialog.EamPassword : null;
        s.Srs.EamPassword = dialog.Remember ? eam : null;
        listen.SessionPassword = eam;
        listen.Reconnect();
        if (PersistOnExit)
        {
            try { s.Save(); }
            catch (IOException) { /* kept in memory; saved again on exit */ }
        }
        await AppState.Connection.ConnectAsync(dialog.Host, dialog.Port, dialog.Password);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => AppState.Connection.Disconnect();

    private void NewGciWindow_Click(object sender, RoutedEventArgs e) => OpenWorkspace(GciPreset);
    private void NewAtcWindow_Click(object sender, RoutedEventArgs e) => OpenWorkspace(AtcPreset);

    private void NewMap_Click(object sender, RoutedEventArgs e) => OpenMap(null);

    /// <summary>Adds a map window to the document area and activates it.</summary>
    public LayoutDocument OpenMap(BaseMap? baseMap)
    {
        var pane = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (pane is null)
        {
            pane = new LayoutDocumentPane();
            Dock.Layout.RootPanel.Children.Insert(0, new LayoutDocumentPaneGroup(pane));
        }
        var doc = MapDocument(baseMap, Dock.Layout.Descendents().OfType<LayoutDocument>().Count() + 1);
        pane.Children.Add(doc);
        doc.IsActive = true;
        return doc;
    }

    /// <summary>Floats a tool panel into its own window (snapshot aid); returns its content.</summary>
    public object? FloatPanel(string contentId)
    {
        var anchorable = Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == contentId);
        if (anchorable is null) return null;
        anchorable.Show();
        anchorable.IsSelected = true;  // an unselected tab floats without its content
        if (contentId == AtcId) (anchorable.FloatingWidth, anchorable.FloatingHeight) = (1000, 850);
        anchorable.Float();
        return anchorable.Content;
    }

    private void ShowObjects_Click(object sender, RoutedEventArgs e) => ShowAnchorable(ObjectsId, "Objects");
    private void ShowZones_Click(object sender, RoutedEventArgs e) => ShowAnchorable(ZonesId, "Zones");
    private void ShowSrs_Click(object sender, RoutedEventArgs e) => ShowAnchorable(SrsId, "SRS");
    private void ShowAtc_Click(object sender, RoutedEventArgs e) => ShowAnchorable(AtcId, "ATC");
    private void ShowPackages_Click(object sender, RoutedEventArgs e) => ShowAnchorable(PackagesId, "Packages");

    private void ShowAtcView_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) ShowAtcView(id);
    }

    /// <summary>Opens one of the ATC views (AtcViewPanel ids) as a panel of its own: a new one
    /// floats in its own window, so several can be seen at once; it can be docked anywhere.</summary>
    public void ShowAtcView(string id)
    {
        if (Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id) is { } existing)
        {
            existing.Show();
            existing.IsActive = true;
            return;
        }
        var view = Anchorable(id, AtcViewPanel.TitleOf(id));
        (view.FloatingWidth, view.FloatingHeight) = (900, 700);
        view.CanClose = true;
        view.AddToLayout(Dock, AnchorableShowStrategy.Right);
        view.Float();
    }

    /// <summary>The docking chrome's theme (ThemeManager: VS2013 dark or light).</summary>
    public void SetDockTheme(AvalonDock.Themes.Theme theme)
    {
        if (Dock.Theme?.GetType() != theme.GetType()) Dock.Theme = theme;
    }

    private void Theme_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        var mode = Themes.ThemeManager.Mode;
        ThemeSystem.IsChecked = mode == Themes.ThemeMode.System;
        ThemeDark.IsChecked = mode == Themes.ThemeMode.Dark;
        ThemeLight.IsChecked = mode == Themes.ThemeMode.Light;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<Themes.ThemeMode>(tag, out var mode)) Themes.ThemeManager.Set(mode);
    }

    /// <summary>Saves every window's layout, maps and placement now (as on exit).</summary>
    private void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        SaveLayouts();
        ConnectionText.Text = $"{AppState.Connection.Status} · layout saved {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>Brings the SRS tab to the front (Listen traffic).</summary>
    public void ShowSrs() => ShowAnchorable(SrsId, "SRS");

    /// <summary>The workspace window an element is in (a floating panel's window is owned by it).</summary>
    public static MainWindow? Of(DependencyObject element)
    {
        var w = Window.GetWindow(element);
        while (w is not null and not MainWindow) w = w.Owner;
        return w as MainWindow ?? Application.Current.MainWindow as MainWindow;
    }

    /// <summary>Brings the Packages panel to the front where it is docked (snapshot aid).</summary>
    public object? ShowPackages()
    {
        ShowAnchorable(PackagesId, "Packages");
        return Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == PackagesId)?.Content;
    }

    private void SetDiagrams_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Store.Theatre is not { } theatre)
        {
            MessageBox.Show(this, "Connect to a server first: the folder is set for the theatre it runs.", "Map data");
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Airfield diagrams for {theatre.Id} (folder with diagrams.json)" };
        if (dialog.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "diagrams.json")))
        {
            MessageBox.Show(this, "That folder has no diagrams.json. Run tools\\DiagramIndexer on it first.", "Map data");
            return;
        }
        AppState.Settings.Diagrams[theatre.Id] = dialog.FolderName;
        AppState.Settings.Save();
        Map.Airfields.AirfieldDiagrams.Forget(dialog.FolderName);
        Map.Airfields.ChartLabels.Forget(dialog.FolderName);
        AppState.Store.SetAirfields(AppState.Store.Airfields);  // maps redraw
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        foreach (var map in Maps.ToList()) AppState.Settings.Maps.Remove(map.ContentId);
        BuildDefaultLayout();
    }
}
