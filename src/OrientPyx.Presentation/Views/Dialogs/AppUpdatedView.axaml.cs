using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class AppUpdatedView : UserControl
{
    public AppUpdatedView() => InitializeComponent();

    // Esc closes the dialog regardless of which control inside holds focus (mirrors ScreenHelpView).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is AppUpdatedViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // «Що нового в цій версії»: the version's section of the changelog on the documentation site, in the default
    // browser. The dialog stays open.
    private void OnChangelogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppUpdatedViewModel vm && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(vm.ChangelogUri);
    }
}
