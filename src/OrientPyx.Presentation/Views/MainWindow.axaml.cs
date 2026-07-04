using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OrientPyx.Presentation.Views;

public partial class MainWindow : Window
{
    // The element that had keyboard focus when Alt went down, captured so we can put focus back if the
    // menu grabs it during a Shift+Alt keyboard-layout switch. Cleared once the Alt chord ends.
    private IInputElement? _focusBeforeAlt;
    // True while an Alt press that also involved Shift is in progress — the exact Windows layout-switch
    // chord (Alt+Shift / Shift+Alt). When the menu opens during such a chord we bounce it back closed.
    private bool _altShiftChord;
    private Menu? _topMenu;

    public MainWindow()
    {
        InitializeComponent();

        // Windows switches keyboard layout with Shift+Alt (or Alt+Shift). Avalonia's Menu treats a lone
        // Alt press/release as "enter menu-bar navigation" and, on Alt-up, opens the menu and pulls
        // keyboard focus into it — so switching layout while typing in any input drops focus out of that
        // input. We can't stop the framework's access-key handler by marking the event handled (it doesn't
        // check Handled), so instead we detect the Alt+Shift chord and, if the menu opens as a result,
        // close it again and restore focus to where it was. Lone-Alt menu access is left working.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        _topMenu = this.FindControl<Menu>("TopMenu");
        if (_topMenu is not null)
            _topMenu.Opened += OnTopMenuOpened;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var isAlt = e.Key is Key.LeftAlt or Key.RightAlt;
        var shiftHeld = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var altHeld = (e.KeyModifiers & KeyModifiers.Alt) != 0;

        if (isAlt)
        {
            // Remember where focus is now (before any menu activation) and whether Shift is already held —
            // that's the Shift-then-Alt order of the layout switch.
            _focusBeforeAlt = FocusManager?.GetFocusedElement();
            if (shiftHeld)
                _altShiftChord = true;
        }
        else if (altHeld && e.Key is Key.LeftShift or Key.RightShift)
        {
            // Alt-then-Shift order: Shift pressed while Alt is held. (This order already suppresses the
            // menu in the framework, but flag it too so the restore path is symmetric.)
            _altShiftChord = true;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        // The Alt release is what opens the menu; the menu's Opened event fires synchronously during this
        // release, and it consults our chord flag. Clear the tracking only after this event has fully
        // dispatched (deferred) so it can't be reset before the framework's own KeyUp handler opens the
        // menu — regardless of the order our two tunnel handlers run in.
        if (e.Key is Key.LeftAlt or Key.RightAlt)
            Dispatcher.UIThread.Post(() =>
            {
                _altShiftChord = false;
                _focusBeforeAlt = null;
            });
    }

    private void OnTopMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (!_altShiftChord || _topMenu is null)
            return;

        // The menu opened purely because of a keyboard-layout switch, not a deliberate Alt tap. Close it
        // and hand focus back to the input the user was in. Post the restore so it runs after the menu has
        // finished taking focus during this event.
        var restore = _focusBeforeAlt;
        _topMenu.Close();
        Dispatcher.UIThread.Post(() => restore?.Focus());
    }
}
