using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dcb.Client.Core;
using Dcb.Client.Map;
using Dcb.Client.Srs;

namespace Dcb.Client.Panels;

/// <summary>SRS tab: where the controller's radio stands, the controller's ten channels
/// (frequency, transmit, who of the own side is on each — from DCB's listen connection), and the
/// radios of a player picked with "Listen traffic", each of which can be put on a free channel.
/// The SRS connection itself follows the DCB connection (port and EAM password in the connect
/// dialog).</summary>
public partial class SrsPanel : UserControl
{
    private static readonly Brush AtcBrush = Frozen(Color.FromRgb(0x4C, 0xB8, 0x64));
    private static readonly Brush TxBrush = Frozen(Color.FromRgb(0xFF, 0x70, 0x40));
    private static readonly Brush SendingBrush = Frozen(Color.FromRgb(0xE0, 0x20, 0x20));
    private static Brush TalkBrush => Themes.ThemeBrush.Get("Dcb.Warn");

    private sealed class ChannelRow
    {
        public required int Channel;
        public required TextBox Box;
        public required TextBlock Role;
        public required Border RoleBadge;
        public required Button Tx;
        public required Button People;
        public required TextBlock Names;
        public bool Expanded;
    }

    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<ChannelRow> _rows = [];
    private readonly HashSet<string> _expandedListen = [];
    private string _listenShown = "";
    private bool _loading = true;
    private static SrsSettings Settings => AppState.Settings.Srs;

    public SrsPanel()
    {
        InitializeComponent();
        (Settings.Position == SrsPosition.Fixed ? PositionFixed : PositionAtc).IsChecked = true;
        _loading = false;
        ShowFixed();
        for (var ch = 1; ch <= ChannelPlan.Count; ch++) _rows.Add(BuildRow(ch));
        _status.Tick += (_, _) => ShowStatus();
        Loaded += (_, _) =>
        {
            _status.Start();
            SrsListen.Instance.Changed += Refresh;
            SrsListen.Instance.TalkChanged += Refresh;
            SrsChannels.Changed += Refresh;
            AppState.ListenUnitChanged += Refresh;
            AppState.Store.Changed += Refresh;
            AppState.Store.Changed += ShowAirfields;  // carriers and AWACS come and go
            AppState.Store.AirfieldsChanged += ShowAirfields;
            AppState.AirfieldSelectionChanged += ShowAirfieldSelection;
            Themes.ThemeManager.Changed += ThemeChanged;
            ShowStatus();
            Refresh();
            ShowAirfields();
        };
        Unloaded += (_, _) =>
        {
            _status.Stop();
            SrsListen.Instance.Changed -= Refresh;
            SrsListen.Instance.TalkChanged -= Refresh;
            SrsChannels.Changed -= Refresh;
            AppState.ListenUnitChanged -= Refresh;
            AppState.Store.Changed -= Refresh;
            AppState.Store.Changed -= ShowAirfields;
            AppState.Store.AirfieldsChanged -= ShowAirfields;
            AppState.AirfieldSelectionChanged -= ShowAirfieldSelection;
            Themes.ThemeManager.Changed -= ThemeChanged;
        };
    }

    // ---- Airfields: where the radio stands -----------------------------------------------------

    private bool _selectingAirfield;
    private string _placesShown = "";

    /// <summary>A place the radio can stand: an airfield (id) or an own carrier / AWACS (unit name).</summary>
    private sealed record Place(string Kind, string Text, string Owner, int? FieldId, string? UnitName);

    private static readonly string[] AwacsTypes = ["E-3", "E-2", "A-50", "KJ-2000", "E-7"];

