using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Disciplines;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// The central participant database. A participant's identity is competition-level; group and chip
/// are per-day. The day picker carries a leading "Мандатка" (roster) option that aggregates every
/// day — one row per participant with a group column per day — alongside the per-day options.
///
/// Selecting a real day switches the session's current day (so other day-scoped pages follow);
/// selecting Мандатка does NOT touch the session — it is a view-only aggregation. The page remembers
/// its own choice (it is a singleton VM), so returning to it shows the last mode the user picked. If
/// the session day changes from another page while this page is on Мандатка, it stays on Мандатка and
/// only refreshes the roster.
/// </summary>
public sealed partial class ParticipantsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public ParticipantsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDisciplineStrategyProvider strategies,
        IDialogService dialogs)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        _strategies = strategies;
        _dialogs = dialogs;
        // Singleton VM: reload when the competition/day changes. SessionChanged may be raised on a
        // pool thread (session writes run inside RunAsync), so marshal onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.Participants";
    public override string TitleKey => "Page.Participants.Title";
    public override string TextKey => "Page.Participants.Text";

    /// <summary>Day-mode rows (the selected real day's participants).</summary>
    public ObservableCollection<ParticipantDayRowViewModel> Participants { get; } = [];

    /// <summary>Roster ("Мандатка") rows (one per participant, all days aggregated).</summary>
    public ObservableCollection<ParticipantRosterRowViewModel> Roster { get; } = [];

    /// <summary>Selectable options: a leading roster sentinel, then each real day.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    /// <summary>
    /// The competition's days, in order — the source of truth for the roster's per-day columns. Kept
    /// independent of the roster rows so the columns can be built even when the roster is empty.
    /// </summary>
    public IReadOnlyList<EventDay> RosterDays { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRosterMode))]
    [NotifyPropertyChangedFor(nameof(IsDayMode))]
    private DayOption? _selectedDay;

    /// <summary>True while the roster ("Мандатка") aggregate is shown.</summary>
    public bool IsRosterMode => SelectedDay?.IsRoster ?? false;

    /// <summary>True while a real day's participants are shown (the inverse of roster mode).</summary>
    public bool IsDayMode => !IsRosterMode;

    /// <summary>Day picker is always shown — the roster option alone makes more than one entry.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

    /// <summary>The team column is shown only when the current real day's discipline uses it (rogaine).</summary>
    public bool ShowTeamColumn =>
        _session.CurrentDay is { } day && _strategies.For(day.DefaultDiscipline)
            .UsesParticipantColumn(BusinessLogic.Enums.ParticipantColumn.Team);

    /// <summary>Raised when the set of visible columns (team) or the roster day columns may have changed.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Raised when the roster's day set changed, so the view rebuilds its per-day columns.</summary>
    public event EventHandler? RosterColumnsChanged;

    // True while LoadAsync syncs SelectedDay, so the setter does not act on the programmatic change.
    private bool _syncingDay;

    // Remembers that the user chose Мандатка, so a reload (incl. one triggered by another page's day
    // switch) keeps the page on the roster rather than snapping back to the session day.
    private bool _rosterChosen;

    /// <summary>Reloads the page for the current selection. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var hasEvent = _session.CurrentEvent is not null;
        var days = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDaysAsync())
            : (IReadOnlyList<EventDay>)[];
        RosterDays = days;

        _syncingDay = true;
        try
        {
            RebuildDayOptions(days);

            // Resolve the selected option: honour a remembered roster choice, else follow the
            // session day, else the first real day (or the roster option when no day exists).
            if (_rosterChosen)
            {
                SelectedDay = DayOptions.FirstOrDefault(o => o.IsRoster);
            }
            else
            {
                var current = _session.CurrentDay?.Number;
                SelectedDay =
                    DayOptions.FirstOrDefault(o => !o.IsRoster && o.Number == current)
                    ?? DayOptions.FirstOrDefault(o => !o.IsRoster)
                    ?? DayOptions.FirstOrDefault();
            }
        }
        finally
        {
            _syncingDay = false;
        }

        OnPropertyChanged(nameof(ShowDaySelector));
        await ReloadContentAsync();
    }

    // Rebuilds the day options only when the day set changed, so the ComboBox keeps a valid
    // SelectedItem reference across reloads. The roster sentinel is always first.
    private void RebuildDayOptions(IReadOnlyList<EventDay> days)
    {
        if (SameDays(days))
            return;

        DayOptions.Clear();
        DayOptions.Add(DayOption.Roster(Localization, "Participants.Roster"));
        foreach (var day in days)
            DayOptions.Add(new DayOption(day, Localization));
    }

    private bool SameDays(IReadOnlyList<EventDay> days)
    {
        // First option is always the roster sentinel; the rest must match the day numbers in order.
        if (DayOptions.Count != days.Count + 1)
            return false;
        for (var i = 0; i < days.Count; i++)
            if (DayOptions[i + 1].Number != days[i].Number)
                return false;
        return true;
    }

    // Loads the rows for the current mode (roster vs the selected real day).
    private async Task ReloadContentAsync()
    {
        ClearParticipantRows();
        ClearRosterRows();

        if (_session.CurrentEvent is null)
            return;

        if (IsRosterMode)
        {
            var roster = await _busy.RunAsync(() => _editor.GetParticipantRosterAsync());
            // The roster's group dropdowns need each day's groups; gather them once.
            var groupsByDay = await _busy.RunAsync(() => LoadGroupsByDayAsync());
            foreach (var row in roster)
                Roster.Add(CreateRosterRow(row, groupsByDay));
            RosterColumnsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_session.CurrentDay is not null)
        {
            var (rows, groupOptions) = await _busy.RunAsync(async () =>
            {
                var r = await _editor.GetParticipantDayRowsAsync();
                var g = await BuildGroupOptionsAsync(_session.CurrentDay!.Id);
                return (r, g);
            });
            foreach (var row in rows)
                Participants.Add(CreateDayRow(row, groupOptions));
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }

        OnPropertyChanged(nameof(ShowTeamColumn));
    }

    // Builds the "(none)" + day-group options list for a given day.
    private async Task<IReadOnlyList<GroupOption>> BuildGroupOptionsAsync(Guid dayId)
    {
        var groups = await _editor.GetGroupsForDayAsync(dayId);
        var options = new List<GroupOption>(groups.Count + 1)
        {
            new(null, string.Empty, Localization)
        };
        foreach (var g in groups)
            options.Add(new GroupOption(g.GroupId, g.Name, Localization));
        return options;
    }

    // For the roster: a per-day map of group options, so each cell offers its own day's groups.
    private async Task<Dictionary<Guid, IReadOnlyList<GroupOption>>> LoadGroupsByDayAsync()
    {
        var days = await _editor.GetDaysAsync();
        var map = new Dictionary<Guid, IReadOnlyList<GroupOption>>(days.Count);
        foreach (var day in days)
            map[day.Id] = await BuildGroupOptionsAsync(day.Id);
        return map;
    }

    private ParticipantDayRowViewModel CreateDayRow(ParticipantDayRow row, IReadOnlyList<GroupOption> groupOptions)
        => new(row, groupOptions, Localization, _strategies, RequestRowSave, LeaveDay);

    private ParticipantRosterRowViewModel CreateRosterRow(
        ParticipantRosterRow row,
        IReadOnlyDictionary<Guid, IReadOnlyList<GroupOption>> groupsByDay)
    {
        var cells = new List<RosterDayCellViewModel>(row.Days.Count);
        foreach (var cell in row.Days)
        {
            var options = groupsByDay.TryGetValue(cell.DayId, out var o)
                ? o
                : [new GroupOption(null, string.Empty, Localization)];
            cells.Add(new RosterDayCellViewModel(row.ParticipantId, cell, options, Localization, RequestCellGroupChange));
        }
        return new ParticipantRosterRowViewModel(row, cells, Localization, RequestRosterRowSave);
    }

    // Driven by the day ComboBox. A real day switches the session (so other pages follow); the roster
    // option is view-only and never touches the session, but is remembered for next time.
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value is null)
            return;

        if (value.IsRoster)
        {
            _rosterChosen = true;
            _ = ReloadContentAsync();
            return;
        }

        _rosterChosen = false;
        if (value.Day is null || _session.CurrentDay?.Number == value.Number)
        {
            // Same day already active (e.g. coming back from roster) — just reload this page's rows.
            _ = ReloadContentAsync();
            return;
        }

        _ = SwitchDayAsync(value.Day);
    }

    private async Task SwitchDayAsync(EventDay day)
    {
        // Switching the session day re-raises SessionChanged → LoadAsync, which reloads the rows.
        await _busy.RunAsync(() => _session.SetCurrentDayAsync(day));
    }

    [RelayCommand]
    private async Task AddParticipantAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        if (IsRosterMode)
        {
            // Roster add: create a participant that starts out not participating on any day, then
            // append the aggregate row. Reuse the cached per-day group options for its cells.
            var roster = await _busy.RunAsync(() => _editor.AddRosterParticipantAsync());
            if (roster is null)
                return;
            var groupsByDay = await _busy.RunAsync(() => LoadGroupsByDayAsync());
            Roster.Add(CreateRosterRow(roster, groupsByDay));
            // The per-day columns are derived from the day set; raise so they appear when this was the
            // first row added to a previously empty roster.
            RosterColumnsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_session.CurrentDay is null)
            return;

        var groupOptions = await _busy.RunAsync(() => BuildGroupOptionsAsync(_session.CurrentDay!.Id));
        var row = await _busy.RunAsync(() => _editor.AddParticipantToDayAsync());
        if (row is null)
        {
            // The day has no groups yet; a participant cannot be added without a group to put them in.
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization, "Participants.NoGroups.Title", "Participants.NoGroups.Message",
                confirmKey: "Common.Ok", cancelKey: "Common.Ok"));
            return;
        }
        Participants.Add(CreateDayRow(row, groupOptions));
    }

    // A day row whose group was set to "не участвує": drop it from the grid and delete the link in the
    // background (cascade-deletes the participant when this was their last day).
    private void LeaveDay(ParticipantDayRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        if (ReferenceEquals(SelectedParticipant, row))
            SelectedParticipant = GridSelection.NeighbourAfterRemoval(Participants, row);
        Participants.Remove(row);

        var (linkId, participantId) = (row.Id, row.ParticipantId);
        _ = Task.Run(() => _editor.RemoveParticipantFromDayAsync(linkId, participantId));
    }

    // The grid's delete button binds to this command (plain click confirms; Ctrl+Click skips).
    [RelayCommand]
    private Task DeleteParticipantAsync(ParticipantDayRowViewModel? row) => RemoveAsync(row, skipConfirm: false);

    public Task DeleteParticipantNoConfirmAsync(ParticipantDayRowViewModel? row) => RemoveAsync(row, skipConfirm: true);

    public Task DeleteSelectedParticipantAsync(bool skipConfirm) => RemoveAsync(SelectedParticipant, skipConfirm);

    [ObservableProperty]
    private ParticipantDayRowViewModel? _selectedParticipant;

    private async Task RemoveAsync(ParticipantDayRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.Delete.ConfirmTitle",
                messageKey: "Participants.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        // Remove from the grid immediately; run the SQLite delete in the background.
        if (ReferenceEquals(SelectedParticipant, row))
            SelectedParticipant = GridSelection.NeighbourAfterRemoval(Participants, row);
        Participants.Remove(row);

        var (linkId, participantId) = (row.Id, row.ParticipantId);
        _ = Task.Run(() => _editor.RemoveParticipantFromDayAsync(linkId, participantId));

        if (confirmed)
            RequestGridFocus();
    }

    // ── Day-row autosave (debounced per row) ──────────────────────────────────────────────────
    private void RequestRowSave(ParticipantDayRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token);
    }

    private async Task SaveRowDebouncedAsync(ParticipantDayRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var dto = row.ToRow();
            await Task.Run(() => _editor.UpdateParticipantDayRowAsync(dto, token), token);
        }
        catch (OperationCanceledException) { }
        catch { /* never crash the UI over an autosave */ }
    }

    // ── Roster identity autosave (debounced per row) ──────────────────────────────────────────
    private void RequestRosterRowSave(ParticipantRosterRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.ParticipantId, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.ParticipantId] = cts;
        _ = SaveRosterRowDebouncedAsync(row, cts.Token);
    }

    private async Task SaveRosterRowDebouncedAsync(ParticipantRosterRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var dto = new ParticipantRosterRow(
                row.ParticipantId,
                (row.Surname ?? string.Empty).Trim(),
                (row.Name ?? string.Empty).Trim(),
                (row.Number ?? string.Empty).Trim(),
                (row.Rank ?? string.Empty).Trim(),
                (row.Coach ?? string.Empty).Trim(),
                row.BirthDate,
                []);
            await Task.Run(() => _editor.UpdateParticipantIdentityAsync(dto, token), token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ── Roster per-day group change ───────────────────────────────────────────────────────────
    // Picking a real group joins the day (or changes the group); picking "не участвує" (null) leaves
    // the day. Either way the write runs in the background and the cell's membership is updated.
    private void RequestCellGroupChange(RosterDayCellViewModel cell)
    {
        _ = ApplyCellGroupChangeAsync(cell);
    }

    private async Task ApplyCellGroupChangeAsync(RosterDayCellViewModel cell)
    {
        var participantId = cell.ParticipantId;
        var dayId = cell.DayId;
        var groupId = cell.SelectedGroup.Id;
        try
        {
            if (groupId is null)
            {
                // "не участвує": leave the day. Nothing to do if not currently a member.
                if (cell.LinkId is { } linkId)
                    await Task.Run(() => _editor.RemoveParticipantFromDayAsync(linkId, participantId));
                cell.ApplyMembership(isMember: false, linkId: null);
            }
            else
            {
                var linkId = await Task.Run(() => _editor.SetParticipantDayGroupAsync(participantId, dayId, groupId));
                cell.ApplyMembership(isMember: true, linkId: linkId);
            }
        }
        catch { }
    }

    private void ClearParticipantRows()
    {
        Participants.Clear();
        SelectedParticipant = null;
    }

    private void ClearRosterRows() => Roster.Clear();

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}
