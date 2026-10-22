using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dcb.Client.Core;
using Dcb.Client.Map.Airfields;

namespace Dcb.Client.Panels;

/// <summary>A list row. A class, not a record: the list is rebuilt on every airfield update
/// and filter change, and value-equal rows let the DataGrid keep a stale selection.</summary>
public sealed class AirfieldRow
{
    public AirfieldRow(Airfield field) => Field = field;

    /// <summary>A row for one of the coalition's carriers (listed first).</summary>
    public AirfieldRow(CarrierInfo carrier, string coalition)
    {
        Carrier = carrier;
        _coalition = coalition;
    }

    private readonly string? _coalition;

    public Airfield? Field { get; }
    public CarrierInfo? Carrier { get; }
    /// <summary>Airfield id; carriers get −(unit id), never an airfield's.</summary>
    public int Id => Field?.Id ?? -(int)Carrier!.Id;
    public string Name => Carrier is { } c ? "⚓ " + CarrierGeometry.Label(c.Type) : Field!.IsHelipad ? $"{Field.Name} (H)" : Field.Name;
    public string Owner => Field?.Owner ?? _coalition ?? "";
    public string ParkingCount => Field is { } f ? f.Parking.Count.ToString() : "";
    public string RunwaysText => Carrier is { } c ? $"carrier{(c.Tacan is { } t ? $" · TACAN {t}" : "")}{(c.Icls is { } i ? $" · ICLS {i}" : "")}"
        : string.Join(", ", Field!.Runways.Select(r =>
        {
            var (near, far) = r.Designators;
            return $"{near}/{far} {r.Length:0} m";
        }));
    public Brush Brush => Themes.ThemeBrush.Get(Carrier is not null ? (_coalition == "red" ? "Dcb.Hostile" : "Dcb.Friendly")
        : Field!.Coalition switch { 1 => "Dcb.Hostile", 2 => "Dcb.Friendly", _ => "Dcb.Neutral" });
}

/// <summary>ATC tab: pick an airfield from the list and work its traffic on DCB's own airport
/// diagram below — a separate zone, not a map (runways, taxiways, parking, names, live
/// traffic). Like any tab it can float in its own window. Double-click in the list also
/// centres the main map on the field.</summary>
public partial class AtcPanel : UserControl
{
    // The airfield being worked. Only the controller's own pick changes it; rebuilding the
    // list (airfield updates every minute, filtering) restores it and never replaces it.
    private int? _selectedId = AppState.Settings.AtcAirfieldId;
    private bool _rebuilding;

