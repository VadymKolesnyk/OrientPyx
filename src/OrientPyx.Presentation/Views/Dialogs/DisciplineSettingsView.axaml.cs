using Avalonia.Controls;
using Avalonia.Input;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class DisciplineSettingsView : UserControl
{
    public DisciplineSettingsView()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is DisciplineSettingsViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