    /// <summary>What the filters let through: own carriers and AWACS first, then airfields A–Z.</summary>
    private List<Place> Places()
    {
        var store = AppState.Store;
        var own = store.Coalition ?? "";
        var places = new List<Place>();
        if (ShowCarriers.IsChecked == true)
        {
            var carrierIds = store.Carriers.Select(c => c.Id).ToHashSet();
            foreach (var u in store.Tracks.Select(t => t.Unit).Where(u => !u.Detected && carrierIds.Contains(u.Id) && u.Name is not null))
                places.Add(new Place("carrier", $"⚓ {Symbology.NameOf(u) ?? u.Name}  ·  {u.Type}", own, null, u.Name));
        }
        if (ShowAwacs.IsChecked == true)
            foreach (var u in store.Tracks.Select(t => t.Unit).Where(u => !u.Detected && u.Name is not null && u.Type is { } ty && AwacsTypes.Any(a => ty.StartsWith(a, StringComparison.OrdinalIgnoreCase))))
                places.Add(new Place("awacs", $"✈ {Symbology.NameOf(u) ?? u.Name}  ·  {u.Type}", own, null, u.Name));
        foreach (var a in store.Airfields.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (a.IsHelipad ? ShowHelipads.IsChecked != true : ShowFields.IsChecked != true) continue;
            places.Add(new Place(a.IsHelipad ? "helipad" : "airfield", $"{(a.IsHelipad ? "H " : "")}{a.Name.Replace('_', ' ')}  ·  {a.Owner}", a.Owner, a.Id, null));
        }
        var find = AirfieldFind.Text.Trim();
        bool OwnerShown(string o) => o == "blue" ? ShowBlue.IsChecked == true : o == "red" ? ShowRed.IsChecked == true : ShowNeutral.IsChecked == true;
        return places.Where(p => OwnerShown(p.Owner) && (find.Length == 0 || p.Text.Contains(find, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Rebuilds the list when its contents change (carriers and AWACS come and go).</summary>
    private void ShowAirfields()
    {
        var places = Places();
        var signature = string.Join("|", places.Select(p => p.Text));
        if (signature == _placesShown) return;
        _placesShown = signature;
        _selectingAirfield = true;
        AirfieldList.Items.Clear();
        foreach (var p in places)
        {
            var item = new ListBoxItem { Content = p.Text, Tag = p, Padding = new Thickness(2, 0, 2, 0) };
            if (p.Owner is "blue" or "red") item.Foreground = p.Owner == "blue" ? BlueOwner : RedOwner;
            else item.SetResourceReference(ForegroundProperty, "Dcb.TextDim");
            AirfieldList.Items.Add(item);
        }
        _selectingAirfield = false;
        ShowAirfieldSelection();
    }

    private void ShowAirfieldSelection()
    {
        _selectingAirfield = true;
        AirfieldList.SelectedItem = AirfieldList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is Place p &&
            (Settings.Position == SrsPosition.Unit ? p.UnitName is not null && p.UnitName == Settings.PositionUnit : p.FieldId is not null && p.FieldId == AppState.SelectedAirfieldId));
        if (AirfieldList.SelectedItem is not null) AirfieldList.ScrollIntoView(AirfieldList.SelectedItem);
        _selectingAirfield = false;
    }

    private void AirfieldFind_Changed(object sender, TextChangedEventArgs e) => ShowAirfields();

    private void AirfieldFilter_Click(object sender, RoutedEventArgs e) => ShowAirfields();

    private void AirfieldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingAirfield || AirfieldList.SelectedItem is not ListBoxItem { Tag: Place p }) return;
        // Picking a place here means: stand there.
        if (p.UnitName is { } name)
        {
            Settings.PositionUnit = name;
            Settings.Position = SrsPosition.Unit;
        }
        else if (p.FieldId is { } id)
        {
            AppState.SelectAirfield(id);
            Settings.Position = SrsPosition.AtcAirfield;
        }
        _loading = true;
        PositionAtc.IsChecked = true;
        _loading = false;
    }

    private static readonly Brush BlueOwner = Frozen(Color.FromRgb(0x5A, 0xA0, 0xFF));
    private static readonly Brush RedOwner = Frozen(Color.FromRgb(0xFF, 0x60, 0x60));

    private void ThemeChanged()
    {
        _listenShown = "";  // the player's radio rows are built with the theme's colours
        Refresh();
    }

    // ---- Channels -------------------------------------------------------------------------

    private ChannelRow BuildRow(int ch)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        foreach (var w in new[] { 34.0, 92, 72, double.NaN, double.NaN, double.NaN })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? GridLength.Auto : new GridLength(w) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Add(grid, new TextBlock { Text = $"CH{ch}", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center }, 0);
        var role = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis };
        var badge = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Child = role };
        Add(grid, badge, 1);
        var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "MHz, e.g. 251.000 — Enter tunes the channel" };
        Add(grid, box, 2);
        var set = new Button { Content = "Set", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Tune this channel of your SRS app" };
        Add(grid, set, 3);
        var tx = new Button { Content = "TX", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), FontWeight = FontWeights.Bold, ToolTip = "Transmit on this channel now" };
        Add(grid, tx, 4);
        var people = new Button { Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Own SRS users on this frequency: click for the list" };
        Add(grid, people, 5);
        var names = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 1, 0, 3), Visibility = Visibility.Collapsed };
        names.SetResourceReference(TextBlock.ForegroundProperty, "Dcb.TextDim");
        Grid.SetRow(names, 1);
        Grid.SetColumnSpan(names, 7);
        grid.Children.Add(names);
        ChannelRows.Children.Add(grid);

        var row = new ChannelRow { Channel = ch, Box = box, Role = role, RoleBadge = badge, Tx = tx, People = people, Names = names };
        void Apply()
        {
            if (Parse(box.Text) is var (ok, mhz) && ok) SrsChannels.Set(ch, mhz);
            else box.Text = Format(SrsChannels.Mhz(ch));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        box.LostFocus += (_, _) => { if (Parse(box.Text) is (true, var m) && m != SrsChannels.Mhz(ch)) SrsChannels.Set(ch, m); };
        set.Click += (_, _) => Apply();
        tx.Click += (_, _) =>
        {
            Apply();
            SrsChannels.Transmit(ch);
        };
        people.Click += (_, _) =>
        {
            row.Expanded = !row.Expanded;
            Refresh();
        };
        return row;
    }

    private static void Add(Grid g, UIElement e, int column)
    {
        Grid.SetColumn(e, column);
        g.Children.Add(e);
    }

    /// <summary>(true, MHz) for a frequency, (true, null) for an empty box, (false, _) otherwise.</summary>
    private static (bool Ok, double? Mhz) Parse(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return (true, null);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 1000 ? (true, v) : (false, null);
    }

    private static string Format(double? mhz) => mhz is { } f ? f.ToString("0.000", CultureInfo.InvariantCulture) : "";

    private void Refresh()
    {
        using var perf = RenderStats.Measure("panel: srs");
        var listen = SrsListen.Instance;
        var now = DateTime.UtcNow;
        string Who(SrsClient c) => listen.Roster.TalkingOn(c.ClientGuid, now) is not null ? $"▶ {c.Name}" : c.Name;

        foreach (var row in _rows)
        {
            var ch = row.Channel;
            var mhz = SrsChannels.Mhz(ch);
            if (!row.Box.IsKeyboardFocusWithin) row.Box.Text = Format(mhz);
            var package = ChannelPlan.PackageOf(ch);
            row.Role.Text = package > 0 ? $"P{package} {AppState.Packages[package].Name}" : ch == ChannelPlan.Atc ? "ATC" : SrsChannels.RadioName(ch) is { Length: > 0 } radioName ? radioName : "free";
            row.RoleBadge.Background = package > 0 ? PackageColors.Brush(package) : ch == ChannelPlan.Atc ? AtcBrush : Brushes.Transparent;
            row.Role.Foreground = package > 0 || ch == ChannelPlan.Atc ? Brushes.Black : Themes.ThemeBrush.Get("Dcb.TextDim");
            var transmitting = SrsChannels.Transmitting == ch;
            var sending = SrsChannels.SendingNow == ch;
            row.Tx.Background = sending ? SendingBrush : transmitting ? TxBrush : Themes.ThemeBrush.Get("Dcb.PanelAlt");
            row.Tx.Content = sending ? "TX ●" : "TX";
            row.Tx.Foreground = sending ? Brushes.White : transmitting ? Brushes.Black : Themes.ThemeBrush.Get("Dcb.Text");
            row.Tx.IsEnabled = mhz is not null;
            var people = mhz is { } f && listen.Connected ? listen.Roster.ClientsOn(f * 1e6) : [];
            var talking = people.Any(c => listen.Roster.TalkingOn(c.ClientGuid, now) is not null);
            // Without DCB's SRS connection: the app's own count (no names).
            row.People.Content = listen.Connected ? $"{people.Count} on it {(row.Expanded ? "▴" : "▾")}"
                : SrsChannels.AppCount(ch) is { } appCount && mhz is not null ? $"{appCount} on it" : "—";
            row.People.Foreground = talking ? TalkBrush : Themes.ThemeBrush.Get("Dcb.Text");
            row.People.IsEnabled = people.Count > 0;
            row.Names.Text = string.Join(", ", people.Select(Who));
            row.Names.Visibility = row.Expanded && people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowListen();
        ListenStatusText.Text = listen.Status;
    }

    // ---- Listen traffic -------------------------------------------------------------------

    /// <summary>Snapshot aid: the SRS user whose radios are shown, by SRS name (the test mission
    /// has no players to right-click).</summary>
    public static string? DebugListenName { get; set; }

    private void ShowListen()
    {
        var listen = SrsListen.Instance;
        var unit = AppState.ListenUnit is { } id ? AppState.Store.Find(id)?.Unit : null;
        var client = unit is null ? null : listen.ClientFor(unit);
        if (DebugListenName is { } debugName && unit is not null)
            client = listen.Roster.Own.FirstOrDefault(c => c.Name == debugName);
        StopListenButton.Visibility = unit is null ? Visibility.Collapsed : Visibility.Visible;
        if (unit is null)
        {
            ListenBox.Header = "Listen traffic";
            ListenText.Text = AppState.ListenUnit is null ? "Right-click a player's aircraft on a map or in Objects → Listen traffic." : "That aircraft is gone.";
            Rebuild("", []);
            return;
        }
        var name = Symbology.NameOf(unit) ?? unit.Player ?? $"#{unit.Id}";
        ListenBox.Header = $"Listen traffic: {name}";
        if (client is null)
        {
            ListenText.Text = !listen.Connected ? "DCB is not connected to SRS." : $"{unit.Player ?? name} is not on SRS (or uses another name there).";
            Rebuild("", []);
            return;
        }
        ListenText.Text = $"{client.Name}'s radios as SRS reports them:";
        var now = DateTime.UtcNow;
        var radios = SrsRoster.RadiosOf(client).Select(r =>
        {
            var people = listen.Roster.ClientsOn(r.Channel.Hz);
            var who = string.Join(", ", people.Select(c => listen.Roster.TalkingOn(c.ClientGuid, now) is not null ? $"▶ {c.Name}" : c.Name));
            return (r.Radio, r.Channel, r.Guard, Count: people.Count, Who: who);
        }).ToList();
        var signature = string.Join("|", radios.Select(r => $"{r.Radio}{r.Channel}{r.Guard}{r.Who}")) + string.Join(",", _expandedListen);
        Rebuild(signature, radios);
    }

    /// <summary>Rebuilds the player's radio rows only when they change (keeps the channel pickers).</summary>
    private void Rebuild(string signature, List<(int Radio, SrsChannel Channel, bool Guard, int Count, string Who)> radios)
    {
        if (signature == _listenShown) return;
        _listenShown = signature;
        ListenRows.Children.Clear();
        foreach (var r in radios)
        {
            var key = $"{r.Radio}{r.Guard}";
            var line = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var put = new Button { Content = "Put on", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Tune that channel of yours to this frequency" };
            var pick = new ComboBox { Width = 58, ItemsSource = ChannelPlan.Free.Select(c => $"CH{c}").ToList(), SelectedIndex = 0 };
            DockPanel.SetDock(put, Dock.Right);
            DockPanel.SetDock(pick, Dock.Right);
            line.Children.Add(put);
            line.Children.Add(pick);
            var people = new Button { Content = $"{r.Count} on it {(_expandedListen.Contains(key) ? "▴" : "▾")}", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = r.Count > 0 };
            DockPanel.SetDock(people, Dock.Right);
            line.Children.Add(people);
            line.Children.Add(new TextBlock { Text = $"Radio {r.Radio}{(r.Guard ? " guard" : "")}   {r.Channel}", FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center });
            var channel = r.Channel;
            put.Click += (_, _) =>
            {
                var ch = ChannelPlan.Free.ElementAt(Math.Max(0, pick.SelectedIndex));
                SrsChannels.Set(ch, channel.Mhz);
            };
            people.Click += (_, _) =>
            {
                if (!_expandedListen.Remove(key)) _expandedListen.Add(key);
                _listenShown = "";
                Refresh();
            };
            ListenRows.Children.Add(line);
            if (_expandedListen.Contains(key) && r.Who.Length > 0)
                ListenRows.Children.Add(new TextBlock { Text = r.Who, TextWrapping = TextWrapping.Wrap, Foreground = Themes.ThemeBrush.Get("Dcb.TextDim"), Margin = new Thickness(12, 0, 0, 3) });
        }
    }

    private void StopListen_Click(object sender, RoutedEventArgs e) => AppState.ListenTo(null);

    // ---- Status and position --------------------------------------------------------------

    private void ShowStatus()
    {
        var running = SrsApp.Running;
        AppStatusText.Text = running
            ? (SrsChannels.IsLive ? "SRS app connected: channels read live from it." : "SRS app running, not connected (click Connect in its window, External AWACS Mode): channels as DCB set them.")
            : SrsListen.Instance.AppStatus is { Length: > 0 } s ? s : "SRS app not running.";
        StartAppButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        var bridge = SrsBridge.Instance;
        CommandText.Text = string.Join("\n", new[] { bridge.CommandStatus, bridge.PositionStatus, bridge.LineOfSightStatus }.Where(t => t is { Length: > 0 }));
    }

    private void StartApp_Click(object sender, RoutedEventArgs e)
    {
        AppStatusText.Text = SrsApp.EnsureRunning();
    }

    private void ShowFixed() =>
        FixedText.Text = Settings.FixedLat is { } lat && Settings.FixedLon is { } lon ? $"{lat:F4}, {lon:F4}" : "(not set)";

    private void Position_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Back to the list: a carrier or AWACS picked there before stays the position.
        Settings.Position = PositionFixed.IsChecked == true ? SrsPosition.Fixed
            : Settings.PositionUnit is not null && AirfieldList.SelectedItem is ListBoxItem { Tag: Place { UnitName: not null } } ? SrsPosition.Unit
            : SrsPosition.AtcAirfield;
    }

    private void SetFixed_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.ActiveMap is not { } map)
        {
            MessageBox.Show(Window.GetWindow(this), "Click on a map first: its centre becomes the point.", "SRS");
            return;
        }
        var (x, y, _) = map.ViewCenter;
        var (lon, lat) = Mercator.ToLonLat(x, y);
        Settings.FixedLat = Math.Round(lat, 5);
        Settings.FixedLon = Math.Round(lon, 5);
        PositionFixed.IsChecked = true;
        ShowFixed();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