    public AtcPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppState.Store.AirfieldsChanged += Refresh;
            AppState.Store.Changed += Diagram.InvalidateVisual;
            AppState.Store.Changed += Approach.InvalidateVisual;
            AppState.Store.Changed += CarrierView.InvalidateVisual;
            AppState.Store.Changed += CarriersMaybeChanged;
            AppState.Store.LsoChanged += Lso.InvalidateVisual;
            AppState.Store.LsoChanged += CarrierView.InvalidateVisual;
            AppState.Queues.Changed += Approach.InvalidateVisual;
            AppState.Queues.Changed += CarrierView.InvalidateVisual;
            Themes.ThemeManager.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppState.Store.AirfieldsChanged -= Refresh;
            AppState.Store.Changed -= Diagram.InvalidateVisual;
            AppState.Store.Changed -= Approach.InvalidateVisual;
            AppState.Store.Changed -= CarrierView.InvalidateVisual;
            AppState.Store.Changed -= CarriersMaybeChanged;
            AppState.Store.LsoChanged -= Lso.InvalidateVisual;
            AppState.Store.LsoChanged -= CarrierView.InvalidateVisual;
            AppState.Queues.Changed -= Approach.InvalidateVisual;
            AppState.Queues.Changed -= CarrierView.InvalidateVisual;
            Themes.ThemeManager.Changed -= Refresh;
        };
    }

    private string _carrierIds = "";

    /// <summary>Pictures come every second; the list is rebuilt only when the carriers change.</summary>
    private void CarriersMaybeChanged()
    {
        var ids = string.Join(",", AppState.Store.Carriers.Select(c => c.Id));
        if (ids == _carrierIds) return;
        _carrierIds = ids;
        Refresh();
    }

    private void Refresh()
    {
        var filter = FilterBox.Text.Trim();
        var store = AppState.Store;
        var carriers = store.Carriers
            .Select(c => new AirfieldRow(c, store.Coalition ?? ""))
            .Where(r => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var rows = carriers.Concat(store.Airfields
            .Where(a => (HelipadBox.IsChecked == true || !a.IsHelipad) &&
                        (a.Coalition switch { 1 => RedBox.IsChecked, 2 => BlueBox.IsChecked, _ => NeutralBox.IsChecked }) == true &&
                        (filter.Length == 0 || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(a => a.Name)
            .Select(a => new AirfieldRow(a)))
            .ToList();
        _rebuilding = true;
        try
        {
            Grid.ItemsSource = rows;
            // Filtered out: no row highlighted, but the field stays the one being worked.
            Grid.SelectedItem = rows.FirstOrDefault(r => r.Id == _selectedId);
        }
        finally
        {
            _rebuilding = false;
        }
        if (Grid.SelectedItem is AirfieldRow row)
        {
            Grid.ScrollIntoView(row);
            Show(row);  // fresh data (owner, parking) for the same field
        }
        else if (_selectedId is { } id && AppState.Store.Airfields.FirstOrDefault(a => a.Id == id) is { } field)
        {
            Show(new AirfieldRow(field));
        }
        else if (_selectedId is { } cid && AppState.Store.Carriers.FirstOrDefault(c => -(int)c.Id == cid) is { } carrier)
        {
            Show(new AirfieldRow(carrier, AppState.Store.Coalition ?? ""));
        }
        if (AppState.Store.Airfields.Count == 0) FieldText.Text = "No airfields yet: connect to a server.";
    }

    /// <summary>Snapshot aid: show the approach view instead of the diagram.</summary>
    public void ShowApproach() => Views.SelectedItem = ApproachTab;

    public void ShowDiagram() => Views.SelectedItem = DiagramTab;

    /// <summary>Selects an airfield by name (snapshot/test aid); true when found.</summary>
    public bool Select(string name)
    {
        var row = (Grid.ItemsSource as IEnumerable<AirfieldRow>)?.FirstOrDefault(r =>
            (r.Field?.Name ?? r.Carrier!.Type).Equals(name, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return false;
        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row);
        return true;
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => Refresh();
    private void Filter_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The row actually picked, not Grid.SelectedItem (which can lag behind a rebuild).
        if (_rebuilding || e.AddedItems.OfType<AirfieldRow>().FirstOrDefault() is not { } row) return;
        _selectedId = row.Id;
        AppState.Settings.AtcAirfieldId = row.Id;
        Show(row);
    }

    private void Show(AirfieldRow row)
    {
        if (row.Carrier is { } carrier)
        {
            ShowCarrier(carrier);
            return;
        }
        var field = row.Field!;
        SetTabs(carrier: false);
        Queue.Show(RunwayQueues.AirfieldKey(field.Id));
        AppState.SelectAirfield(row.Id);
        var tower = ChartLabels.TowerFor(field) is { } t ? $" · tower {t}" : "";
        FieldText.Text = $"{row.Name} · {row.Owner} · {row.RunwaysText} · {row.ParkingCount} parking{tower}";
        if (Diagram.Field?.Id != row.Id)
        {
            Diagram.Show(field, ChartLabels.For(field));
            Approach.Show(field);
        }
        AtisButton.IsEnabled = InfoButton.IsEnabled = !field.IsHelipad;
        ShowAtcChannel(AppState.Store.Theatre is { } theatre
            ? Srs.SrsBridge.FrequenciesOf(theatre.Id, field.Id).Select(f => ($"{f.Band} {f.Mhz:0.000} {f.Modulation}", f.Mhz)).ToList()
            : []);
    }

    private void ShowCarrier(CarrierInfo carrier)
    {
        SetTabs(carrier: true);
        AppState.SelectCarrier(carrier.Id);
        Queue.Show(RunwayQueues.CarrierKey(carrier.Id));
        var parts = new List<string> { CarrierGeometry.Label(carrier.Type) };
        if (carrier.Tacan is { } tacan) parts.Add($"TACAN {tacan}");
        if (carrier.Icls is { } icls) parts.Add($"ICLS {icls}");
        if (carrier.Link4 is { } l4) parts.Add($"Link 4 {l4:0.000}");
        if (carrier.Radio is { } radio) parts.Add($"radio {radio:0.000}");
        FieldText.Text = string.Join(" · ", parts);
        CarrierView.Show(carrier.Id);
        Lso.Show(carrier.Id);
        AtisButton.IsEnabled = InfoButton.IsEnabled = false;
        ShowAtcChannel(carrier.Radio is { } shipRadio ? [($"Radio {shipRadio:0.000}", shipRadio)] : []);
    }

    /// <summary>Airfields show Diagram and Approach; carriers show Carrier and LSO.</summary>
    private void SetTabs(bool carrier)
    {
        DiagramTab.Visibility = ApproachTab.Visibility = carrier ? Visibility.Collapsed : Visibility.Visible;
        CarrierTab.Visibility = LsoTab.Visibility = carrier ? Visibility.Visible : Visibility.Collapsed;
        if (Views.SelectedItem is TabItem { Visibility: not Visibility.Visible })
            Views.SelectedItem = carrier ? CarrierTab : DiagramTab;
    }

    /// <summary>Snapshot aid: show the LSO view.</summary>
    public void ShowLso() => Views.SelectedItem = LsoTab;

    private Airfield? Current => _selectedId is { } id && id >= 0 ? AppState.Store.Airfields.FirstOrDefault(a => a.Id == id) : null;

    private void Atis_Click(object sender, RoutedEventArgs e) => OpenSend(atis: true);
    private void Info_Click(object sender, RoutedEventArgs e) => OpenSend(atis: false);

    /// <summary>The frequencies the ATC channel (6) can be set to for the field or carrier on show.</summary>
    private void ShowAtcChannel(List<(string Text, double Mhz)> frequencies)
    {
        // Keep the controller's pick while the same list is shown again (pictures refresh it).
        var keep = AtcChannelBox.SelectedItem is ComboBoxItem { Tag: double was } && frequencies.Any(f => f.Mhz == was) ? was : (double?)null;
        AtcChannelBox.Items.Clear();
        foreach (var (text, mhz) in frequencies) AtcChannelBox.Items.Add(new ComboBoxItem { Content = text, Tag = mhz });
        AtcChannelBox.SelectedIndex = keep is { } k ? frequencies.FindIndex(f => f.Mhz == k) : frequencies.Count > 0 ? 0 : -1;
        AtcChannelBox.IsEnabled = AtcChannelButton.IsEnabled = frequencies.Count > 0;
    }

    private void AtcChannel_Click(object sender, RoutedEventArgs e)
    {
        if (AtcChannelBox.SelectedItem is ComboBoxItem { Tag: double mhz }) Srs.SrsChannels.Set(ChannelPlan.Atc, mhz);
    }

    private void OpenSend(bool atis)
    {
        if (Current is not { } field) return;
        new SendMessageWindow(field, recipient: null, atis) { Owner = Window.GetWindow(this) }.Show();
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedId is { } id && id >= 0) AppState.ActiveMap?.ShowAirfield(id);
    }
}
