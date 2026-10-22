using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AvalonDock.Themes;
using Microsoft.Win32;

namespace Dcb.Client.Themes;

public enum ThemeMode { System, Dark, Light }

/// <summary>The tactical console look in dark or light (Window → Theme): swaps the colour set
/// the styles read (Themes/Colors.*.xaml), gives every window the theme's background and a dark
/// or light title bar, and switches the docking chrome. "System" follows Windows' app mode,
/// also when it changes while DCB runs.</summary>
public static class ThemeManager
{
    private static readonly Uri DarkColors = new("pack://application:,,,/Themes/Colors.Dark.xaml");
    private static readonly Uri LightColors = new("pack://application:,,,/Themes/Colors.Light.xaml");

    public static bool IsDark { get; private set; } = true;
    public static ThemeMode Mode => Enum.TryParse<ThemeMode>(AppState.Settings.Theme, out var m) ? m : ThemeMode.System;

    /// <summary>Raised after the colours changed (views that paint themselves redraw).</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Attach((Window)s)));
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
        Apply();
    }

    public static void Set(ThemeMode mode)
    {
        AppState.Settings.Theme = mode.ToString();
        Apply();
    }

    private static void Apply()
    {
        IsDark = Mode switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => !WindowsUsesLightApps() };
        // Replace the colour set in place: among merged dictionaries the last one wins, so an
        // extra set added in front would be overridden by the old one.
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var colors = new ResourceDictionary { Source = IsDark ? DarkColors : LightColors };
        var at = dictionaries.ToList().FindIndex(d => d.Source?.OriginalString.Contains("Colors.") == true);
        if (at >= 0) dictionaries[at] = colors;
        else dictionaries.Insert(0, colors);
        foreach (Window w in Application.Current.Windows) Attach(w);
        Changed?.Invoke();
    }

    /// <summary>The theme's background, text colour, title bar and (workspace windows) docking theme.</summary>
    public static void Attach(Window w)
    {
        w.SetResourceReference(Control.BackgroundProperty, "Dcb.Bg");
        w.SetResourceReference(Control.ForegroundProperty, "Dcb.Text");
        if (w is MainWindow main) main.SetDockTheme(IsDark ? new Vs2013DarkTheme() : new Vs2013LightTheme());
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var on = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        }
    }

    private static bool WindowsUsesLightApps() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int v || v != 0;

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
