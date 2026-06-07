using Avalonia.Controls;
using Avalonia.Input;
using OrientDesk.Presentation.ViewModels;

namespace OrientDesk.Presentation.Views;

public partial class EventSelectionView : UserControl
{
    public EventSelectionView()
    {
        InitializeComponent();
    }

    // Double-clicking a row opens that competition.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is EventSelectionViewModel vm && vm.OpenCommand.CanExecute(null))
            vm.OpenCommand.Execute(null);
    }
}
