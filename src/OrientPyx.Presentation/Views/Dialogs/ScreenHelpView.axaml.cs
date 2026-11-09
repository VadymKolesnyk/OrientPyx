using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class ScreenHelpView : UserControl
{
    public ScreenHelpView() => InitializeComponent();

    // Esc closes the help regardless of which control inside holds focus (mirrors CoursePatternHelpView).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ScreenHelpViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // «Детальніше в документації»: this screen's page on the documentation site, in the default browser.
    private void OnDocsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScreenHelpViewModel vm && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(vm.DocsUri);
    }
}
