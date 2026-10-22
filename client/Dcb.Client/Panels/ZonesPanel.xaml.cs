using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dcb.Client.Core;
using Dcb.Client.Map;

namespace Dcb.Client.Panels;

public sealed record ZoneRow(FootholdZone Zone)
{
    public int? Waypoint => Zone.Waypoint;
    public string Name => Zone.Name;
    public string Owner => Zone.Owner;
    public bool Suspended => Zone.Suspended;
    public string StrengthText => Zone.Max > 0 ? $"{Zone.Built}/{Zone.Max}" : Zone.Built > 0 ? Zone.Built.ToString() : "";
    public string Kind => (Zone.Facility ?? (Zone.Airbase is not null ? "airbase" : "")) + (Zone.Logistic ? " · logistics" : "");
    public string Status => !Zone.Active ? "inactive" : Zone.Suspended ? "idle" : "active";
    public Brush Brush => Themes.ThemeBrush.Get(Zone.Side switch { 1 => "Dcb.Hostile", 2 => "Dcb.Friendly", _ => "Dcb.Neutral" });
}

/// <summary>Foothold campaign zones (when the server runs Foothold). Selection highlights the
/// zone on every map; double-click centres the map last used.</summary>
public partial class ZonesPanel : UserControl
{
    public ZonesPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppState.Store.FootholdChanged += Refresh;
            Themes.ThemeManager.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppState.Store.FootholdChanged -= Refresh;
            Themes.ThemeManager.Changed -= Refresh;
        };
    }

    private void Refresh()
    {
        var state = AppState.Store.Foothold;
        EmptyText.Visibility = state is null ? Visibility.Visible : Visibility.Collapsed;
        Grid.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        if (state is null)
        {
            SummaryText.Text = "";
            return;
        }
        var selected = (Grid.SelectedItem as ZoneRow)?.Name;
        var rows = state.Zones.Where(Visible).Select(z => new ZoneRow(z))
            .OrderBy(r => r.Waypoint ?? int.MaxValue).ThenBy(r => r.Name).ToList();
        Grid.ItemsSource = rows;
        Grid.SelectedItem = rows.FirstOrDefault(r => r.Name == selected);
        SummaryText.Text = $"Blue {state.Zones.Count(z => z.Side == 2)} · Red {state.Zones.Count(z => z.Side == 1)} · " +
                           $"Neutral {state.Zones.Count(z => z.Side == 0)} · {state.Links.Count} links";
    }

    private bool Visible(FootholdZone z)
    {
        var side = z.Side switch { 1 => RedBox.IsChecked, 2 => BlueBox.IsChecked, _ => NeutralBox.IsChecked } == true;
        return side && (SuspendedBox.IsChecked == true || !z.Suspended);
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AppState.SelectZone((Grid.SelectedItem as ZoneRow)?.Name);

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is ZoneRow row) AppState.ActiveMap?.CenterOnZone(row.Name);
    }
}
