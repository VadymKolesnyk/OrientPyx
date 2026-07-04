using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class AddRegionView : UserControl
{
    public AddRegionView()
    {
        InitializeComponent();
    }

    // Focus the name field when shown so the user can type immediately.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus();
    }

    // Enter confirms, Escape cancels (matching the other dialogs).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is AddRegionViewModel vm)
        {
            if (e.Key == Key.Enter)
            {
                vm.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        }

        base.OnKeyDown(e);
    }
}
