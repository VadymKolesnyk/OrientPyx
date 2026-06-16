using Avalonia.Controls;
using Avalonia.Input;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.Views.Dialogs;

public partial class BulkEditView : UserControl
{
    public BulkEditView()
    {
        InitializeComponent();
    }

    // Escape cancels, matching the other dialogs.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is BulkEditViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
