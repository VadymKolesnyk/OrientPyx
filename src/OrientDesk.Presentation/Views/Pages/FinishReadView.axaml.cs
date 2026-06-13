using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.Controls;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class FinishReadView : UserControl
{
    private FinishReadViewModel? _vm;

    public FinishReadView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    // File picking needs the window's StorageProvider, so it lives in the view. The picker only fills
    // the watched path; the VM polls it on its own.
    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var path = await PickCsvAsync();
        if (path is not null)
            _vm.AutoReadFilePath = path;
    }

    private async Task<string?> PickCsvAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _vm is null)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("FinishRead.AutoRead.PickerTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV")
                {
                    Patterns = ["*.csv"],
                    MimeTypes = ["text/csv"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as FinishReadViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // Builds the table's columns. Every column is read-only (no editPath) — the finish log is not
    // edited by the user. Headers are baked into the band model, so a language switch rebuilds.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("FinishRead.Col.Id", nameof(FinishReadRowViewModel.Order), minWidth: 60)
            .Text("FinishRead.Col.Chip", nameof(FinishReadRowViewModel.ChipNumber), minWidth: 120)
            .Text("FinishRead.Col.FinishTime", nameof(FinishReadRowViewModel.FinishTimeText), minWidth: 110)
            .Text("FinishRead.Col.Number", nameof(FinishReadRowViewModel.ParticipantNumber), minWidth: 80)
            .Text("FinishRead.Col.FullName", nameof(FinishReadRowViewModel.FullName), minWidth: 220)
            .Text("FinishRead.Col.Group", nameof(FinishReadRowViewModel.GroupName), minWidth: 140)
            // Status: short code (OK/MP/OVT/DNF) with the MP detail as a tooltip.
            .Custom("FinishRead.Col.Status", BuildStatusCell, minWidth: 80,
                    sortPath: nameof(FinishReadRowViewModel.StatusText))
            .Bands;
    }

    private static Control BuildStatusCell()
    {
        var block = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0),
            FontWeight = Avalonia.Media.FontWeight.Medium,
            [!TextBlock.TextProperty] = new Binding(nameof(FinishReadRowViewModel.StatusText)),
            [!ToolTip.TipProperty] = new Binding(nameof(FinishReadRowViewModel.StatusDetail)),
        };
        return block;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
