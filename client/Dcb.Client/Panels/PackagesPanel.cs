using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dcb.Client.Core;
using Dcb.Client.Map;

namespace Dcb.Client.Panels;

/// <summary>The controller's four packages: each with its colour, a name, a frequency and one
/// of the controller's SRS radios 5–8 kept for it, a JUMP button that sets that radio to the
/// package's frequency and makes it the one that transmits, and its aircraft (✕ takes one out).
/// Aircraft go in by right-clicking them on a map or in Objects.</summary>
public sealed class PackagesPanel : UserControl
{
    // The theme's brushes, read when the cards are built (rebuilt when the theme changes).
    private static Brush Back => Themes.ThemeBrush.Get("Dcb.Panel");
    private static Brush Card => Themes.ThemeBrush.Get("Dcb.PanelAlt");
    private static Brush Text => Themes.ThemeBrush.Get("Dcb.Text");
    private static Brush Dim => Themes.ThemeBrush.Get("Dcb.TextDim");

    private (Package Package, Button Jump, WrapPanel Members, TextBox Mhz, TextBox Name)[] _cards = [];

    public PackagesPanel()
    {
        Build();
        Loaded += (_, _) =>
        {
            AppState.Store.Changed += Refresh;
            AppState.Packages.Changed += Refresh;
            Themes.ThemeManager.Changed += Rebuild;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppState.Store.Changed -= Refresh;
            AppState.Packages.Changed -= Refresh;
            Themes.ThemeManager.Changed -= Rebuild;
        };
    }

    private void Build()
    {
        Background = Back;
        var stack = new StackPanel { Margin = new Thickness(6) };
        stack.Children.Add(new TextBlock
        {
            Text = "Right-click an own aircraft on a map or in Objects → Add to package. Packages use your SRS channels 7–10; JUMP tunes the channel to the package's frequency and transmits on it at once.",
            Foreground = Dim, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6),
        });
        _cards = AppState.Packages.All.Select(p => BuildCard(p, stack)).ToArray();
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Rebuild()
    {
        Build();
        Refresh();
    }

    private (Package, Button, WrapPanel, TextBox, TextBox) BuildCard(Package p, Panel parent)
    {
        var colour = PackageColors.Brush(p.Number);
        var card = new Border { Background = Card, BorderBrush = colour, BorderThickness = new Thickness(5, 1, 1, 1), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(6, 4, 6, 6) };
        var body = new StackPanel();
        card.Child = body;

        // Tag, name, frequency, channel.
        var head = new DockPanel { LastChildFill = true };
        var tag = new Border { Background = colour, CornerRadius = new CornerRadius(2), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        tag.Child = new TextBlock { Text = p.Tag, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, FontSize = 13 };
        DockPanel.SetDock(tag, Dock.Left);
        head.Children.Add(tag);

        var channel = new TextBlock { Text = $"CH{p.Radio}", Foreground = Text, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Your SRS channel kept for this package" };
        DockPanel.SetDock(channel, Dock.Right);
        head.Children.Add(channel);

        var mhz = new TextBox { Width = 64, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Frequency, MHz (e.g. 251.000)", VerticalContentAlignment = VerticalAlignment.Center };
        mhz.Text = p.Mhz is { } f ? f.ToString("0.000", CultureInfo.InvariantCulture) : "";
        mhz.LostFocus += (_, _) => SetMhz(p, mhz);
        mhz.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) SetMhz(p, mhz); };
        DockPanel.SetDock(mhz, Dock.Right);
        head.Children.Add(mhz);
        var mhzLabel = new TextBlock { Text = "MHz", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(mhzLabel, Dock.Right);
        head.Children.Add(mhzLabel);

        var name = new TextBox { Text = p.Name, FontWeight = FontWeights.SemiBold, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Package name" };
        name.LostFocus += (_, _) =>
        {
            var n = name.Text.Trim();
            if (n.Length == 0 || n == p.Name) return;
            p.Name = n;
            AppState.Packages.Edited();
        };
        head.Children.Add(name);
        body.Children.Add(head);

        // The jump button.
        var jump = new Button { Margin = new Thickness(0, 6, 0, 4), Padding = new Thickness(0, 4, 0, 4), FontSize = 14, FontWeight = FontWeights.Bold, Background = colour, Foreground = Brushes.Black };
        jump.Click += (_, _) =>
        {
            Srs.SrsChannels.Transmit(p.Radio);
        };
        body.Children.Add(jump);

        var members = new WrapPanel();
        body.Children.Add(members);
        parent.Children.Add(card);
        return (p, jump, members, mhz, name);
    }

    private static void SetMhz(Package p, TextBox box)
    {
        var text = box.Text.Trim().Replace(',', '.');
        double? mhz = text.Length == 0 ? null
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 1000 ? v : p.Mhz;
        box.Text = mhz is { } f ? f.ToString("0.000", CultureInfo.InvariantCulture) : "";
        if (mhz == p.Mhz) return;
        Srs.SrsChannels.Set(p.Radio, mhz);  // the package's frequency, and its channel tuned
    }

    private void Refresh()
    {
        using var perf = RenderStats.Measure("panel: packages");
        var store = AppState.Store;
        foreach (var (p, jump, members, mhzBox, nameBox) in _cards)
        {
            // Edited in another window (or by code): show it here unless being typed in.
            if (!mhzBox.IsKeyboardFocusWithin) mhzBox.Text = p.Mhz is { } m ? m.ToString("0.000", CultureInfo.InvariantCulture) : "";
            if (!nameBox.IsKeyboardFocusWithin) nameBox.Text = p.Name;
            jump.Content = p.Mhz is { } f ? $"▶ JUMP  ·  CH{p.Radio}  ·  {f:0.000}" : $"set a frequency for CH{p.Radio}";
            jump.IsEnabled = p.Mhz is not null;
            members.Children.Clear();
            foreach (var id in p.Members)
            {
                if (store.Find(id)?.Unit is not { } u) continue;
                var chip = new Border { Background = Back, BorderBrush = PackageColors.Brush(p.Number), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 2, 4, 2), Padding = new Thickness(5, 1, 2, 1) };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var type = u.Type is null ? "" : " " + Symbology.ShortType(u.Type);
                row.Children.Add(new TextBlock { Foreground = Text, VerticalAlignment = VerticalAlignment.Center, Text = $"{Symbology.NameOf(u) ?? $"#{id}"}{type}  {(u.AltFt ?? 0) / 100:000}" });
                var remove = new Button { Content = "✕", Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), FontSize = 10, ToolTip = "Take out of the package" };
                remove.Click += (_, _) => AppState.Packages.Remove(id);
                row.Children.Add(remove);
                chip.Child = row;
                members.Children.Add(chip);
            }
            if (members.Children.Count == 0)
                members.Children.Add(new TextBlock { Text = "No aircraft", Foreground = Dim, FontSize = 11 });
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
