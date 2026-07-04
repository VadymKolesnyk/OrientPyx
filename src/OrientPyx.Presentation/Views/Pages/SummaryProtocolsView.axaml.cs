using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class SummaryProtocolsView : UserControl
{
    private SummaryPreviewTable? _previewTable;
    private SummaryProtocolsViewModel? _vm;

    public SummaryProtocolsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        _previewTable ??= this.FindControl<Grid>("PreviewTableHost") is { } host
            ? new SummaryPreviewTable(host)
            : null;

        if (_vm is { } old)
            old.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as SummaryProtocolsViewModel;
        if (_vm is { } vm)
            vm.PropertyChanged += OnVmPropertyChanged;

        _previewTable?.SetHost(_vm);
        _previewTable?.Render(_vm?.Preview);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummaryProtocolsViewModel.Preview))
            _previewTable?.Render(_vm?.Preview);
    }

    // Build the protocol (the VM owns the build + settings persistence), then run the save dialog and write the
    // .docx bytes. Mirrors ProtocolsView.
    private async void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SummaryProtocolsViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var result = await vm.GenerateAsync();
        if (result is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = vm.Localization.Get("SummaryProtocol.Generate"),
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
            ProtocolFileLauncher.TryOpen(file);
        }
        catch
        {
            // Best-effort; the user can retry.
        }
    }
}
