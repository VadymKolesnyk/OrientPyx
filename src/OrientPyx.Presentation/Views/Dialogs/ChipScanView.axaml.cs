using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class ChipScanView : UserControl
{
    private ChipScanViewModel? _vm;

    public ChipScanView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ChipScanViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    // Enter in the manual box scans the typed number (the box is cleared by the command).
    private void OnManualKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null)
        {
            _vm.SubmitManualCommand.Execute(null);
            e.Handled = true;
        }
    }

    // File picking needs the window's StorageProvider, so it lives in the view; the VM restarts the watch.
    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var path = await PickCsvAsync();
        if (path is not null)
            _vm.FilePath = path;
    }

    private async Task<string?> PickCsvAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _vm is null)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("Chips.Import.PickerTitle"),
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
        _vm = DataContext as ChipScanViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // Who, what kind of chip they need (editable dropdown), the chip scanned for them and its outcome, plus a
    // trailing action that takes the chip back. Headers are baked into the band model, so a language switch
    // is handled by rebuilding.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var builder = new SheetColumnBuilder(_vm.Localization)
            .Text("Participants.Col.Number", nameof(ChipScanRowViewModel.Number), width: 90)
            .Text("Participants.Col.FullName", nameof(ChipScanRowViewModel.FullName), minWidth: 220)
            .Text("Participants.Col.Group", nameof(ChipScanRowViewModel.Group), width: 100)
            // The type is a dropdown of the dialog's rules, so it can be changed on the spot.
            .Combo("Participants.ChipScan.Col.Need",
                   itemsPath: nameof(ChipScanRowViewModel.Rules),
                   selectedPath: nameof(ChipScanRowViewModel.SelectedRule),
                   labelPath: nameof(ChipScanRuleViewModel.Caption),
                   width: 190)
            .Text("Participants.Col.Chip", nameof(ChipScanRowViewModel.Chip), width: 120);
        // The SportIdent card model is only meaningful when the competition is punched with SportIdent.
        if (_vm.ShowCardColumn)
            builder.Text("Participants.ChipScan.Col.Card", nameof(ChipScanRowViewModel.CardText), width: 150);
        Sheet.Bands = builder
            .Text("Participants.ChipScan.Col.Status", nameof(ChipScanRowViewModel.Status), minWidth: 200)
            .DeleteAction(OnClearChip, "Participants.ChipScan.ClearRow")
            .Bands;
    }

    private void OnClearChip(object row) => _vm?.ClearRow(row);

    private void Unsubscribe()
    {
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
