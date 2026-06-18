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

        // The header context menu offers "bulk edit this column" for every column that maps to a field,
        // and routes the pick here. Both tables share the same column model, so both are wired.
        // Both tables offer the column menu for every column that maps to a bulk-editable field. The
        // roster supports group/start/OOC too: they fan out to a participant's days (group resolved per
        // day by id), mirroring the roster's collapsed cells.
        DayTable.CanBulkEditColumn = c => BulkEditKeyFor(c) is not null;
        RosterTable.CanBulkEditColumn = c => BulkEditKeyFor(c) is not null;
        DayTable.BulkEditColumnRequested += OnBulkEditColumnRequested;
        RosterTable.BulkEditColumnRequested += OnBulkEditColumnRequested;
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
    // field + value and applies it to each. The dialog opens preselected on the column the user was last
    // focused in, when that column is bulk-editable (otherwise on the first field).
    private async void OnBulkEditClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        var preselect = BulkEditKeyFor(table.FocusedColumn);
        await _vm.BulkEditCommand.ExecuteAsync(new BulkEditRequest(table.VisibleItems, preselect));
    }

    // A "bulk edit this column" header-menu pick: open the dialog preselected on that column. (The menu
    // item only shows for columns BulkEditKeyFor accepts, so the key here is always non-null.)
    private async void OnBulkEditColumnRequested(object? sender, SheetColumn column)
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        await _vm.BulkEditCommand.ExecuteAsync(new BulkEditRequest(table.VisibleItems, BulkEditKeyFor(column)));
    }

    // Maps a sheet column to the bulk-edit field key it edits, or null when the column can't be
    // bulk-edited (the unique number/chip columns, name/birth date, the computed fee tail, actions).
    // The mapping is by the column's kind + bound property so it works for both the day grid and the
    // roster (they share kinds and identity paths); per-day group/chip/start cells in the roster map to
    // their field, but only day-mode offers the per-day fields (group via the day combo, start, OOC).
    private static string? BulkEditKeyFor(SheetColumn? column)
    {
        if (column is null)
            return null;

        switch (column.Kind)
        {
            case SheetCellKind.RowGroup:
            case SheetCellKind.Group:
            case SheetCellKind.CollapsedGroup:
                return "Group";
            case SheetCellKind.RowRegion:
                return "Region";
            case SheetCellKind.RowClub:
                return "Club";
            case SheetCellKind.RowDussh:
                return "Dussh";
            case SheetCellKind.RowRank:
                return "Rank";
            case SheetCellKind.PaymentText:
                return "Payment";
            case SheetCellKind.StartTimeText:
            case SheetCellKind.StartTime:
            case SheetCellKind.CollapsedStartTime:
                return "StartTime";
            case SheetCellKind.RaisedFeeFlag:
                return "PaysRaisedFee";
            case SheetCellKind.OutOfCompetition:
            case SheetCellKind.CollapsedOutOfCompetition:
                return "OutOfCompetition";
            case SheetCellKind.IdentityText:
                // Plain text identity columns map by their bound property; name is excluded (no field).
                return column.IdentityPath switch
                {
                    nameof(ParticipantDayRowViewModel.Coach) => "Coach",
                    nameof(ParticipantDayRowViewModel.Representative) => "Representative",
                    nameof(ParticipantDayRowViewModel.FsouCode) => "FsouCode",
                    nameof(ParticipantDayRowViewModel.Note) => "Note",
                    nameof(ParticipantDayRowViewModel.Team) => "Team",
                    _ => null,
                };
            case SheetCellKind.IdentityBool:
                return column.IdentityPath switch
                {
                    nameof(ParticipantDayRowViewModel.IsFsouMember) => "IsFsouMember",
                    nameof(ParticipantDayRowViewModel.OutOfCompetition) => "OutOfCompetition",
                    _ => null,
                };
            // Number, ChipText/Chip/CollapsedChip, BirthDate, fee total, actions, custom: not bulk-editable.
            default:
                return null;
        }
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
