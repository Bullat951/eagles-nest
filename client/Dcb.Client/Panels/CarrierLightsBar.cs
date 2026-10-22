using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dcb.Client.Panels;

/// <summary>The carrier's lighting mode, set from the Carrier view (command "carrier_lights", as
/// the Mission Editor's SET CARRIER LIGHTING MODE): OFF, AUTO, NAV, LAUNCH, RECOVERY. DCS does
/// not report a carrier's current mode, so the bar shows the last mode set from Eagle's Nest
/// (by any Carrier view of this client) and the server's answer.</summary>
public sealed class CarrierLightsBar : UserControl
{
    private static readonly (int Mode, string Label, string Tip)[] Modes =
    [
        (-2, "OFF", "All lights off (DCS takes ~12 s)"),
        (-1, "AUTO", "Let DCS decide (its own recovery logic)"),
        (0, "NAV", "Navigation lights only"),
        (1, "LAUNCH", "Launch lighting"),
        (2, "RECOVERY", "Recovery lighting: landing area, drop and deck-edge lights"),
    ];

    /// <summary>Last mode set per carrier unit id in this session, shared by every bar.</summary>
    private static readonly Dictionary<long, (int Mode, DateTime At)> Last = [];
    private static event Action? LastChanged;

    private readonly List<(int Mode, Button Button)> _buttons = [];
    private readonly List<(int Case, CheckBox Chip)> _cases = [];
    private readonly CheckBox _auto = new() { Content = "auto", ToolTip = "Show only the case DCS is running now (weather, and Case III at night)" };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private long? _carrier;
    private bool _busy;
    private int? _pending;  // a mode clicked while the previous one was still being sent

    public CarrierLightsBar()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        row.Children.Add(new TextBlock { Text = "Lights", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var (mode, label, tip) in Modes)
        {
            var b = new Button { Content = label, ToolTip = tip, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(8, 1, 8, 1) };
            b.Click += async (_, _) => await SetAsync(mode);
            _buttons.Add((mode, b));
            row.Children.Add(b);
        }
        // Guidance lines per recovery case (drawn by the Carrier view).
        row.Children.Add(new TextBlock { Text = "Guidance", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
        foreach (var c in new[] { 1, 2, 3 })
        {
            var chip = new CheckBox { Content = $"Case {new[] { "", "I", "II", "III" }[c]}" };
            chip.SetResourceReference(StyleProperty, "Dcb.Chip");
            chip.Click += (_, _) => Map.Airfields.CarrierCases.Toggle(c);
            _cases.Add((c, chip));
            row.Children.Add(chip);
        }
        _auto.SetResourceReference(StyleProperty, "Dcb.Chip");
        _auto.Click += (_, _) => Map.Airfields.CarrierCases.Auto();
        row.Children.Add(_auto);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Dcb.TextDim");
        row.Children.Add(_status);
        Content = row;
        Loaded += (_, _) =>
        {
            LastChanged += Show;
            Themes.ThemeManager.Changed += Show;
            Map.Airfields.CarrierCases.Changed += ShowCases;
            Show();
            ShowCases();
        };
        Unloaded += (_, _) =>
        {
            LastChanged -= Show;
            Themes.ThemeManager.Changed -= Show;
            Map.Airfields.CarrierCases.Changed -= ShowCases;
        };
    }

    private void ShowCases()
    {
        var current = Map.Airfields.CarrierCases.Current;
        foreach (var (c, chip) in _cases)
        {
            chip.IsChecked = Map.Airfields.CarrierCases.Shown(c);
            chip.ToolTip = c == current ? "The case DCS is running now" : "Show this case's guidance lines too";
            chip.FontWeight = c == current ? FontWeights.Bold : FontWeights.Normal;
        }
        _auto.IsChecked = Map.Airfields.CarrierCases.IsAuto;
    }

    /// <summary>The carrier the buttons act on (null: none picked).</summary>
    public long? CarrierId
    {
        get => _carrier;
        set
        {
            _carrier = value;
            _status.Text = "";
            Show();
        }
    }

    /// <summary>Sends a mode. A click while one is still on its way is not lost: the latest such
    /// click is sent as soon as the previous answer arrives (quick clicks used to vanish).</summary>
    private async Task SetAsync(int mode)
    {
        if (_carrier is not { } id) return;
        if (_busy)
        {
            _pending = mode;
            _status.Text = $"{Modes.First(m => m.Mode == mode).Label} next…";
            return;
        }
        _busy = true;
        try
        {
            int? next = mode;
            while (next is { } m)
            {
                _pending = null;
                _status.Text = $"Sending {Modes.First(x => x.Mode == m).Label}…";
                var result = await AppState.Connection.SendCommandAsync("carrier_lights", new Dictionary<string, object> { ["unit"] = id, ["mode"] = m });
                if (result.Ok)
                {
                    Last[id] = (m, DateTime.Now);
                    LastChanged?.Invoke();
                }
                else
                {
                    _status.Text = $"Not set: {result.Error}";
                    if (_pending is null) return;
                }
                next = _pending;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Show() => Show(keepError: false);

    private void Show(bool keepError)
    {
        var last = _carrier is { } id && Last.TryGetValue(id, out var l) ? l : ((int Mode, DateTime At)?)null;
        foreach (var (mode, b) in _buttons)
        {
            var on = last?.Mode == mode;
            b.Background = on ? Themes.ThemeBrush.Get("Dcb.Accent") : Themes.ThemeBrush.Get("Dcb.PanelAlt");
            b.Foreground = on ? Themes.ThemeBrush.Get("Dcb.AccentText") : Themes.ThemeBrush.Get("Dcb.Text");
            b.IsEnabled = _carrier is not null;
        }
        if (keepError) return;
        _status.Text = _carrier is null ? ""
            : last is { } set ? $"set to {Modes.First(m => m.Mode == set.Mode).Label} at {set.At:HH:mm:ss} (DCS does not report the mode)"
            : "not set from Eagle's Nest yet";
    }
}
