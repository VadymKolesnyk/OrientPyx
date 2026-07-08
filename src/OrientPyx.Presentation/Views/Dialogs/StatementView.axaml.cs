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

        try
        {
            await using (var stream = await file.OpenWriteAsync())
                await stream.WriteAsync(result.Bytes);

            // Open the freshly saved document in the OS default app (Word) so the user sees it immediately.
            ProtocolFileLauncher.TryOpen(file);
        }
        catch
        {
            // The file couldn't be written (permissions, removed drive, etc.). The user can retry.
        }
    }
}
