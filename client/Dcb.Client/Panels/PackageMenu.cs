using System.Windows.Controls;
using System.Windows.Shapes;
using Dcb.Client.Core;
using Dcb.Client.Map;

namespace Dcb.Client.Panels;

/// <summary>"Add to package ▸" and "Remove from package" for an own aircraft, on the map's and
/// the Objects list's right-click menus.</summary>
public static class PackageMenu
{
    /// <summary>Whether a unit can go into a package: an own aircraft or helicopter.</summary>
    public static bool Packable(Unit u) => !u.Detected && Symbology.IsAir(Symbology.KindOf(u));

    /// <summary>"Listen traffic" for an own player: the SRS tab shows that player's radios.</summary>
    public static MenuItem? ListenItem(Unit u, System.Windows.DependencyObject from)
    {
        if (u.Detected || u.Player is null) return null;
        var onSrs = Srs.SrsListen.Instance.ClientFor(u) is not null;
        var item = new MenuItem { Header = onSrs ? "Listen traffic" : "Listen traffic (not on SRS)" };
        item.Click += (_, _) =>
        {
            AppState.ListenTo(u.Id);
            MainWindow.Of(from)?.ShowSrs();
        };
        return item;
    }

    public static IEnumerable<object> Items(Unit u)
    {
        var book = AppState.Packages;
        var current = book.PackageOf(u.Id);
        var add = new MenuItem { Header = current is null ? "Add to package" : "Move to package" };
        foreach (var p in book.All)
        {
            if (p.Number == current?.Number) continue;
            var item = new MenuItem
            {
                Header = $"{p.Tag}  {p.Name}{(p.Mhz is { } f ? $"  ({f:0.000})" : "")}",
                Icon = new Rectangle { Width = 12, Height = 12, Fill = PackageColors.Brush(p.Number) },
            };
            var number = p.Number;
            item.Click += (_, _) => book.Add(number, u.Id);
            add.Items.Add(item);
        }
        yield return add;
        if (current is not null)
        {
            var remove = new MenuItem { Header = $"Remove from {current.Tag} {current.Name}" };
            remove.Click += (_, _) => book.Remove(u.Id);
            yield return remove;
        }
    }
}
