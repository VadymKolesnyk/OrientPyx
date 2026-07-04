using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A pre-applied day-grid filter the page can be opened with (e.g. from a dashboard tile that drills
/// into "participants without a chip"). Targets the day grid's chip / group column with an IsEmpty /
/// "no group" filter; <see cref="ParticipantQuickFilter.None"/> means open normally.
/// </summary>
public enum ParticipantQuickFilter
{
    None,
    WithoutChip,
    WithoutGroup,

    /// <summary>Still on course: no actual (chip) start, no finish, and no status yet.</summary>
    OnCourse,
}

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
    private readonly ICsvImportFlow _csvImportFlow;
    private readonly IParticipantExportFlow _exportFlow;
    private readonly IEntryFeeCalculator _entryFeeCalculator;
    private readonly IActivityLog _log;
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
        ICsvImportFlow csvImportFlow,
        IParticipantExportFlow exportFlow,
        IEntryFeeCalculator entryFeeCalculator,
        IActivityLog log,
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
        _csvImportFlow = csvImportFlow;
        _exportFlow = exportFlow;
        _entryFeeCalculator = entryFeeCalculator;
        _log = log;
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
        new(RosterField.OutOfCompetition, "Participants.Roster.Block.OutOfCompetition", c => c.IsMember),
        // Result blocks — collapsible per day like Groups/Chips. Score is dropped by the column builder
        // on days that don't score points.
        new(RosterField.ActualStart, "Participants.Col.ActualStart", c => c.IsMember),
        new(RosterField.Finish, "Participants.Col.Finish", c => c.IsMember),
        new(RosterField.ResultStatus, "Participants.Col.ResultStatus", c => c.IsMember),
        new(RosterField.Result, "Participants.Col.Result", c => c.IsMember),
        new(RosterField.Place, "Participants.Col.Place", c => c.IsMember),
        // Editable «Бонус» (cause) before the computed «Бали» (effect); both dropped on non-scoring days.
        new(RosterField.Bonus, "Participants.Col.Bonus", c => c.IsMember),
        new(RosterField.Score, "Participants.Col.Score", c => c.IsMember),
        // Ranking points («Очки»), shown on every day (any discipline can have a points rule assigned).
        new(RosterField.Points, "Participants.Col.Points", c => c.IsMember),
        // Awarded sports rank («Виконаний розряд», Додаток 89), computed per group (read-only).
        new(RosterField.AwardedRank, "Participants.Col.AwardedRank", c => c.IsMember)
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

    /// <summary>
    /// The system-info line shown on the right of each participants table's status bar: rental-chip
    /// totals (in rental / free) plus, in day mode, the day's member count. Recomputed whenever the
    /// rows or the rental set change. The status bar's row counts and per-column sums are owned by the
    /// table itself; this is the extra context the page supplies.
    /// </summary>
    [ObservableProperty]
    private string _statusInfo = string.Empty;

    // The shared fee snapshot used to recompute each row's total live; rebuilt on every load from the
    // current info / group fees / chip prices / discounts / rental chips.
    private EntryFeeContext _feeContext = null!;

    /// <summary>
    /// Set by the View: reports whether the «Стартовий внесок» (fee:total) column is currently visible on
    /// the active table. When it isn't, per-row fee recompute is pointless, so it's deferred — see
    /// <see cref="RefreshFeesIfVisible"/> and <see cref="OnFeeColumnShown"/>. Null ⇒ assume visible.
    /// </summary>
    public Func<bool>? IsFeeColumnVisible { get; set; }

    // True when a fee recompute was skipped because the total column was hidden; the rows' totals are
    // stale until the column is shown again (OnFeeColumnShown flushes it).
    private bool _feesDirty;

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
        private set
        {
            if (SetProperty(ref _rosterDays, value))
                OnPropertyChanged(nameof(CanEditStartOrder));
        }
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
        => DayBands = new Controls.DayColumnBuilder(Localization).Build(ShowTeamColumn, ShowScoreColumn, Discounts, RaisedFeeEnabled, _dayBands);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRosterMode))]
    [NotifyPropertyChangedFor(nameof(IsDayMode))]
    [NotifyPropertyChangedFor(nameof(CanEditStartOrder))]
    private DayOption? _selectedDay;

    /// <summary>True while the roster ("Мандатка") aggregate is shown.</summary>
    public bool IsRosterMode => SelectedDay?.IsRoster ?? false;

    /// <summary>True while a real day's participants are shown (the inverse of roster mode).</summary>
    public bool IsDayMode => !IsRosterMode;

    /// <summary>
    /// The single day the page is currently bound to, or null when there is no unambiguous day: a real day
    /// in day mode, or — in roster mode — the sole day of a single-day competition (the roster shows the
    /// whole competition, but with one day it still targets that day). Null when the roster spans multiple
    /// days (no single day to act on).
    /// </summary>
    private Guid? EffectiveDayId => IsDayMode
        ? SelectedDay?.Day?.Id
        : RosterDays.Count == 1 ? RosterDays[0].Id : null;

    /// <summary>True when a single day is unambiguously in view, gating the manual start-order action.</summary>
    public bool CanEditStartOrder => EffectiveDayId is not null;

    /// <summary>The day picker is shown only when the competition has more than one real day. With a
    /// single day there is nothing to switch between, so we hide it and always show the roster
    /// ("Мандатка"). (DayOptions carries a leading roster sentinel, so >2 means two or more real days.)</summary>
    public bool ShowDaySelector => DayOptions.Count > 2;

    // Cached per-day flag: true when the day's default discipline OR any group on the day (via its
    // DisciplineOverride) uses teams. Recomputed when the day's rows load (see ReloadContentAsync).
    private bool _dayUsesTeam;

    /// <summary>The team column is shown when the current real day's discipline uses it (rogaine) or
    /// any group on that day overrides to a team discipline.</summary>
    public bool ShowTeamColumn => _dayUsesTeam;

    // Cached roster-wide flag: true when ANY day (its default discipline or any group's override) uses
    // teams. The roster spans all days, so its team column appears whenever teams apply anywhere.
    private bool _rosterShowsTeam;

    /// <summary>True when the roster's team column should be shown (any day uses a team discipline).</summary>
    public bool RosterShowsTeam => _rosterShowsTeam;

    // Cached flags for the «Бали» (score) result column — true when the day (or any day, for the roster)
    // uses a point-scoring discipline (rogaine). Mirrors the team flags above.
    private bool _dayUsesScore;
    private bool _rosterShowsScore;

    /// <summary>True when the current day's discipline scores points — drives the day-grid «Бали» column.</summary>
    public bool ShowScoreColumn => _dayUsesScore;

    /// <summary>True when any day scores points — drives the roster's «Бали» column.</summary>
    public bool RosterShowsScore => _rosterShowsScore;

    /// <summary>Raised when the roster's day set changed, so the view rebuilds its per-day columns.</summary>
    public event EventHandler? RosterColumnsChanged;

    /// <summary>
    /// Raised after a reload that was opened with a pending quick filter, once the day grid is populated,
    /// so an already-attached view applies the matching column filter. The view ALSO consumes the pending
    /// filter on attach (see <see cref="ConsumePendingQuickFilter"/>) — the event covers the case where the
    /// view is already on screen (re-navigation), the attach path covers a first open (the page is loaded
    /// before it is shown, so this event would fire before the view subscribes).
    /// </summary>
    public event EventHandler<ParticipantQuickFilter>? QuickFilterRequested;

    // A filter the page should apply when next shown (set by RequestQuickFilter before LoadAsync). Forces
    // day mode on load so the chip/group column it targets is the current day's. Held until the view
    // consumes it (on attach or via the event), so a first open — where LoadAsync runs before the view
    // exists — still applies it.
    private ParticipantQuickFilter _pendingQuickFilter = ParticipantQuickFilter.None;

    /// <summary>
    /// Asks the page to open with a day-grid filter pre-applied (e.g. only chipless participants). Must be
    /// called before <see cref="LoadAsync"/>; the filter targets the current day's grid, so it forces day
    /// mode on load. <see cref="ParticipantQuickFilter.None"/> clears any pending request.
    /// </summary>
    public void RequestQuickFilter(ParticipantQuickFilter filter) => _pendingQuickFilter = filter;

    /// <summary>
    /// Returns the pending quick filter and clears it (so it applies once). Day-grid filters only apply in
    /// day mode; returns <see cref="ParticipantQuickFilter.None"/> in roster mode. Called by the view when
    /// it is shown.
    /// </summary>
    public ParticipantQuickFilter ConsumePendingQuickFilter()
    {
        var filter = _pendingQuickFilter;
        _pendingQuickFilter = ParticipantQuickFilter.None;
        return IsDayMode ? filter : ParticipantQuickFilter.None;
    }

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

            // A pending quick filter (from a dashboard drill-in) targets the current day's chip/group
            // column, so force day mode for the current day regardless of the remembered roster choice.
            if (_pendingQuickFilter != ParticipantQuickFilter.None)
            {
                _rosterChosen = false;
                var current = _session.CurrentDay?.Number;
                SelectedDay =
                    DayOptions.FirstOrDefault(o => !o.IsRoster && o.Number == current)
                    ?? DayOptions.FirstOrDefault(o => !o.IsRoster)
                    ?? DayOptions.FirstOrDefault();
            }
            // Resolve the selected option: honour a remembered roster choice, else follow the
            // session day, else the first real day (or the roster option when no day exists).
            // When the picker is hidden (a single real day), force the roster — there is no UI to
            // switch back to it, so a stale day choice must not leave the page stuck in day mode.
            else if (_rosterChosen || DayOptions.Count <= 2)
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

        // Signal an already-attached view to apply a pending quick filter now that the day grid is up.
        // The handler consumes it (clearing it) — a first open applies it from the view's attach path
        // instead, since LoadAsync runs before the view is shown. Day-mode only (filter targets the grid).
        if (_pendingQuickFilter != ParticipantQuickFilter.None && IsDayMode)
            QuickFilterRequested?.Invoke(this, _pendingQuickFilter);
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

    /// <summary>
    /// Imports participants from a CSV file (the view reads/decodes the file and hands over the text).
    /// Runs the CSV flow (parse header → column-mapping modal → import on all days) and reloads on success.
    /// </summary>
    public async Task ImportFromCsvAsync(string csv)
    {
        if (await _csvImportFlow.RunAsync(csv))
            await LoadAsync();
    }

    /// <summary>
    /// Imports participants from an .xlsx workbook (the view reads the file's bytes and hands them over).
    /// Same flow as <see cref="ImportFromCsvAsync"/> with the workbook parsed instead of CSV text.
    /// </summary>
    public async Task ImportFromXlsxAsync(byte[] bytes)
    {
        if (await _csvImportFlow.RunXlsxAsync(bytes))
            await LoadAsync();
    }

    /// <summary>
    /// Exports the active table's current on-screen view (the visible columns and displayed rows the
    /// view captures from the SheetTable). Shows the format modal (CSV / Excel) and serialises the
    /// snapshot; returns the bytes for the view to save (file picking lives in the view). Null when there
    /// is nothing to export or the user cancelled.
    /// </summary>
    public Task<ParticipantExportResult?> ExportAsync(CsvParticipantData view) => _exportFlow.RunAsync(view);

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
        // Fresh rows compute their totals at construction, so no deferred fee recompute is owed anymore.
        _feesDirty = false;

        if (_session.CurrentEvent is null)
            return;

        if (IsRosterMode)
        {
            var roster = await _busy.RunAsync(() => _editor.GetParticipantRosterAsync());
            // The roster's group dropdowns need each day's groups; gather them once.
            var groupsByDay = await _busy.RunAsync(() => LoadGroupsByDayAsync());
            // The roster spans all days, so its team column shows when ANY day uses teams (its default
            // discipline or any group's override).
            _rosterShowsTeam = await _busy.RunAsync(() => AnyDayUsesTeamAsync());
            _rosterShowsScore = await _busy.RunAsync(() => AnyDayUsesScoreAsync());
            foreach (var row in roster)
                Roster.Add(CreateRosterRow(row, groupsByDay));
            OnPropertyChanged(nameof(RosterShowsTeam));
            OnPropertyChanged(nameof(RosterShowsScore));
            RosterColumnsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_session.CurrentDay is not null)
        {
            var (rows, groupOptions, dayGroups) = await _busy.RunAsync(async () =>
            {
                var r = await _editor.GetParticipantDayRowsAsync();
                var g = await BuildGroupOptionsAsync(_session.CurrentDay!.Id);
                var dg = await _editor.GetGroupsForDayAsync(_session.CurrentDay!.Id);
                return (r, g, dg);
            });
            // A day grid normally lists only real groups (no "(none)" sentinel), but a day can have
            // participant rows while having zero groups defined. In that state the empty list would
            // crash the row VM's group-fallback ([0]); seed the "(none)" sentinel so it never does.
            if (groupOptions.Count == 0)
                groupOptions = [new GroupOption(null, string.Empty, Localization)];
            foreach (var row in rows)
                Participants.Add(CreateDayRow(row, groupOptions));

            // Teams apply when the day's default discipline uses them, or any group on the day
            // overrides to a team discipline (e.g. a rogaine group on a set-course day).
            var day = _session.CurrentDay;
            _dayUsesTeam =
                _strategies.For(day.DefaultDiscipline).UsesParticipantColumn(BusinessLogic.Enums.ParticipantColumn.Team)
                || dayGroups.Any(g => _strategies.For(g.DisciplineOverride ?? day.DefaultDiscipline)
                    .UsesParticipantColumn(BusinessLogic.Enums.ParticipantColumn.Team));
            // «Бали» applies when the day's discipline scores points, or any group overrides to one.
            _dayUsesScore =
                _strategies.For(day.DefaultDiscipline).UsesControlPointPoints
                || dayGroups.Any(g => _strategies.For(g.DisciplineOverride ?? day.DefaultDiscipline).UsesControlPointPoints);
        }
        else
        {
            _dayUsesTeam = false;
            _dayUsesScore = false;
        }

        OnPropertyChanged(nameof(ShowTeamColumn));
        OnPropertyChanged(nameof(ShowScoreColumn));
        RebuildDayBands();
        RecomputeStatusInfo();
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
            options.Add(new GroupOption(g.GroupId, g.Name, Localization, g.MinBirthYear, g.MaxBirthYear));
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

    // True when any day uses a team discipline — either the day's default discipline, or any group on
    // it overriding to one (effective = override ?? day default). Drives the roster's team column.
    private async Task<bool> AnyDayUsesTeamAsync()
    {
        var days = await _editor.GetDaysAsync();
        foreach (var day in days)
        {
            if (_strategies.For(day.DefaultDiscipline).UsesParticipantColumn(BusinessLogic.Enums.ParticipantColumn.Team))
                return true;
            var groups = await _editor.GetGroupsForDayAsync(day.Id);
            if (groups.Any(g => _strategies.For(g.DisciplineOverride ?? day.DefaultDiscipline)
                    .UsesParticipantColumn(BusinessLogic.Enums.ParticipantColumn.Team)))
                return true;
        }
        return false;
    }

    // True when any day scores points — its default discipline or any group's override is point-scoring
    // (rogaine). Drives the roster's «Бали» column.
    private async Task<bool> AnyDayUsesScoreAsync()
    {
        var days = await _editor.GetDaysAsync();
        foreach (var day in days)
        {
            if (_strategies.For(day.DefaultDiscipline).UsesControlPointPoints)
                return true;
            var groups = await _editor.GetGroupsForDayAsync(day.Id);
            if (groups.Any(g => _strategies.For(g.DisciplineOverride ?? day.DefaultDiscipline).UsesControlPointPoints))
                return true;
        }
        return false;
    }

    private ParticipantDayRowViewModel CreateDayRow(ParticipantDayRow row, IReadOnlyList<GroupOption> groupOptions)
        => new(row, groupOptions, _regionOptions, _clubOptions, _dusshOptions, _rankOptions, Discounts, _feeContext, Localization, _strategies,
            RequestRowSave, LeaveDay, RequestDayRowChipChange,
            RequestDayRowRegionChange, RequestDayRowAddRegion,
            RequestDayRowClubChange, RequestDayRowAddClub,
            RequestDayRowDusshChange, RequestDayRowAddDussh,
            RequestRowRaisedFeeChange, RequestRowDiscountChange,
            RequestDayRowResultStatusChange, RequestDayRowBonusChange);

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
            cells.Add(new RosterDayCellViewModel(row.ParticipantId, cell, options, Localization, RequestCellGroupChange, RequestCellChipChange, RequestCellStartTimeChange, RequestCellOutOfCompetitionChange, RequestCellResultStatusChange, RequestCellBonusChange));
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
                (row.Note ?? string.Empty).Trim(),
                (row.Team ?? string.Empty).Trim(),
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

    // ── Bulk assign start numbers ─────────────────────────────────────────────────────────────
    // Assigns sequential start numbers to the rows the user currently sees, in on-screen order. The
    // visible (filtered + sorted) order lives in the SheetTable, so the view passes its VisibleItems in.
    // When "reassign existing" is off: already-numbered rows are skipped and numbers already taken by
    // anyone are stepped over (the counter stays contiguous, never collides). When on: every visible row
    // is renumbered and a number held by a different participant is cleared from that participant first.
    // Setting a row's Number goes through its existing debounced autosave, so each touched participant
    // persists on its own timer; we only orchestrate the assignment here and write the activity log.
    [RelayCommand]
    private async Task AssignNumbersAsync(IReadOnlyList<object?>? visibleRows)
    {
        if (visibleRows is null || visibleRows.Count == 0)
            return;

        // All numbers currently taken across the whole competition (not just the visible set), mapped to
        // their holder, so we can skip/step over taken numbers and clear a number from its previous holder.
        var holders = BuildNumberHolders();

        // Pre-fill the dialog with the next free number after the largest one already assigned.
        var suggestedStart = holders.Count == 0 ? 1 : holders.Keys.Max() + 1;

        var result = await _dialogs.ShowAssignNumbersAsync(new AssignNumbersViewModel(Localization, suggestedStart));
        if (result is null)
            return;

        // Project the heterogeneous row view-models (roster vs day) to a common shape so the assignment
        // logic is written once. Both expose ParticipantId, FullName and a settable Number.
        var visible = ProjectNumberRows(visibleRows);
        if (visible.Count == 0)
            return;

        var assignments = new List<(string Name, int Number)>();
        var cleared = new List<(string Name, int Number)>();
        // Every row whose number actually changed, in assignment order — the source of the one batch write.
        // Keyed by participant id so a participant appearing on two visible rows (different days share the
        // competition-level number) is written once with its final value.
        var touched = new Dictionary<Guid, NumberRow>();
        // Every touched row instance (with duplicates) so we can cancel each one's own debounce timer — a
        // participant on two visible day rows has two distinct timer keys that must both be cancelled.
        var touchedRows = new List<NumberRow>();
        var n = result.StartNumber;

        foreach (var row in visible)
        {
            if (!result.ReassignExisting)
            {
                // Leave a participant who already has a number untouched.
                if (!string.IsNullOrWhiteSpace(row.GetNumber()))
                    continue;
                // Step past numbers already held by anyone (keeps the counter contiguous, no collisions).
                while (holders.ContainsKey(n))
                    n++;
            }
            else if (holders.TryGetValue(n, out var prev) && !ReferenceEquals(prev.Source, row.Source))
            {
                // The number is held by a *different* participant: free it from them before reusing it.
                prev.SetNumber(string.Empty);
                cleared.Add((prev.GetName(), n));
                touched[prev.ParticipantId] = prev;
                touchedRows.Add(prev);
                holders.Remove(n);
            }

            // Drop this participant's previous number from the map so a later identical counter value
            // doesn't see them as still holding it (and wrongly clear an already-moved participant).
            if (int.TryParse((row.GetNumber() ?? string.Empty).Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var old)
                && holders.TryGetValue(old, out var oldHolder)
                && ReferenceEquals(oldHolder.Source, row.Source))
                holders.Remove(old);

            row.SetNumber(n.ToString(CultureInfo.InvariantCulture));
            holders[n] = row;
            touched[row.ParticipantId] = row;
            touchedRows.Add(row);
            assignments.Add((row.GetName(), n));
            n++;
        }

        // Persist the whole assignment in ONE transaction. Setting each row's Number above also queued the
        // row's own debounced autosave; those overlapping per-row writes are exactly the race that dropped
        // numbers, so cancel every touched row's pending timer and write the batch ourselves instead.
        if (touched.Count > 0)
        {
            foreach (var row in touchedRows)
            {
                if (_saveTimers.TryGetValue(row.TimerKey, out var cts))
                {
                    cts.Cancel();
                    _saveTimers.Remove(row.TimerKey);
                }
            }

            var batch = touched.Values
                .Select(r => (r.ParticipantId, Number: r.GetNumber()))
                .ToList();
            try
            {
                await Task.Run(() => _editor.SetParticipantNumbersBatchAsync(batch));
            }
            catch
            {
                // A failed batch leaves the in-memory rows showing the intended numbers; a reload reverts
                // them. Don't crash the UI — the activity log below still records what was attempted.
            }
        }

        LogNumberAssignment(assignments, cleared);
    }

    // A common view over the two participant-row view-model types for bulk numbering. Neither shares a
    // base class, so the getters/setters are bound per concrete type here.
    private sealed class NumberRow
    {
        public required object Source { get; init; }
        public required Guid ParticipantId { get; init; }
        // The key under which this row's debounced autosave is tracked in _saveTimers (the link id for a
        // day row, the participant id for a roster row), so bulk assign can cancel it before its own batch.
        public required Guid TimerKey { get; init; }
        public required Func<string> GetNumber { get; init; }
        public required Action<string> SetNumber { get; init; }
        public required Func<string> GetName { get; init; }
    }

    private static List<NumberRow> ProjectNumberRows(IReadOnlyList<object?> rows)
    {
        var list = new List<NumberRow>(rows.Count);
        foreach (var item in rows)
        {
            switch (item)
            {
                case ParticipantRosterRowViewModel r:
                    list.Add(new NumberRow
                    {
                        Source = r,
                        ParticipantId = r.ParticipantId,
                        TimerKey = r.ParticipantId,
                        GetNumber = () => r.Number ?? string.Empty,
                        SetNumber = v => r.Number = v,
                        GetName = () => r.FullName ?? string.Empty
                    });
                    break;
                case ParticipantDayRowViewModel d:
                    list.Add(new NumberRow
                    {
                        Source = d,
                        ParticipantId = d.ParticipantId,
                        TimerKey = d.Id,
                        GetNumber = () => d.Number ?? string.Empty,
                        SetNumber = v => d.Number = v,
                        GetName = () => d.FullName ?? string.Empty
                    });
                    break;
            }
        }
        return list;
    }

    // Maps every number currently in use (across the full backing collection of the active mode) to the
    // NumberRow that holds it, so the assignment can skip taken numbers and clear a previous holder.
    private Dictionary<int, NumberRow> BuildNumberHolders()
    {
        var all = IsRosterMode
            ? ProjectNumberRows(Roster)
            : ProjectNumberRows(Participants);
        var map = new Dictionary<int, NumberRow>();
        foreach (var row in all)
        {
            if (int.TryParse((row.GetNumber() ?? string.Empty).Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                map[value] = row; // last writer wins; numbers are unique per competition so this is safe
        }
        return map;
    }

    private void LogNumberAssignment(
        IReadOnlyList<(string Name, int Number)> assignments,
        IReadOnlyList<(string Name, int Number)> cleared)
    {
        if (assignments.Count == 0 && cleared.Count == 0)
        {
            _log.Action(Localization.Get("Participants.AssignNumbers.Log.None"));
            return;
        }

        _log.Action(string.Format(
            CultureInfo.CurrentCulture,
            Localization.Get("Participants.AssignNumbers.Log.Assigned"),
            assignments.Count));
        foreach (var (name, number) in assignments)
            _log.Action(string.Format(
                CultureInfo.CurrentCulture,
                Localization.Get("Participants.AssignNumbers.Log.AssignedRow"),
                number,
                string.IsNullOrWhiteSpace(name) ? Localization.Get("Participants.Chip.UnnamedHolder") : name));

        if (cleared.Count > 0)
        {
            _log.Action(string.Format(
                CultureInfo.CurrentCulture,
                Localization.Get("Participants.AssignNumbers.Log.ClearedCount"),
                cleared.Count));
            foreach (var (name, number) in cleared)
                _log.Action(string.Format(
                    CultureInfo.CurrentCulture,
                    Localization.Get("Participants.AssignNumbers.Log.ClearedRow"),
                    number,
                    string.IsNullOrWhiteSpace(name) ? Localization.Get("Participants.Chip.UnnamedHolder") : name));
        }
    }

    // ── Manual start-order editing ────────────────────────────────────────────────────────────
    // Opens a modal to re-order the start sequence within a group on the day currently in view (a real day
    // in day mode, or the sole day of a single-day competition when on the roster). The dialog lists a
    // group's members ordered by their start time (from the start protocol) and lets the user drag them into
    // a new order; the SET of start minutes stays fixed and is re-handed out in the new order on save. The
    // changed links are written in ONE batch and the page reloads so the start-time cells refresh.
    [RelayCommand]
    private async Task EditStartOrderAsync()
    {
        if (EffectiveDayId is not { } dayId)
            return;

        var data = await _busy.RunAsync(() => _editor.GetStartOrderDataAsync(dayId));
        if (data.Groups.Count == 0)
        {
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.StartOrder.Title",
                messageKey: "Participants.StartOrder.NoGroups",
                confirmKey: "Common.Ok",
                cancelKey: "Common.Ok"));
            return;
        }

        var assignments = await _dialogs.ShowStartOrderAsync(new StartOrderViewModel(Localization, data));
        if (assignments is null || assignments.Count == 0)
            return;

        await _busy.RunAsync(() => _editor.SaveDrawStartTimesAsync(assignments));
        _log.Action(string.Format(
            CultureInfo.CurrentCulture,
            Localization.Get("Participants.StartOrder.Log.Saved"),
            assignments.Count));

        await LoadAsync();
    }

    // ── Bulk assign rental chips ──────────────────────────────────────────────────────────────
    // Hands out unused rental chips, in ascending number order, to every shown participant (or member
    // day) that has no chip yet — in the table's on-screen order (passed in as VisibleItems). A dropdown
    // narrows the pool to chips carrying a given note ("type"), or all of them. Chips are per-day, so in
    // day mode each shown row gets a chip on the current day, and in the roster a participant's one chip is
    // applied to all their chip-less member days. We assign only chips nobody holds on any day, so there is
    // no conflict and the per-row reassign flow is bypassed. The cells update instantly in memory; the
    // whole set is then persisted in ONE background batch (a single transaction), not one write per cell —
    // which is what made it slow and drip in one-by-one.
    [RelayCommand]
    private async Task AssignChipsAsync(IReadOnlyList<object?>? visibleRows)
    {
        if (visibleRows is null || visibleRows.Count == 0)
            return;

        // The rental pool (ordered by number) and the set of chip numbers already held on any day.
        var pool = await _busy.RunAsync(() => _editor.GetRentalChipsAsync());
        if (pool.Count == 0)
        {
            _log.Action(Localization.Get("Participants.AssignChips.Log.NoChips"));
            return;
        }
        var used = await _busy.RunAsync(() => _editor.GetRentalChipHoldersAsync());

        // The note dropdown: a leading "all" option, then each distinct note present in the pool.
        var notes = pool
            .Select(c => (c.Note ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();
        var options = new List<ChipNoteOption> { ChipNoteOption.All(Localization.Get("Participants.AssignChips.AllChips")) };
        options.AddRange(notes.Select(n => ChipNoteOption.ForNote(n, n)));

        var result = await _dialogs.ShowAssignChipsAsync(new AssignChipsViewModel(Localization, options));
        if (result is null)
            return;

        // The free chips for this run: unused (held by nobody on any day), matching the note filter,
        // by ascending number — drawn from the front as we assign.
        var free = new Queue<string>(pool
            .Where(c => result.Note is null
                || string.Equals((c.Note ?? string.Empty).Trim(), result.Note, StringComparison.OrdinalIgnoreCase))
            .Select(c => (c.Number ?? string.Empty).Trim())
            .Where(number => number.Length > 0 && !used.ContainsKey(number)));

        // Confirm before touching anything (this is a sweeping change). Count the recipients WITHOUT
        // mutating any cell — chip-less shown rows, capped by the number of free chips — so the prompt
        // states exactly how many participants will be handed a chip. If nobody qualifies (no chip-less
        // rows, or no free chips for the filter), say so and stop rather than opening an empty confirm.
        var recipients = CountChipRecipients(visibleRows, free.Count);
        if (recipients == 0)
        {
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.AssignChips.ConfirmTitle",
                messageKey: "Participants.AssignChips.NoneToAssign",
                confirmKey: "Common.Ok",
                cancelKey: "Common.Ok"));
            _log.Action(Localization.Get("Participants.AssignChips.Log.None"));
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "Participants.AssignChips.ConfirmTitle",
            messageKey: "Participants.AssignChips.ConfirmMessage",
            confirmKey: "Participants.AssignChips.Confirm",
            cancelKey: "Common.Cancel")
        {
            MessageArgs = [recipients, free.Count]
        });
        if (!confirmed)
            return;

        // Build the assignment in memory (updating the cells live), collecting the (participant, day, chip)
        // writes; then commit them all at once.
        var assignments = new List<(string Name, string Chip)>();
        var writes = new List<(Guid ParticipantId, Guid DayId, string Chip)>();
        if (IsRosterMode)
            AssignChipsToRoster(visibleRows, free, assignments, writes);
        else
            AssignChipsToDay(visibleRows, free, assignments, writes);

        if (writes.Count > 0)
        {
            await _busy.RunAsync(() => _editor.SetParticipantDayChipsBatchAsync(writes));

            // The assigned cells were set "silently" (no per-cell fee recompute), so refresh each row's
            // «Стартовий внесок» from the current fee snapshot — a chip drawn from a note-tagged pool may
            // carry a different rental price. Also refresh the status bar (rental in-use / without-chip
            // counts changed); the batch path never went through the per-cell change handlers that do this.
            RefreshFeesIfVisible();
            RecomputeStatusInfo();
        }

        LogChipAssignment(assignments, free.Count);
    }

    // How many participants would actually be handed a chip, WITHOUT mutating anything — used by the
    // confirmation prompt. Mirrors the eligibility in AssignChipsToDay/AssignChipsToRoster (a chip-less
    // shown row in day mode; a shown participant with ≥1 chip-less member day in the roster), capped by
    // the number of free chips available (each recipient consumes exactly one chip).
    private int CountChipRecipients(IReadOnlyList<object?> visibleRows, int freeCount)
    {
        if (freeCount == 0)
            return 0;

        var count = 0;
        foreach (var item in visibleRows)
        {
            if (count >= freeCount)
                break;

            if (IsRosterMode)
            {
                if (item is ParticipantRosterRowViewModel row
                    && row.Days.Any(d => d.IsMember && string.IsNullOrWhiteSpace(d.Chip)))
                    count++;
            }
            else
            {
                if (item is ParticipantDayRowViewModel row && string.IsNullOrWhiteSpace(row.Chip))
                    count++;
            }
        }
        return count;
    }

    // Day mode: every shown row is a member of the current day, so give each one without a chip the next
    // free chip on that day. Updates the cell in memory and records the write for the batch commit.
    private void AssignChipsToDay(
        IReadOnlyList<object?> visibleRows,
        Queue<string> free,
        List<(string Name, string Chip)> assignments,
        List<(Guid ParticipantId, Guid DayId, string Chip)> writes)
    {
        if (_session.CurrentDay is not { } day)
            return;

        foreach (var item in visibleRows)
        {
            if (free.Count == 0)
                break;
            if (item is not ParticipantDayRowViewModel row || !string.IsNullOrWhiteSpace(row.Chip))
                continue;

            var chip = free.Dequeue();
            row.SetChipSilently(chip);
            row.MarkChipCommitted(chip);
            writes.Add((row.ParticipantId, day.Id, chip));
            assignments.Add((row.FullName ?? string.Empty, chip));
        }
    }

    // Roster mode: a chip belongs to a person and is reused across their days, so each shown participant
    // gets ONE free chip applied to every member day-cell that has no chip yet. A participant who already
    // has a chip on every member day consumes nothing.
    private void AssignChipsToRoster(
        IReadOnlyList<object?> visibleRows,
        Queue<string> free,
        List<(string Name, string Chip)> assignments,
        List<(Guid ParticipantId, Guid DayId, string Chip)> writes)
    {
        foreach (var item in visibleRows)
        {
            if (free.Count == 0)
                break;
            if (item is not ParticipantRosterRowViewModel row)
                continue;

            var emptyDays = row.Days.Where(d => d.IsMember && string.IsNullOrWhiteSpace(d.Chip)).ToList();
            if (emptyDays.Count == 0)
                continue;

            var chip = free.Dequeue();
            foreach (var cell in emptyDays)
            {
                cell.SetChipSilently(chip);
                cell.MarkChipCommitted(chip);
                writes.Add((cell.ParticipantId, cell.DayId, chip));
            }
            assignments.Add((row.FullName ?? string.Empty, chip));
        }
    }

    private void LogChipAssignment(IReadOnlyList<(string Name, string Chip)> assignments, int remainingFree)
    {
        if (assignments.Count == 0)
        {
            _log.Action(Localization.Get("Participants.AssignChips.Log.None"));
            return;
        }

        _log.Action(string.Format(
            CultureInfo.CurrentCulture,
            Localization.Get("Participants.AssignChips.Log.Assigned"),
            assignments.Count,
            remainingFree));
        foreach (var (name, chip) in assignments)
            _log.Action(string.Format(
                CultureInfo.CurrentCulture,
                Localization.Get("Participants.AssignChips.Log.AssignedRow"),
                chip,
                string.IsNullOrWhiteSpace(name) ? Localization.Get("Participants.Chip.UnnamedHolder") : name));
    }

    // ── Mark age-window violators "поза конкурсом" ─────────────────────────────────────────────
    // Sets «поза конкурсом» (out of competition) on every shown participant whose birth year falls
    // outside their group's allowed age window — the same rule that red-tints the birth-date cell
    // (Group.ViolatesAgeWindow). In day mode this checks the current day's group; in the roster it
    // checks each member day's own group (a participant may breach on one day but not another, so it is
    // marked per day). Already-OOC and non-violating rows are left untouched. Each change goes through
    // the existing per-row / per-cell OOC persistence (debounced save / background write), so there is
    // no new DB code. Confirms first (it is a sweeping change) and writes a summary to the activity log.
    [RelayCommand]
    private async Task MarkAgeViolatorsOutOfCompetitionAsync(IReadOnlyList<object?>? visibleRows)
    {
        if (visibleRows is null || visibleRows.Count == 0 || _session.CurrentEvent is null)
            return;

        // Collect who will be marked (and the deferred set-OOC actions) BEFORE asking, so the
        // confirmation can list them. Day mode marks the current day's row; the roster marks each
        // breaching member day of a participant (a participant may breach on one day but not another).
        var names = new List<string>();
        var apply = new List<Action>();

        if (IsRosterMode)
        {
            foreach (var item in visibleRows)
            {
                if (item is not ParticipantRosterRowViewModel row)
                    continue;
                var year = row.BirthDate?.Year;
                var cells = row.Days
                    .Where(c => c.IsMember && !c.OutOfCompetition
                        && Group.ViolatesAgeWindow(year, c.SelectedGroup.MinBirthYear, c.SelectedGroup.MaxBirthYear))
                    .ToList();
                if (cells.Count == 0)
                    continue;
                names.Add(row.FullName ?? string.Empty);
                // Setting it fires the cell's change callback, which persists it in the background.
                apply.Add(() => { foreach (var cell in cells) cell.OutOfCompetition = true; });
            }
        }
        else
        {
            foreach (var item in visibleRows)
            {
                if (item is not ParticipantDayRowViewModel row)
                    continue;
                if (row.OutOfCompetition || !row.BirthDateViolatesAge)
                    continue;
                names.Add(row.FullName ?? string.Empty);
                // Setting it goes through the row's debounced autosave (OnOutOfCompetitionChanged).
                apply.Add(() => row.OutOfCompetition = true);
            }
        }

        // Nobody to mark — say so (and skip the prompt) rather than opening an empty confirmation.
        if (names.Count == 0)
        {
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.MarkAgeViolators.ConfirmTitle",
                messageKey: "Participants.MarkAgeViolators.NoneMessage",
                confirmKey: "Common.Ok",
                cancelKey: "Common.Ok"));
            LogAgeViolatorsMarked([]);
            return;
        }

        // List the affected participants in the confirmation message (one per line). A "(без імені)"
        // placeholder stands in for a still-unnamed row.
        var list = string.Join("\n", names.Select(n =>
            "• " + (string.IsNullOrWhiteSpace(n) ? Localization.Get("Participants.Chip.UnnamedHolder") : n)));
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "Participants.MarkAgeViolators.ConfirmTitle",
            messageKey: "Participants.MarkAgeViolators.ConfirmMessage",
            confirmKey: "Participants.MarkAgeViolators.Confirm",
            cancelKey: "Common.Cancel")
        {
            MessageArgs = [names.Count, list]
        });
        if (!confirmed)
            return;

        foreach (var action in apply)
            action();

        LogAgeViolatorsMarked(names);
    }

    private void LogAgeViolatorsMarked(IReadOnlyList<string> marked)
    {
        if (marked.Count == 0)
        {
            _log.Action(Localization.Get("Participants.MarkAgeViolators.Log.None"));
            return;
        }

        _log.Action(string.Format(
            CultureInfo.CurrentCulture,
            Localization.Get("Participants.MarkAgeViolators.Log.Marked"),
            marked.Count));
        foreach (var name in marked)
            _log.Action(string.Format(
                CultureInfo.CurrentCulture,
                Localization.Get("Participants.MarkAgeViolators.Log.MarkedRow"),
                string.IsNullOrWhiteSpace(name) ? Localization.Get("Participants.Chip.UnnamedHolder") : name));
    }

    // ── Bulk edit one field across the shown rows ──────────────────────────────────────────────
    // Changes a single field on every row currently shown (the filtered + sorted set the view hands in
    // as VisibleItems), e.g. set the same group / representative / "Член ФСОУ" for everyone visible. We
    // never touch the unique fields (number, chip). The modal picks a field + value; we then set the
    // matching property on each row, which goes through that field's existing per-row persistence
    // (debounced save for text/bool/rank, the dedicated region/club/ДЮСШ/group callbacks) — so there is
    // no new DB code. Group is offered in day mode only: across the roster a single group can't span
    // days that don't all have it. Region/club/ДЮСШ dropdowns reuse the shared "+ new" sentinel; picking
    // it routes back here to the existing create flow and re-seeds the dialog.
    [RelayCommand]
    private async Task BulkEditAsync(BulkEditRequest? request)
    {
        var visibleRows = request?.Rows;
        if (visibleRows is null || visibleRows.Count == 0 || _session.CurrentEvent is null)
            return;

        // The group dropdown needs a real-group list. In day mode it's the current day's groups; in the
        // roster it's every competition group (each participant's day cells resolve the matching group by
        // id from their own day's list, so a day without that group is left untouched — same as the
        // roster's collapsed Groups cell). Either way "(none)" is excluded — bulk edit sets a group.
        var groupOptions = IsDayMode && _session.CurrentDay is { } day
            ? (await _busy.RunAsync(() => BuildGroupOptionsAsync(day.Id)))
                .Where(o => o.Id is not null).ToList()
            : IsRosterMode
                ? (await _busy.RunAsync(() => _editor.GetGroupsAsync()))
                    .Select(g => new GroupOption(g.Id, g.Name, Localization, g.MinBirthYear, g.MaxBirthYear)).ToList()
                : new List<GroupOption>();

        var fields = BuildBulkEditFields(groupOptions.Count > 0);
        // Remember each field's localized label so the activity log can name the field, not its key.
        var fieldLabels = fields.ToDictionary(f => f.Key, f => f.Label);
        // Preselect the field the request points at (the focused column / the right-clicked header),
        // when that field is actually offered in the current mode; otherwise fall back to the first.
        var preselect = request?.PreselectKey is { } key
            ? fields.FirstOrDefault(f => f.Key == key)
            : null;
        var dialog = new Dialogs.BulkEditViewModel(
            Localization, fields, groupOptions,
            _regionOptions, _clubOptions, _dusshOptions, _rankOptions, visibleRows.Count, preselect);

        // The list "+ new" sentinels create the value inline, reusing the existing add flows.
        dialog.AddRequested += OnBulkEditAddRequested;
        BulkEditResult? result;
        try
        {
            result = await _dialogs.ShowBulkEditAsync(dialog);
        }
        finally
        {
            dialog.AddRequested -= OnBulkEditAddRequested;
        }
        if (result is null)
            return;

        ApplyBulkEdit(visibleRows, result);

        // Group/region etc. don't shift fees, but group changes the discount/rental picture for the
        // total; refresh fees + the status bar so the change is reflected immediately (cheap, in-memory).
        RefreshFeesIfVisible();
        RecomputeStatusInfo();

        _log.Action(string.Format(
            CultureInfo.CurrentCulture,
            Localization.Get("Participants.BulkEdit.Log.Applied"),
            visibleRows.Count,
            fieldLabels.TryGetValue(result.Key, out var label) ? label : result.Key));
    }

    // The fields the user can bulk-edit, in display order. Unique fields (number, chip) are excluded.
    // Context-sensitive fields are dropped when they don't apply: group (day mode only), team (only when
    // shown for the current mode), raised fee (only when enabled), out-of-competition (day mode only).
    private IReadOnlyList<Dialogs.BulkEditFieldOption> BuildBulkEditFields(bool includeGroup)
    {
        var fields = new List<Dialogs.BulkEditFieldOption>();
        Dialogs.BulkEditFieldOption F(string key, Dialogs.BulkEditFieldKind kind, string labelKey)
            => new(key, kind, Localization.Get(labelKey));

        if (includeGroup)
            fields.Add(F("Group", Dialogs.BulkEditFieldKind.Group, "Participants.Col.Group"));
        fields.Add(F("Region", Dialogs.BulkEditFieldKind.Region, "Participants.Col.Region"));
        fields.Add(F("Club", Dialogs.BulkEditFieldKind.Club, "Participants.Col.Club"));
        fields.Add(F("Dussh", Dialogs.BulkEditFieldKind.Dussh, "Participants.Col.Dussh"));
        fields.Add(F("Rank", Dialogs.BulkEditFieldKind.Rank, "Participants.Col.Rank"));
        fields.Add(F("Coach", Dialogs.BulkEditFieldKind.Text, "Participants.Col.Coach"));
        fields.Add(F("Representative", Dialogs.BulkEditFieldKind.Text, "Participants.Col.Representative"));
        fields.Add(F("FsouCode", Dialogs.BulkEditFieldKind.Text, "Participants.Col.FsouCode"));
        fields.Add(F("Payment", Dialogs.BulkEditFieldKind.Text, "Participants.Col.Payment"));
        fields.Add(F("Note", Dialogs.BulkEditFieldKind.Text, "Participants.Col.Note"));
        if ((IsDayMode && ShowTeamColumn) || (IsRosterMode && RosterShowsTeam))
            fields.Add(F("Team", Dialogs.BulkEditFieldKind.Text, "Participants.Col.Team"));
        fields.Add(F("IsFsouMember", Dialogs.BulkEditFieldKind.Bool, "Participants.Col.IsFsouMember"));
        if (RaisedFeeEnabled)
            fields.Add(F("PaysRaisedFee", Dialogs.BulkEditFieldKind.Bool, "Participants.Col.RaisedFee"));
        // Start time and out-of-competition are per-day fields. Day mode edits the current day directly;
        // the roster fans the value out to each participant's member days (mirrors its collapsed cells).
        // Both modes therefore offer them. Start is a free-text HH:mm:ss field like the cell editor.
        fields.Add(F("StartTime", Dialogs.BulkEditFieldKind.Text, "Participants.Col.StartTime"));
        fields.Add(F("OutOfCompetition", Dialogs.BulkEditFieldKind.Bool, "Participants.Col.OutOfCompetition"));
        return fields;
    }

    // A list field's "+ new" sentinel was picked in the dialog: run the existing create flow for that
    // kind, then push the rebuilt shared options onto the dialog (selecting the new value) or revert.
    private void OnBulkEditAddRequested(object? sender, Dialogs.BulkEditFieldKind kind)
    {
        if (sender is not Dialogs.BulkEditViewModel dialog)
            return;
        _ = kind switch
        {
            Dialogs.BulkEditFieldKind.Region => AddRegionForDialogAsync(dialog),
            Dialogs.BulkEditFieldKind.Club => AddClubForDialogAsync(dialog),
            Dialogs.BulkEditFieldKind.Dussh => AddDusshForDialogAsync(dialog),
            _ => Task.CompletedTask
        };
    }

    private async Task AddRegionForDialogAsync(Dialogs.BulkEditViewModel dialog)
    {
        await _regionGate.WaitAsync();
        try
        {
            var name = await _dialogs.ShowAddRegionAsync(new Dialogs.AddRegionViewModel(Localization));
            var region = string.IsNullOrWhiteSpace(name)
                ? null
                : await _busy.RunAsync(() => _editor.AddRegionAsync(name));
            if (region is null)
            {
                dialog.RevertList(Dialogs.BulkEditFieldKind.Region);
                return;
            }
            await RefreshRegionOptionsAsync(hasEvent: true);
            ApplyRegionOptionsToRows();
            dialog.ApplyNewRegion(_regionOptions, region.Id);
        }
        finally
        {
            _regionGate.Release();
        }
    }

    private async Task AddClubForDialogAsync(Dialogs.BulkEditViewModel dialog)
    {
        await _clubGate.WaitAsync();
        try
        {
            var name = await _dialogs.ShowAddClubAsync(new Dialogs.AddClubViewModel(Localization));
            var club = string.IsNullOrWhiteSpace(name)
                ? null
                : await _busy.RunAsync(() => _editor.AddClubAsync(name));
            if (club is null)
            {
                dialog.RevertList(Dialogs.BulkEditFieldKind.Club);
                return;
            }
            await RefreshClubOptionsAsync(hasEvent: true);
            ApplyClubOptionsToRows();
            dialog.ApplyNewClub(_clubOptions, club.Id);
        }
        finally
        {
            _clubGate.Release();
        }
    }

    private async Task AddDusshForDialogAsync(Dialogs.BulkEditViewModel dialog)
    {
        await _dusshGate.WaitAsync();
        try
        {
            var name = await _dialogs.ShowAddDusshAsync(new Dialogs.AddDusshViewModel(Localization));
            var dussh = string.IsNullOrWhiteSpace(name)
                ? null
                : await _busy.RunAsync(() => _editor.AddDusshAsync(name));
            if (dussh is null)
            {
                dialog.RevertList(Dialogs.BulkEditFieldKind.Dussh);
                return;
            }
            await RefreshDusshOptionsAsync(hasEvent: true);
            ApplyDusshOptionsToRows();
            dialog.ApplyNewDussh(_dusshOptions, dussh.Id);
        }
        finally
        {
            _dusshGate.Release();
        }
    }

    // Sets the chosen field to the chosen value on every visible row. Setting each property fires that
    // field's existing per-row change handler, which persists it — list fields are resolved to each
    // row's own option instance by id (groups in particular have a per-day list and must match by id).
    private void ApplyBulkEdit(IReadOnlyList<object?> visibleRows, BulkEditResult result)
    {
        foreach (var item in visibleRows)
        {
            switch (item)
            {
                case ParticipantDayRowViewModel d:
                    ApplyBulkEditToDayRow(d, result);
                    break;
                case ParticipantRosterRowViewModel r:
                    ApplyBulkEditToRosterRow(r, result);
                    break;
            }
        }
    }

    private void ApplyBulkEditToDayRow(ParticipantDayRowViewModel row, BulkEditResult r)
    {
        switch (r.Key)
        {
            case "Group":
                var g = row.GroupOptions.FirstOrDefault(o => o.Id == r.Id);
                if (g is not null)
                    row.SelectedGroup = g;
                break;
            case "Region":
                row.SelectedRegion = row.RegionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == r.Id) ?? row.RegionOptions[0];
                break;
            case "Club":
                row.SelectedClub = row.ClubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == r.Id) ?? row.ClubOptions[0];
                break;
            case "Dussh":
                row.SelectedDussh = row.DusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == r.Id) ?? row.DusshOptions[0];
                break;
            case "Rank":
                row.SelectedRank = row.RankOptions.FirstOrDefault(o => string.Equals(o.Value, r.Text, StringComparison.OrdinalIgnoreCase)) ?? row.RankOptions[0];
                break;
            case "Coach": row.Coach = r.Text ?? string.Empty; break;
            case "Representative": row.Representative = r.Text ?? string.Empty; break;
            case "FsouCode": row.FsouCode = r.Text ?? string.Empty; break;
            case "Payment": row.Payment = r.Text ?? string.Empty; break;
            case "Note": row.Note = r.Text ?? string.Empty; break;
            case "Team": row.Team = r.Text ?? string.Empty; break;
            case "StartTime": row.StartTimeText = r.Text ?? string.Empty; break;
            case "IsFsouMember": row.IsFsouMember = r.Bool ?? false; break;
            case "PaysRaisedFee": row.PaysRaisedFee = r.Bool ?? false; break;
            case "OutOfCompetition": row.OutOfCompetition = r.Bool ?? false; break;
        }
    }

    private void ApplyBulkEditToRosterRow(ParticipantRosterRowViewModel row, BulkEditResult r)
    {
        switch (r.Key)
        {
            // Group fans out to every day that has the chosen group (resolved per day by id), exactly
            // like the roster's collapsed Groups cell. Days without that group are left as they were.
            case "Group": row.SetGroupForAllDays(r.Id); break;
            case "Region":
                row.SelectedRegion = row.RegionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == r.Id) ?? row.RegionOptions[0];
                break;
            case "Club":
                row.SelectedClub = row.ClubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == r.Id) ?? row.ClubOptions[0];
                break;
            case "Dussh":
                row.SelectedDussh = row.DusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == r.Id) ?? row.DusshOptions[0];
                break;
            case "Rank":
                row.SelectedRank = row.RankOptions.FirstOrDefault(o => string.Equals(o.Value, r.Text, StringComparison.OrdinalIgnoreCase)) ?? row.RankOptions[0];
                break;
            case "Coach": row.Coach = r.Text ?? string.Empty; break;
            case "Representative": row.Representative = r.Text ?? string.Empty; break;
            case "FsouCode": row.FsouCode = r.Text ?? string.Empty; break;
            case "Payment": row.Payment = r.Text ?? string.Empty; break;
            case "Note": row.Note = r.Text ?? string.Empty; break;
            case "Team": row.Team = r.Text ?? string.Empty; break;
            case "IsFsouMember": row.IsFsouMember = r.Bool ?? false; break;
            case "PaysRaisedFee": row.PaysRaisedFee = r.Bool ?? false; break;
            // Per-day fields fan out to the participant's member days (like the collapsed roster cells).
            case "StartTime": row.SetStartTimeForMemberDays(r.Text ?? string.Empty); break;
            case "OutOfCompetition": row.SetOutOfCompetitionForMemberDays(r.Bool ?? false); break;
        }
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

    // ── Result status override (day grid + roster) ────────────────────────────────────────────
    // The status dropdown persists a manual override on the participant-day, then re-ranks the day so
    // places re-number live across the visible rows. Day grid uses the session's current day; the roster
    // cell carries its own day.
    private void RequestDayRowResultStatusChange(ParticipantDayRowViewModel row)
    {
        if (_session.CurrentDay is not { } day)
            return;
        _ = ApplyResultStatusAsync(row.ParticipantId, day.Id, row.ResultStatusOverride);
    }

    private void RequestCellResultStatusChange(RosterDayCellViewModel cell)
    {
        if (!cell.IsMember)
            return;
        _ = ApplyResultStatusAsync(cell.ParticipantId, cell.DayId, cell.ResultStatusOverride);
    }

    private async Task ApplyResultStatusAsync(Guid participantId, Guid dayId, FinishStatus? status)
    {
        try
        {
            await Task.Run(() => _editor.SetParticipantDayResultStatusAsync(participantId, dayId, status));
            var results = await _editor.GetDayResultsByParticipantAsync(dayId);
            RefreshResults(dayId, results);
        }
        catch { /* never crash the UI over a status edit */ }
    }

    // ── Bonus (points correction) edit (day grid + roster) ─────────────────────────────────────
    // The «бонус» cell persists a points correction on the participant-day, then recomputes the day so
    // «Бали» and places re-number live (a correction can re-rank individuals and rogaine teams). Day grid
    // uses the session's current day; the roster cell carries its own day.
    private void RequestDayRowBonusChange(ParticipantDayRowViewModel row)
    {
        if (_session.CurrentDay is not { } day)
            return;
        _ = ApplyBonusAsync(row.ParticipantId, day.Id, row.Bonus);
    }

    private void RequestCellBonusChange(RosterDayCellViewModel cell)
    {
        if (!cell.IsMember)
            return;
        _ = ApplyBonusAsync(cell.ParticipantId, cell.DayId, cell.Bonus);
    }

    private async Task ApplyBonusAsync(Guid participantId, Guid dayId, int? bonus)
    {
        try
        {
            await Task.Run(() => _editor.SetParticipantDayBonusAsync(participantId, dayId, bonus));
            var results = await _editor.GetDayResultsByParticipantAsync(dayId);
            RefreshResults(dayId, results);
        }
        catch { /* never crash the UI over a bonus edit */ }
    }

    // Re-applies recomputed day results onto the visible rows/cells so places re-number after a status
    // edit. Day grid: every row is the current day. Roster: the matching day's cell on each row.
    private void RefreshResults(Guid dayId, IReadOnlyDictionary<Guid, ParticipantDayResult> results)
    {
        if (IsDayMode)
        {
            if (_session.CurrentDay?.Id != dayId)
                return;
            foreach (var row in Participants)
                row.ApplyResult(results.TryGetValue(row.ParticipantId, out var r) ? r : ParticipantDayResult.Empty);
        }
        else
        {
            foreach (var row in Roster)
                foreach (var cell in row.Days)
                    if (cell.DayId == dayId && cell.IsMember)
                        cell.ApplyResult(results.TryGetValue(row.ParticipantId, out var r) ? r : ParticipantDayResult.Empty);
        }
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
    // <paramref name="silent"/> runs the reads off the busy service (plain Task.Run) so no loading
    // overlay flashes — used by background refreshes like a rental-chip toggle.
    private async Task RefreshFeeDataAsync(bool hasEvent, bool silent = false)
    {
        if (!hasEvent)
        {
            Discounts = [];
            RaisedFeeEnabled = false;
            _feeContext = new EntryFeeContext(_entryFeeCalculator, null, [], [], [], []);
            return;
        }

        Task<T> Read<T>(Func<Task<T>> read) => silent ? Task.Run(read) : _busy.RunAsync(read);

        var info = await Read(() => _editor.GetInfoAsync());
        var groups = await Read(() => _editor.GetGroupsAsync());
        var chipPrices = await Read(() => _editor.GetChipPriceOverridesAsync());
        var discounts = await Read(() => _editor.GetEntryFeeDiscountsAsync());
        var rentalChips = await Read(() => _editor.GetRentalChipsAsync());

        // Only reassign Discounts when the set actually changed: the property reference changing forces
        // the SheetTable to Rebuild() its columns (one checkbox column per discount), which destroys the
        // focused cell. GetEntryFeeDiscountsAsync hands back a fresh list each call, so guard by value —
        // otherwise a background refresh (e.g. a rental toggle) would steal focus for no visible change.
        if (!DiscountsEqual(Discounts, discounts))
            Discounts = discounts;
        RaisedFeeEnabled = info?.RaisedFeeEnabled ?? false;
        _feeContext = new EntryFeeContext(_entryFeeCalculator, info, groups, chipPrices, discounts, rentalChips);
    }

    // Value-equality for the discount list: same count and, pairwise, same id / name / amount / kind.
    // Used to skip a no-op Discounts reassignment that would needlessly rebuild the table columns.
    private static bool DiscountsEqual(IReadOnlyList<EntryFeeDiscount> a, IReadOnlyList<EntryFeeDiscount> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name
                || a[i].Percent != b[i].Percent || a[i].AppliesToChipRental != b[i].AppliesToChipRental)
                return false;
        return true;
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
    // highlight flips immediately; the DB write runs in the background, after which the fee snapshot
    // Recomputes every row's «Стартовий внесок» from the current fee snapshot — but only while the total
    // column is actually visible. When it's hidden the work is skipped and a dirty flag is set; showing
    // the column again flushes it (OnFeeColumnShown). Call this anywhere a fee-affecting change happened.
    private void RefreshFeesIfVisible()
    {
        if (IsFeeColumnVisible is not null && !IsFeeColumnVisible())
        {
            _feesDirty = true;
            return;
        }
        foreach (var row in Participants)
            row.RefreshFees(_feeContext);
        foreach (var row in Roster)
            row.RefreshFees(_feeContext);
        _feesDirty = false;
    }

    /// <summary>Called by the View when the fee:total column becomes visible: if a recompute was
    /// deferred while it was hidden, run it now so the shown totals are current.</summary>
    public void OnFeeColumnShown()
    {
        if (_feesDirty)
            RefreshFeesIfVisible();
    }

    // is rebuilt so every row's total reflects whether that chip is now charged rental.
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

        _ = ToggleRentalChipAndRecomputeAsync(number);
    }

    // Persists the rental toggle, then rebuilds the shared fee snapshot from the updated rental set and
    // pushes it onto every live row so the «Стартовий внесок» column recomputes (a chip in the rental
    // pool is charged rental; one removed from it is the participant's own, so no rental fee).
    private async Task ToggleRentalChipAndRecomputeAsync(string number)
    {
        await Task.Run(() => _editor.ToggleRentalChipAsync(number));
        await RefreshFeeDataAsync(hasEvent: true, silent: true);
        RefreshFeesIfVisible();
        // A rental toggle changes the in-rental / free counts shown in the status bar.
        RecomputeStatusInfo();
    }

    private void ClearParticipantRows()
    {
        Participants.Clear();
        SelectedParticipant = null;
    }

    private void ClearRosterRows() => Roster.Clear();

    // Builds the status bar's system-info line: how many chips are in the rental pool, how many
    // participants in the current view still lack a chip, and — in day mode — the day's member count.
    // Recomputed after the rows load and whenever the rental set changes (a toggle).
    private void RecomputeStatusInfo()
    {
        if (_session.CurrentEvent is null)
        {
            StatusInfo = string.Empty;
            return;
        }

        var rented = RentalChips.Count;

        // Participants without a chip. In the day grid each row is one participant on the day, so it's
        // the rows with a blank chip. In the roster a participant runs several days (chips are per-day),
        // so count those who are a member of at least one day yet have a member day without a chip.
        var withoutChip = 0;
        if (IsRosterMode)
        {
            foreach (var row in Roster)
            {
                var memberDays = row.Days.Where(d => d.IsMember).ToList();
                if (memberDays.Count > 0 && memberDays.Any(d => string.IsNullOrWhiteSpace(d.Chip)))
                    withoutChip++;
            }
        }
        else
        {
            foreach (var row in Participants)
                if (string.IsNullOrWhiteSpace(row.Chip))
                    withoutChip++;
        }

        var parts = new List<string>(3)
        {
            Localization.Get("Participants.Status.ChipsRented").Replace("{0}", rented.ToString()),
            Localization.Get("Participants.Status.WithoutChip").Replace("{0}", withoutChip.ToString()),
        };
        // The roster aggregates every day, so a single "members on day" figure isn't meaningful there.
        if (IsDayMode)
            parts.Add(Localization.Get("Participants.Status.Members").Replace("{0}", Participants.Count.ToString()));

        StatusInfo = string.Join("    •    ", parts);
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}

/// <summary>
/// The argument for the bulk-edit command: the shown (filtered + sorted) rows to change, plus an
/// optional <see cref="PreselectKey"/> — the bulk-edit field key the dialog should open on. The view
/// sets it from the focused column (toolbar button) or the right-clicked header column (column menu);
/// null ⇒ the dialog opens on its first field. An unknown/unavailable key is ignored.
/// </summary>
public sealed record BulkEditRequest(IReadOnlyList<object?> Rows, string? PreselectKey = null);
