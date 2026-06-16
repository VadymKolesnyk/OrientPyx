using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OrientDesk.Presentation.Controls;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class ParticipantsView : UserControl
{
    private ParticipantsViewModel? _vm;

    public ParticipantsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ParticipantsViewModel;
        if (_vm is null)
            return;

        _vm.RosterColumnsChanged += OnRosterColumnsChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
    }

    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => DayTable.Focus());

    // File picking is a view concern (it needs the window's StorageProvider). We read the chosen
    // file's bytes and decode them honouring the encoding declared in the XML prolog (UOF files are
    // windows-1251), then hand the text to the VM, which owns the import flow. Mirrors GroupsView.
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("ParticipantsImport.PickerTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("UOF XML")
                {
                    Patterns = ["*.xml"],
                    MimeTypes = ["application/xml", "text/xml"]
                }
            ]
        });

        if (files.Count == 0)
            return;

        string xml;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            // Decode by the declared encoding (windows-1251 for these files), not a fixed UTF-8 reader.
            xml = XmlEncodingReader.DecodeXml(memory.ToArray());
        }
        catch
        {
            // Couldn't read the file (permissions, removed, etc.) — let the VM report via the modal.
            xml = string.Empty;
        }

        await _vm.ImportFromXmlAsync(xml);
    }

    // CSV / Excel import: pick the file (needs the window's StorageProvider), read its bytes, then route
    // by extension — an .xlsx workbook goes through the VM's xlsx entry point, anything else is decoded
    // as CSV text. Both share the same column-mapping modal + import. Mirrors OnImportClick.
    private async void OnImportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("ParticipantsImport.PickerCsvTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV / Excel")
                {
                    Patterns = ["*.csv", "*.txt", "*.xlsx"],
                    MimeTypes =
                    [
                        "text/csv", "text/plain",
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                    ]
                }
            ]
        });

        if (files.Count == 0)
            return;

        // An .xlsx is a binary workbook; everything else is treated as CSV text.
        var isXlsx = files[0].Name.EndsWith(".xlsx", System.StringComparison.OrdinalIgnoreCase);

        byte[] bytes;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            bytes = memory.ToArray();
        }
        catch
        {
            // Couldn't read the file (permissions, removed, etc.) — let the VM report via the modal.
            bytes = [];
        }

        if (isXlsx)
            await _vm.ImportFromXlsxAsync(bytes);
        else
            await _vm.ImportFromCsvAsync(CsvEncodingReader.Decode(bytes));
    }

    // Bulk-assign start numbers. The on-screen (filtered + sorted) row order lives in the SheetTable,
    // so we read the active table's VisibleItems here — the VM never references the table directly —
    // and hand that ordered list to the command, which prompts for the start number and applies them.
    private async void OnAssignNumbersClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        await _vm.AssignNumbersCommand.ExecuteAsync(table.VisibleItems);
    }

    // Bulk-assign rental chips. Same shape as OnAssignNumbersClick: read the active table's on-screen
    // (filtered + sorted) rows and hand them to the command, which prompts for the note filter.
    private async void OnAssignChipsClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        await _vm.AssignChipsCommand.ExecuteAsync(table.VisibleItems);
    }

    // Bulk-edit one field across the shown rows. Same shape as OnAssignNumbersClick: read the active
    // table's on-screen (filtered + sorted) rows and hand them to the command, which prompts for the
    // field + value and applies it to each.
    private async void OnBulkEditClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        await _vm.BulkEditCommand.ExecuteAsync(table.VisibleItems);
    }

    // A collapse/expand toggle (or day-set change) asks the roster table to rebuild its columns.
    private void OnRosterColumnsChanged(object? sender, System.EventArgs e) => RosterTable.Rebuild();

    // The day table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnDayDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not ParticipantDayRowViewModel row)
            return;
        if (e.SkipConfirm)
            _ = _vm.DeleteParticipantNoConfirmAsync(row);
        else
            _ = _vm.DeleteParticipantCommand.ExecuteAsync(row);
    }

    // The roster table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnRosterDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not ParticipantRosterRowViewModel row)
            return;
        if (e.SkipConfirm)
            _ = _vm.DeleteRosterParticipantNoConfirmAsync(row);
        else
            _ = _vm.DeleteRosterParticipantCommand.ExecuteAsync(row);
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.RosterColumnsChanged -= OnRosterColumnsChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
