using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class FileLockedView : UserControl
{
    public FileLockedView()
    {
        InitializeComponent();
    }

    // Focus the name field when shown and preselect just the leading type part with its number
    // ("Протокол результатів (2)") — that is the half worth retyping. The competition/day/date tail and the
    // extension are left alone, so a stray keystroke can't wipe them.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        FileNameBox.Focus();

        var text = FileNameBox.Text ?? string.Empty;
        var cut = text.IndexOf(Services.ExportFileName.Separator, StringComparison.Ordinal);
        if (cut < 0)
        {
            // A name the user typed themselves in the save dialog: select all but the extension.
            var dot = text.LastIndexOf('.');
            cut = dot > 0 ? dot : text.Length;
        }

        FileNameBox.SelectionStart = 0;
        FileNameBox.SelectionEnd = cut;
    }

    // Enter confirms, Escape cancels (matching the other dialogs).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is FileLockedViewModel vm)
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
