using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class ChipsView : UserControl
{
    private ChipsViewModel? _vm;

    // Semi-transparent red painted over a chip-number cell that duplicates another chip (numbers must
    // be unique); the collision is never saved, so this tint is the signal the number can't be used.
    private static readonly ISolidColorBrush DuplicateBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xDC, 0x26, 0x26));

    public ChipsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press, so Ctrl+Click on
        // Delete can skip the confirmation prompt (see ControlPointsView for the same approach).
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    // The table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not RentalChipRowViewModel row)
            return;
        DeleteRow(row, e.SkipConfirm);
    }

    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteButton(object row)
    {
        if (_vm is null || row is not RentalChipRowViewModel chip)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteRow(chip, skipConfirm);
    }

    private void DeleteRow(RentalChipRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteChipNoConfirmAsync(row);
        else
            _ = _vm!.DeleteChipCommand.ExecuteAsync(row);
    }

    // File picking needs the window's StorageProvider, so it lives in the view. The auto-read picker
    // only fills the path; the VM polls it on its own.
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
        _vm = DataContext as ChipsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // After the delete-confirmation modal closes, keyboard focus is on the overlay and would
    // otherwise land on the top menu. Return it to the grid (on its new selected row). Posted so it
    // runs once the overlay has been torn down and the grid is interactive again.
    private void OnFocusGridRequested(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    // Builds the table's columns. Headers are baked into the band model, so a language switch is
    // handled by rebuilding. The chip number is digits-only; this page is the rental database itself,
    // so (unlike the participants page) chip numbers carry no rental highlight here.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("Chips.Col.Number", nameof(RentalChipRowViewModel.Number),
                  editPath: nameof(RentalChipRowViewModel.Number), minWidth: 140,
                  mask: SheetColumnBuilder.NumericMask.Digits)
            // Tint the number cell red when it duplicates another chip (numbers must be unique): the
            // duplicate is never saved, so the tint + tooltip is the only signal it can't be used.
            .CellTint(nameof(RentalChipRowViewModel.IsDuplicate), DuplicateBrush,
                      tooltipPath: nameof(RentalChipRowViewModel.DuplicateTooltip))
            .Text("Chips.Col.Note", nameof(RentalChipRowViewModel.Note),
                  editPath: nameof(RentalChipRowViewModel.Note), minWidth: 240)
            // Read-only: who holds this chip (full names across all days, comma-separated). No editPath.
            .Text("Chips.Col.AssignedTo", nameof(RentalChipRowViewModel.AssignedTo), minWidth: 240)
            .DeleteAction(OnDeleteButton, "Chips.Delete")
            .Bands;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
