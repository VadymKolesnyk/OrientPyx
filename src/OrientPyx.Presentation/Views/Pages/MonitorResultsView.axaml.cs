using System.ComponentModel;
using Avalonia.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class MonitorResultsView : UserControl
{
    private MonitorPreviewTable? _previewTable;
    private MonitorResultsViewModel? _vm;

    public MonitorResultsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // The preview table renders the SELECTED FILE's monitor-screen mock-up (the same look as the generated
    // HTML) with drag-reorderable column headers. We re-bind it to the file currently selected on the page.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MonitorResultsViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        BindPreview();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorResultsViewModel.SelectedFile))
            BindPreview();
    }

    private void BindPreview()
    {
        _previewTable ??= this.FindControl<Grid>("PreviewTableHost") is { } host
            ? new MonitorPreviewTable(host)
            : null;
        _previewTable?.Bind(_vm?.SelectedFile);
    }
}
