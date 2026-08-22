using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class CoursePatternVariantsView : UserControl
{
    public CoursePatternVariantsView() => InitializeComponent();

    // Puts every listed order on the clipboard, one per line. Clipboard access needs the TopLevel, so it
    // lives here rather than in the view model (same as the other copy buttons in the app).
    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CoursePatternVariantsViewModel vm)
            return;

        var text = vm.ClipboardText;
        if (string.IsNullOrEmpty(text))
            return;

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
    }

    // Esc closes the modal regardless of which control inside holds focus.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CoursePatternVariantsViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
