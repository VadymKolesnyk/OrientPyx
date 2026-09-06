using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class ProtocolsView : UserControl
{
    private ProtocolPreviewTable? _previewTable;

    public ProtocolsView()
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

    // ── Generate (.docx) ─────────────────────────────────────────────────────────────────────────────────

    // Build the protocol (the VM owns the build + settings persistence), then run the save dialog (it needs
    // the window's StorageProvider) and write the .docx bytes. Mirrors the participants export code-behind.
    private async void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProtocolsViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var result = await vm.GenerateAsync();
        if (result is null)
            return; // nothing to export (no competition / no day)

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = vm.Localization.Get("Protocols.Generate"),
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
            return; // save cancelled

        // The saver handles a target that is open in another program (Word keeps a .docx locked):
        // it then writes "name (2).ext" beside it and tells the user. Null means nothing was saved.
        var saved = await vm.FileSaver.SaveAsync(file, result.Bytes);
        if (saved is not null)
            ProtocolFileLauncher.TryOpen(saved);
    }
}
