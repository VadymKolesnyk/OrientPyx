using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Disciplines;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.BusinessLogic.Services;
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
    private readonly IAppStore _appStore;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly IDialogService _dialogs;
    private readonly IParticipantImportFlow _importFlow;
    private readonly IEntryFeeCalculator _entryFeeCalculator;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();
    // Serialises chip-reassignment prompts so a collapsed-block edit that fans a chip out to several
    // days never stacks overlapping confirm dialogs (the overlay shows one dialog at a time).
    private readonly SemaphoreSlim _chipGate = new(1, 1);

    public ParticipantsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        IAppStore appStore,
        ISessionService session,
        IBusyService busy,
        IDisciplineStrategyProvider strategies,
        IDialogService dialogs,
        IParticipantImportFlow importFlow,
        IEntryFeeCalculator entryFeeCalculator,
        ITableLayoutStore layoutStore)
        : base(localization)
    {
        LayoutStore = layoutStore;
        _editor = editor;
        _appStore = appStore;
        _session = session;
        _busy = busy;
        _strategies = strategies;
        _dialogs = dialogs;
        _importFlow = importFlow;
        _entryFeeCalculator = entryFeeCalculator;
        // Singleton VM: reload when the competition/day changes. SessionChanged may be raised on a
        // pool thread (session writes run inside RunAsync), so marshal onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Re-localize the day-table column headers on a language switch (their text is baked into the
        // band model at build time, so the bands must be rebuilt — the RosterTable picks it up).
        Localization.PropertyChanged += (_, _) => RebuildDayBands();
    }

    public override string NavKey => "Nav.Participants";
    public override string TitleKey => "Page.Participants.Title";
    public override string TextKey => "Page.Participants.Text";

    /// <summary>The per-competition table-view store, bound by both participant tables so their column
    /// order/width/visibility persist to <c>events/&lt;id&gt;/views.json</c>.</summary>
    public ITableLayoutStore LayoutStore { get; }

    /// <summary>Day-mode rows (the selected real day's participants).</summary>
    public ObservableCollection<ParticipantDayRowViewModel> Participants { get; } = [];

    /// <summary>Roster ("Мандатка") rows (one per participant, all days aggregated).</summary>
    public ObservableCollection<ParticipantRosterRowViewModel> Roster { get; } = [];

    /// <summary>
    /// The roster's collapsible per-day field blocks, in display order. Groups span every day; chips
    /// span only the days a participant runs. Both start collapsed; state is in-memory only.
    /// </summary>
    public ObservableCollection<RosterFieldBlockViewModel> Blocks { get; } =
    [
        new(RosterField.Groups, "Participants.Roster.Block.Groups", _ => true),
        new(RosterField.Chips, "Participants.Roster.Block.Chips", c => c.IsMember),
        new(RosterField.StartTimes, "Participants.Roster.Block.StartTimes", c => c.IsMember),
        new(RosterField.OutOfCompetition, "Participants.Roster.Block.OutOfCompetition", c => c.IsMember)
    ];

    /// <summary>Selectable options: a leading roster sentinel, then each real day.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    /// <summary>
    /// The competition's rental-chip numbers, shared with the table so chip cells bold-red a number
    /// that is not in the rental database. Reloaded with the page and mutated live on a toggle.
    /// </summary>
    public RentalChipRegistry RentalChips { get; } = new();

    /// <summary>
    /// The competition's entry-fee discounts (FSOU-member first, then by name), shared with the table
    /// so it builds one checkbox column per discount. Bound to the roster SheetTable's Discounts; the
    /// day-mode table bakes them into DayBands. Reloaded with the page.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<EntryFeeDiscount> _discounts = [];

    /// <summary>Whether the raised-fee column is shown (mirrors CompetitionInfo.RaisedFeeEnabled).</summary>
    [ObservableProperty]
    private bool _raisedFeeEnabled;

    // The shared fee snapshot used to recompute each row's total live; rebuilt on every load from the
    // current info / group fees / chip prices / discounts / rental chips.
    private EntryFeeContext _feeContext = null!;

    /// <summary>
    /// The competition's region options, shared by every row's region dropdown: a leading "(none)"
    /// sentinel, then regions A→Z, then a trailing "+ new" sentinel. Region is competition-level, so
    /// (unlike per-day groups) one shared list matched by id is correct. Rebuilt on reload and when a
    /// region is created via "+ new". Reassigned to each live row so the new option appears everywhere.
    /// </summary>
    private IReadOnlyList<RegionOption> _regionOptions = [];
    private IReadOnlyList<ClubOption> _clubOptions = [];
    private IReadOnlyList<DusshOption> _dusshOptions = [];

    /// <summary>
    /// The application-level rank options, shared by every row's rank dropdown: a leading "(none)"
    /// sentinel then the ranks in their configured order. Rank is stored as text, so (unlike region/club)
    /// it has no "+ new" option and a row whose stored value is unknown prepends its own one-off option.
    /// Rebuilt on reload (the Ranks page may have changed the list since this page was last shown).
    /// Built in <see cref="RefreshRankOptionsAsync"/> before any row is created.
    /// </summary>
    private IReadOnlyList<RankOption> _rankOptions = [];

    // Serialises "+ new" region/club/ДЮСШ prompts so two overlapping add-modals never stack.
    private readonly SemaphoreSlim _regionGate = new(1, 1);
    private readonly SemaphoreSlim _clubGate = new(1, 1);
    private readonly SemaphoreSlim _dusshGate = new(1, 1);

    /// <summary>
    /// The competition's days, in order — the source of truth for the roster's per-day columns. Kept
    /// independent of the roster rows so the columns can be built even when the roster is empty.
    /// </summary>
    private IReadOnlyList<EventDay> _rosterDays = [];
    public IReadOnlyList<EventDay> RosterDays
    {
        get => _rosterDays;
        private set => SetProperty(ref _rosterDays, value);
    }

    /// <summary>
    /// The flat (non-banded) column model for the day-mode table, so it reuses the same custom
    /// <c>RosterTable</c> control. Rebuilt when the team column toggles or the language changes; the
    /// builder carries user-set widths forward.
    /// </summary>
    private IReadOnlyList<Controls.SheetBand> _dayBands = [];
    public IReadOnlyList<Controls.SheetBand> DayBands
    {
        get => _dayBands;
        private set => SetProperty(ref _dayBands, value);
    }

    private void RebuildDayBands()
        => DayBands = new Controls.DayColumnBuilder(Localization).Build(ShowTeamColumn, Discounts, RaisedFeeEnabled, _dayBands);

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

    /// <summary>Raised when the roster's day set changed, so the view rebuilds its per-day columns.</summary>
    public event EventHandler? RosterColumnsChanged;

    // True while LoadAsync syncs SelectedDay, so the setter does not act on the programmatic change.
    private bool _syncingDay;

    // Remembers that the user chose Мандатка, so a reload (incl. one triggered by another page's day
    // switch) keeps the page on the roster rather than snapping back to the session day. Defaults to
    // true so the page opens on the roster until the user picks a real day.
    private bool _rosterChosen = true;

    /// <summary>Reloads the page for the current selection. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var hasEvent = _session.CurrentEvent is not null;
        var days = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDaysAsync())
            : (IReadOnlyList<EventDay>)[];
        RosterDays = days;

        // Refresh the rental-chip set so cells highlight non-rental numbers against the current DB.
        await RefreshRentalChipsAsync(hasEvent);

        // Rebuild the shared region/club options ("(none)" + A→Z + "+ new") for every row's dropdown.
        await RefreshRegionOptionsAsync(hasEvent);
        await RefreshClubOptionsAsync(hasEvent);
        await RefreshDusshOptionsAsync(hasEvent);
        // Rank options come from the app database (shared across competitions), so they load regardless
        // of whether a competition is selected.
        await RefreshRankOptionsAsync();

        // Entry-fee inputs: the discount set + the fee snapshot used to compute each row's total.
        await RefreshFeeDataAsync(hasEvent);

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

    /// <summary>
    /// Imports participants from a UOF XML file (the view reads the file and hands over the decoded
    /// text). Runs the shared flow (parse → clear-vs-keep modal → import) and reloads on success.
    /// </summary>
    public async Task ImportFromXmlAsync(string xml)
    {
        if (await _importFlow.RunAsync(xml))
            await LoadAsync();
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
        }

        OnPropertyChanged(nameof(ShowTeamColumn));
        RebuildDayBands();
    }

    // Builds the day-group options list for a given day. The roster prepends the "(none)" / "не
    // участвує" sentinel (picking it leaves the day); the day grid passes includeNone: false so its
    // dropdown offers only real groups — a participant shown there is always a day member.
    private async Task<IReadOnlyList<GroupOption>> BuildGroupOptionsAsync(Guid dayId, bool includeNone = false)
    {
        var groups = await _editor.GetGroupsForDayAsync(dayId);
        var options = new List<GroupOption>(groups.Count + (includeNone ? 1 : 0));
        if (includeNone)
            options.Add(new GroupOption(null, string.Empty, Localization));
        foreach (var g in groups)
            options.Add(new GroupOption(g.GroupId, g.Name, Localization));
        return options;
    }

    // For the roster: a per-day map of group options, so each cell offers its own day's groups. The
    // roster keeps the "(none)" sentinel so a cell can mark a member as having no group / leave the day.
    private async Task<Dictionary<Guid, IReadOnlyList<GroupOption>>> LoadGroupsByDayAsync()
    {
        var days = await _editor.GetDaysAsync();
        var map = new Dictionary<Guid, IReadOnlyList<GroupOption>>(days.Count);
        foreach (var day in days)
            map[day.Id] = await BuildGroupOptionsAsync(day.Id, includeNone: true);
        return map;
    }

    private ParticipantDayRowViewModel CreateDayRow(ParticipantDayRow row, IReadOnlyList<GroupOption> groupOptions)
        => new(row, groupOptions, _regionOptions, _clubOptions, _dusshOptions, _rankOptions, Discounts, _feeContext, Localization, _strategies,
            RequestRowSave, LeaveDay, RequestDayRowChipChange,
            RequestDayRowRegionChange, RequestDayRowAddRegion,
            RequestDayRowClubChange, RequestDayRowAddClub,
            RequestDayRowDusshChange, RequestDayRowAddDussh,
            RequestRowRaisedFeeChange, RequestRowDiscountChange);

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
            cells.Add(new RosterDayCellViewModel(row.ParticipantId, cell, options, Localization, RequestCellGroupChange, RequestCellChipChange, RequestCellStartTimeChange, RequestCellOutOfCompetitionChange));
        }
        return new ParticipantRosterRowViewModel(row, cells, _regionOptions, _clubOptions, _dusshOptions, _rankOptions, Discounts, _feeContext, Localization,
            RequestRosterRowSave,
            RequestRosterRowRegionChange, RequestRosterRowAddRegion,
            RequestRosterRowClubChange, RequestRosterRowAddClub,
            RequestRosterRowDusshChange, RequestRosterRowAddDussh,
            RequestRosterRaisedFeeChange, RequestRosterDiscountChange);
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

    /// <summary>The roster ("Мандатка") row the table has selected (for keyboard delete).</summary>
    [ObservableProperty]
    private ParticipantRosterRowViewModel? _selectedRosterRow;

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

    // ── Roster ("Мандатка") delete ────────────────────────────────────────────────────────────
    // The roster table binds this command (the trailing delete button) and raises a keyboard delete
    // request. A roster participant may run on zero days, so we hard-delete the participant entirely
    // rather than removing a day link.
    [RelayCommand]
    private Task DeleteRosterParticipantAsync(ParticipantRosterRowViewModel? row) => RemoveRosterAsync(row, skipConfirm: false);

    public Task DeleteRosterParticipantNoConfirmAsync(ParticipantRosterRowViewModel? row) => RemoveRosterAsync(row, skipConfirm: true);

    public Task DeleteSelectedRosterAsync(bool skipConfirm) => RemoveRosterAsync(SelectedRosterRow, skipConfirm);

    private async Task RemoveRosterAsync(ParticipantRosterRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        if (!skipConfirm)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.Delete.ConfirmTitle",
                messageKey: "Participants.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.ParticipantId, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.ParticipantId);
        }

        // Remove from the grid immediately; run the SQLite delete in the background.
        if (ReferenceEquals(SelectedRosterRow, row))
            SelectedRosterRow = GridSelection.NeighbourAfterRemoval(Roster, row);
        Roster.Remove(row);

        var participantId = row.ParticipantId;
        _ = Task.Run(() => _editor.DeleteParticipantAsync(participantId));
    }

    // ── Day-row chip change (conflict-resolved, not part of the debounced save) ─────────────────
    // A chip edit on the day grid may collide with another competitor on the same day. Confirm the
    // reassignment; on yes, the editor moves the chip (clearing the previous holder); on no, the cell
    // reverts to the last committed value. Resolved off the debounced row save so the prompt is shown
    // exactly when the edit commits (the chip box updates on lost focus).
    private void RequestDayRowChipChange(ParticipantDayRowViewModel row)
    {
        _ = ResolveDayRowChipAsync(row);
    }

    private async Task ResolveDayRowChipAsync(ParticipantDayRowViewModel row)
    {
        if (_session.CurrentDay is not { } day)
            return;

        var newChip = (row.Chip ?? string.Empty).Trim();
        var (participantId, dayId) = (row.ParticipantId, day.Id);

        await _chipGate.WaitAsync();
        try
        {
            var ok = await ResolveChipReassignmentAsync(participantId, dayId, newChip);
            if (!ok)
            {
                // Rejected: restore the previous chip without re-triggering this handler.
                row.SetChipSilently(row.CommittedChip);
                return;
            }

            row.MarkChipCommitted(newChip);
            await Task.Run(() => _editor.ReassignParticipantDayChipAsync(participantId, dayId, newChip));
            // The other holder (if any) had their chip cleared in the DB; refresh that row in the grid.
            ClearChipOnConflictingDayRow(participantId, newChip);
        }
        finally
        {
            _chipGate.Release();
        }
    }

    // After a reassignment, drop the chip from whatever OTHER day-grid row was showing it, so the UI
    // matches the DB (only one competitor holds a chip per day).
    private void ClearChipOnConflictingDayRow(Guid keepParticipantId, string chip)
    {
        if (chip.Length == 0)
            return;
        foreach (var other in Participants)
        {
            if (other.ParticipantId == keepParticipantId)
                continue;
            if (string.Equals((other.Chip ?? string.Empty).Trim(), chip, StringComparison.OrdinalIgnoreCase))
            {
                other.SetChipSilently(string.Empty);
                other.MarkChipCommitted(string.Empty);
            }
        }
    }

    // Shared confirm step for a chip reassignment (day grid and roster). Returns true when the chip is
    // free or the user confirmed taking it; false when another competitor holds it and the user
    // declined. A blank chip is always free.
    private async Task<bool> ResolveChipReassignmentAsync(Guid participantId, Guid dayId, string chip)
    {
        if (chip.Length == 0)
            return true;

        var holder = await _busy.RunAsync(() => _editor.FindChipHolderAsync(dayId, chip, participantId));
        if (holder is null)
            return true;

        var who = string.IsNullOrWhiteSpace(holder) ? Localization.Get("Participants.Chip.UnnamedHolder") : holder;
        var dialog = new ConfirmDialogViewModel(
            Localization,
            titleKey: "Participants.Chip.Reassign.Title",
            messageKey: "Participants.Chip.Reassign.Message",
            confirmKey: "Participants.Chip.Reassign.Confirm",
            cancelKey: "Common.Cancel")
        {
            MessageArgs = [chip, who]
        };
        return await _dialogs.ConfirmAsync(dialog);
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
                (row.FullName ?? string.Empty).Trim(),
                (row.Number ?? string.Empty).Trim(),
                (row.Rank ?? string.Empty).Trim(),
                (row.Coach ?? string.Empty).Trim(),
                row.BirthDate,
                row.SelectedRegion.Id,
                row.SelectedRegion.Label,
                row.SelectedClub.Id,
                row.SelectedClub.Label,
                row.SelectedDussh.Id,
                row.SelectedDussh.Label,
                (row.Representative ?? string.Empty).Trim(),
                (row.FsouCode ?? string.Empty).Trim(),
                row.IsFsouMember,
                (row.Payment ?? string.Empty).Trim(),
                // Fee fields are persisted through their own callbacks, not the identity save; pass
                // through the row's current values so the DTO is well-formed (the editor ignores them).
                row.PaysRaisedFee,
                [],
                0m,
                []);
            await Task.Run(() => _editor.UpdateParticipantIdentityAsync(dto, token), token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ── Roster block collapse/expand ──────────────────────────────────────────────────────────
    // Flips a block between its merged (collapsed) and per-day (expanded) views. The view rebuilds
    // the roster's per-day columns in response to RosterColumnsChanged.
    [RelayCommand]
    private void ToggleBlock(RosterFieldBlockViewModel? block)
    {
        if (block is null)
            return;
        block.IsCollapsed = !block.IsCollapsed;
        RosterColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Roster per-day group change ───────────────────────────────────────────────────────────
    // Picking a real group joins the day (or changes the group); picking "не участвує" (null) leaves
    // the day. Either way the write runs in the background and the cell's membership is updated.
    private void RequestCellGroupChange(RosterDayCellViewModel cell)
    {
        _ = ApplyCellGroupChangeAsync(cell);
    }

    // ── Roster per-day chip change ────────────────────────────────────────────────────────────
    // Editing a member day's chip (expanded cell, or a collapsed all-days edit) may collide with
    // another competitor on that day: confirm the reassignment, then persist (clearing the previous
    // holder) or revert. Only member days carry a chip, so there is no membership change to apply here.
    private void RequestCellChipChange(RosterDayCellViewModel cell)
    {
        if (!cell.IsMember)
            return;
        _ = ResolveCellChipAsync(cell);
    }

    // Start time / out-of-competition have no uniqueness rule, so they persist directly in the
    // background (no confirm flow). Member-only — a non-member cell is ignored.
    private void RequestCellStartTimeChange(RosterDayCellViewModel cell)
    {
        if (!cell.IsMember)
            return;
        var (participantId, dayId, startTime) = (cell.ParticipantId, cell.DayId, cell.StartTime);
        _ = Task.Run(() => _editor.SetParticipantDayStartTimeAsync(participantId, dayId, startTime));
    }

    private void RequestCellOutOfCompetitionChange(RosterDayCellViewModel cell)
    {
        if (!cell.IsMember)
            return;
        var (participantId, dayId, value) = (cell.ParticipantId, cell.DayId, cell.OutOfCompetition);
        _ = Task.Run(() => _editor.SetParticipantDayOutOfCompetitionAsync(participantId, dayId, value));
    }

    private async Task ResolveCellChipAsync(RosterDayCellViewModel cell)
    {
        var newChip = (cell.Chip ?? string.Empty).Trim();
        var (participantId, dayId) = (cell.ParticipantId, cell.DayId);

        await _chipGate.WaitAsync();
        try
        {
            var ok = await ResolveChipReassignmentAsync(participantId, dayId, newChip);
            if (!ok)
            {
                cell.SetChipSilently(cell.CommittedChip);
                return;
            }

            cell.MarkChipCommitted(newChip);
            await Task.Run(() => _editor.ReassignParticipantDayChipAsync(participantId, dayId, newChip));
            // Mirror the DB: drop the chip from any other roster cell on the SAME day that showed it.
            ClearChipOnConflictingRosterCell(participantId, dayId, newChip);
        }
        finally
        {
            _chipGate.Release();
        }
    }

    // After a roster reassignment, clear the chip from the other participant's cell on the same day.
    private void ClearChipOnConflictingRosterCell(Guid keepParticipantId, Guid dayId, string chip)
    {
        if (chip.Length == 0)
            return;
        foreach (var row in Roster)
        {
            if (row.ParticipantId == keepParticipantId)
                continue;
            foreach (var cell in row.Days)
            {
                if (cell.DayId == dayId && cell.IsMember &&
                    string.Equals((cell.Chip ?? string.Empty).Trim(), chip, StringComparison.OrdinalIgnoreCase))
                {
                    cell.SetChipSilently(string.Empty);
                    cell.MarkChipCommitted(string.Empty);
                }
            }
        }
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
                // Joining a day may have copied a chip from another member day (see
                // ParticipantRosterRowViewModel.CopyChipOnJoin); ApplyMembership ran with saves
                // suppressed, so persist the (possibly copied) chip now.
                var chip = (cell.Chip ?? string.Empty).Trim();
                if (chip.Length > 0)
                    await Task.Run(() => _editor.SetParticipantDayChipAsync(participantId, dayId, chip));
            }
        }
        catch { }
    }

    // ── Region options + per-row region edit ──────────────────────────────────────────────────────
    // Rebuilds the shared region options list from the current DB. Called on reload and after a "+ new"
    // create, so the list is always: "(none)", regions A→Z, "+ new".
    private async Task RefreshRegionOptionsAsync(bool hasEvent)
    {
        var regions = hasEvent
            ? await _busy.RunAsync(() => _editor.GetRegionsAsync())
            : (IReadOnlyList<BusinessLogic.Entities.Region>)[];
        _regionOptions = BuildRegionOptions(regions);
    }

    private IReadOnlyList<RegionOption> BuildRegionOptions(IReadOnlyList<BusinessLogic.Entities.Region> regions)
    {
        var options = new List<RegionOption>(regions.Count + 2) { RegionOption.None(Localization) };
        foreach (var r in regions.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            options.Add(new RegionOption(r.Id, r.Name, Localization));
        options.Add(RegionOption.Add(Localization));
        return options;
    }

    // A real region / "(none)" was picked on a row: persist the participant's region in the background.
    private void RequestDayRowRegionChange(ParticipantDayRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantRegionAsync(row.ParticipantId, row.SelectedRegion.Id));

    private void RequestRosterRowRegionChange(ParticipantRosterRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantRegionAsync(row.ParticipantId, row.SelectedRegion.Id));

    // "+ new" was picked: prompt for a name, create the region, refresh every row's options, and
    // select the new region on the originating row. On cancel, revert the row to its previous region.
    private void RequestDayRowAddRegion(ParticipantDayRowViewModel row)
        => _ = AddRegionForRowAsync(
            select: id => SelectRegionOnDayRow(row, id),
            revert: () => row.SetRegionSilently(row.CommittedRegion));

    private void RequestRosterRowAddRegion(ParticipantRosterRowViewModel row)
        => _ = AddRegionForRowAsync(
            select: id => SelectRegionOnRosterRow(row, id),
            revert: () => row.SetRegionSilently(row.CommittedRegion));

    private async Task AddRegionForRowAsync(Action<Guid> select, Action revert)
    {
        if (_session.CurrentEvent is null)
        {
            revert();
            return;
        }

        await _regionGate.WaitAsync();
        try
        {
            var name = await _dialogs.ShowAddRegionAsync(new Dialogs.AddRegionViewModel(Localization));
            if (string.IsNullOrWhiteSpace(name))
            {
                revert();
                return;
            }

            var region = await _busy.RunAsync(() => _editor.AddRegionAsync(name));
            if (region is null)
            {
                revert();
                return;
            }

            // Rebuild the shared options so the new region appears in every row's dropdown, then push
            // the rebuilt list onto every live row and select the new region on the originating one.
            await RefreshRegionOptionsAsync(hasEvent: true);
            ApplyRegionOptionsToRows();
            select(region.Id);
        }
        finally
        {
            _regionGate.Release();
        }
    }

    // Reassigns the rebuilt shared options list onto every live row, preserving each row's current
    // selection by id. RegionOptions is set-once per row, so rebuild the rows' lists by recreating
    // the option set reference: each row holds the same shared list, so we re-seed selection by id.
    private void ApplyRegionOptionsToRows()
    {
        foreach (var row in Participants)
            row.ResetRegionOptions(_regionOptions);
        foreach (var row in Roster)
            row.ResetRegionOptions(_regionOptions);
    }

    private void SelectRegionOnDayRow(ParticipantDayRowViewModel row, Guid regionId)
    {
        var option = _regionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == regionId);
        if (option is not null)
        {
            row.SetRegionSilently(option);
            RequestDayRowRegionChange(row);
        }
    }

    private void SelectRegionOnRosterRow(ParticipantRosterRowViewModel row, Guid regionId)
    {
        var option = _regionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == regionId);
        if (option is not null)
        {
            row.SetRegionSilently(option);
            RequestRosterRowRegionChange(row);
        }
    }

    // ── Club options + per-row club edit (mirrors the region flow above) ───────────────────────────
    private async Task RefreshClubOptionsAsync(bool hasEvent)
    {
        var clubs = hasEvent
            ? await _busy.RunAsync(() => _editor.GetClubsAsync())
            : (IReadOnlyList<BusinessLogic.Entities.Club>)[];
        _clubOptions = BuildClubOptions(clubs);
    }

    private IReadOnlyList<ClubOption> BuildClubOptions(IReadOnlyList<BusinessLogic.Entities.Club> clubs)
    {
        var options = new List<ClubOption>(clubs.Count + 2) { ClubOption.None(Localization) };
        foreach (var c in clubs.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            options.Add(new ClubOption(c.Id, c.Name, Localization));
        options.Add(ClubOption.Add(Localization));
        return options;
    }

    private void RequestDayRowClubChange(ParticipantDayRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantClubAsync(row.ParticipantId, row.SelectedClub.Id));

    private void RequestRosterRowClubChange(ParticipantRosterRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantClubAsync(row.ParticipantId, row.SelectedClub.Id));

    private void RequestDayRowAddClub(ParticipantDayRowViewModel row)
        => _ = AddClubForRowAsync(
            select: id => SelectClubOnDayRow(row, id),
            revert: () => row.SetClubSilently(row.CommittedClub));

    private void RequestRosterRowAddClub(ParticipantRosterRowViewModel row)
        => _ = AddClubForRowAsync(
            select: id => SelectClubOnRosterRow(row, id),
            revert: () => row.SetClubSilently(row.CommittedClub));

    private async Task AddClubForRowAsync(Action<Guid> select, Action revert)
    {
        if (_session.CurrentEvent is null)
        {
            revert();
            return;
        }

        await _clubGate.WaitAsync();
        try
        {
            var name = await _dialogs.ShowAddClubAsync(new Dialogs.AddClubViewModel(Localization));
            if (string.IsNullOrWhiteSpace(name))
            {
                revert();
                return;
            }

            var club = await _busy.RunAsync(() => _editor.AddClubAsync(name));
            if (club is null)
            {
                revert();
                return;
            }

            await RefreshClubOptionsAsync(hasEvent: true);
            ApplyClubOptionsToRows();
            select(club.Id);
        }
        finally
        {
            _clubGate.Release();
        }
    }

    private void ApplyClubOptionsToRows()
    {
        foreach (var row in Participants)
            row.ResetClubOptions(_clubOptions);
        foreach (var row in Roster)
            row.ResetClubOptions(_clubOptions);
    }

    private void SelectClubOnDayRow(ParticipantDayRowViewModel row, Guid clubId)
    {
        var option = _clubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == clubId);
        if (option is not null)
        {
            row.SetClubSilently(option);
            RequestDayRowClubChange(row);
        }
    }

    private void SelectClubOnRosterRow(ParticipantRosterRowViewModel row, Guid clubId)
    {
        var option = _clubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == clubId);
        if (option is not null)
        {
            row.SetClubSilently(option);
            RequestRosterRowClubChange(row);
        }
    }

    // ── ДЮСШ options + per-row school edit (mirrors the region/club flow above) ─────────────────────
    private async Task RefreshDusshOptionsAsync(bool hasEvent)
    {
        var dusshes = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDusshesAsync())
            : (IReadOnlyList<BusinessLogic.Entities.Dussh>)[];
        _dusshOptions = BuildDusshOptions(dusshes);
    }

    private IReadOnlyList<DusshOption> BuildDusshOptions(IReadOnlyList<BusinessLogic.Entities.Dussh> dusshes)
    {
        var options = new List<DusshOption>(dusshes.Count + 2) { DusshOption.None(Localization) };
        foreach (var d in dusshes.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            options.Add(new DusshOption(d.Id, d.Name, Localization));
        options.Add(DusshOption.Add(Localization));
        return options;
    }

    private void RequestDayRowDusshChange(ParticipantDayRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantDusshAsync(row.ParticipantId, row.SelectedDussh.Id));

    private void RequestRosterRowDusshChange(ParticipantRosterRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantDusshAsync(row.ParticipantId, row.SelectedDussh.Id));

    private void RequestDayRowAddDussh(ParticipantDayRowViewModel row)
        => _ = AddDusshForRowAsync(
            select: id => SelectDusshOnDayRow(row, id),
            revert: () => row.SetDusshSilently(row.CommittedDussh));

    private void RequestRosterRowAddDussh(ParticipantRosterRowViewModel row)
        => _ = AddDusshForRowAsync(
            select: id => SelectDusshOnRosterRow(row, id),
            revert: () => row.SetDusshSilently(row.CommittedDussh));

    private async Task AddDusshForRowAsync(Action<Guid> select, Action revert)
    {
        if (_session.CurrentEvent is null)
        {
            revert();
            return;
        }

        await _dusshGate.WaitAsync();
        try
        {
            var name = await _dialogs.ShowAddDusshAsync(new Dialogs.AddDusshViewModel(Localization));
            if (string.IsNullOrWhiteSpace(name))
            {
                revert();
                return;
            }

            var dussh = await _busy.RunAsync(() => _editor.AddDusshAsync(name));
            if (dussh is null)
            {
                revert();
                return;
            }

            await RefreshDusshOptionsAsync(hasEvent: true);
            ApplyDusshOptionsToRows();
            select(dussh.Id);
        }
        finally
        {
            _dusshGate.Release();
        }
    }

    private void ApplyDusshOptionsToRows()
    {
        foreach (var row in Participants)
            row.ResetDusshOptions(_dusshOptions);
        foreach (var row in Roster)
            row.ResetDusshOptions(_dusshOptions);
    }

    private void SelectDusshOnDayRow(ParticipantDayRowViewModel row, Guid dusshId)
    {
        var option = _dusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == dusshId);
        if (option is not null)
        {
            row.SetDusshSilently(option);
            RequestDayRowDusshChange(row);
        }
    }

    private void SelectDusshOnRosterRow(ParticipantRosterRowViewModel row, Guid dusshId)
    {
        var option = _dusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == dusshId);
        if (option is not null)
        {
            row.SetDusshSilently(option);
            RequestRosterRowDusshChange(row);
        }
    }

    // ── Rank options (application-level, no "+ new") ───────────────────────────────────────────────
    // Rebuilds the shared rank options from the app database: "(none)" then the ranks in order. Rank is
    // stored as text, so there is no create flow here — ranks are managed on their own page.
    private async Task RefreshRankOptionsAsync()
    {
        var ranks = await _busy.RunAsync(() => _appStore.GetRanksAsync());
        var options = new List<RankOption>(ranks.Count + 1) { RankOption.None(Localization) };
        foreach (var r in ranks)
            if (!string.IsNullOrWhiteSpace(r.Name))
                options.Add(new RankOption(r.Name, Localization));
        _rankOptions = options;
    }

    // ── Entry-fee data (discounts + the recompute snapshot) ─────────────────────────────────────────
    // Loads the discount set and rebuilds the shared fee context from the current competition inputs.
    // The context lets each row recompute its total live (on a discount/raised-fee/group/chip change)
    // without a DB round-trip; the rows are still seeded with the server-precomputed total on load.
    private async Task RefreshFeeDataAsync(bool hasEvent)
    {
        if (!hasEvent)
        {
            Discounts = [];
            RaisedFeeEnabled = false;
            _feeContext = new EntryFeeContext(_entryFeeCalculator, null, [], [], [], []);
            return;
        }

        var info = await _busy.RunAsync(() => _editor.GetInfoAsync());
        var groups = await _busy.RunAsync(() => _editor.GetGroupsAsync());
        var chipPrices = await _busy.RunAsync(() => _editor.GetChipPriceOverridesAsync());
        var discounts = await _busy.RunAsync(() => _editor.GetEntryFeeDiscountsAsync());
        var rentalChips = await _busy.RunAsync(() => _editor.GetRentalChipsAsync());

        Discounts = discounts;
        RaisedFeeEnabled = info?.RaisedFeeEnabled ?? false;
        _feeContext = new EntryFeeContext(_entryFeeCalculator, info, groups, chipPrices, discounts, rentalChips);
    }

    // A row's raised-fee flag toggled: persist in the background (competition-level, all days).
    private void RequestRowRaisedFeeChange(ParticipantDayRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantPaysRaisedFeeAsync(row.ParticipantId, row.PaysRaisedFee));

    private void RequestRosterRaisedFeeChange(ParticipantRosterRowViewModel row)
        => _ = Task.Run(() => _editor.SetParticipantPaysRaisedFeeAsync(row.ParticipantId, row.PaysRaisedFee));

    // A row's discount checkbox toggled: persist the link add/remove in the background.
    private void RequestRowDiscountChange(ParticipantDayRowViewModel row, Guid discountId, bool on)
        => _ = Task.Run(() => _editor.SetParticipantDiscountAsync(row.ParticipantId, discountId, on));

    private void RequestRosterDiscountChange(ParticipantRosterRowViewModel row, Guid discountId, bool on)
        => _ = Task.Run(() => _editor.SetParticipantDiscountAsync(row.ParticipantId, discountId, on));

    // ── Rental-chip highlight + toggle ────────────────────────────────────────────────────────────
    private async Task RefreshRentalChipsAsync(bool hasEvent)
    {
        var chips = hasEvent
            ? await _busy.RunAsync(() => _editor.GetRentalChipsAsync())
            : (IReadOnlyList<BusinessLogic.Entities.RentalChip>)[];
        RentalChips.Reset(chips.Select(c => c.Number));
    }

    // Double-clicking a chip cell toggles its number in the rental database: a non-rental chip is
    // added, a rental one removed. The registry is updated optimistically so the cell's bold-red
    // highlight flips immediately; the DB write runs in the background.
    [RelayCommand]
    private void ToggleRentalChip(string? chip)
    {
        var number = (chip ?? string.Empty).Trim();
        if (number.Length == 0 || _session.CurrentEvent is null)
            return;

        var wasRental = RentalChips.Contains(number);
        if (wasRental)
            RentalChips.Remove(number);
        else
            RentalChips.Add(number);

        _ = Task.Run(() => _editor.ToggleRentalChipAsync(number));
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
