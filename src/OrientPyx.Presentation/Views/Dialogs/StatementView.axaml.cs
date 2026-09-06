using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Presentation.Views.Pages;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class StatementView : UserControl
{
    private ProtocolPreviewTable? _previewTable;

    public StatementView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Bind the shared preview table (header drag-reorder + aligned cells) to the VM and the host grid.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        _previewTable ??= this.FindControl<Grid>("PreviewTableHost") is { } host
            ? new ProtocolPreviewTable(host)
            : null;
        _previewTable?.Bind(DataContext as IProtocolPreviewHost);
    }

    // Export the statement to a .docx. The VM builds the bytes (and persists the settings); the save dialog runs
    // here (it needs the window's StorageProvider), mirroring ProtocolsView.OnGenerateClick.
    private async void OnExportWordClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StatementViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var result = await vm.GenerateWordAsync();
        if (result is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = vm.ExportWordLabel,
            SuggestedFileName = result.SuggestedFileName,
            DefaultExtension = "docx",
            FileTypeChoices =
            [
                new FilePickerFileType("Word")
                {
                    Patterns = ["*.docx"],
                    MimeTypes = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"]
                }
            ]
        });

        if (file is null)
            return;

        // The saver handles a target that is open in another program (Word keeps a .docx locked):
        // it then writes "name (2).ext" beside it and tells the user. Null means nothing was saved.
        var saved = await vm.FileSaver.SaveAsync(file, result.Bytes);
        if (saved is not null)
            ProtocolFileLauncher.TryOpen(saved);
    }
}
