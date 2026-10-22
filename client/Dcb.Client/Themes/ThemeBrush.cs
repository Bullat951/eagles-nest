using System.Windows;
using System.Windows.Media;

namespace Dcb.Client.Themes;

/// <summary>The current theme's brush for a key ("Dcb.Text", "Dcb.Friendly"…), for code that
/// builds or colours controls itself; such code redraws on <see cref="ThemeManager.Changed"/>.</summary>
public static class ThemeBrush
{
    public static Brush Get(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
