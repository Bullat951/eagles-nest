using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dcb.Client.Core;
using Dcb.Client.Map.Airfields;

namespace Dcb.Client.Panels;

/// <summary>The ATC tab's runway queue for the selected airfield or carrier: one strip per
/// aircraft in the controller's order (arrivals orange, departures blue, as NATS colours its
/// tower strips) with bearing/range from the base, altitude, speed, time to the base and state,
/// buttons to reorder, and own aircraft DCB suggests adding. Strips drop out on their own once an
/// arrival has landed and slowed, or a departure is airborne and clear.</summary>
public partial class RunwayQueuePanel : UserControl
{
    private string? _key;

    public sealed record StripRow(long Unit, string PlaceText, string Name, string Type, string KindText, string State, string Detail, Brush Brush);
    public sealed record CandidateRow(long Unit, string Name, string Type, string Detail, string AddText, QueueKind Kind, Brush Brush);

    public RunwayQueuePanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppState.Store.Changed += Refresh;
            AppState.Queues.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppState.Store.Changed -= Refresh;
            AppState.Queues.Changed -= Refresh;
        };
    }

    /// <summary>Shows the queue of a base key ("af:&lt;id&gt;" or "cv:&lt;id&gt;").</summary>
    public void Show(string key)
    {
        _key = key;
        Refresh();
    }

    private bool _refreshing;

    private void Refresh()
    {
        if (_refreshing) return;  // pruning below raises Changed
        using var perf = RenderStats.Measure("panel: runway queue");
        _refreshing = true;
        try
        {
            Build();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Build()
    {
        // Strips that are done drop out of every queue, not only the one on show.
        foreach (var q in AppState.Queues.All.ToList())
            if (QueueBase.ForKey(q.Key) is { } b) AppState.Queues.Prune(q.Key, b.Observe());

        if (_key is null || QueueBase.ForKey(_key) is not { } bas)
        {
            TitleText.Text = "RUNWAY QUEUE";
            Strips.ItemsSource = null;
            Candidates.ItemsSource = null;
            GapText.Visibility = Visibility.Collapsed;
            return;
        }
        TitleText.Text = $"RUNWAY QUEUE · {bas.Name.ToUpperInvariant()}";
        var queue = AppState.Queues.For(_key);
        var obs = bas.Observe();
        var store = AppState.Store;

        var strips = new List<StripRow>();
        foreach (var e in queue.Entries)
        {
            if (store.Find(e.Unit)?.Unit is not { } u || !obs.TryGetValue(e.Unit, out var o)) continue;
            strips.Add(new StripRow(e.Unit, $"{queue.Place(e.Unit)}", Symbology.NameOf(u) ?? $"#{u.Id}", ShortType(u),
                e.Kind == QueueKind.Arrival ? "ARR" : "DEP", RunwayQueue.StateOf(e.Kind, o), Detail(bas, u, o, e.Kind), QueueBase.BrushOf(e.Kind)));
        }
        Strips.ItemsSource = strips;
        EmptyText.Visibility = strips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var gap = queue.DepartureGap(obs);
        GapText.Text = gap ?? "";
        GapText.Visibility = gap is null ? Visibility.Collapsed : Visibility.Visible;

        var candidates = queue.Candidates(obs.Values).Take(12)
            .Where(c => store.Find(c.Obs.Id)?.Unit is not null)
            .Select(c =>
            {
                var u = store.Find(c.Obs.Id)!.Unit;
                return new CandidateRow(u.Id, Symbology.NameOf(u) ?? $"#{u.Id}", ShortType(u), Detail(bas, u, c.Obs, c.Kind),
                    c.Kind == QueueKind.Arrival ? "+ ARR" : "+ DEP", c.Kind, QueueBase.BrushOf(c.Kind));
            }).ToList();
        Candidates.ItemsSource = candidates;
        NoCandidatesText.Visibility = candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"247°/12.3 NM · 045 · 220 KT · 3:20" (the time only for airborne arrivals closing in).</summary>
    private static string Detail(QueueBase bas, Unit u, QueueObs o, QueueKind kind)
    {
        var parts = new List<string> { $"{bas.BearingRange(u)} NM" };
        parts.Add(o.Airborne ? $"{(u.AltFt ?? 0) / 100:000}" : "GND");
        parts.Add($"{o.RelSpeedMs * Groove.KnotsPerMs:0} KT");
        if (kind == QueueKind.Arrival && o.Airborne && o.Closing && o.RelSpeedMs > 20)
        {
            var s = o.RangeNm * Groove.MetersPerNm / o.RelSpeedMs;
            parts.Add($"{(int)(s / 60)}:{(int)(s % 60):00}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Snapshot aid: queues every suggested aircraft as suggested.</summary>
    public void AddAllSuggested()
    {
        if (_key is null || Candidates.ItemsSource is not IEnumerable<CandidateRow> rows) return;
        foreach (var row in rows.ToList()) AppState.Queues.Add(_key, row.Unit, row.Kind);
    }

    private static string ShortType(Unit u) => u.Type is null ? "" : Symbology.ShortType(u.Type);

    private static long UnitOf(object sender) => (long)((FrameworkElement)sender).Tag;

    private void Up_Click(object sender, RoutedEventArgs e) { if (_key is not null) AppState.Queues.Move(_key, UnitOf(sender), -1); }
    private void Down_Click(object sender, RoutedEventArgs e) { if (_key is not null) AppState.Queues.Move(_key, UnitOf(sender), 1); }
    private void Priority_Click(object sender, RoutedEventArgs e) { if (_key is not null) AppState.Queues.Priority(_key, UnitOf(sender)); }
    private void Toggle_Click(object sender, RoutedEventArgs e) { if (_key is not null) AppState.Queues.Toggle(_key, UnitOf(sender)); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (_key is not null) AppState.Queues.Remove(_key, UnitOf(sender)); }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_key is null || ((FrameworkElement)sender).DataContext is not CandidateRow row) return;
        AppState.Queues.Add(_key, row.Unit, row.Kind);
    }
}
