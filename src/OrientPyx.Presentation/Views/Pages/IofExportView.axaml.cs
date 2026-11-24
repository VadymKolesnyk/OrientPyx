using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class IofExportView : UserControl
{
    public IofExportView()
    {
        InitializeComponent();
    }

    // Build the IOF XML (the VM owns the build), run the save dialog (it needs the window's StorageProvider) and write
    // the bytes. The file is meant for another program, so it is not opened: a toast offers to show it in its folder.
    private async void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IofExportViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var result = await vm.GenerateAsync();
        if (result is null)
            return; // nothing to export

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = vm.Localization.Get("IofExport.Generate"),
            SuggestedFileName = result.SuggestedFileName,
            DefaultExtension = "xml",
            FileTypeChoices =
            [
                new FilePickerFileType("IOF XML")
                {
                    Patterns = ["*.xml"],
                    MimeTypes = ["application/xml"]
                }
            ]
        });

        if (file is null)
            return; // save cancelled

        var saved = await vm.FileSaver.SaveAsync(file, result.Bytes);
        if (saved is null)
            return;
        vm.Toasts.Show(
            vm.Localization.Get("IofExport.Saved.Title"),
            string.Format(vm.Localization.Get("IofExport.Saved.Message"), System.IO.Path.GetFileName(saved)),
            onClick: () => FileReveal.TryReveal(saved));
    }
}
