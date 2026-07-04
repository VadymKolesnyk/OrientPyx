using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class ConfirmDialogView : UserControl
{
    public ConfirmDialogView()
    {
        InitializeComponent();

        // When the dialog appears, move focus inside it so Enter/Esc and Tab work immediately
        // without the user having to click first. Focus with the Tab navigation method so the
        // keyboard focus adorner (:focus-visible) is drawn — a plain Focus() would focus the button
        // but show no highlight. The Confirm button is IsDefault, Cancel IsCancel.
        AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() => ConfirmButton.Focus(NavigationMethod.Tab), DispatcherPriority.Input);
    }

    // Esc cancels regardless of which control inside the dialog holds focus. Handled in the tunnel
    // phase so it wins over any inner control, and works even if IsCancel routing doesn't fire.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ConfirmDialogViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
