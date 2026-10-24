using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dcb.Client.Core;
using Dcb.Client.Map;

namespace Dcb.Client.Panels;

/// <summary>Row of the Objects table: one track, formatted as on the map. Rows live as long as
/// their track and are updated in place, so the table only redraws the cells that changed.</summary>
public sealed class TrackRow(long id) : INotifyPropertyChanged
{
    public long Id { get; } = id;
    public Identity Identity { get; private set; }
    public SymbolKind Kind { get; private set; }
    public string? Name { get; private set; }
    public string? Type { get; private set; }
    public string Altitude { get; private set; } = "";
    public double? Hdg { get; private set; }
    public double? Speed { get; private set; }
    public string? Group { get; private set; }
    public bool Lost { get; private set; }
    public bool IsPlayer { get; private set; }
    /// <summary>"P1"… when the aircraft is in a package.</summary>
    public string? Package { get; private set; }
    public Brush? PackageBrush { get; private set; }

    public string IdentityText => Identity.ToString();
    public Brush Brush => Themes.ThemeBrush.Get(Identity switch
    {
        Identity.Friendly => "Dcb.Friendly",
        Identity.Hostile => "Dcb.Hostile",
        _ => "Dcb.Unknown",
    });

    /// <summary>The theme changed: the row's colour comes from it.</summary>
    public void ThemeChanged() => Changed(nameof(Brush));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(Track t, string own)
    {
        var u = t.Unit;
        var kind = Symbology.KindOf(u);
        var identity = Symbology.IdentityOf(u, own);
        if (Set(Identity, identity, v => Identity = v))
        {
            Changed(nameof(IdentityText));
            Changed(nameof(Brush));
        }
        Set(Kind, kind, v => Kind = v);
        Set(Name, Symbology.NameOf(u), v => Name = v);
        Set(Type, u.Shown, v => Type = v);
        Set(Altitude, Symbology.IsAir(kind) ? $"{Symbology.AltitudeHundreds(u)}{Symbology.TrendOf(u)}" : "", v => Altitude = v);
        Set(Hdg, u.Hdg, v => Hdg = v);
        Set(Speed, u.SpdKt, v => Speed = v);
        Set(Group, u.Group, v => Group = v);
        Set(Lost, t.Lost, v => Lost = v);
        Set(IsPlayer, u.Player is not null, v => IsPlayer = v);
        var package = AppState.Packages.PackageOf(u.Id);
        if (Set(Package, package?.Tag, v => Package = v))
        {
            PackageBrush = package is null ? null : PackageColors.Brush(package.Number);
            Changed(nameof(PackageBrush));
        }
    }

    private bool Set<T>(T current, T value, Action<T> assign, [CallerArgumentExpression(nameof(current))] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        Changed(name);
        return true;
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>All tracks in a table. Selection is shared with the maps; double-click centres the
/// map last used. Picture updates are applied to the existing rows at background priority:
/// rebuilding the table took 30–50 ms on the UI thread every update, a hitch in every pan and
/// zoom of the maps.</summary>
public partial class ObjectsPanel : UserControl
{
    private readonly ObservableCollection<TrackRow> _rows = [];
    private readonly Dictionary<long, TrackRow> _byId = [];
    private bool _syncingSelection;
    private bool _queued;

    public ObjectsPanel()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            AppState.Store.Changed += QueueRefresh;
            AppState.Packages.Changed += QueueRefresh;
            AppState.Store.SelectionChanged += SyncSelectionFromStore;
            Themes.ThemeManager.Changed += ThemeChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppState.Store.Changed -= QueueRefresh;
            AppState.Packages.Changed -= QueueRefresh;
            AppState.Store.SelectionChanged -= SyncSelectionFromStore;
            Themes.ThemeManager.Changed -= ThemeChanged;
        };
    }

    private void ThemeChanged()
    {
        foreach (var row in _byId.Values) row.ThemeChanged();
    }

    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _queued = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        using var perf = RenderStats.Measure("ObjectsPanel");
        var store = AppState.Store;
        if (store.Coalition is not { } own)
        {
            _rows.Clear();
            _byId.Clear();
            return;
        }
        var wanted = new List<TrackRow>();
        foreach (var t in store.Tracks)
        {
            if (!_byId.TryGetValue(t.Unit.Id, out var row)) _byId[t.Unit.Id] = row = new TrackRow(t.Unit.Id);
            row.Update(t, own);
            if (Visible(row)) wanted.Add(row);
        }
        wanted.Sort((a, b) =>
        {
            var c = a.Identity.CompareTo(b.Identity);
            if (c == 0) c = a.Kind.CompareTo(b.Kind);
            if (c == 0) c = string.Compare(a.Name ?? a.Type, b.Name ?? b.Type, StringComparison.Ordinal);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        var keep = wanted.ToHashSet();
        foreach (var id in _byId.Keys.Where(id => store.Find(id) is null).ToList()) _byId.Remove(id);

        // Bring the collection to the wanted order with the fewest changes: rows that stay are
        // moved (usually not at all), others added or removed.
        _syncingSelection = true;
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(_rows[i])) _rows.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            var at = i < _rows.Count && ReferenceEquals(_rows[i], wanted[i]) ? i : _rows.IndexOf(wanted[i]);
            if (at < 0) _rows.Insert(i, wanted[i]);
            else if (at != i) _rows.Move(at, i);
        }
        if (Grid.SelectedItem is not TrackRow sel || sel.Id != store.SelectedId)
            Grid.SelectedItem = store.SelectedId is { } sid && _byId.TryGetValue(sid, out var s) && keep.Contains(s) ? s : null;
        _syncingSelection = false;
    }

    private bool Visible(TrackRow r)
    {
        var kindOk = r.Kind switch
        {
            SymbolKind.Aircraft or SymbolKind.Helicopter => AirBox.IsChecked == true,
            SymbolKind.Ship => ShipsBox.IsChecked == true,
            _ => GroundBox.IsChecked == true,
        };
        var sideOk = r.Identity == Identity.Friendly ? OwnBox.IsChecked == true : ContactsBox.IsChecked == true;
        var playerOk = PlayersBox.IsChecked != true || r.IsPlayer;
        return kindOk && sideOk && playerOk;
    }

    /// <summary>Right-click on a row: that aircraft's package items (own aircraft only).</summary>
    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (e.OriginalSource as DependencyObject) is { } src ? FindRow(src) : null;
        if (row?.Item is not TrackRow r || AppState.Store.Find(r.Id)?.Unit is not { } u || !PackageMenu.Packable(u))
        {
            e.Handled = true;  // nothing to offer
            return;
        }
        Grid.SelectedItem = r;
        var menu = Grid.ContextMenu!;
        menu.Items.Clear();
        foreach (var item in PackageMenu.Items(u)) menu.Items.Add(item);
        if (PackageMenu.ListenItem(u, this) is { } listen)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(listen);
        }
    }

    private static DataGridRow? FindRow(DependencyObject d)
    {
        while (d is not null and not DataGridRow) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as DataGridRow;
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        AppState.Store.Select((Grid.SelectedItem as TrackRow)?.Id);
    }

    private void SyncSelectionFromStore()
    {
        _syncingSelection = true;
        Grid.SelectedItem = AppState.Store.SelectedId is { } id && _byId.TryGetValue(id, out var row) && _rows.Contains(row) ? row : null;
        if (Grid.SelectedItem is not null) Grid.ScrollIntoView(Grid.SelectedItem);
        _syncingSelection = false;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is TrackRow row) AppState.ActiveMap?.CenterOn(row.Id);
    }
}
