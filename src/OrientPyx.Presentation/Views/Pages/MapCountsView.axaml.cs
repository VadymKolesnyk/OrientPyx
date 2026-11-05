using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class MapCountsView : UserControl
{
    private MapCountsViewModel? _vm;

    // Amber over the «(без групи)» row's name cell: those runners need a map nobody can print yet.
    private static readonly ISolidColorBrush NoGroupBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B));

    public MapCountsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as MapCountsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // The column set follows the day list and the reserve toggle; headers are baked in, so rebuild.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapCountsViewModel.DayNumbers) or nameof(MapCountsViewModel.ShowReserve))
            BuildBands();
    }

    // Group name, then per day the headcount (and, with a reserve set, the headcount plus reserve beside it).
    // Every numeric column is summed in the status bar, so the per-day total — the figure the printer actually
    // needs — is always on screen. No row total: maps are ordered per day, a sum over days means nothing.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var builder = new SheetColumnBuilder(loc)
            .Text("MapCounts.Col.Group", nameof(MapCountRowViewModel.GroupName), minWidth: 180)
            .CellTint(nameof(MapCountRowViewModel.IsNoGroup), NoGroupBrush,
                      tooltipPath: nameof(MapCountRowViewModel.NoGroupTooltip));

        var days = _vm.DayNumbers;
        var showReserve = _vm.ShowReserve;
        for (var i = 0; i < days.Count; i++)
        {
            var counts = $"{nameof(MapCountRowViewModel.Counts)}[{i}]";
            builder.Text("MapCounts.Col.Day", counts, width: 110)
                   .HeaderText(loc.Get("MapCounts.Col.Day").Replace("{0}", days[i].ToString()))
                   .Summary(counts);
            if (showReserve)
            {
                var reserve = $"{nameof(MapCountRowViewModel.WithReserve)}[{i}]";
                builder.Text("MapCounts.Col.DayReserve", reserve, width: 140)
                       .HeaderText(loc.Get("MapCounts.Col.DayReserve").Replace("{0}", days[i].ToString()))
                       .Summary(reserve);
            }
        }

        Sheet.Bands = builder.Bands;
    }

    // Export the table as shown (visible columns, displayed rows): the VM shows the format modal and
    // serialises; the save dialog needs the window's StorageProvider, so it runs here. Mirrors ParticipantsView.
    private void OnExportClick(object? sender, RoutedEventArgs e) => _ = ExportAsync();

    private async Task ExportAsync()
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var result = await _vm.ExportAsync(Sheet.ExportView());
        if (result is null)
            return; // nothing to export or cancelled

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _vm.Localization.Get("MapCounts.Export"),
            SuggestedFileName = result.SuggestedFileName,
            DefaultExtension = result.Extension,
            FileTypeChoices =
            [
                new FilePickerFileType(result.Extension.ToUpperInvariant())
                {
                    Patterns = [$"*.{result.Extension}"],
                    MimeTypes = [result.MimeType]
                }
            ]
        });

        if (file is null)
            return; // save cancelled

        // The saver handles a locked target (the workbook open in Excel) by writing "name (2).ext" beside it.
        await _vm.FileSaver.SaveAsync(file, result.Bytes);
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = null;
    }
}
