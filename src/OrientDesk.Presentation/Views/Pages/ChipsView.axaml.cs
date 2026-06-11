using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class ChipsView : UserControl
{
    private ChipsViewModel? _vm;

    public ChipsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press, so Ctrl+Click on
        // Delete can skip the confirmation prompt (see ControlPointsView for the same approach).
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    // Delete on the table deletes the selected chip. Ctrl+Delete skips the confirmation. Ignored
    // while a cell editor (TextBox) has focus, so Delete still edits text inside a cell.
    private void OnSheetKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.Delete)
            return;

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;

        var skipConfirm = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        e.Handled = true;
        _ = _vm.DeleteSelectedChipAsync(skipConfirm);
    }

    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control { Tag: RentalChipRowViewModel row })
            return;

        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;

        if (skipConfirm)
            _ = _vm.DeleteChipNoConfirmAsync(row);
        else
            _ = _vm.DeleteChipCommand.ExecuteAsync(row);
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
        ApplyHeaders();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => ApplyHeaders();

    // DataGrid column headers live outside the visual tree, so they can't bind to Localization[...]
    // like cells do. We resolve them here and re-apply on language change.
    private void ApplyHeaders()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var columns = Sheet.Columns;
        columns[0].Header = loc.Get("Chips.Col.Number");
        columns[1].Header = loc.Get("Chips.Col.Note");
        columns[2].Header = loc.Get("Chips.Col.Actions");
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
