using Avalonia.Controls;
using Avalonia.Input;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.Views.Dialogs;

public partial class BulkAddChipsView : UserControl
{
    public BulkAddChipsView()
    {
        InitializeComponent();
    }

    // Escape cancels, matching the confirm dialog.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is BulkAddChipsViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
