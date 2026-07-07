using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class ParticipantsView : UserControl
{
    private ParticipantsViewModel? _vm;

    public ParticipantsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // After navigation the new page isn't given keyboard focus — it sits on the sidebar button — so a
        // tunnel KeyDown on this control never sees Ctrl+F / Shift+F3 until the user clicks into the page.
        // Pull focus onto the page root on attach (the control is Focusable) so those shortcuts work the
        // moment the page opens. A subsequent click into the table just moves focus deeper, which is fine.
        AttachedToVisualTree += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus());
            // Apply a quick filter requested before the page was shown (dashboard drill-in first open).
            ApplyPendingQuickFilter();
        };

        // Ctrl+F (focus search) and Shift+F3 (clear the last filter) work even when focus is on the page,
        // not yet inside the table. Tunnel so the page sees them before any child; the table also handles
        // both itself (for focus already inside it). FocusSearch is idempotent and ClearLastFilter is a
        // no-op when there's nothing to clear, so a double-route is harmless.
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);

        // The header context menu offers "bulk edit this column" for every column that maps to a field,
        // and routes the pick here. Both tables share the same column model, so both are wired.
        // Both tables offer the column menu for every column that maps to a bulk-editable field. The
        // roster supports group/start/OOC too: they fan out to a participant's days (group resolved per
        // day by id), mirroring the roster's collapsed cells.
        DayTable.CanBulkEditColumn = c => BulkEditKeyFor(c) is not null;
        RosterTable.CanBulkEditColumn = c => BulkEditKeyFor(c) is not null;
        DayTable.BulkEditColumnRequested += OnBulkEditColumnRequested;
        RosterTable.BulkEditColumnRequested += OnBulkEditColumnRequested;

        // Skip the per-row fee recompute while the «Стартовий внесок» column is hidden; flush it when the
        // column is shown again. The active table owns the fee:total column (same key on both).
        DayTable.ColumnVisibilityChanged += OnFeeColumnVisibilityChanged;
        RosterTable.ColumnVisibilityChanged += OnFeeColumnVisibilityChanged;
    }

    private const string FeeTotalKey = "fee:total";

    // The fee column is on whichever table is currently shown; default to visible when neither is up yet.
    private bool IsFeeColumnVisible()
        => ActiveTable is not { } table || table.IsColumnVisible(FeeTotalKey);

    private SheetTable? ActiveTable
        => _vm is null ? null : _vm.IsRosterMode ? RosterTable : DayTable;

    private void OnFeeColumnVisibilityChanged(object? sender, System.EventArgs e)
    {
        if (sender is SheetTable table && table.IsColumnVisible(FeeTotalKey))
            _vm?.OnFeeColumnShown();
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ParticipantsViewModel;
        if (_vm is null)
            return;

        _vm.RosterColumnsChanged += OnRosterColumnsChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        _vm.QuickFilterRequested += OnQuickFilterRequested;
        _vm.IsFeeColumnVisible = IsFeeColumnVisible;

        // The VM may already hold a quick filter requested before this view got its DataContext (first
        // open via a dashboard drill-in). Apply it now; the attach handler / event covers the other orders.
        ApplyPendingQuickFilter();
    }

    // Stable column keys (DayColumnBuilder keys identity columns off their header key).
    private const string ChipColumnKey = "id:Participants.Col.Chip";
    private const string GroupColumnKey = "id:Participants.Col.Group";
    private const string ActualStartColumnKey = "id:Participants.Col.ActualStart";
    private const string FinishColumnKey = "id:Participants.Col.Finish";
    private const string StatusColumnKey = "id:Participants.Col.ResultStatus";

    // An "is empty" condition filter on a day-grid column (keyed off its header loc key).
    private void SetEmptyFilter(string columnKey, string headerKey) =>
        DayTable.SetColumnFilter(columnKey, new SheetFilter
        {
            ColumnKey = columnKey,
            Header = _vm!.Localization.Get(headerKey),
            Mode = SheetFilterMode.Condition,
            Condition = SheetFilterCondition.IsEmpty,
        });

    // The VM signals (re-navigation case) that a pending quick filter is ready; consume + apply it.
    private void OnQuickFilterRequested(object? sender, ParticipantQuickFilter filter)
        => ApplyPendingQuickFilter();

    // A dashboard drill-in ("учасники без чіпа / без групи") opens the page and asks for a pre-applied
    // day-grid filter. Consume the VM's pending filter and apply it to the day table once its bands/rows
    // are up (post to let the load settle). Called both on the VM event and when the page is shown
    // (attach) — whichever comes first consumes it; the other then no-ops (filter is None).
    private void ApplyPendingQuickFilter()
    {
        if (_vm is null)
            return;

        var filter = _vm.ConsumePendingQuickFilter();
        if (filter == ParticipantQuickFilter.None)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            switch (filter)
            {
                case ParticipantQuickFilter.WithoutChip:
                    // Chip cell is empty for a chipless member → an IsEmpty condition matches exactly.
                    SetEmptyFilter(ChipColumnKey, "Participants.Col.Chip");
                    break;

                case ParticipantQuickFilter.OnCourse:
                    // Still on course: no actual (chip) start, no finish and no status — three IsEmpty
                    // filters AND together. The status column filters on its effective short code (blank
                    // for an un-computed status; see DayColumnBuilder), so IsEmpty catches it.
                    SetEmptyFilter(ActualStartColumnKey, "Participants.Col.ActualStart");
                    SetEmptyFilter(FinishColumnKey, "Participants.Col.Finish");
                    SetEmptyFilter(StatusColumnKey, "Participants.Col.ResultStatus");
                    break;

                case ParticipantQuickFilter.WithoutGroup:
                    // A groupless member shows the "no group" label, so keep exactly that value.
                    DayTable.SetColumnFilter(GroupColumnKey, new SheetFilter
                    {
                        ColumnKey = GroupColumnKey,
                        Header = _vm.Localization.Get("Participants.Col.Group"),
                        Mode = SheetFilterMode.Values,
                        AllowedValues = new System.Collections.Generic.HashSet<string>
                        {
                            _vm.Localization.Get("Participants.Group.None"),
                        },
                    });
                    break;
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => DayTable.Focus());

    // Page-level shortcuts that should fire even when focus is on the page chrome (not inside the table):
    //   • Ctrl+F        → focus the active table's search box.
    //   • Shift+F3      → clear the most recently added column filter.
    // Plus the grouped toolbar actions (the «Імпорт» / «Дії» dropdowns), so each menu item has a hotkey:
    //   • Ctrl+I        → import from UOF XML file       • Ctrl+Shift+I → import from CSV/Excel
    //   • Ctrl+E        → export the current view         • Ctrl+B       → assign start numbers
    //   • Ctrl+K        → assign rental chips             • Ctrl+G       → mark out-of-competition by age
    //   • F6            → bulk-edit (matches the table's in-cell F6); routed here when focus is outside
    //                     the table so it opens the modal on all visible rows.
    // The table's own handler covers focus already inside it; this one covers focus elsewhere on the page.
    private void OnPageKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);

        if (e.Key == Avalonia.Input.Key.F && ctrl)
        {
            ActiveTable?.FocusSearch();
            e.Handled = true;
            return;
        }

        // The grouped action shortcuts. These run the same handlers/commands as the dropdown menu items,
        // so they fire regardless of which dropdown is open (or closed). Ctrl+Shift+I must be checked
        // before Ctrl+I so the CSV variant isn't shadowed.
        if (ctrl && e.Key == Avalonia.Input.Key.I)
        {
            if (shift) _ = ImportCsvAsync(); else _ = ImportXmlAsync();
            e.Handled = true;
            return;
        }
        if (ctrl && !shift)
        {
            switch (e.Key)
            {
                case Avalonia.Input.Key.E: _ = ExportAsync(); e.Handled = true; return;
                case Avalonia.Input.Key.B: _ = AssignNumbersAsync(); e.Handled = true; return;
                case Avalonia.Input.Key.K: _ = AssignChipsAsync(); e.Handled = true; return;
                case Avalonia.Input.Key.G: _ = MarkAgeViolatorsAsync(); e.Handled = true; return;
                case Avalonia.Input.Key.W:
                    if (_vm?.CanEditStartOrder == true)
                        _ = _vm.QuickWithdrawalCommand.ExecuteAsync(null);
                    e.Handled = true;
                    return;
            }
        }

        // F6 = bulk-edit. The table handles its own in-cell F6 (bulk-edit the focused column); here we
        // cover focus outside the table, opening the modal on whichever field the user last focused.
        if (e.Key == Avalonia.Input.Key.F6
            && (e.KeyModifiers & ~Avalonia.Input.KeyModifiers.Shift) == Avalonia.Input.KeyModifiers.None
            && !shift
            && ActiveTable is { } bulkTable
            && !IsFocusInside(bulkTable))
        {
            _ = BulkEditAsync();
            e.Handled = true;
            return;
        }

        // Shift+F3 clears the last-added filter. Only act when focus is *outside* the table — when focus
        // is inside it the table's own handler runs and does the smarter focused-column case. Match the
        // in-table chord exactly: F3 + Shift only.
        if (e.Key == Avalonia.Input.Key.F3
            && (e.KeyModifiers & ~Avalonia.Input.KeyModifiers.Shift) == Avalonia.Input.KeyModifiers.None
            && shift
            && ActiveTable is { } table
            && !IsFocusInside(table))
        {
            table.ClearLastFilter();
            e.Handled = true;
        }
    }

    // True when the keyboard-focused element lives inside the given table (so the table's own key handler
    // will run for it, and the page-level fallback should stay out of the way).
    private static bool IsFocusInside(SheetTable table)
    {
        var focused = TopLevel.GetTopLevel(table)?.FocusManager?.GetFocusedElement() as Avalonia.Visual;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, table))
                return true;
            focused = focused.GetVisualParent();
        }
        return false;
    }

    // A Button hosted inside a Button.Flyout doesn't dismiss the flyout when clicked (unlike a real
    // MenuItem). So every menu-item handler below closes the containing flyout first: we walk up from the
    // clicked control to its hosting PopupRoot and close whichever Popup opened it. Without this the «Імпорт»
    // / «Дії» dropdowns stay open after a pick.
    private static void CloseContainingFlyout(object? sender)
    {
        if (sender is not Avalonia.Visual visual)
            return;

        // The flyout content lives in a PopupRoot whose hosting control is the Popup; closing it dismisses
        // the flyout. GetTopLevel on anything inside the popup returns that PopupRoot.
        if (TopLevel.GetTopLevel(visual) is Avalonia.Controls.Primitives.PopupRoot { Parent: Avalonia.Controls.Primitives.Popup popup })
            popup.IsOpen = false;
    }

    // File picking is a view concern (it needs the window's StorageProvider). We read the chosen
    // file's bytes and decode them honouring the encoding declared in the XML prolog (UOF files are
    // windows-1251), then hand the text to the VM, which owns the import flow. Mirrors GroupsView.
    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = ImportXmlAsync();
    }

    private async System.Threading.Tasks.Task ImportXmlAsync()
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
    private void OnImportCsvClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = ImportCsvAsync();
    }

    private async System.Threading.Tasks.Task ImportCsvAsync()
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

    // Export the active table's current view. The visible columns + displayed rows live in the
    // SheetTable, so we capture them here (the VM never references the table directly) and hand the
    // snapshot to the VM's export flow, which shows the format modal and serialises the bytes. When the
    // user confirmed, we run the save dialog (it needs the window's StorageProvider) and write the file.
    private void OnExportClick(object? sender, RoutedEventArgs e) => _ = ExportAsync();

    private async System.Threading.Tasks.Task ExportAsync()
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        var view = table.ExportView();

        var result = await _vm.ExportAsync(view);
        if (result is null)
            return; // nothing to export or cancelled

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _vm.Localization.Get("Participants.Export"),
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

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(result.Bytes);
        }
        catch
        {
            // The file couldn't be written (permissions, removed drive, etc.). Nothing more we can do
            // here; the user can retry. A future toast could surface it.
        }
    }

    // Bulk-assign start numbers. The on-screen (filtered + sorted) row order lives in the SheetTable,
    // so we read the active table's VisibleItems here — the VM never references the table directly —
    // and hand that ordered list to the command, which prompts for the start number and applies them.
    private void OnAssignNumbersClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = AssignNumbersAsync();
    }

    private async System.Threading.Tasks.Task AssignNumbersAsync()
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        await _vm.AssignNumbersCommand.ExecuteAsync(table.VisibleItems);
    }

    // Bulk-assign rental chips. Same shape as OnAssignNumbersClick: read the active table's on-screen
    // (filtered + sorted) rows and hand them to the command, which prompts for the note filter.
    private void OnAssignChipsClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = AssignChipsAsync();
    }

    private async System.Threading.Tasks.Task AssignChipsAsync()
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        await _vm.AssignChipsCommand.ExecuteAsync(table.VisibleItems);
    }

    // Mark every shown participant who breaches their group's age window "поза конкурсом". Same shape as
    // OnAssignChipsClick: read the active table's on-screen (filtered + sorted) rows and hand them to the
    // command, which confirms before applying.
    private void OnMarkAgeViolatorsClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = MarkAgeViolatorsAsync();
    }

    private async System.Threading.Tasks.Task MarkAgeViolatorsAsync()
    {
        if (_vm is null)
            return;

        var table = _vm.IsRosterMode ? RosterTable : DayTable;
        await _vm.MarkAgeViolatorsOutOfCompetitionCommand.ExecuteAsync(table.VisibleItems);
    }

    // Manually re-order the start sequence within a group. Works off the day currently in view (resolved
    // by the VM), not the visible rows, so it just runs the command.
    private void OnEditStartOrderClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = _vm?.EditStartOrderCommand.ExecuteAsync(null);
    }

    // Quick withdrawal ("Швидке зняття"): type a number, set a status. Works off the day currently in view
    // (resolved by the VM), not the visible rows, so it just runs the command.
    private void OnQuickWithdrawalClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = _vm?.QuickWithdrawalCommand.ExecuteAsync(null);
    }

    // Bulk-edit one field across the shown rows. Same shape as OnAssignNumbersClick: read the active
    // table's on-screen (filtered + sorted) rows and hand them to the command, which prompts for the
    // field + value and applies it to each. The dialog opens preselected on the column the user was last
    // focused in, when that column is bulk-editable (otherwise on the first field).
    private void OnBulkEditClick(object? sender, RoutedEventArgs e)
    {
        CloseContainingFlyout(sender);
        _ = BulkEditAsync();
    }

    private async System.Threading.Tasks.Task BulkEditAsync()
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
            _vm.QuickFilterRequested -= OnQuickFilterRequested;
            _vm.IsFeeColumnVisible = null;
        }
        _vm = null;
    }
}
