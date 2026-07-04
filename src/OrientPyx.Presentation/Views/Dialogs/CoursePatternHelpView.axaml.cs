using Avalonia.Controls;
using Avalonia.Input;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class CoursePatternHelpView : UserControl
{
    public CoursePatternHelpView() => InitializeComponent();

    // Esc closes the help regardless of which control inside holds focus (handled here so it wins over
    // any inner control and works even if IsCancel routing doesn't fire).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CoursePatternHelpViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
