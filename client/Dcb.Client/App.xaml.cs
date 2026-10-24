using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Dcb.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Themes.ThemeManager.Initialize();
        // The bullseye key works in every window: the main one and floated map windows alike.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKey));
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnKey));
        Deactivated += (_, _) => AppState.SetBullsKeyHeld(false);  // key released while elsewhere
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        // Alt released after an Alt+click on a map: not a menu request.
        if (!e.IsDown && e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt && AppState.AltClicked)
        {
            AppState.AltClicked = false;
            e.Handled = true;
            return;
        }
        // The RWR key opens the clock on the map under the pointer (MapPanel decides on which aircraft).
        if (e.IsDown && !e.IsRepeat && Enum.TryParse<Key>(AppState.Settings.RwrKey, ignoreCase: true, out var rwr) && e.Key == rwr
            && Keyboard.FocusedElement is not (TextBoxBase or PasswordBox))
        {
            if (AppState.RequestRwrClock()) e.Handled = true;
            return;
        }
        // The NCTR key: drag an area on the map under the pointer.
        if (e.IsDown && !e.IsRepeat && Enum.TryParse<Key>(AppState.Settings.NctrKey, ignoreCase: true, out var nctr) && e.Key == nctr
            && Keyboard.FocusedElement is not (TextBoxBase or PasswordBox))
        {
            if (AppState.RequestNctr()) e.Handled = true;
            return;
        }
        if (!Enum.TryParse<Key>(AppState.Settings.BullseyeKey, ignoreCase: true, out var key) || e.Key != key) return;
        // Typing a "b" into a text box is not a bullseye request.
        if (e.IsDown && Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        AppState.SetBullsKeyHeld(e.IsDown);
        e.Handled = true;
    }
}
