using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

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

        try
        {
            await using (var stream = await file.OpenWriteAsync())
                await stream.WriteAsync(result.Bytes);

            // Open the freshly saved splits HTML in the OS default app (browser) so the user sees it
            // immediately — same behaviour as the result-protocol export.
            ProtocolFileLauncher.TryOpen(file);
        }
        catch
        {
            // The file couldn't be written (permissions, removed drive, etc.). The user can retry. Mirrors
            // the result-protocol export behaviour.
        }
    }
}
