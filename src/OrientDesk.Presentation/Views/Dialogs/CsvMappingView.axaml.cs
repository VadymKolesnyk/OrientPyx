using Avalonia.Controls;
using Avalonia.Input;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.Views.Dialogs;

public partial class CsvMappingView : UserControl
{
    public CsvMappingView()
    {
        InitializeComponent();
    }

    // Escape cancels, matching the other dialogs.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CsvMappingViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
