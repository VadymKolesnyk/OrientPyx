using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class CoursePatternCheckView : UserControl
{
    public CoursePatternCheckView() => InitializeComponent();

    // The order box is the only thing to do here — focus it as soon as the modal appears.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        OrderBox.Focus();
    }

    // Esc closes the modal regardless of which control inside holds focus (handled here so it wins over
    // the TextBox, which would otherwise swallow the key).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CoursePatternCheckViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
