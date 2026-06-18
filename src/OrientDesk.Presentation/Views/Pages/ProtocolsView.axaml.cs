using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class ProtocolsView : UserControl
{
    public ProtocolsView()
    {
        InitializeComponent();
    }

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

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(result.Bytes);
        }
        catch
        {
            // The file couldn't be written (permissions, removed drive, etc.). The user can retry; a future
            // toast could surface it. Mirrors the participants export behaviour.
        }
    }
}
