using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class SplitsExportView : UserControl
{
    public SplitsExportView()
    {
        InitializeComponent();
    }

    // Build the split HTML (the VM owns the build), then run the save dialog (it needs the window's
    // StorageProvider) and write the bytes. Mirrors the result-protocol export code-behind.
    private async void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SplitsExportViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var result = await vm.GenerateAsync();
        if (result is null)
            return; // nothing to export (no competition / no day)

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = vm.Localization.Get("Splits.Generate"),
            SuggestedFileName = result.SuggestedFileName,
            DefaultExtension = "html",
            FileTypeChoices =
            [
                new FilePickerFileType("HTML")
                {
                    Patterns = ["*.html", "*.htm"],
                    MimeTypes = ["text/html"]
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
