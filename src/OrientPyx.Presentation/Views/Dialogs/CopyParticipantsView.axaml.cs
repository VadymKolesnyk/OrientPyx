using Avalonia.Controls;
using Avalonia.Input;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class CopyParticipantsView : UserControl
{
    public CopyParticipantsView()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CopyParticipantsViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
