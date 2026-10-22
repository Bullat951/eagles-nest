using System.Windows;
using System.Windows.Controls;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;

namespace Dcb.Client;

/// <summary>Where DCS World is: the installs the search found (best first, preselected), a folder
/// of the controller's choosing, or none. Shown on the first start and from Window → Map data.</summary>
public sealed class DcsLocationWindow : Window
{
    private readonly ListBox _list = new() { MinHeight = 90, Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _empty = new() { Text = "No DCS World install found on this PC.", Margin = new Thickness(0, 6, 0, 8), Visibility = Visibility.Collapsed };
    private readonly Button _use = new() { Content = "Use this folder", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };

    private DcsLocationWindow(bool firstStart)
    {
        Title = "DCS World location";
        SizeToContent = SizeToContent.Height;
        Width = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = (firstStart ? "Welcome to Eagle's Nest. " : "") +
                   "The DCS Map, DCS Charts and DCS Satellite base maps and the airfield details (taxiway letters, " +
                   "beacons, tower frequencies) are read from your own DCS World install. Nothing there is changed. " +
                   "Without DCS everything else works: the Scope and Satellite maps, tracks, ATC, SRS.",
        };
        var browse = new Button { Content = "Browse…", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var search = new Button { Content = "Search again", MinWidth = 90 };
        var none = new Button { Content = "Continue without DCS", MinWidth = 140 };
        browse.Click += (_, _) => Browse();
        search.Click += (_, _) => Fill(DcsInstall.Search(again: true), Selected?.Path);
        _use.Click += (_, _) => Choose(Selected?.Path);
        none.Click += (_, _) => Choose("");
        _list.SelectionChanged += (_, _) => _use.IsEnabled = Selected is not null;
        _list.MouseDoubleClick += (_, _) => { if (Selected is not null) Choose(Selected.Path); };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(browse);
        left.Children.Add(search);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_use);
        right.Children.Add(none);
        var buttons = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(left, Dock.Left);
        buttons.Children.Add(left);
        buttons.Children.Add(right);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(intro);
        panel.Children.Add(new TextBlock { Text = "DCS World installs:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(_list);
        panel.Children.Add(_empty);
        panel.Children.Add(buttons);
        Content = panel;

        var current = AppState.Settings.DcsPath is { Length: > 0 } p ? p : null;
        Fill(DcsInstall.Search(), current);
    }

    private DcsCandidate? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as DcsCandidate;

    private void Fill(IReadOnlyList<DcsCandidate> found, string? select)
    {
        var all = found.ToList();
        // A folder chosen earlier stays in the list even when the search does not find it.
        if (select is not null && all.All(c => !string.Equals(c.Path, select, StringComparison.OrdinalIgnoreCase)) && DcsLocator.IsDcsFolder(select))
            all.Insert(0, new DcsCandidate(select, "chosen earlier", DcsLocator.Terrains(select)));
        _list.Items.Clear();
        foreach (var c in all)
        {
            var text = new TextBlock();
            text.Inlines.Add(new System.Windows.Documents.Run(c.Path) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.LineBreak());
            text.Inlines.Add(new System.Windows.Documents.Run($"{c.Source} · {(c.Terrains.Count > 0 ? string.Join(", ", c.Terrains) : "no terrains")}") { FontSize = 11 });
            _list.Items.Add(new ListBoxItem { Content = text, Tag = c, Padding = new Thickness(4, 3, 4, 3) });
        }
        _list.SelectedIndex = Math.Max(0, all.FindIndex(c => string.Equals(c.Path, select, StringComparison.OrdinalIgnoreCase)));
        var any = all.Count > 0;
        _list.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _use.IsEnabled = Selected is not null;
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "DCS World folder (the one with bin and Mods)" };
        if (dialog.ShowDialog(this) != true) return;
        if (!DcsLocator.IsDcsFolder(dialog.FolderName))
        {
            MessageBox.Show(this, "That folder is not a DCS World install: it needs bin\\DCS.exe (or bin-mt) and Mods\\terrains.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var found = DcsInstall.Search().ToList();
        if (found.All(c => !string.Equals(c.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
            found.Insert(0, new DcsCandidate(dialog.FolderName, "chosen", DcsLocator.Terrains(dialog.FolderName)));
        Fill(found, dialog.FolderName);
    }

    private void Choose(string? path)
    {
        if (path is null) return;
        var before = DcsInstall.Find();
        AppState.Settings.DcsPath = path;
        AppState.Settings.DcsPathConfirmed = true;
        AppState.Settings.Save();
        DialogResult = true;
        var after = DcsInstall.Find();
        // Map data already read from the old folder stays until restart.
        if (_offerRestart && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show(Owner, "Restart Eagle's Nest now to read the maps from the new location?", Title,
                   MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Restart();
    }

    private bool _offerRestart;

    private static void Restart()
    {
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        Application.Current.Shutdown();
    }

    /// <summary>First start: asks once. Closed with the window's X, the search result is used and
    /// the question comes again next start. A restart is offered when a map already read DCS data
    /// from another folder than the one chosen.</summary>
    public static void AskOnFirstStart(Window owner)
    {
        if (AppState.Settings.DcsPathConfirmed) return;
        new DcsLocationWindow(firstStart: true) { Owner = owner, _offerRestart = true }.ShowDialog();
    }

    /// <summary>Snapshot aid: the first-start dialog, not modal, so the capture can see it.</summary>
    public static Window ShowForSnapshot(Window owner)
    {
        var w = new DcsLocationWindow(firstStart: true) { Owner = owner };
        w.Show();
        return w;
    }

    /// <summary>Window → Map data → DCS World folder….</summary>
    public static void Change(Window owner) =>
        new DcsLocationWindow(firstStart: false) { Owner = owner, _offerRestart = true }.ShowDialog();
}
