using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // Copies the service-role key to the clipboard.
    private void OnCopyServiceKeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || string.IsNullOrEmpty(vm.OnlineServiceRoleKey))
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        _ = clipboard.SetTextAsync(vm.OnlineServiceRoleKey);
    }
}
