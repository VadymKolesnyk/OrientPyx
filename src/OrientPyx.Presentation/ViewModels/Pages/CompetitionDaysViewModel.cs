using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.Services.Undo;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Table of the current competition's days. Each row edits its date, venue and discipline straight in the table —
/// the row saves itself in the background (debounced), no Save button. Opened from the "Competition → Days" top menu.
/// </summary>
public sealed partial class CompetitionDaysViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IActivityLog _log;
    // Held directly rather than through UseDayLock: that binds the page to the SESSION's day, and this
    // page shows every day at once — each row's lock is its own.
    private readonly IDayLockService _dayLock;
    private readonly IDisciplineStrategyProvider _strategies;
    // The days table has no undo of its own (deleting / renumbering days is not undoable); it only drops the undo
    // histories of a day it deletes.
    private readonly IUndoHistory _undo;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();
    private readonly PendingSaves _pendingSaves = new();
    // The competition (and registered disciplines) the rows were read for: a SessionChanged that keeps both (a day
    // switched, the active day's own row edited — here or on «Групи») merges the rows in place instead of rebuilding
    // the table under the operator; a plugin switched on or off rebuilds it, so the type dropdowns follow.
    private (string? Event, string Disciplines) _loadedFor;

    private (string? Event, string Disciplines) SessionKey()
        => (_session.CurrentEvent?.FolderPath, DisciplineTypeOption.RegisteredKey(_strategies));

    public CompetitionDaysViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore,
        IDayLockService dayLock,
        IActivityLog log,
        IDisciplineStrategyProvider strategies,
        IDataChangeBus bus,
        IWorkspaceMode mode,
        IUndoHistory undo)
        : base(localization)
    {
        _undo = undo;
        _strategies = strategies;
        _dayLock = dayLock;
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;
        _log = log;
        // Singleton VM: reload the day rows whenever the competition/day changes so a switched
        // event never leaves the previous competition's days on screen. The event may arrive on a
        // pool thread (session writes run inside RunAsync), so marshal LoadAsync onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = OnSessionChangedAsync());
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Days, DataArea.Competition);
    }

    /// <summary>Per-competition table-view store; persists this page's table column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.CompetitionDays";
    public override string TitleKey => "Page.CompetitionDays.Title";
    public override string TextKey => "Page.CompetitionDays.Text";

    public ObservableCollection<DayRowViewModel> Days { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private DayRowViewModel? _selectedRow;

    /// <inheritdoc />
    public override Task ExplainDayLockAsync(int dayNumber = 0, bool merged = false)
        => _dayLock.ExplainLockedCellAsync(dayNumber, merged);

    /// <summary>
    /// The gate for a row action (renumber, delete): a closed day refuses, naming itself. The
    /// page-wide <c>EnsureDayEditableForActionAsync</c> can't be used here — it asks about the session's
    /// day, and every row on this page is a different day.
    /// </summary>
    private async Task<bool> EnsureRowEditableAsync(DayRowViewModel row)
    {
        if (!row.IsLocked)
            return true;

        await ExplainDayLockAsync(row.Number);
        return false;
    }

    /// <inheritdoc />
    protected override async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        // The rows' pending saves land first (the reload then reads them back); the rows are merged in place
        // (RowSync): the selection survives, a row typed on meanwhile keeps its edit.
        await _pendingSaves.WaitAsync();
        await LoadCoreAsync(merge: true);
        if (SelectedRow is { } selected && !Days.Contains(selected))
            SelectedRow = null;
    }

    /// <inheritdoc />
    public override Task FlushPendingWritesAsync() => _pendingSaves.WaitAsync();

    /// <summary>Reloads the day rows from the current competition. Called when the page is shown.</summary>
    public Task LoadAsync()
    {
        CancelAllTimers();
        return LoadCoreAsync(merge: false);
    }

    // Another competition (or none) rebuilds the table and drops the previous one's unsaved edits; the same one merges.
    private Task OnSessionChangedAsync()
        => _loadedFor.Event is not null && _loadedFor == SessionKey() ? ReloadAsync() : LoadAsync();

    // Reads the days and either rebuilds the rows (first open / session switch) or merges them in place.
    private async Task LoadCoreAsync(bool merge)
    {
        var activeNumber = _session.CurrentDay?.Number;
        var loadingFor = SessionKey();
        // BD read runs off the UI thread; the collection is rebuilt afterwards on the UI thread.
        var (days, info) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var i = await _editor.GetInfoAsync();
            return (d, i);
        });

        var venuePlaceholder = info?.Venue?.Trim() ?? string.Empty;
        _loadedFor = loadingFor;

        if (!merge)
        {
            Days.Clear();
            SelectedRow = null;
        }

        RowSync.Sync(Days, days,
            itemKey: day => day.Id,
            rowKey: row => row.Id,
            create: day => new DayRowViewModel(day, day.Number == activeNumber, Localization, _strategies,
                RequestRowSave, venuePlaceholder),
            update: (row, day) => row.Apply(day, day.Number == activeNumber));
    }

    [RelayCommand]
    private async Task AddDayAsync()
    {
        await _busy.RunAsync(() => _editor.AddDayAsync());
        await LoadAsync();
    }

    /// <summary>
    /// Opens the change-day-number modal for a row and applies the chosen number: the editor updates
    /// the day's number and renames its files folder. If the renumbered day was the active one, the
    /// session is pointed at its new number. The grid is reloaded either way.
    /// </summary>
    [RelayCommand]
    private async Task ChangeDayNumberAsync(DayRowViewModel? row)
    {
        if (row is null || !await EnsureRowEditableAsync(row))
            return;

        // The row's pending edit lands under its current number before the day is renumbered.
        await _pendingSaves.WaitAsync();
        var taken = Days.Where(d => d.Id != row.Id).Select(d => d.Number).ToHashSet();
        var dialog = new ChangeDayNumberViewModel(Localization, row.Number, taken);
        var newNumber = await _dialogs.ShowChangeDayNumberAsync(dialog);
        if (newNumber is null)
            return;

        var wasActive = row.IsActive;
        var updated = await _busy.RunAsync(() => _editor.ChangeDayNumberAsync(row.Id, newNumber.Value));
        if (updated is null)
            return; // rejected (e.g. number taken in a race) — leave the grid as-is

        // The active day's in-memory number is now stale; re-point the session so the day picker and
        // last-session pointer carry the new number. This re-raises SessionChanged, which already
        // reloads this page, so we only reload directly when the renumbered day wasn't the active one.
        if (wasActive)
            await _busy.RunAsync(() => _session.SetCurrentDayAsync(updated));
        else
            await LoadAsync();
    }

    // Invoked by a row on every edit of its date / venue / discipline (UI thread): restarts that row's debounce, so a
    // venue typed letter by letter is one write. The write runs in the background — no busy overlay, no Save button.
    private void RequestRowSave(DayRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _pendingSaves.Track(SaveRowDebouncedAsync(row, cts));
    }

    private async Task SaveRowDebouncedAsync(DayRowViewModel row, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SaveDebounce, cts.Token);
            // Deleted while the edit waited — nothing left to save.
            if (!Days.Contains(row))
                return;

            // Snapshot the row on the UI thread, then run the synchronous SQLite write on the pool.
            var version = row.EditVersion;
            var entity = row.ToEntity();
            await Task.Run(() => _editor.UpdateDayAsync(entity, CancellationToken.None));
            row.MarkSaved(version);

            // The session caches the active day (incl. its DefaultDiscipline): re-point it so the other pages
            // (Participants, Groups, results) pick up the change at once — they refresh on SessionChanged.
            if (row.IsActive)
                _session.UpdateCurrentDay(entity);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Refused (the day was closed meanwhile, e.g. on another station) or failed: show what the store holds.
            _log.Error($"Save day {row.Number}", ex);
            row.DiscardEdits();
            await LoadCoreAsync(merge: true);
        }
        finally
        {
            if (_saveTimers.TryGetValue(row.Id, out var current) && ReferenceEquals(current, cts))
                _saveTimers.Remove(row.Id);
        }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }

    /// <summary>
    /// Closes or opens a day straight from the grid — the one screen that shows every day at once, so a
    /// judge can see and set which days are finished. Same rule as the lock next to the day selector:
    /// closing is immediate, opening asks first, since that is when the protection is dropped.
    /// </summary>
    // Guards against a second click landing while the first toggle is still in flight — the command
    // awaits a confirmation dialog and a database write, and the button stays clickable throughout.
    private bool _togglingDayLock;

    [RelayCommand]
    private async Task ToggleDayLockAsync(DayRowViewModel? row)
    {
        if (row is null || _togglingDayLock)
            return;

        _togglingDayLock = true;
        try
        {
            await ToggleDayLockCoreAsync(row);
        }
        finally
        {
            _togglingDayLock = false;
        }
    }

    private async Task ToggleDayLockCoreAsync(DayRowViewModel row)
    {
        // An edit made right before closing the day lands first — closed, the day would refuse it.
        await _pendingSaves.WaitAsync();
        if (row.IsLocked)
        {
            var confirm = new ConfirmDialogViewModel(
                Localization,
                "DayLock.Confirm.Title",
                "DayLock.Confirm.Message",
                confirmKey: "DayLock.Confirm.Ok")
            {
                MessageArgs = [row.Number]
            };

            if (!await _dialogs.ConfirmAsync(confirm))
                return;
        }

        // Flip from what the database says, not from the row: the confirmation above is awaited, and the
        // row may have been re-read (or changed on another page) while the dialog was open.
        var updated = await _busy.RunAsync(() => _editor.ToggleDayLockedAsync(row.Id));
        if (updated is null)
            return;

        row.IsLocked = updated.IsLocked;
        _log.Action(updated.IsLocked
            ? $"Day {updated.Number} closed for editing"
            : $"Day {updated.Number} opened for editing");

        // The session caches the active day, and every per-day page reads the lock off it — re-point it
        // so those pages (and their day selectors) follow immediately.
        if (row.IsActive)
            _session.UpdateCurrentDay(updated);
    }

    // The grid's delete button binds to this command. A plain click asks for confirmation first;
    // Ctrl+Click routes through DeleteDayNoConfirm. The Delete key on a selected row is routed
    // through DeleteSelectedDayAsync in the view.
    [RelayCommand]
    private Task DeleteDayAsync(DayRowViewModel? row) => RemoveDayAsync(row, skipConfirm: false);

    public Task DeleteDayNoConfirmAsync(DayRowViewModel? row) => RemoveDayAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected day (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedDayAsync(bool skipConfirm) => RemoveDayAsync(SelectedRow, skipConfirm);

    private async Task RemoveDayAsync(DayRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        // Keep at least one day, and never delete the active one.
        if (Days.Count <= 1 || row.IsActive)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "CompetitionDays.Delete.ConfirmTitle",
                messageKey: "CompetitionDays.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        // Remove from the grid immediately and run the SQLite delete in the background — the user
        // never waits on the DB for a delete. If the removed row was the focused one, move the
        // selection onto its neighbour so the grid keeps a sensible focus instead of clearing it.
        // (Day numbers are stable ids assigned on add, not positions, so nothing renumbers here.)
        if (ReferenceEquals(SelectedRow, row))
            SelectedRow = GridSelection.NeighbourAfterRemoval(Days, row);
        Days.Remove(row);
        if (_saveTimers.Remove(row.Id, out var pending))
            pending.Cancel();

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteDayAsync(id));
        // The day's steps (on every page) point at rows that are gone with it.
        _undo.ForgetDay(id);

        // The confirmation modal stole keyboard focus to the overlay; pull it back to the grid
        // (now on the new selected row) so focus doesn't end up on the top menu.
        if (confirmed)
            RequestGridFocus();
    }
}
