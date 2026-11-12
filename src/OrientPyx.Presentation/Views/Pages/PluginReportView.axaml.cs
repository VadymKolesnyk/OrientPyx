using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class PluginReportView : UserControl
{
    private ProtocolPreviewTable? _previewTable;
    private PluginReportViewModel? _vm;

    public PluginReportView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(IsAttached ? DataContext as PluginReportViewModel : null);
    }

    private bool IsAttached => VisualRoot is not null;

    // The VM is a singleton that outlives this view: it is listened to only while the view is on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as PluginReportViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Attach(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach(PluginReportViewModel? vm)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        _previewTable ??= this.FindControl<Grid>("PreviewTableHost") is { } host ? new ProtocolPreviewTable(host) : null;
        _previewTable?.Bind(vm);
    }

    // Each build hands over a new preview: re-bind the table to it (one rebuild per document).
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginReportViewModel.Preview))
            _previewTable?.Bind(_vm);
    }

    // Export the report to a .docx: the VM builds the bytes, the save dialog runs here (it needs the window's
    // StorageProvider), as on the protocol pages.
    private async void OnExportWordClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginReportViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel)
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

        // A target open in Word is written beside it as "name (2).docx"; null means nothing was saved.
        if (await vm.FileSaver.SaveAsync(file, result.Bytes) is { } saved)
            ProtocolFileLauncher.TryOpen(saved);
    }
}
