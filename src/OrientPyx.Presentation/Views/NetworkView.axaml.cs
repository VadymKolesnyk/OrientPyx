using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels;

namespace OrientPyx.Presentation.Views;

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        // The host / station names are saved when their box loses focus (no explicit save button).
        HostNameBox.LostFocus += OnNameLostFocus;
        StationNameBox.LostFocus += OnNameLostFocus;
        // The PIN box takes digits only (the VM drops anything else too; this keeps the caret steady).
        PinBox.AddHandler(TextInputEvent, OnPinTextInput, RoutingStrategies.Tunnel);
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NetworkViewModel vm)
            vm.SaveNamesCommand.Execute(null);
    }

    private static void OnPinTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { } text && !text.All(char.IsDigit))
            e.Handled = true;
    }
}
