using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.Controls;
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

    // The table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt). Ignored while a
    // cell editor (TextBox) has focus — handled inside SheetTable, which won't raise it then.
    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not ControlPointRowViewModel row)
            return;
        DeleteRow(row, e.SkipConfirm);
    }

    // The per-row delete button. Button.Click doesn't carry key modifiers, and the button marks its
    // own PointerPressed handled — so we capture the Ctrl state in the tunnel phase. A plain click
    // confirms first; Ctrl+Click deletes immediately.
    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteButton(object row)
    {
        if (_vm is null || row is not ControlPointRowViewModel point)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteRow(point, skipConfirm);
    }

    private void DeleteRow(ControlPointRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeletePointNoConfirmAsync(row);
        else
            _ = _vm!.DeletePointCommand.ExecuteAsync(row);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ControlPointsViewModel;
        if (_vm is null)
            return;

        // Column headers are baked into the band model at build time, so a language switch (or a
        // change to which columns are shown) is handled by rebuilding the bands.
        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.ColumnsChanged += OnColumnsChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void OnColumnsChanged(object? sender, System.EventArgs e) => BuildBands();

    // After the delete-confirmation modal closes, return keyboard focus to the table (on its new
    // selected row). Posted so it runs once the overlay has been torn down and the table is live again.
    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    // Builds the table's columns. The points column appears only when the day's discipline (or a
    // group on the day) scores points — so it's omitted from the band list when hidden.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var builder = new SheetColumnBuilder(loc)
            .Text("ControlPoints.Col.Code", nameof(ControlPointRowViewModel.Code),
                  editPath: nameof(ControlPointRowViewModel.Code), minWidth: 100)
            .Combo("ControlPoints.Col.Type",
                   nameof(ControlPointRowViewModel.TypeOptions),
                   nameof(ControlPointRowViewModel.SelectedType),
                   nameof(ControlPointTypeOption.Label),
                   minWidth: 120,
                   sortPath: $"{nameof(ControlPointRowViewModel.SelectedType)}.Value");

        // Coordinate columns toggle between the relative "by map" ground metres (default, read-only —
        // derived from the imported map position + scale) and the editable real WGS-84 lat/lon.
        if (_vm.ShowMapCoordinates)
            builder
                .Text("ControlPoints.Col.MapX", nameof(ControlPointRowViewModel.MapXText), minWidth: 100,
                      mask: SheetColumnBuilder.NumericMask.Decimal)
                .Text("ControlPoints.Col.MapY", nameof(ControlPointRowViewModel.MapYText), minWidth: 100,
                      mask: SheetColumnBuilder.NumericMask.Decimal);
        else
            builder
                .Text("ControlPoints.Col.Lat", nameof(ControlPointRowViewModel.LatitudeText),
                      editPath: nameof(ControlPointRowViewModel.LatitudeText), minWidth: 100,
                      mask: SheetColumnBuilder.NumericMask.Decimal)
                .Text("ControlPoints.Col.Lon", nameof(ControlPointRowViewModel.LongitudeText),
                      editPath: nameof(ControlPointRowViewModel.LongitudeText), minWidth: 100,
                      mask: SheetColumnBuilder.NumericMask.Decimal);

        if (_vm.ShowPointsColumn)
            builder.Text("ControlPoints.Col.Points", nameof(ControlPointRowViewModel.PointsText),
                         editPath: nameof(ControlPointRowViewModel.PointsText), minWidth: 80,
                         mask: SheetColumnBuilder.NumericMask.Integer);

        // «Проблемний» toggle: marks a control that stopped working so it stops being required (no MP /
        // not counted), the same flag the read-out page's «Проблемні КП» modal sets.
        builder.Check("ControlPoints.Col.Disabled", nameof(ControlPointRowViewModel.IsDisabled),
                      width: 110, minWidth: 90);

        builder.DeleteAction(OnDeleteButton, "ControlPoints.Delete");

        Sheet.Bands = builder.Bands;
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
