using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class ControlPointsView : UserControl
{
    private ControlPointsViewModel? _vm;

    public ControlPointsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press (it marks
        // PointerPressed handled), so Ctrl+Click on Delete can skip the confirmation prompt.
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    // Delete on the table deletes the selected control point. Ctrl+Delete skips the confirmation.
    // Ignored while a cell editor (TextBox) has focus, so Delete still edits text inside a cell.
    private void OnSheetKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.Delete)
            return;

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;

        var skipConfirm = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        e.Handled = true;
        _ = _vm.DeleteSelectedPointAsync(skipConfirm);
    }

    // The per-row delete button. Button.Click doesn't carry key modifiers, and the button marks its
    // own PointerPressed handled — so we capture the Ctrl state in the tunnel phase. A plain click
    // confirms first; Ctrl+Click deletes immediately.
    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control { Tag: ControlPointRowViewModel row })
            return;

        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;

        if (skipConfirm)
            _ = _vm.DeletePointNoConfirmAsync(row);
        else
            _ = _vm.DeletePointCommand.ExecuteAsync(row);
    }

    // DataGrid column headers live outside the visual tree, so they can't bind to
    // Localization[...] like cells do. We resolve them here and re-apply on language change.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ControlPointsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.ColumnsChanged += OnColumnsChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        ApplyHeaders();
        ApplyColumnVisibility();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => ApplyHeaders();

    // After the delete-confirmation modal closes, keyboard focus is on the overlay and would
    // otherwise land on the top menu. Return it to the grid (on its new selected row). Posted so it
    // runs once the overlay has been torn down and the grid is interactive again.
    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    private void OnColumnsChanged(object? sender, System.EventArgs e) => ApplyColumnVisibility();

    // The points column appears only when the day's discipline (or a group on the day) scores points.
    private void ApplyColumnVisibility()
    {
        if (_vm is null)
            return;

        Sheet.Columns[4].IsVisible = _vm.ShowPointsColumn;
    }

    // File picking is a view concern (it needs the window's StorageProvider), so it lives here
    // rather than in the view model. We read the chosen file's text and hand it to the VM, which
    // owns parsing, the options modal, and the import itself.
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("Import.PickerTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("IOF XML")
                {
                    Patterns = ["*.xml"],
                    MimeTypes = ["application/xml", "text/xml"]
                }
            ]
        });

        if (files.Count == 0)
            return;

        // Read the raw bytes once so we can both parse the text and archive the exact original file
        // into the day's folder. Decode through a StreamReader so any byte-order mark is detected and
        // stripped (matching the previous behaviour); the kept bytes stay the untouched original.
        string xml;
        byte[]? content = null;
        var fileName = files[0].Name;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            content = memory.ToArray();

            using var reader = new StreamReader(new MemoryStream(content), detectEncodingFromByteOrderMarks: true);
            xml = await reader.ReadToEndAsync();
        }
        catch
        {
            // Couldn't read the file (permissions, removed, etc.) — let the VM report via the modal.
            xml = string.Empty;
        }

        await _vm.ImportFromXmlAsync(xml, fileName, content);
    }

    private void ApplyHeaders()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var columns = Sheet.Columns;
        columns[0].Header = loc.Get("ControlPoints.Col.Code");
        columns[1].Header = loc.Get("ControlPoints.Col.Type");
        columns[2].Header = loc.Get("ControlPoints.Col.Lat");
        columns[3].Header = loc.Get("ControlPoints.Col.Lon");
        columns[4].Header = loc.Get("ControlPoints.Col.Points");
        columns[5].Header = loc.Get("ControlPoints.Col.Actions");
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.ColumnsChanged -= OnColumnsChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
