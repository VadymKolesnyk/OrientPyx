using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Table of the current competition's days. Each row can edit its date, venue and discipline,
/// and be made the active (current) day, which switches the running session.
/// Opened from the "Competition → Days" top menu.
/// </summary>
public sealed partial class CompetitionDaysViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IActivityLog _log;
    // Held directly rather than through UseDayLock: that binds the page to the SESSION's day, and this
    // page shows every day at once — each row's lock is its own.
    private readonly IDayLockService _dayLock;

    public CompetitionDaysViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore,
        IDayLockService dayLock,
        IActivityLog log)
        : base(localization)
    {
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
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
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
    /// The gate for a row action (save, renumber, delete): a closed day refuses, naming itself. The
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

    /// <summary>Reloads the day rows from the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        var activeNumber = _session.CurrentDay?.Number;
        // BD read runs off the UI thread; the collection is rebuilt afterwards on the UI thread.
        var (days, info) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var i = await _editor.GetInfoAsync();
            return (d, i);
        });

        var venuePlaceholder = info?.Venue?.Trim() ?? string.Empty;

        Days.Clear();
        SelectedRow = null;
        foreach (var day in days)
            Days.Add(new DayRowViewModel(day, day.Number == activeNumber, Localization, venuePlaceholder));
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

    [RelayCommand]
    private async Task SaveDayAsync(DayRowViewModel? row)
    {
        if (row is null || !await EnsureRowEditableAsync(row))
            return;

        var entity = row.ToEntity();
        await _busy.RunAsync(() => _editor.UpdateDayAsync(entity));
        row.MarkSaved();

        // The session caches the active day (incl. its DefaultDiscipline). When the edited row IS the
        // active day, re-point the session so other pages (Participants, Groups) pick up a discipline
        // change immediately — they read it from the session and refresh on SessionChanged. Without
        // this they keep the stale discipline until the app is restarted.
        if (row.IsActive)
            await _busy.RunAsync(() => _session.SetCurrentDayAsync(entity));
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

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteDayAsync(id));

        // The confirmation modal stole keyboard focus to the overlay; pull it back to the grid
        // (now on the new selected row) so focus doesn't end up on the top menu.
        if (confirmed)
            RequestGridFocus();
    }
}
