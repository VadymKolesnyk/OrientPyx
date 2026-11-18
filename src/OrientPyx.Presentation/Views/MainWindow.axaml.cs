using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels;

namespace OrientPyx.Presentation.Views;

public partial class MainWindow : Window, IArchiveFilePicker
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

        // The competition export/import flow needs this window's StorageProvider for its file dialogs, so
        // the window plays the picker. Hand it to the flow once the DataContext (the root VM) is set.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ArchiveFlow.Picker = this;
                vm.PluginMenu.CollectionChanged += (_, _) => BuildPluginMenu(vm);
                BuildPluginMenu(vm);
            }
        };

        // Closing the window (title-bar ×) stops the network host / leaves the host first, best-effort and bounded
        // (~2 s): cancel this close, shut the network down, then close again — the flag lets the second one through.
        Closing += async (_, e) =>
        {
            if (DataContext is not MainWindowViewModel vm || vm.NetworkShutdownDone)
                return;
            e.Cancel = true;
            await vm.ShutdownNetworkAsync();
            Close();
        };
    }

    // The «Плагіни» menu mixes fixed items (declared in XAML) with one item per switched-on plugin, which a bound
    // ItemsSource cannot do — so the plugin items are kept after the separator here.
    private void BuildPluginMenu(MainWindowViewModel vm)
    {
        if (this.FindControl<MenuItem>("PluginsMenu") is not { } menu
            || this.FindControl<Separator>("PluginsMenuSeparator") is not { } separator)
            return;

        var keep = menu.Items.IndexOf(separator) + 1;
        while (menu.Items.Count > keep)
            menu.Items.RemoveAt(keep);
        foreach (var entry in vm.PluginMenu)
        {
            // A plugin with reports gets a submenu: its settings, then a separator and one item per report.
            if (entry.Reports.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = entry.Name,
                    Command = vm.OpenPluginSettingsCommand,
                    CommandParameter = entry.Id,
                });
                continue;
            }

            var sub = new MenuItem { Header = entry.Name };
            sub.Items.Add(new MenuItem
            {
                Header = vm.Localization.Get("Menu.Plugins.Settings"),
                Command = vm.OpenPluginSettingsCommand,
                CommandParameter = entry.Id,
            });
            sub.Items.Add(new Separator());
            foreach (var report in entry.Reports)
                sub.Items.Add(new MenuItem
                {
                    Header = report.Name,
                    Command = vm.OpenPluginReportCommand,
                    CommandParameter = report.Id,
                });
            menu.Items.Add(sub);
        }
        separator.IsVisible = vm.PluginMenu.Count > 0;
    }

    private static readonly FilePickerFileType ArchiveFileType = new("OrientPyx")
    {
        Patterns = [$"*.{EventArchiveConstants.Extension}"],
    };

    public async Task<string?> PickSaveArchiveAsync(string suggestedFileName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = EventArchiveConstants.Extension,
            FileTypeChoices = [ArchiveFileType],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenArchiveAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [ArchiveFileType],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryUndoKey(e))
            return;

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

    // Ctrl+Z (undo), Ctrl+Y / Ctrl+Shift+Z (redo) — Cmd too on macOS — for the shown page. Inside a text box the box's
    // own undo works as before (its typing is part of the step anyway); a live combo or date editor is no obstacle —
    // the undo ends its edit first. Physical Z / Y as well, so a Cyrillic layout behaves the same.
    private bool TryUndoKey(KeyEventArgs e)
    {
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (!command || (e.KeyModifiers & KeyModifiers.Alt) != 0)
            return false;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var isZ = e.Key == Key.Z || e.PhysicalKey == PhysicalKey.Z;
        var isY = e.Key == Key.Y || e.PhysicalKey == PhysicalKey.Y;
        if (!isZ && !(isY && !shift))
            return false;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return false;
        if (DataContext is not MainWindowViewModel vm || !vm.IsEventSelected)
            return false;
        if (!vm.EditMenu.HandleKey(redo: isY || shift))
            return false;
        e.Handled = true;
        return true;
    }

    // The «Правка» menu re-reads its labels as it opens (a page's day switch changes which history is shown).
    private void OnEditMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.EditMenu.Refresh();
    }

    // «Довідка → Документація»: the documentation site in the default browser.
    private void OnDocumentationClick(object? sender, RoutedEventArgs e)
    {
        if (Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(DocsLinks.Home);
    }
}
