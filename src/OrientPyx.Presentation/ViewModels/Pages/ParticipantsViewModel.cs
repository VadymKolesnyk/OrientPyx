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
using OrientPyx.Presentation.Services.Undo;
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
    private readonly IStatementFlow _statementFlow;
    private readonly IReadoutParserSelector _readoutParsers;
    private readonly IAppSettingsService _appSettings;
    private readonly IEntryFeeCalculator _entryFeeCalculator;
    private readonly IActivityLog _log;
    private readonly IEventFiles _eventFiles;
    private readonly IToastService _toasts;
    private readonly IWorkspaceMode _mode;

    /// <summary>The per-machine key the chip-scan file path is stored under on a station (<see cref="IEventFiles.GetMachineValueAsync"/>).</summary>
    private const string ChipScanPathKey = "chipScan.readoutPath";
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();
    // The undo step each pending (debounced) row save writes under, by the same key as _saveTimers. A program-made
    // change re-saving the row (a refused number put back) keeps the step of the edit still waiting (§9.2). Holds no
    // row view model — the entry is an id and a label.
    private readonly Dictionary<Guid, UndoEntry> _saveSteps = new();
    private readonly PendingSaves _pendingSaves = new();
    // Serialises chip-reassignment prompts so a collapsed-block edit that fans a chip out to several
    // days never stacks overlapping confirm dialogs (the overlay shows one dialog at a time).
    private readonly SemaphoreSlim _chipGate = new(1, 1);
    // Serialises the relay cells' edits: a fill-down paste of «100.1, 100.2, …» must create a team once and see
    // each leg the previous row took, and a «leg taken» question shows one at a time.
    private readonly SemaphoreSlim _relayGate = new(1, 1);
    // The relay runners a paste / bulk edit of the group column reached, answered together once it is done.
    private readonly List<RelayBulkGroupPick> _relayBulkGroupPicks = [];
    private bool _relayBulkGroupQueued;

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
        IExportFileSaver fileSaver,
        IStatementFlow statementFlow,
        IReadoutParserSelector readoutParsers,
        IEntryFeeCalculator entryFeeCalculator,
        IActivityLog log,
        ITableLayoutStore layoutStore,
        IDayLockService dayLock,
        IAppSettingsService appSettings,
        IDataChangeBus bus,
        IWorkspaceMode mode,
        IEventFiles eventFiles,
        IToastService toasts,
        IUndoHistory undo)
        : base(localization)
    {
        _toasts = toasts;
        // Ctrl+Z / Ctrl+Y: one history per day table and one for «Мандатка» (UndoDayId).
        UseUndo(undo);
        UseDayLock(dayLock);
        _appSettings = appSettings;
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
        FileSaver = fileSaver;
        _statementFlow = statementFlow;
        _readoutParsers = readoutParsers;
        _entryFeeCalculator = entryFeeCalculator;
        _log = log;
        _eventFiles = eventFiles;
        _mode = mode;
        // Singleton VM: reload when the competition/day changes. SessionChanged may be raised on a
        // pool thread (session writes run inside RunAsync), so marshal onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // A drill-in's day grid lasts while the page is on screen (see _drillInDayGrid).
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsActive) && !IsActive)
                _drillInDayGrid = false;
        };
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Participants, DataArea.ParticipantDays, DataArea.Groups, DataArea.GroupDaySettings, DataArea.Regions, DataArea.Clubs, DataArea.Dusshes, DataArea.Chips, DataArea.Fees, DataArea.CustomFields, DataArea.Relay, DataArea.Readouts, DataArea.Days, DataArea.Competition, DataArea.ControlPoints, DataArea.CourseVariants, DataArea.Settings, DataArea.AppData);
        // Re-localize the day-table column headers on a language switch (their text is baked into the
        // band model at build time, so the bands must be rebuilt — the RosterTable picks it up).
        Localization.PropertyChanged += (_, _) =>
        {
            RebuildDayBands();
            OnPropertyChanged(nameof(ToggleRosterLabel));
        };
    }

    public override string NavKey => "Nav.Participants";
    public override string TitleKey => "Page.Participants.Title";
    public override string TextKey => "Page.Participants.Text";

    // ── Undo / redo
    // Each day table has its own history and «Мандатка» one more (У-2). A change made on a row goes to the history of
    // the row's own day (a day row) or of the roster (a roster row / cell) — normally the page's current one; right on
    // a day switch the rows on screen are still the previous day's, and so is their step (§9.2).

    /// <inheritdoc />
    public override Guid? UndoDayId => IsDayMode ? SelectedDay?.Day?.Id : null;

    // The history of a day table (dayId) or of the roster (null).
    private static UndoHistoryKey HistoryKey(string page, Guid? dayId) => new(page, dayId);

    private UndoEntry? CaptureDayRow(ParticipantDayRowViewModel row, string label)
        => CaptureUndo(HistoryKey(UndoKey, row.DayId), label);

    private UndoEntry? CaptureRoster(string label) => CaptureUndo(HistoryKey(UndoKey, null), label);

    // «column — row» for a change of a roster cell (the cells know their participant, not the row's name).
    private string RunnerLabel(string columnKey, Guid participantId) => UndoLabel(columnKey, RunnerName(participantId));

    /// <inheritdoc />
    /// <remarks>
    /// The debounced row saves (600 ms + the write), the field / chip / relay / status writes tracked with them, and a
    /// relay group pick a paste just queued for the next dispatcher turn.
    /// </remarks>
    public override async Task FlushPendingWritesAsync()
    {
        if (_relayBulkGroupQueued)
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await _pendingSaves.WaitAsync();
    }

    /// <inheritdoc />
    /// <remarks>Selects the row the undone step touched (by its day link or participant id), when it is shown.</remarks>
    public override void OnUndoApplied(UndoRevertResult result)
    {
        if (result.RowIds is not { Count: > 0 } ids)
            return;
        var touched = ids.ToHashSet();
        if (IsDayMode)
        {
            if (Participants.FirstOrDefault(r => touched.Contains(r.Id) || touched.Contains(r.ParticipantId)) is { } row)
                SelectedParticipant = row;
        }
        else if (Roster.FirstOrDefault(r => touched.Contains(r.ParticipantId)
                     || r.Days.Any(d => d.LinkId is { } link && touched.Contains(link))) is { } rosterRow)
        {
            SelectedRosterRow = rosterRow;
        }
    }

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
        // The relay leg number «100.2» — only on the days that run a relay (DayIds, set on load); the column builder
        // drops the block when no day does.
        new(RosterField.Relay, "Participants.Roster.Block.Relay", c => c.IsRelayDay),
        new(RosterField.RelayTeam, "Participants.Col.RelayTeamName", c => c.IsRelayDay),
        new(RosterField.RelayTeamNumber, "Participants.Col.RelayTeam", c => c.IsRelayDay),
        new(RosterField.RelayLeg, "Participants.Col.RelayLeg", c => c.IsRelayDay),
        new(RosterField.Chips, "Participants.Roster.Block.Chips", c => c.IsMember),
        // Per-day «Оплата» — relevant on the days the participant runs, like «Чіпи». The column builder
        // drops the block entirely unless the competition charges per day.
        new(RosterField.Payment, "Participants.Roster.Block.Payment", c => c.IsMember),
        // Per-day raised (late) fee flag, next to «Оплата» since they belong to the same mode. Dropped by
        // the column builder unless the competition charges per day AND the raised fee is enabled.
        new(RosterField.RaisedFee, "Participants.Roster.Block.RaisedFee", c => c.IsMember),
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
    /// Whether the competition charges the entry fee per day (mirrors CompetitionInfo.PaymentPerDay). It
    /// decides where the «Оплата» column writes: the day link in the day grid, a per-day block in the
    /// roster. Reloaded with the page; a change to it rebuilds both tables' columns.
    /// </summary>
    [ObservableProperty]
    private bool _paymentPerDay;

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

    // The competition's closed days. The roster spans every day at once, so its cells and merged blocks
    // need the whole set — unlike the day grid, which only ever shows the one selected day.
    private IReadOnlyCollection<Guid> _lockedDayIds = [];

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

    // The plugin result columns of the disciplines used on the shown day (see LoadAsync).
    private IReadOnlyList<ResultColumnDef> _dayResultColumns = [];

    /// <summary>
    /// The competition's additional participant fields (Р-13): one column each in both tables (the roster binds it,
    /// the day grid bakes it into <see cref="DayBands"/>). Reloaded with the page and after the fields dialog.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<CustomField> _customFields = [];

    partial void OnCustomFieldsChanged(IReadOnlyList<CustomField> value) => RebuildDayBands();

    private void RebuildDayBands()
        => DayBands = new Controls.DayColumnBuilder(Localization)
            .Build(ShowTeamColumn, ShowScoreColumn, Discounts, RaisedFeeEnabled, PaymentPerDay, _dayBands,
                _dayResultColumns, CustomFields, _dayUsesRelay);

    // Builds a row's additional-field cells from the loaded values. dayId: the day table the row is on (its undo
    // history), null for a roster row.
    private ParticipantFieldsViewModel FieldsFor(Guid participantId, IReadOnlyDictionary<string, string> values, Guid? dayId)
        => new(participantId, CustomFields, values, (id, cell) => RequestFieldSave(id, cell, dayId));

    // An additional-field edit: persist it (normalised, Р-18/Р-19) and show what was stored. Text cells commit on
    // leaving the cell, so there is no keystroke storm to debounce.
    private void RequestFieldSave(Guid participantId, ParticipantFieldCellViewModel cell, Guid? dayId)
    {
        var key = cell.Key;
        var value = cell.Value;
        var label = CustomFields.FirstOrDefault(f => f.Key == key)?.Label ?? key;
        var step = CaptureUndo(HistoryKey(UndoKey, dayId),
            string.Format(Localization.Get("Undo.Label.Cell"), label, RunnerName(participantId)));
        _pendingSaves.Track(SaveFieldAsync());

        async Task SaveFieldAsync()
        {
            try
            {
                var stored = await RunUndoAsync(step, () => _editor.SetParticipantFieldAsync(participantId, key, value));
                if (cell.Value == value)
                    cell.ApplySaved(stored);
            }
            catch
            {
                return; // never crash the UI over an autosave
            }
            // A plugin rule may read the field (the weighted relay's «Стать»): the results of the person's days follow.
            await RefreshDaysResultsAsync(dayId is { } d ? [d] : MemberDaysOf(participantId));
        }
    }

    /// <summary>Opens the «Додаткові поля» dialog (add / rename / delete fields); reloads the page after.</summary>
    [RelayCommand]
    private async Task EditCustomFieldsAsync()
    {
        if (_session.CurrentEvent is null)
            return;
        // Its add / rename / delete-field writes are steps in the «Мандатка» history (the fields are competition-level).
        var dialog = new CustomFieldsViewModel(Localization, _editor, _busy, UndoHistory);
        await dialog.LoadAsync();
        await _dialogs.ShowCustomFieldsAsync(dialog);
        if (dialog.Changed)
            await LoadAsync();
    }

    // The day grid's «Оплата» column binds a different property in each mode, so the bands must be rebuilt
    // when the mode changes (the roster table rebuilds itself off its own PaymentPerDay binding).
    partial void OnPaymentPerDayChanged(bool value) => RebuildDayBands();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRosterMode))]
    [NotifyPropertyChangedFor(nameof(IsDayMode))]
    [NotifyPropertyChangedFor(nameof(CanToggleDayLock))]
    [NotifyPropertyChangedFor(nameof(IsDayLocked))]
    [NotifyPropertyChangedFor(nameof(IsEffectiveDayLocked))]
    [NotifyPropertyChangedFor(nameof(CanEditStartOrder))]
    private DayOption? _selectedDay;

    /// <summary>True while the roster ("Мандатка") aggregate is shown.</summary>
    public bool IsRosterMode => SelectedDay?.IsRoster ?? false;

    /// <summary>True while a real day's participants are shown (the inverse of roster mode).</summary>
    public bool IsDayMode => !IsRosterMode;

    /// <summary>
    /// The lock closes ONE day, so it only makes sense while a real day is in view. In roster mode the
    /// table spans every day at once — a per-day lock has no single day to act on there, and each day's
    /// own cells already carry their lock (a closed day's cells rest read-only inside the live roster).
    /// </summary>
    public override bool CanToggleDayLock => base.CanToggleDayLock && IsDayMode;

    /// <summary>
    /// The roster isn't one day, so it reports no lock state of its own — otherwise the selector would
    /// show the session day's padlock next to a table that spans every day.
    /// </summary>
    public override bool IsDayLocked => IsDayMode && base.IsDayLocked;

    /// <summary>
    /// The single day the page is currently bound to, or null when there is no unambiguous day: a real day
    /// in day mode, or — in roster mode — the sole day of a single-day competition (the roster shows the
    /// whole competition, but with one day it still targets that day). Null when the roster spans multiple
    /// days (no single day to act on).
    /// </summary>
    private Guid? EffectiveDayId => EffectiveDay?.Id;

    /// <summary>The day <see cref="EffectiveDayId"/> refers to, or null when no single day is in view.</summary>
    private EventDay? EffectiveDay => IsDayMode
        ? SelectedDay?.Day
        : RosterDays.Count == 1 ? RosterDays[0] : null;

    /// <summary>True when a single day is unambiguously in view, gating the manual start-order action.</summary>
    public bool CanEditStartOrder => EffectiveDayId is not null;

    /// <summary>
    /// The gate for a toolbar action that writes to the day in view. Uses <see cref="EffectiveDay"/>
    /// rather than the page's own <see cref="IsDayLocked"/>, because the roster of a single-day
    /// competition acts on that day too while reporting no lock of its own (see <see cref="IsDayLocked"/>).
    /// Multi-day roster actions have no one day to check and pass; each row's per-day cells hold their
    /// own lock, and the editor refuses a closed day underneath in any case.
    /// </summary>
    /// <summary>
    /// True when the day the toolbar acts on is closed — what the action buttons grey out on. Differs
    /// from <see cref="IsDayLocked"/> (which reports nothing in roster mode, so the selector shows no
    /// padlock next to a table spanning every day): the roster of a SINGLE-day competition still acts
    /// on that one day, so its buttons must follow that day's lock.
    /// </summary>
    public bool IsEffectiveDayLocked => EffectiveDay is { IsLocked: true };

    private async Task<bool> EnsureEffectiveDayEditableAsync()
    {
        if (EffectiveDay is not { IsLocked: true } day)
            return true;

        await ExplainDayLockAsync(day.Number);
        return false;
    }

    /// <summary>
    /// The same gate for the view's own actions (the file-picking import handlers, which must run in
    /// code-behind for the window's StorageProvider). Overridden here so those go through the effective
    /// day rather than the session's.
    /// </summary>
    public override Task<bool> EnsureDayEditableForActionAsync() => EnsureEffectiveDayEditableAsync();

    /// <summary>The day picker is shown only when the competition has more than one real day. With a
    /// single day there is nothing to switch between (the roster and that day carry the same people),
    /// so we hide it and show whichever single view applies.</summary>
    public bool ShowDaySelector => RosterDays.Count > 1;

    // Cached per-day flag: true when the day's default discipline OR any group on the day (via its
    // DisciplineOverride) uses teams. Recomputed when the day's rows load (see ReloadContentAsync).
    private bool _dayUsesTeam;

    /// <summary>The team column is shown when the current real day's discipline uses it (rogaine) or
    /// any group on that day overrides to a team discipline.</summary>
    public bool ShowTeamColumn => _dayUsesTeam;

    // True when the shown day runs a relay in some group (the team / leg / «100.2» columns).
    private bool _dayUsesRelay;

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

    // Set by a drill-in: the page shows the day grid its filter targets even on a one-day competition, where it
    // otherwise always shows the roster (see ResolveSelectedDay), and keeps it through reloads while the page stays
    // on screen. Leaving the page clears it, so the next plain open shows the roster again.
    private bool _drillInDayGrid;

    /// <summary>
    /// Asks the page to open with a day-grid filter pre-applied (e.g. only chipless participants). Must be
    /// called before <see cref="LoadAsync"/>; the filter targets the current day's grid, so it forces day
    /// mode on load — on a one-day competition too. <see cref="ParticipantQuickFilter.None"/> clears any
    /// pending request.
    /// </summary>
    public void RequestQuickFilter(ParticipantQuickFilter filter)
    {
        _pendingQuickFilter = filter;
        _drillInDayGrid = filter != ParticipantQuickFilter.None;
    }

    /// <inheritdoc />
    /// <remarks>The row is a day link (the readiness check's participant): like a dashboard drill-in it shows the day
    /// grid — on a one-day competition too — until the page is left. Call before <see cref="LoadAsync"/>.</remarks>
    public override void RequestReveal(Guid id, string? columnKey = null)
    {
        _drillInDayGrid = true;
        base.RequestReveal(id, columnKey);
    }

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

    // Whether this competition offers the roster view at all (CompetitionInfo.RosterEnabled). Loaded
    // with the page and toggled from the «Дії» menu; drives whether DayOptions carries the sentinel.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleRosterLabel))]
    private bool _rosterEnabled = true;

    /// <summary>
    /// The roster toggle only makes sense with two or more days: with a single day the roster and that
    /// day show the same people, and hiding it would leave the page with nothing to switch to.
    /// </summary>
    public bool CanToggleRoster => RosterDays.Count > 1;

    /// <summary>The toggle's menu caption — "hide" while the roster is on, "show" while it is off.</summary>
    public string ToggleRosterLabel => Localization.Get(RosterEnabled
        ? "Participants.HideRoster"
        : "Participants.ShowRoster");

    /// <summary>
    /// Turns the roster («Мандатка») view on or off for this competition and persists the choice. When
    /// it goes off the page falls back to a real day; when it comes back on the roster is selected.
    /// </summary>
    [RelayCommand]
    private async Task ToggleRosterAsync()
    {
        if (_session.CurrentEvent is null || !CanToggleRoster)
            return;

        var enabled = !RosterEnabled;
        await _busy.RunAsync(() => _editor.SetRosterEnabledAsync(enabled));
        RosterEnabled = enabled;
        _rosterChosen = enabled;

        _syncingDay = true;
        try
        {
            RebuildDayOptions(RosterDays);
            SelectedDay = ResolveSelectedDay();
        }
        finally
        {
            _syncingDay = false;
        }

        RefreshDayLock();
        OnPropertyChanged(nameof(IsEffectiveDayLocked));
        await ReloadContentAsync();
    }

    /// <inheritdoc />
    /// <remarks>
    /// The full load path, not just the rows: the page's dropdowns (groups, regions, clubs, schools, ranks), the
    /// rental-chip set, the fee inputs and the additional fields all come from areas this page observes. Pending
    /// row and field saves finish first: LoadAsync cancels them, which is right for a session switch but would
    /// drop the operator's last edit here (see PendingSaves). Unlike LoadAsync, the rows of both views are merged
    /// into the existing row objects (<see cref="RowSync"/>) and every shared list (options, fields, days) is kept
    /// while its content is unchanged — so the table repaints exactly the cells another station changed, and the
    /// selected row, the scroll position and an open dropdown survive.
    /// </remarks>
    protected override async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _pendingSaves.WaitAsync();

        var hasEvent = _session.CurrentEvent is not null;
        var days = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDaysAsync())
            : (IReadOnlyList<EventDay>)[];
        // The roster table rebuilds its per-day columns when the day list changes, so the bound list is replaced
        // only when the day set moved; otherwise the fresh entities are swapped in silently (the page reads the
        // lock and the discipline off them).
        if (SameDayEntities(days))
            _rosterDays = days;
        else
            RosterDays = days;

        RosterEnabled = !hasEvent || await _busy.RunAsync(() => _editor.GetRosterEnabledAsync());

        await RefreshRentalChipsAsync(hasEvent);
        await RefreshRegionOptionsAsync(hasEvent);
        await RefreshClubOptionsAsync(hasEvent);
        await RefreshDusshOptionsAsync(hasEvent);
        await RefreshRankOptionsAsync();
        await RefreshFeeDataAsync(hasEvent);

        var fields = hasEvent ? await _busy.RunAsync(() => _editor.GetCustomFieldsAsync()) : [];
        if (!CustomFieldsEqual(CustomFields, fields))
            CustomFields = fields;
        // Which (day, group) requires which fields (a plugin discipline's RequiredFields) — only worth asking with fields.
        _requiredFields = hasEvent && fields.Count > 0
            ? (await _busy.RunAsync(() => _editor.GetRequiredFieldsAsync()))
                .ToDictionary(e => (e.DayId, e.GroupId), e => e.FieldKeys)
            : [];

        _lockedDayIds = days.Where(d => d.IsLocked).Select(d => d.Id).ToHashSet();

        _syncingDay = true;
        try
        {
            RebuildDayOptions(days);
            SelectedDay = ResolveSelectedDay();
        }
        finally
        {
            _syncingDay = false;
        }

        OnPropertyChanged(nameof(ShowDaySelector));
        OnPropertyChanged(nameof(CanToggleRoster));
        RefreshDayLock();
        OnPropertyChanged(nameof(IsEffectiveDayLocked));
        await ReloadContentAsync(merge: true);

        if (SelectedParticipant is { } dayRow && !Participants.Contains(dayRow))
            SelectedParticipant = null;
        if (SelectedRosterRow is { } rosterRow && !Roster.Contains(rosterRow))
            SelectedRosterRow = null;
    }

    // True when the loaded days are the ones the roster columns were built for (same ids and numbers, in order).
    private bool SameDayEntities(IReadOnlyList<EventDay> days)
    {
        if (_rosterDays.Count != days.Count)
            return false;
        for (var i = 0; i < days.Count; i++)
            if (_rosterDays[i].Id != days[i].Id || _rosterDays[i].Number != days[i].Number)
                return false;
        return true;
    }

    // Value equality for the additional-field set: a changed list reference rebuilds both tables' columns, so
    // the page keeps the old list while nothing in it moved.
    private static bool CustomFieldsEqual(IReadOnlyList<CustomField> a, IReadOnlyList<CustomField> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Key != y.Key || x.Label != y.Label || x.Kind != y.Kind || x.Source != y.Source || x.Order != y.Order
                || !x.Options.SequenceEqual(y.Options))
                return false;
        }
        return true;
    }

    /// <summary>Reloads the page for the current selection. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var hasEvent = _session.CurrentEvent is not null;
        var days = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDaysAsync())
            : (IReadOnlyList<EventDay>)[];
        RosterDays = days;

        // The per-competition roster switch, read before the options are rebuilt (it decides whether the
        // sentinel is in the list at all).
        RosterEnabled = !hasEvent || await _busy.RunAsync(() => _editor.GetRosterEnabledAsync());

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

        // The additional fields, before any row is built (each row gets one cell per field). The list is replaced
        // only when its content moved (a new reference rebuilds the columns).
        var fields = hasEvent ? await _busy.RunAsync(() => _editor.GetCustomFieldsAsync()) : [];
        if (!CustomFieldsEqual(CustomFields, fields))
            CustomFields = fields;
        // Which (day, group) requires which fields (a plugin discipline's RequiredFields) — only worth asking with fields.
        _requiredFields = hasEvent && fields.Count > 0
            ? (await _busy.RunAsync(() => _editor.GetRequiredFieldsAsync()))
                .ToDictionary(e => (e.DayId, e.GroupId), e => e.FieldKeys)
            : [];

        _lockedDayIds = days.Where(d => d.IsLocked).Select(d => d.Id).ToHashSet();

        _syncingDay = true;
        try
        {
            RebuildDayOptions(days);

            // A pending quick filter (from a dashboard drill-in) targets the current day's chip/group
            // column, so force day mode for the current day regardless of the remembered roster choice.
            if (_pendingQuickFilter != ParticipantQuickFilter.None || HasPendingReveal)
                _rosterChosen = false;

            SelectedDay = ResolveSelectedDay();
        }
        finally
        {
            _syncingDay = false;
        }

        OnPropertyChanged(nameof(ShowDaySelector));
        OnPropertyChanged(nameof(CanToggleRoster));
        RefreshDayLock();
        OnPropertyChanged(nameof(IsEffectiveDayLocked));
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
    /// <summary>Writes the export to the file the user picked (handles a target locked by Excel/Word). Used by the view.</summary>
    public IExportFileSaver FileSaver { get; }

    public Task<ParticipantExportResult?> ExportAsync(CsvParticipantData view) =>
        _exportFlow.RunAsync(view, EffectiveDay, multiDay: RosterDays.Count > 1);

    /// <summary>Opens the participant-statement («відомість») modal for the given table's current view (the
    /// page hands in the active table). Scopes per-day fields to the day in view (day mode / a single-day
    /// roster), or gathers them across all days when the roster spans several days.</summary>
    public async Task OpenStatementAsync(Controls.SheetTable table) =>
        ApplyPrintedMarks(await _statementFlow.OpenAsync(table, EffectiveDayId));

    /// <summary>Prints the statement for the given table's current view straight to the configured A4 printer,
    /// with no preview/settings modal (the Ctrl+Shift+P «швидкий друк» path). Scopes per-day fields like
    /// <see cref="OpenStatementAsync"/>.</summary>
    public async Task PrintStatementDirectAsync(Controls.SheetTable table) =>
        ApplyPrintedMarks(await _statementFlow.PrintDirectAsync(table, EffectiveDayId));

    // Reflects the «Відомість надрукована» flags a statement run just set: the rows' field cells are updated in
    // memory, or — when the flag field was only now created — the page reloads so its new column appears.
    private void ApplyPrintedMarks(StatementOutcome outcome)
    {
        if (outcome.MarkedParticipantIds.Count == 0)
            return;

        var key = CustomField.StatementPrintedKey;
        if (outcome.FieldCreated || CustomFields.All(f => f.Key != key))
        {
            _ = LoadAsync();
            return;
        }

        var marked = outcome.MarkedParticipantIds.ToHashSet();
        foreach (var row in Participants.Where(r => marked.Contains(r.ParticipantId)))
            row.Fields[key].ApplySaved("1");
        foreach (var row in Roster.Where(r => marked.Contains(r.ParticipantId)))
            row.Fields[key].ApplySaved("1");
    }

    // Rebuilds the day options only when the day set changed, so the ComboBox keeps a valid
    // SelectedItem reference across reloads. The roster sentinel leads the list while the roster view
    // is enabled; with it off the picker lists the real days alone.
    private void RebuildDayOptions(IReadOnlyList<EventDay> days)
    {
        if (SameDays(days))
        {
            // The options are reused so the ComboBox keeps a valid SelectedItem — but they still wrap
            // the entities they were built with. Re-point them at this read, or the page would judge
            // the day's lock (IsEffectiveDayLocked) from a stale EventDay.
            DayOption.SyncAll(DayOptions, days);
            return;
        }

        DayOptions.Clear();
        if (RosterEnabled)
            DayOptions.Add(DayOption.Roster(Localization, "Participants.Roster"));
        foreach (var day in days)
            DayOptions.Add(new DayOption(day, Localization));
    }

    // Picks the option to show: honour a remembered roster choice, else follow the session day, else the
    // first real day. When the picker is hidden (a single real day) the roster wins while it is enabled —
    // there is no UI to switch back to it, so a stale day choice must not leave the page stuck in day mode. The one
    // exception is a dashboard drill-in (_drillInDayGrid): its filter lives on the day grid, and it ends when the
    // page is left. With the roster off there is no sentinel to fall back to, so a real day is always chosen.
    private DayOption? ResolveSelectedDay()
    {
        if (RosterEnabled && (_rosterChosen || (RosterDays.Count <= 1 && !_drillInDayGrid)))
            return DayOptions.FirstOrDefault(o => o.IsRoster);

        var current = _session.CurrentDay?.Number;
        return DayOptions.FirstOrDefault(o => !o.IsRoster && o.Number == current)
            ?? DayOptions.FirstOrDefault(o => !o.IsRoster)
            ?? DayOptions.FirstOrDefault();
    }

    private bool SameDays(IReadOnlyList<EventDay> days)
    {
        // The leading roster sentinel is present only while the roster view is on; the rest must match
        // the day numbers in order.
        var offset = RosterEnabled ? 1 : 0;
        if (DayOptions.Count != days.Count + offset)
            return false;
        if (RosterEnabled != (DayOptions.Count > 0 && DayOptions[0].IsRoster))
            return false;
        for (var i = 0; i < days.Count; i++)
            if (DayOptions[i + offset].Number != days[i].Number)
                return false;
        return true;
    }

    // The group options the current rows were built with, kept while their content is unchanged so a live refresh
    // leaves every group combo's items (and selected instance) alone.
    private IReadOnlyList<GroupOption> _dayGroupOptions = [];
    private Dictionary<Guid, IReadOnlyList<GroupOption>> _groupsByDay = new();

    // Loads the rows for the current mode (roster vs the selected real day). With merge the fresh rows are applied
    // onto the existing row objects (RowSync) — a live refresh; otherwise the tables are rebuilt from scratch.
    private async Task ReloadContentAsync(bool merge = false)
    {
        if (!merge)
        {
            ClearParticipantRows();
            ClearRosterRows();
        }
        // Rows compute their totals when built or applied, so no deferred fee recompute is owed anymore.
        _feesDirty = false;

        if (_session.CurrentEvent is null)
        {
            if (merge)
            {
                ClearParticipantRows();
                ClearRosterRows();
            }
            return;
        }

        if (IsRosterMode)
        {
            if (merge)
                ClearParticipantRows();

            var roster = await _busy.RunAsync(() => _editor.GetParticipantRosterAsync());
            // The roster's group dropdowns need each day's groups; gather them once.
            var groupsByDay = await _busy.RunAsync(() => LoadGroupsByDayAsync());
            // The roster spans all days, so its team column shows when ANY day uses teams (its default
            // discipline or any group's override).
            var showsTeam = await _busy.RunAsync(() => AnyDayUsesTeamAsync());
            var showsScore = await _busy.RunAsync(() => AnyDayUsesScoreAsync());

            // Keep each day's option list while its groups are unchanged, so the cells' combos keep their items.
            foreach (var (dayId, options) in groupsByDay.ToList())
                if (_groupsByDay.TryGetValue(dayId, out var previous) && SameGroupOptions(previous, options))
                    groupsByDay[dayId] = previous;
            _groupsByDay = groupsByDay;

            // The «Естафета» block spans the days that run a relay (a relay group or a team) — the same on every row.
            var relayDays = roster.Count == 0
                ? []
                : roster[0].Days.Where(c => c.IsRelayDay).Select(c => c.DayId).ToList();
            var relayBlocks = Blocks.Where(b => b.Field is RosterField.Relay or RosterField.RelayTeam
                or RosterField.RelayTeamNumber or RosterField.RelayLeg).ToList();
            var relayDaysChanged = relayBlocks.Any(b => b.DayIds is not { } before || !before.SequenceEqual(relayDays));
            foreach (var block in relayBlocks)
                block.DayIds = relayDays;

            var columnsChanged = !merge || _rosterShowsTeam != showsTeam || _rosterShowsScore != showsScore || relayDaysChanged;
            _rosterShowsTeam = showsTeam;
            _rosterShowsScore = showsScore;

            RowSync.Sync(Roster, roster,
                itemKey: row => row.ParticipantId,
                rowKey: row => row.ParticipantId,
                create: row => CreateRosterRow(row, groupsByDay),
                update: (vm, row) => ApplyRosterRow(vm, row, groupsByDay));

            if (columnsChanged)
            {
                OnPropertyChanged(nameof(RosterShowsTeam));
                OnPropertyChanged(nameof(RosterShowsScore));
                // A column rebuild repaints the whole table, so a live refresh raises it only when the set moved.
                RosterColumnsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (_session.CurrentDay is not null)
        {
            if (merge)
                ClearRosterRows();

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
            if (SameGroupOptions(_dayGroupOptions, groupOptions))
                groupOptions = _dayGroupOptions;
            _dayGroupOptions = groupOptions;

            // Teams apply when the day's default discipline uses them, or any group on the day
            // overrides to a team discipline (e.g. a rogaine group on a set-course day).
            var day = _session.CurrentDay;
            var usesTeam =
                _strategies.For(day.DefaultDiscipline).UsesParticipantColumn(BusinessLogic.Enums.ParticipantColumn.Team)
                || dayGroups.Any(g => _strategies.For(g.DisciplineOverride ?? day.DefaultDiscipline)
                    .UsesParticipantColumn(BusinessLogic.Enums.ParticipantColumn.Team));
            // Relay columns («Номер етапу», team, leg, team name) when the day or any group runs a relay, or some
            // runner of the day is on a relay team.
            var usesRelay =
                _strategies.For(day.DefaultDiscipline).Competitors == CompetitorModel.TeamWithLegs
                || dayGroups.Any(g => _strategies.For(g.DisciplineOverride ?? day.DefaultDiscipline).Competitors
                    == CompetitorModel.TeamWithLegs)
                || rows.Any(r => r.Relay is not null);
            // «Бали» applies when the day's discipline scores points, or any group overrides to one.
            var usesScore =
                _strategies.For(day.DefaultDiscipline).UsesControlPointPoints
                || dayGroups.Any(g => _strategies.For(g.DisciplineOverride ?? day.DefaultDiscipline).UsesControlPointPoints);
            // A plugin discipline's own result columns (e.g. «Штраф»), once per key, for every discipline in use.
            IReadOnlyList<ResultColumnDef> resultColumns = new[] { day.DefaultDiscipline }
                .Concat(dayGroups.Select(g => g.DisciplineOverride ?? day.DefaultDiscipline))
                .Distinct()
                .SelectMany(id => _strategies.For(id).ResultColumns)
                .DistinctBy(c => c.Key)
                .ToList();
            // Kept while the keys are the same: the columns are baked into the bands and the rows' extras view.
            if (_dayResultColumns.Select(c => c.Key).SequenceEqual(resultColumns.Select(c => c.Key)))
                resultColumns = _dayResultColumns;

            var bandsChanged = !merge || _dayUsesTeam != usesTeam || _dayUsesRelay != usesRelay
                || _dayUsesScore != usesScore || !ReferenceEquals(_dayResultColumns, resultColumns);
            _dayUsesTeam = usesTeam;
            _dayUsesRelay = usesRelay;
            _dayUsesScore = usesScore;
            _dayResultColumns = resultColumns;

            RowSync.Sync(Participants, rows,
                itemKey: row => row.LinkId,
                rowKey: row => row.Id,
                create: row => CreateDayRow(row, groupOptions),
                update: (vm, row) => ApplyDayRow(vm, row, groupOptions));

            if (bandsChanged)
            {
                OnPropertyChanged(nameof(ShowTeamColumn));
                OnPropertyChanged(nameof(ShowScoreColumn));
                RebuildDayBands();
            }
        }
        else
        {
            if (merge)
            {
                ClearParticipantRows();
                ClearRosterRows();
            }
            _dayUsesTeam = false;
            _dayUsesRelay = false;
            _dayUsesScore = false;
            _dayResultColumns = [];
            OnPropertyChanged(nameof(ShowTeamColumn));
            OnPropertyChanged(nameof(ShowScoreColumn));
            RebuildDayBands();
        }

        MarkRequiredFields();
        RecomputeStatusInfo();
    }

    // (day, group) → the fields its discipline requires; empty when no plugin discipline asks for any.
    private Dictionary<(Guid DayId, Guid GroupId), IReadOnlyList<string>> _requiredFields = [];

    // Tints an empty required field red: a day row by its group on the shown day, a roster row by every day it runs.
    private void MarkRequiredFields()
    {
        IReadOnlyList<string> For(Guid? dayId, Guid? groupId) =>
            dayId is { } d && groupId is { } g && _requiredFields.TryGetValue((d, g), out var keys) ? keys : [];

        var dayId = EffectiveDayId;
        foreach (var row in Participants)
            row.Fields.MarkRequired(For(dayId, row.SelectedGroup?.Id).ToList());
        foreach (var row in Roster)
            row.Fields.MarkRequired(row.Days.Where(c => c.IsMember).SelectMany(c => For(c.DayId, c.SelectedGroup?.Id)).Distinct().ToList());
    }

    // Value equality for a group option list (what the combos show and what the age / chip checks read).
    private static bool SameGroupOptions(IReadOnlyList<GroupOption> a, IReadOnlyList<GroupOption> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Id != y.Id || x.Label != y.Label || x.MinBirthYear != y.MinBirthYear
                || x.MaxBirthYear != y.MaxBirthYear || x.ControlCount != y.ControlCount || x.IsRelay != y.IsRelay)
                return false;
        }
        return true;
    }

    // Applies a fresh day row onto its existing row object (a live refresh): the row's own values, then the
    // additional-field cells — rebuilt only when the field set changed (the columns are rebuilt then too).
    private void ApplyDayRow(ParticipantDayRowViewModel vm, ParticipantDayRow row, IReadOnlyList<GroupOption> groupOptions)
    {
        vm.Apply(row, groupOptions, _regionOptions, _clubOptions, _dusshOptions, _rankOptions, Discounts, _feeContext,
            _dayResultColumns);
        if (vm.Fields.BuiltFor(CustomFields))
            vm.Fields.Apply(row.Fields);
        else
            vm.Fields = FieldsFor(row.ParticipantId, row.Fields, row.DayId);
    }

    // Applies a fresh roster row onto its existing row object (a live refresh), cells by day id.
    private void ApplyRosterRow(
        ParticipantRosterRowViewModel vm,
        ParticipantRosterRow row,
        IReadOnlyDictionary<Guid, IReadOnlyList<GroupOption>> groupsByDay)
    {
        vm.Apply(row, groupsByDay, cell => CreateRosterCell(row.ParticipantId, cell, groupsByDay),
            _regionOptions, _clubOptions, _dusshOptions, _rankOptions, Discounts, _feeContext);
        if (vm.Fields.BuiltFor(CustomFields))
            vm.Fields.Apply(row.Fields);
        else
            vm.Fields = FieldsFor(row.ParticipantId, row.Fields, dayId: null);
        vm.ApplyDayLocks(_lockedDayIds);
    }

    // Builds the day-group options list for a given day. The roster prepends the "(none)" / "не
    // участвує" sentinel (picking it leaves the day); the day grid passes includeNone: false so its
    // dropdown offers only real groups — a participant shown there is always a day member.
    private async Task<IReadOnlyList<GroupOption>> BuildGroupOptionsAsync(Guid dayId, bool includeNone = false)
    {
        var groups = await _editor.GetGroupsForDayAsync(dayId);
        // The chip-capacity check reads SportIdent card models off the number, so it only makes sense when
        // the competition is punched with SportIdent (app-level «Тип відмітки»); with another system the
        // course length is left at 0 and no chip is ever flagged.
        var sportIdent = await UsesSportIdentCardsAsync();
        var options = new List<GroupOption>(groups.Count + (includeNone ? 1 : 0));
        if (includeNone)
            options.Add(new GroupOption(null, string.Empty, Localization));
        foreach (var g in groups)
        {
            // The longest course a member may run that day — what their chip must be able to hold.
            var strategy = _strategies.For(g.DisciplineOverride ?? g.DayDefaultDiscipline);
            options.Add(new GroupOption(g.GroupId, g.Name, Localization, g.MinBirthYear, g.MaxBirthYear,
                sportIdent ? ChipCapacity.NeededPunches(g, strategy) : 0,
                isRelay: strategy.Competitors == CompetitorModel.TeamWithLegs));
        }
        return options;
    }

    // True when the app-level «Тип відмітки» is SportIdent — the only system whose chip numbers encode the
    // card model (SiCard). Read fresh each time so a Settings change applies on the next page load.
    private async Task<bool> UsesSportIdentCardsAsync()
    {
        var type = await _appSettings.GetReadoutTypeAsync();
        return type == BusinessLogic.Enums.ReadoutType.SportIdent;
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
    {
        var vm = NewDayRow(row, groupOptions);
        vm.SetResultColumns(_dayResultColumns);
        return vm;
    }

    private ParticipantDayRowViewModel NewDayRow(ParticipantDayRow row, IReadOnlyList<GroupOption> groupOptions)
    {
        var vm = NewDayRowCore(row, groupOptions);
        vm.Fields = FieldsFor(row.ParticipantId, row.Fields, row.DayId);
        return vm;
    }

    private ParticipantDayRowViewModel NewDayRowCore(ParticipantDayRow row, IReadOnlyList<GroupOption> groupOptions)
        => new(row, groupOptions, _regionOptions, _clubOptions, _dusshOptions, _rankOptions, Discounts, _feeContext, Localization, _strategies,
            RequestRowSave, LeaveDay, RequestDayRowChipChange,
            RequestDayRowRegionChange, RequestDayRowAddRegion,
            RequestDayRowClubChange, RequestDayRowAddClub,
            RequestDayRowDusshChange, RequestDayRowAddDussh,
            RequestRowRaisedFeeChange, RequestRowDiscountChange,
            RequestDayRowResultStatusChange, RequestDayRowBonusChange, RequestDayRowPaymentChange,
            RequestDayRowRelayPlace, RequestDayRowRelayTeamEdit, RequestDayRowRelayGroupChange, RequestDayRowRelayBulkGroup);

    private ParticipantRosterRowViewModel CreateRosterRow(
        ParticipantRosterRow row,
        IReadOnlyDictionary<Guid, IReadOnlyList<GroupOption>> groupsByDay)
    {
        var cells = new List<RosterDayCellViewModel>(row.Days.Count);
        foreach (var cell in row.Days)
            cells.Add(CreateRosterCell(row.ParticipantId, cell, groupsByDay));
        var rosterRow = new ParticipantRosterRowViewModel(row, cells, _regionOptions, _clubOptions, _dusshOptions, _rankOptions, Discounts, _feeContext, Localization,
            RequestRosterRowSave,
            RequestRosterRowRegionChange, RequestRosterRowAddRegion,
            RequestRosterRowClubChange, RequestRosterRowAddClub,
            RequestRosterRowDusshChange, RequestRosterRowAddDussh,
            RequestRosterRaisedFeeChange, RequestRosterDiscountChange);

        rosterRow.Fields = FieldsFor(row.ParticipantId, row.Fields, dayId: null);

        // Mark the closed days before the row goes live, so a locked cell never accepts a first edit.
        rosterRow.ApplyDayLocks(_lockedDayIds);
        return rosterRow;
    }

    // One per-day cell of a roster row, wired to the page's per-cell callbacks.
    private RosterDayCellViewModel CreateRosterCell(
        Guid participantId, RosterDayCell cell, IReadOnlyDictionary<Guid, IReadOnlyList<GroupOption>> groupsByDay)
    {
        var options = groupsByDay.TryGetValue(cell.DayId, out var o)
            ? o
            : [new GroupOption(null, string.Empty, Localization)];
        return new RosterDayCellViewModel(participantId, cell, options, Localization, RequestCellGroupChange, RequestCellChipChange,
            RequestCellStartTimeChange, RequestCellOutOfCompetitionChange, RequestCellResultStatusChange, RequestCellBonusChange,
            RequestCellPaymentChange, RequestCellRaisedFeeChange, RequestCellRelayPlace, RequestCellRelayTeamEdit,
            RequestCellRelayGroupChange, RequestCellRelayBulkGroup);
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
        // The edits of the day being left land first (§9.2): a row save is debounced 0.6 s and writes to the
        // session's day, so switching first would drop it (LoadAsync cancels the timers) or — had it survived — write
        // it into the new day. Waiting here writes it on its own day, under the step already in that day's history.
        await FlushPendingWritesAsync();
        // Switching the session day re-raises SessionChanged → LoadAsync, which reloads the rows.
        await _busy.RunAsync(() => _session.SetCurrentDayAsync(day));
    }

    [RelayCommand]
    private async Task AddParticipantAsync()
    {
        if (_session.CurrentEvent is null || !await EnsureEffectiveDayEditableAsync())
            return;

        if (IsRosterMode)
        {
            // Roster add: create a participant that starts out not participating on any day, then
            // append the aggregate row. Reuse the cached per-day group options for its cells. Undoing it deletes the
            // row again (the reload after the undo drops it).
            var addStep = CaptureUndoAction(HistoryKey(UndoKey, null), Localization.Get("Undo.Label.AddRow"));
            var roster = await RunUndoAsync(addStep, () => _busy.RunAsync(() => _editor.AddRosterParticipantAsync()));
            if (roster is null)
                return;
            var groupsByDay = await _busy.RunAsync(() => LoadGroupsByDayAsync());
            Roster.Add(CreateRosterRow(roster, groupsByDay));
            // The per-day columns are derived from the day set; raise so they appear when this was the
            // first row added to a previously empty roster.
            RosterColumnsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_session.CurrentDay is not { } currentDay)
            return;

        var dayId = currentDay.Id;
        var groupOptions = await _busy.RunAsync(() => BuildGroupOptionsAsync(dayId));
        var step = CaptureUndoAction(HistoryKey(UndoKey, dayId), Localization.Get("Undo.Label.AddRow"));
        var row = await RunUndoAsync(step, () => _busy.RunAsync(() => _editor.AddParticipantToDayAsync()));
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
        if (row.IsApplying)
            return;
        _ = LeaveDayAsync(row, CaptureDayRow(row, UndoLabel("Participants.Col.Group", row.FullName)));
    }

    private async Task LeaveDayAsync(ParticipantDayRowViewModel row, UndoEntry? step)
    {
        // Their last day: leaving it deletes the person from the competition — say so first; a «no» puts the row back.
        if (!await ConfirmLastDayAsync(row.ParticipantId))
        {
            await LoadAsync();
            return;
        }
        CancelRowSave(row.Id);

        if (ReferenceEquals(SelectedParticipant, row))
            SelectedParticipant = GridSelection.NeighbourAfterRemoval(Participants, row);
        Participants.Remove(row);

        // The runner leaves the ranking: the others' places move up.
        var (linkId, participantId, dayId) = (row.Id, row.ParticipantId, row.DayId);
        _pendingSaves.Track(WriteThenRefreshAsync(step, dayId, () => _editor.RemoveParticipantFromDayAsync(linkId, participantId)));
    }

    // Removing someone from their only day deletes them from the competition (RemoveParticipantFromDayAsync): ask.
    private async Task<bool> ConfirmLastDayAsync(Guid participantId)
    {
        if (await Task.Run(() => _editor.CountParticipantDaysAsync(participantId)) > 1)
            return true;
        return await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization, titleKey: "Participants.Delete.ConfirmTitle", messageKey: "Participants.LeaveLastDay.Message"));
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
            // The last day of the person deletes them from the competition — the question says which it is.
            var lastDay = await Task.Run(() => _editor.CountParticipantDaysAsync(row.ParticipantId)) <= 1;
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.Delete.ConfirmTitle",
                messageKey: lastDay ? "Participants.Delete.LastDayMessage" : "Participants.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        CancelRowSave(row.Id);

        // One step, captured once the delete is confirmed; undoing it puts the row back (the reload after the undo
        // brings it, RowSync) with everything that went with it.
        var step = CaptureUndoAction(HistoryKey(UndoKey, row.DayId),
            string.Format(Localization.Get("Undo.Label.DeleteRow"), row.FullName));

        // Remove from the grid immediately; run the SQLite delete in the background.
        if (ReferenceEquals(SelectedParticipant, row))
            SelectedParticipant = GridSelection.NeighbourAfterRemoval(Participants, row);
        Participants.Remove(row);

        // As for leaving the day: the others' places move up.
        var (linkId, participantId, dayId) = (row.Id, row.ParticipantId, row.DayId);
        _pendingSaves.Track(WriteThenRefreshAsync(step, dayId, () => _editor.RemoveParticipantFromDayAsync(linkId, participantId)));

        if (confirmed)
            RequestGridFocus();
    }

    // ── Roster ("Мандатка") delete
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

        // Deleting from «Мандатка» deletes the person with every day — not while one of those days is closed.
        if (RosterDays.FirstOrDefault(d => d.IsLocked && row.Days.Any(c => c.IsMember && c.DayId == d.Id)) is { } closed)
        {
            await ExplainDayLockAsync(closed.Number);
            return;
        }

        if (!skipConfirm)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.Delete.ConfirmTitle",
                messageKey: "Participants.Roster.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        CancelRowSave(row.ParticipantId);

        var step = CaptureUndoAction(HistoryKey(UndoKey, null),
            string.Format(Localization.Get("Undo.Label.DeleteRow"), row.FullName));

        // Remove from the grid immediately; run the SQLite delete in the background.
        if (ReferenceEquals(SelectedRosterRow, row))
            SelectedRosterRow = GridSelection.NeighbourAfterRemoval(Roster, row);
        Roster.Remove(row);

        // The person leaves the ranking of every day they ran: those days' places move up.
        var participantId = row.ParticipantId;
        var memberDays = row.Days.Where(c => c.IsMember).Select(c => c.DayId).ToList();
        _pendingSaves.Track(DeleteThenRefreshAsync());

        async Task DeleteThenRefreshAsync()
        {
            try
            {
                await RunUndoAsync(step, () => _editor.DeleteParticipantAsync(participantId));
            }
            catch
            {
                return;
            }
            await RefreshDaysResultsAsync(memberDays);
        }
    }

    // ── Day-row chip change (conflict-resolved, not part of the debounced save)
    // A chip edit on the day grid may collide with another competitor on the same day. Confirm the
    // reassignment; on yes, the editor moves the chip (clearing the previous holder); on no, the cell
    // reverts to the last committed value. Resolved off the debounced row save so the prompt is shown
    // exactly when the edit commits (the chip box updates on lost focus).
    private void RequestDayRowChipChange(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        // The step is taken now (the edit's own); the «Забрати» / «Обміняти» answer comes later and writes under it.
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.Chip", row.FullName));
        _pendingSaves.Track(ResolveDayRowChipAsync(row, step));
    }

    private async Task ResolveDayRowChipAsync(ParticipantDayRowViewModel row, UndoEntry? step)
    {
        var newChip = (row.Chip ?? string.Empty).Trim();
        // The row's own day (normally the session's): the chip belongs to the day it was typed on.
        var (participantId, dayId) = (row.ParticipantId, row.DayId);

        var written = false;
        await _chipGate.WaitAsync();
        try
        {
            var previousChip = (row.CommittedChip ?? string.Empty).Trim();
            var choice = await ResolveChipReassignmentAsync(participantId, dayId, newChip, previousChip);
            if (choice == ChipConflictChoice.Cancel)
            {
                // Rejected: restore the previous chip without re-triggering this handler.
                row.SetChipSilently(previousChip);
                return;
            }

            row.MarkChipCommitted(newChip);
            if (choice == ChipConflictChoice.Swap)
            {
                await RunUndoAsync(step, () => _editor.SwapParticipantDayChipsAsync(participantId, dayId, newChip, previousChip));
                // The other holder took this participant's old chip; mirror that in the grid.
                SetChipOnConflictingDayRow(participantId, newChip, previousChip);
            }
            else
            {
                await RunUndoAsync(step, () => _editor.ReassignParticipantDayChipAsync(participantId, dayId, newChip));
                // The other holder (if any) had their chip cleared in the DB; refresh that row in the grid.
                SetChipOnConflictingDayRow(participantId, newChip, string.Empty);
            }
            written = true;
        }
        catch
        {
            // never crash the UI over a chip edit (a closed day, a lost connection); the next reload shows the store
        }
        finally
        {
            _chipGate.Release();
        }
        // The chip decides which read-out is the runner's (and the other holder's): their results and the day's places.
        if (written)
            await RefreshDayResultsAsync(dayId, evenWithoutReadouts: true);
    }

    // After a reassignment, replace the chip on whatever OTHER day-grid row was showing it, so the UI
    // matches the DB (only one competitor holds a chip per day). handBack is blank for a plain
    // take-over and the traded chip for a swap.
    private void SetChipOnConflictingDayRow(Guid keepParticipantId, string chip, string handBack)
    {
        if (chip.Length == 0)
            return;
        // Program-made: the store already holds it (written by the step above).
        using var programmatic = UndoGesture.Programmatic();
        foreach (var other in Participants)
        {
            if (other.ParticipantId == keepParticipantId)
                continue;
            if (OrientPyx.Plugins.Sdk.Toolkit.TextKey.Same((other.Chip ?? string.Empty).Trim(), chip))
            {
                other.SetChipSilently(handBack);
                other.MarkChipCommitted(handBack);
            }
        }
    }

    // What to do with a chip edit that collides with another competitor on the same day.
    private enum ChipConflictChoice
    {
        /// <summary>The user declined - revert the cell.</summary>
        Cancel,

        /// <summary>Take the chip; the previous holder is left without one.</summary>
        Take,

        /// <summary>Trade: the previous holder gets the chip this participant is giving up.</summary>
        Swap
    }

    // Shared confirm step for a chip reassignment (day grid and roster). Returns Take when the chip is
    // free or the user confirmed taking it, Swap when they chose to trade the two chips, and Cancel
    // when they declined. A blank chip is always free. The swap option is offered only when this
    // participant already had a chip to hand over (previousChip) - there is nothing to trade otherwise.
    private async Task<ChipConflictChoice> ResolveChipReassignmentAsync(
        Guid participantId, Guid dayId, string chip, string previousChip)
    {
        if (chip.Length == 0)
            return ChipConflictChoice.Take;

        var holder = await _busy.RunAsync(() => _editor.FindChipHolderAsync(dayId, chip, participantId));
        if (holder is null)
            return ChipConflictChoice.Take;

        var who = string.IsNullOrWhiteSpace(holder) ? Localization.Get("Participants.Chip.UnnamedHolder") : holder;
        var canSwap = previousChip.Length > 0;
        var dialog = new ConfirmDialogViewModel(
            Localization,
            titleKey: "Participants.Chip.Reassign.Title",
            messageKey: canSwap ? "Participants.Chip.Reassign.SwapMessage" : "Participants.Chip.Reassign.Message",
            confirmKey: "Participants.Chip.Reassign.Confirm",
            cancelKey: "Common.Cancel")
        {
            AlternateKey = canSwap ? "Participants.Chip.Reassign.Swap" : null,
            MessageArgs = canSwap ? [chip, who, previousChip] : [chip, who]
        };

        return await _dialogs.ChooseAsync(dialog) switch
        {
            ConfirmDialogResult.Confirm => ChipConflictChoice.Take,
            ConfirmDialogResult.Alternate => ChipConflictChoice.Swap,
            _ => ChipConflictChoice.Cancel
        };
    }

    // ── Day-row autosave (debounced per row)
    // The undo step is captured here, at the change (the write is 0.6 s later): the cell's edit session, a paste /
    // bulk-edit gesture, or a step of its own named after the column. Two changes of a row within the debounce are one
    // write — the earlier step is left empty and skipped (У-16).
    private void RequestRowSave(ParticipantDayRowViewModel row, string columnKey)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel(columnKey, row.FullName));
        var cts = RestartRowSave(row.Id, ref step);
        _pendingSaves.Track(SaveRowDebouncedAsync(row, step, cts));
    }

    // Cancels the row's pending save and registers a new one. A program-made change (no step of its own) keeps the
    // step of the edit whose save it replaces, so that edit is still undoable.
    private CancellationTokenSource RestartRowSave(Guid key, ref UndoEntry? step)
    {
        if (_saveTimers.TryGetValue(key, out var existing))
            existing.Cancel();
        if (step is null && _saveSteps.TryGetValue(key, out var pending) && !pending.Closed)
            step = pending;

        var cts = new CancellationTokenSource();
        _saveTimers[key] = cts;
        if (step is null)
            _saveSteps.Remove(key);
        else
            _saveSteps[key] = step;
        return cts;
    }

    // Drops a row's pending save (the row is being deleted, or a batch writes it instead).
    private void CancelRowSave(Guid key)
    {
        if (_saveTimers.Remove(key, out var cts))
            cts.Cancel();
        _saveSteps.Remove(key);
    }

    // A finished save forgets its timer and step — unless a newer save of the row has taken them over.
    private void EndRowSave(Guid key, CancellationTokenSource cts)
    {
        if (_saveTimers.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
        {
            _saveTimers.Remove(key);
            _saveSteps.Remove(key);
        }
    }

    private async Task SaveRowDebouncedAsync(ParticipantDayRowViewModel row, UndoEntry? step, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await Task.Delay(SaveDebounce, token);
            await RefuseTakenNumberAsync(row.ParticipantId, row.Number, kept => row.Number = kept);
            token.ThrowIfCancellationRequested();
            var dto = row.ToRow();
            // Once started, the write runs to its end: cancelled half-way it could leave the participant saved but not
            // the day link — a step partly in the store that the history would take for an empty one.
            await RunUndoAsync(step, () => _editor.UpdateParticipantDayRowAsync(dto, CancellationToken.None));
        }
        catch (OperationCanceledException) { return; }
        catch { return; /* never crash the UI over an autosave */ }
        finally
        {
            EndRowSave(row.Id, cts);
        }
        // The row's group, start time, «П/К», team, qualification or birth date move results (a plugin rule may read
        // any of the person's values): the day's places follow at once.
        await RefreshDayResultsAsync(row.DayId);
    }

    // A start number another participant already holds is not saved (numbers are unique per competition): say whose
    // it is and put the cell back to the number the participant keeps — instead of a silent revert on the next reload.
    private async Task RefuseTakenNumberAsync(Guid participantId, string? number, Action<string> putBack)
    {
        if (await Task.Run(() => _editor.FindNumberConflictAsync(participantId, number ?? string.Empty)) is not { } conflict)
            return;
        _toasts.Show(
            Localization.Get("Participants.NumberTaken.Title"),
            string.Format(Localization.Get("Participants.NumberTaken.Message"), (number ?? string.Empty).Trim(), conflict.HolderName));
        // Program-made: the stored number is already the kept one, so this re-save joins no step of its own (the
        // pending edit keeps its step, see RestartRowSave).
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            using (UndoGesture.Programmatic())
                putBack(conflict.KeptNumber);
        });
    }

    // ── Roster identity autosave (debounced per row; the step as for a day row)
    private void RequestRosterRowSave(ParticipantRosterRowViewModel row, string columnKey)
    {
        if (row.IsApplying)
            return;
        var step = CaptureRoster(UndoLabel(columnKey, row.FullName));
        var cts = RestartRowSave(row.ParticipantId, ref step);
        _pendingSaves.Track(SaveRosterRowDebouncedAsync(row, step, cts));
    }

    private async Task SaveRosterRowDebouncedAsync(ParticipantRosterRowViewModel row, UndoEntry? step, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await Task.Delay(SaveDebounce, token);
            await RefuseTakenNumberAsync(row.ParticipantId, row.Number, kept => row.Number = kept);
            token.ThrowIfCancellationRequested();
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
                row.PaymentPerDay,
                (row.Note ?? string.Empty).Trim(),
                (row.Team ?? string.Empty).Trim(),
                // Fee fields are persisted through their own callbacks, not the identity save; pass
                // through the row's current values so the DTO is well-formed (the editor ignores them).
                row.PaysRaisedFee,
                [],
                0m,
                []);
            // Runs to its end once started (see SaveRowDebouncedAsync).
            await RunUndoAsync(step, () => _editor.UpdateParticipantIdentityAsync(dto, CancellationToken.None));
        }
        catch (OperationCanceledException) { return; }
        catch { return; }
        finally
        {
            EndRowSave(row.ParticipantId, cts);
        }
        // A team, qualification or birth date moves results on every day the person runs (see SaveRowDebouncedAsync).
        await RefreshDaysResultsAsync(row.Days.Where(c => c.IsMember).Select(c => c.DayId));
    }

    // ── Bulk assign start numbers
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
        if (visibleRows is null || visibleRows.Count == 0 || !await EnsureEffectiveDayEditableAsync())
            return;

        // All numbers currently taken across the whole competition (not just the visible set), mapped to
        // their holder, so we can skip/step over taken numbers and clear a number from its previous holder.
        var holders = await BuildNumberHoldersAsync();

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

        // The whole assignment is one undo step: the rows' own captures (setting Number) join it, and the batch
        // below writes under it.
        var label = Localization.Get("Undo.Label.AssignNumbers");
        using var gesture = UndoGesture.Begin(label);
        var step = CaptureUndoAction(label);
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
        gesture.Dispose();
        if (touched.Count > 0)
        {
            foreach (var row in touchedRows)
                CancelRowSave(row.TimerKey);

            var batch = touched.Values
                .Select(r => (r.ParticipantId, Number: r.GetNumber()))
                .ToList();
            try
            {
                await RunUndoAsync(step, () => _editor.SetParticipantNumbersBatchAsync(batch));
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

    // Maps every number currently in use across the whole competition to the NumberRow that holds it, so
    // the assignment can skip taken numbers and clear a previous holder. The day grid only lists that day's
    // members, so participants entered on other days only are loaded from the DB as off-screen holders —
    // otherwise their numbers would look free and get handed out again.
    private async Task<Dictionary<int, NumberRow>> BuildNumberHoldersAsync()
    {
        var all = new List<NumberRow>();
        if (IsRosterMode)
        {
            all.AddRange(ProjectNumberRows(Roster));
        }
        else
        {
            var onScreen = Participants.Select(p => p.ParticipantId).ToHashSet();
            var roster = await _busy.RunAsync(() => _editor.GetParticipantRosterAsync());
            foreach (var p in roster.Where(p => !onScreen.Contains(p.ParticipantId)))
            {
                var number = p.Number ?? string.Empty;
                all.Add(new NumberRow
                {
                    Source = p,
                    ParticipantId = p.ParticipantId,
                    TimerKey = p.ParticipantId,
                    GetNumber = () => number,
                    SetNumber = v => number = v,
                    GetName = () => p.FullName ?? string.Empty
                });
            }
            all.AddRange(ProjectNumberRows(Participants));
        }

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

    // ── Manual start-order editing
    // Opens a modal to re-order the start sequence within a group on the day currently in view (a real day
    // in day mode, or the sole day of a single-day competition when on the roster). The dialog lists a
    // group's members ordered by their start time (from the start protocol) and lets the user drag them into
    // a new order; the SET of start minutes stays fixed and is re-handed out in the new order on save. The
    // changed links are written in ONE batch and the page reloads so the start-time cells refresh.
    [RelayCommand]
    private async Task EditStartOrderAsync()
    {
        if (EffectiveDayId is not { } dayId || !await EnsureEffectiveDayEditableAsync())
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

    // ── Quick withdrawal ("Швидке зняття")
    // Opens a small spreadsheet modal to quickly set a manual finish status on several competitors at once
    // on the day currently in view (a real day in day mode, or the sole day of a single-day competition on
    // the roster). Each row is «number → status» with the surname auto-filled from the number; DNS is
    // rejected for a competitor already read (they clearly started). On save each entered status is written
    // as the participant's manual override (the same path the status cell uses) and the page reloads so the
    // result columns / places refresh.
    [RelayCommand]
    private async Task QuickWithdrawalAsync()
    {
        if (EffectiveDayId is not { } dayId || !await EnsureEffectiveDayEditableAsync())
            return;

        var data = await _busy.RunAsync(() => _editor.GetQuickWithdrawalDataAsync(dayId));
        if (data.Members.Count == 0)
        {
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.QuickWithdrawal.Title",
                messageKey: "Participants.QuickWithdrawal.NoMembers",
                confirmKey: "Common.Ok",
                cancelKey: "Common.Ok"));
            return;
        }

        var assignments = await _dialogs.ShowQuickWithdrawalAsync(new QuickWithdrawalViewModel(Localization, data, LayoutStore));
        if (assignments is null || assignments.Count == 0)
            return;

        // Each withdrawal is a step of its own in the history of the day the dialog was opened for (У-19): the day
        // table's (or, on a one-day competition's «Мандатка», the roster's — the history the operator is looking at).
        // A row that left the status as it was writes nothing.
        var key = IsDayMode ? HistoryKey(UndoKey, dayId) : CurrentUndoKey;
        var members = data.Members.ToDictionary(m => m.ParticipantId);
        var steps = assignments
            .Where(a => !members.TryGetValue(a.ParticipantId, out var m) || m.CurrentStatus != a.Status)
            .Select(a => (Assignment: a, Step: CaptureUndoAction(key, WithdrawalLabel(a, members))))
            .ToList();
        await _busy.RunAsync(async () =>
        {
            foreach (var (a, step) in steps)
                await RunUndoAsync(step, () => _editor.SetParticipantDayResultStatusAsync(a.ParticipantId, dayId, a.Status));
        });
        _log.Action(string.Format(
            CultureInfo.CurrentCulture,
            Localization.Get("Participants.QuickWithdrawal.Log.Saved"),
            assignments.Count));

        await LoadAsync();
    }

    // «Зняття: DNS — Іваненко Іван» (the "auto" choice reads as such).
    private string WithdrawalLabel(QuickWithdrawalAssignment a, IReadOnlyDictionary<Guid, QuickWithdrawalMember> members)
    {
        var status = a.Status is { } s ? FinishStatusOptions.ShortCode(s) : Localization.Get("FinishRead.Edit.StatusAuto");
        var name = members.TryGetValue(a.ParticipantId, out var m) ? m.FullName : RunnerName(a.ParticipantId);
        return string.Format(Localization.Get("Undo.Label.Withdrawal"), status, name);
    }

    // ── Copy participants from one day to another
    // Adds the source day's people to the target day, skipping anyone already there (their existing record
    // is never overwritten). The group always travels — a member needs one — so a group missing from the
    // target day is added to it; every other per-day field is opt-in in the dialog. Needs at least two
    // days; the target day's lock is enforced by the service, since the day written may not be the one in
    // view. The page reloads afterwards so the copied rows appear.
    [RelayCommand]
    private async Task CopyParticipantsAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var days = await _busy.RunAsync(() => _editor.GetDaysAsync());
        if (days.Count < 2)
        {
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.CopyDay.Title",
                messageKey: "Participants.CopyDay.NeedTwoDays",
                confirmKey: "Common.Ok",
                cancelKey: "Common.Ok"));
            return;
        }

        // Real days only — the roster is an aggregate, not a copy source. Seed the source with the day in
        // view when one is (in roster mode there is none, so the first day is offered).
        var options = days.Select(d => new DayOption(d, Localization)).ToList();
        var initialSource = EffectiveDayId is { } dayId
            ? options.FirstOrDefault(o => o.Day?.Id == dayId)
            : null;

        var request = await _dialogs.ShowCopyParticipantsAsync(
            new CopyParticipantsViewModel(Localization, options, PaymentPerDay, initialSource));
        if (request is null)
            return;

        var sourceNumber = options.FirstOrDefault(o => o.Day?.Id == request.SourceDayId)?.Number ?? 0;
        var targetNumber = options.FirstOrDefault(o => o.Day?.Id == request.TargetDayId)?.Number ?? 0;

        // Confirm before writing — this adds rows to a day the user may not be looking at.
        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "Participants.CopyDay.Title",
            messageKey: "Participants.CopyDay.ConfirmMessage",
            confirmKey: "Participants.CopyDay.Confirm",
            cancelKey: "Common.Cancel")
        {
            MessageArgs = [sourceNumber, targetNumber]
        });
        if (!confirmed)
            return;

        var result = await _busy.RunAsync(() => _editor.CopyParticipantsBetweenDaysAsync(request));

        _log.Action(string.Format(
            Localization.Get("Participants.CopyDay.Log.Copied"),
            result.Copied, sourceNumber, targetNumber, result.AlreadyPresent, result.GroupsCreated));

        // Report the outcome, calling out the chips that could not travel (the number was already taken on
        // the target day) so a silently chip-less row is never a surprise.
        var messageKey = result.ChipsSkipped > 0
            ? "Participants.CopyDay.DoneWithSkippedChips"
            : "Participants.CopyDay.Done";
        await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "Participants.CopyDay.Title",
            messageKey: messageKey,
            confirmKey: "Common.Ok",
            cancelKey: "Common.Ok")
        {
            MessageArgs = [result.Copied, result.AlreadyPresent, result.GroupsCreated, result.ChipsSkipped]
        });

        await LoadAsync();
    }

    // ── Bulk assign rental chips
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
        if (visibleRows is null || visibleRows.Count == 0 || !await EnsureEffectiveDayEditableAsync())
            return;

        // The rental pool (ordered by number) and who holds each chip on each day. An empty
        // pool is fine here — the "starting from a number" mode adds its own chips — so we don't bail yet;
        // an empty base only blocks the "from the base" mode, checked after the dialog returns.
        var pool = await _busy.RunAsync(() => _editor.GetRentalChipsAsync());
        var holders = await _busy.RunAsync(() => _editor.GetRentalChipHoldersByDayAsync());

        // The note dropdown: a leading "all" option, then each distinct note present in the pool.
        var notes = pool
            .Select(c => (c.Note ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .Distinct(OrientPyx.Plugins.Sdk.Toolkit.TextKey.Comparer)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();
        var options = new List<ChipNoteOption> { ChipNoteOption.All(Localization.Get("Participants.AssignChips.AllChips")) };
        options.AddRange(notes.Select(n => ChipNoteOption.ForNote(n, n)));

        var result = await _dialogs.ShowAssignChipsAsync(new AssignChipsViewModel(
            Localization, options, showAvailability: IsDayMode && holders.DayNumbers.Count > 1));
        if (result is null)
            return;

        // "From the base" mode with an empty rental base: nothing to draw from — say so and stop.
        if (!result.IsRange && pool.Count == 0)
        {
            _log.Action(Localization.Get("Participants.AssignChips.Log.NoChips"));
            return;
        }

        // One undo step for the whole action: the range added to the rental base (range mode) and every chip handed
        // out. Taken at its first write (a declined confirmation leaves no step behind).
        UndoEntry? step = null;
        UndoEntry? Step() => step ??= CaptureUndoAction(Localization.Get("Undo.Label.AssignChips"));

        // "Starting from a number" mode: first add the contiguous range to the rental base (skipping any
        // number that already exists), then reload the pool so the newly added chips are in it — the rest
        // of the flow (build the free queue, confirm, assign) is identical to the "from the base" mode.
        if (result.IsRange)
        {
            var start = (result.StartNumber ?? string.Empty).Trim();
            if (start.Length == 0 || result.Count <= 0)
                return;

            await RunUndoAsync(Step(), () => _busy.RunAsync(() =>
                _editor.AddRentalChipRangeAsync(start, result.Count, result.RangeNote ?? string.Empty)));
            pool = await _busy.RunAsync(() => _editor.GetRentalChipsAsync());

            // The range's chips are now in the rental database, so refresh the shared registry the chip
            // cells consult for their "own chip" (non-rental) red highlight. Without this the just-added
            // numbers would flag red until a full page reload rebuilt the registry. The fee snapshot holds
            // its own copy of the rental set, so rebuild it too — otherwise the assigned chips would be
            // charged as the participants' own until the page reloads.
            RentalChips.Reset(pool.Select(c => c.Number));
            await RefreshFeeDataAsync(hasEvent: true, silent: true);
        }

        var used = UsedChipNumbers(holders, result.FreeOnThisDay && IsDayMode ? _session.CurrentDay?.Number : null);

        // The free chips for this run: not in `used`, by ascending number — drawn
        // from the front as we assign. In "from the base" mode narrowed by the note filter; in range mode
        // narrowed to exactly the numbers of the range just added.
        var rangeNumbers = result.IsRange
            ? RangeChipNumbers((result.StartNumber ?? string.Empty).Trim(), result.Count)
            : null;
        var free = new Queue<string>(pool
            .Where(c => result.IsRange
                ? rangeNumbers!.Contains((c.Number ?? string.Empty).Trim())
                : result.Note is null
                    || OrientPyx.Plugins.Sdk.Toolkit.TextKey.Same((c.Note ?? string.Empty).Trim(), result.Note))
            .Select(c => (c.Number ?? string.Empty).Trim())
            .Where(number => number.Length > 0 && !used.Contains(number))
            .OrderBy(number => number, StringComparer.Ordinal));

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
            await RunUndoAsync(Step(), () => _busy.RunAsync(() => _editor.SetParticipantDayChipsBatchAsync(writes)));

            // The assigned cells were set "silently" (no per-cell fee recompute), so refresh each row's
            // «Стартовий внесок» from the current fee snapshot — a chip drawn from a note-tagged pool may
            // carry a different rental price. Also refresh the status bar (rental in-use / without-chip
            // counts changed); the batch path never went through the per-cell change handlers that do this.
            RefreshFeesIfVisible();
            RecomputeStatusInfo();
        }

        LogChipAssignment(assignments, free.Count);
    }

    // Chip numbers that are taken: held on the given day only, or on any day when dayNumber is null.
    private static HashSet<string> UsedChipNumbers(RentalChipHoldersByDay holders, int? dayNumber)
    {
        var dayIndex = dayNumber is { } number ? holders.DayNumbers.ToList().IndexOf(number) : -1;
        return holders.HoldersByChip
            .Where(kvp => dayIndex >= 0
                ? kvp.Value[dayIndex].Length > 0
                : kvp.Value.Any(name => name.Length > 0))
            .Select(kvp => kvp.Key)
            .ToHashSet(OrientPyx.Plugins.Sdk.Toolkit.TextKey.Comparer);
    }

    // The exact chip numbers a "starting from a number" range would produce — the same width-preserving
    // sequence AddRentalChipRangeAsync generates (e.g. "0042" → "0042","0043",…). Used to narrow the free
    // pool to just this range after it has been added. Returns empty for an unparsable start / count ≤ 0.
    private static HashSet<string> RangeChipNumbers(string startNumber, int count)
    {
        var set = new HashSet<string>(OrientPyx.Plugins.Sdk.Toolkit.TextKey.Comparer);
        if (count <= 0 || startNumber.Length == 0 || !ulong.TryParse(startNumber, out var first))
            return set;

        var width = startNumber.Length;
        for (var i = 0; i < count; i++)
            set.Add((first + (ulong)i).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
        return set;
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
                if (item is ParticipantRosterRowViewModel row && ChiplessOpenDays(row).Any())
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
    // has a chip on every open member day consumes nothing. Closed days are skipped: the batch write
    // refuses a closed day outright, which would leave the cells showing chips that were never saved.
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

            var emptyDays = ChiplessOpenDays(row).ToList();
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

    // A roster participant's day-cells that can take a chip: member days with no chip on a day that isn't closed.
    private static IEnumerable<RosterDayCellViewModel> ChiplessOpenDays(ParticipantRosterRowViewModel row)
        => row.Days.Where(d => d.IsMember && !d.IsDayLocked && string.IsNullOrWhiteSpace(d.Chip));

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

    // ── Chip scanning («Сканування чіпів»)
    // Hands chips to the shown participants without one by touching each chip to the read-out station —
    // see ChipScanViewModel for the rules (chip-type field → own chip / rental note). The dialog returns the
    // (participant → chip) assignments plus the rental chips the base did not have; both are committed here in
    // two batch writes, the cells are updated in memory the way the bulk assignment does, and the statement
    // opens on the same table rows so the hand-out sheet can be printed right away. The dialog's set-up (field,
    // rules, file) is saved for the competition whether it was applied or cancelled.
    public async Task ScanChipsAsync(Controls.SheetTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_session.CurrentEvent is not { } ev || !await EnsureEffectiveDayEditableAsync())
            return;

        var candidates = new List<ChipScanCandidate>();
        foreach (var item in table.VisibleItems)
        {
            switch (item)
            {
                case ParticipantDayRowViewModel row when IsDayMode && string.IsNullOrWhiteSpace(row.Chip):
                    candidates.Add(new ChipScanCandidate(
                        row.ParticipantId, row.Number ?? string.Empty, row.FullName ?? string.Empty,
                        row.GroupLabel, row.Fields.Values, row,
                        NeededPunches: row.SelectedGroup?.ControlCount ?? 0));
                    break;
                case ParticipantRosterRowViewModel row when IsRosterMode && ChiplessOpenDays(row).Any():
                    var groups = row.Days
                        .Where(d => d.IsMember)
                        .Select(d => d.GroupLabel)
                        .Where(g => g.Length > 0)
                        .Distinct()
                        .ToList();
                    // The chip goes to every chip-less open day, so it must hold the longest of their courses.
                    var needed = ChiplessOpenDays(row).Select(d => d.SelectedGroup?.ControlCount ?? 0).DefaultIfEmpty(0).Max();
                    candidates.Add(new ChipScanCandidate(
                        row.ParticipantId, row.Number ?? string.Empty, row.FullName ?? string.Empty,
                        string.Join(" / ", groups), row.Fields.Values, row, NeededPunches: needed));
                    break;
            }
        }

        if (candidates.Count == 0)
        {
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Participants.ChipScan.Title",
                messageKey: "Participants.ChipScan.NoCandidates",
                confirmKey: "Common.Ok",
                cancelKey: "Common.Ok"));
            return;
        }

        var (pool, holders, settings, sportIdent) = await _busy.RunAsync(async () =>
        {
            var p = await _editor.GetRentalChipsAsync();
            var h = await _editor.GetRentalChipHoldersByDayAsync();
            var s = await _editor.GetChipScanSettingsAsync();
            var si = await UsesSportIdentCardsAsync();
            return (p, h, s, si);
        });

        // chip → who holds it on any day: nothing in here may be handed out again.
        var holderNames = new Dictionary<string, string>(OrientPyx.Plugins.Sdk.Toolkit.TextKey.Comparer);
        foreach (var (chip, perDay) in holders.HoldersByChip)
        {
            var names = perDay.Where(n => n.Length > 0).Distinct().ToList();
            if (names.Count > 0)
                holderNames[chip] = string.Join(", ", names);
        }

        // The same default file the «Чіпи» page's auto-read watches, so one station set-up serves both. The folder is
        // this machine's (IEventFiles.LocalReadoutDirectory) — the reader is plugged into this computer in either role.
        var defaultPath = System.IO.Path.Combine(_eventFiles.LocalReadoutDirectory(ev.FolderPath, ev.Identifier, null), "rentchip.csv");
        if (_mode.IsStation)
        {
            // On a station the saved path is per machine (П17): it lives in the local machine.json, not in the
            // competition's settings on the host (those are sent with a blank path, see below).
            var localPath = await _eventFiles.GetMachineValueAsync(ev.FolderPath, ChipScanPathKey);
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                settings ??= new ChipScanSettings();
                settings.ReadoutFilePath = localPath;
            }
        }
        // A poller of its own: the shared one is single-watch and may be busy with the finish read-out.
        var dialog = new ChipScanViewModel(
            Localization, candidates, CustomFields, pool, holderNames, settings, defaultPath, LayoutStore,
            new FileReadoutPoller(), _readoutParsers, sportIdentCards: sportIdent);
        var result = await _dialogs.ShowChipScanAsync(dialog);
        var built = dialog.BuildSettings();
        if (_mode.IsStation)
        {
            await _eventFiles.SetMachineValueAsync(ev.FolderPath, ChipScanPathKey,
                built.ReadoutFilePath.Length == 0 ? null : built.ReadoutFilePath);
            built.ReadoutFilePath = string.Empty;
        }
        await _busy.RunAsync(() => _editor.SaveChipScanSettingsAsync(built));
        if (result is null || (result.Assignments.Count == 0 && result.FieldUpdates.Count == 0))
            return;

        // Update the cells in memory and collect the (participant, day, chip) writes — the row objects came
        // back inside the candidates, so each assignment lands on the very row it was built from.
        // Every chip handed out is an undo step of its own in the history the dialog was opened from (the day table's
        // or «Мандатка»'s, У-19): «Чіп 777 — Іваненко Іван», with the chip added to the rental base and the participant's
        // chip type changed in the dialog. A chip type changed for someone who got no chip is a step of its own too.
        var key = CurrentUndoKey;
        var work = new List<(UndoEntry? Step, List<(string Number, string Note)> NewChips,
            List<(Guid ParticipantId, Guid DayId, string Chip)> Writes, List<ChipScanFieldUpdate> Fields)>();
        var assignments = new List<(string Name, string Chip)>();
        foreach (var a in result.Assignments)
        {
            var writes = new List<(Guid ParticipantId, Guid DayId, string Chip)>();
            switch (a.Candidate.Row)
            {
                case ParticipantDayRowViewModel row:
                    row.SetChipSilently(a.Chip);
                    row.MarkChipCommitted(a.Chip);
                    writes.Add((row.ParticipantId, row.DayId, a.Chip));
                    break;
                case ParticipantRosterRowViewModel row:
                    foreach (var cell in ChiplessOpenDays(row).ToList())
                    {
                        cell.SetChipSilently(a.Chip);
                        cell.MarkChipCommitted(a.Chip);
                        writes.Add((cell.ParticipantId, cell.DayId, a.Chip));
                    }
                    break;
            }
            assignments.Add((a.Candidate.FullName, a.Chip));
            var label = string.Format(Localization.Get("Undo.Label.ChipScan"), a.Chip, a.Candidate.FullName);
            work.Add((CaptureUndoAction(key, label), a.AddToBase ? [(a.Chip, a.Note)] : [], writes,
                result.FieldUpdates.Where(u => u.Candidate.ParticipantId == a.Candidate.ParticipantId).ToList()));
        }
        var scanned = result.Assignments.Select(a => a.Candidate.ParticipantId).ToHashSet();
        foreach (var u in result.FieldUpdates.Where(u => !scanned.Contains(u.Candidate.ParticipantId)))
        {
            var fieldLabel = CustomFields.FirstOrDefault(f => f.Key == u.Key)?.Label ?? u.Key;
            var label = string.Format(Localization.Get("Undo.Label.Cell"), fieldLabel, u.Candidate.FullName);
            work.Add((CaptureUndoAction(key, label), [], [], [u]));
        }

        var newChips = result.NewRentalChips;
        var storedFieldValues = new List<(ChipScanFieldUpdate Update, string Stored)>();
        await _busy.RunAsync(async () =>
        {
            foreach (var (step, chips, writes, fields) in work)
                await RunUndoAsync(step, async () =>
                {
                    if (chips.Count > 0)
                        await _editor.AddRentalChipsAsync(chips);
                    if (writes.Count > 0)
                        await _editor.SetParticipantDayChipsBatchAsync(writes);
                    // Chip types changed in the dialog go to the chip-type field of those participants.
                    foreach (var u in fields)
                        storedFieldValues.Add((u, await _editor.SetParticipantFieldAsync(u.Candidate.ParticipantId, u.Key, u.Value)));
                });
        });

        foreach (var (u, stored) in storedFieldValues)
        {
            var cell = u.Candidate.Row switch
            {
                ParticipantDayRowViewModel r => r.Fields[u.Key],
                ParticipantRosterRowViewModel r => r.Fields[u.Key],
                _ => null
            };
            cell?.ApplySaved(stored);
        }

        if (newChips.Count > 0)
        {
            // The base grew: refresh the registry the chip cells consult for the "own chip" red highlight,
            // and the fee snapshot (it carries its own rental set — stale, it would price the newly
            // registered rental chips as the participants' own).
            var refreshed = await _busy.RunAsync(() => _editor.GetRentalChipsAsync());
            RentalChips.Reset(refreshed.Select(c => c.Number));
            await RefreshFeeDataAsync(hasEvent: true, silent: true);
        }
        RefreshFeesIfVisible();
        RecomputeStatusInfo();

        _log.Action(string.Format(
            CultureInfo.CurrentCulture,
            Localization.Get("Participants.ChipScan.Log.Applied"),
            assignments.Count,
            newChips.Count));
        foreach (var (name, chip) in assignments)
            _log.Action(string.Format(
                CultureInfo.CurrentCulture,
                Localization.Get("Participants.AssignChips.Log.AssignedRow"),
                chip,
                string.IsNullOrWhiteSpace(name) ? Localization.Get("Participants.Chip.UnnamedHolder") : name));

        // Straight on to the hand-out sheet for the same rows.
        await OpenStatementAsync(table);
    }

    // ── Mark age-window violators "поза конкурсом"
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
        if (visibleRows is null || visibleRows.Count == 0 || _session.CurrentEvent is null
            || !await EnsureEffectiveDayEditableAsync())
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

        // One undo step: every row's own capture (its debounced save / cell write) joins the gesture.
        using (UndoGesture.Begin(string.Format(Localization.Get("Undo.Label.RowAction"),
                   Localization.Get("Participants.MarkAgeViolators"), names.Count)))
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

    // ── Bulk edit one field across the shown rows
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
        if (visibleRows is null || visibleRows.Count == 0 || _session.CurrentEvent is null
            || !await EnsureEffectiveDayEditableAsync())
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

        // One roster day's group column: that day's own groups, not every group of the competition.
        if (IsRosterMode && request?.RosterDayIndex is { } groupDay
            && visibleRows.OfType<ParticipantRosterRowViewModel>().FirstOrDefault() is { } first
            && groupDay >= 0 && groupDay < first.Days.Count)
            groupOptions = first.Days[groupDay].GroupOptions.Where(o => o.Id is not null).ToList();

        var fields = BuildBulkEditFields(groupOptions.Count > 0);
        // Preselect the field the request points at (the focused column / the right-clicked header),
        // when that field is actually offered in the current mode; otherwise fall back to the first.
        var preselect = request?.PreselectKey is { } key
            ? fields.FirstOrDefault(f => f.Key == key)
            : null;
        // The selected cells of one column: only that column's field, so nothing else can change.
        if (request?.SelectionOnly == true)
        {
            if (preselect is null)
                return;
            preselect = new Dialogs.BulkEditFieldOption(
                preselect.Key, preselect.Kind, request.FieldLabel ?? preselect.Label, preselect.Choices);
            fields = [preselect];
        }
        // Remember each field's localized label so the activity log can name the field, not its key.
        var fieldLabels = fields.ToDictionary(f => f.Key, f => f.Label);
        var dialog = new Dialogs.BulkEditViewModel(
            Localization, fields, groupOptions,
            _regionOptions, _clubOptions, _dusshOptions, _rankOptions, visibleRows.Count, preselect)
        {
            SelectionOnly = request?.SelectionOnly == true
        };

        // The list "+ new" sentinels create the value inline, reusing the existing add flows.
        dialog.AddRequested += OnBulkEditAddRequested;
        _bulkEditStep = null;
        _bulkEditRowCount = visibleRows.Count;
        BulkEditResult? result;
        try
        {
            result = await _dialogs.ShowBulkEditAsync(dialog);
        }
        finally
        {
            dialog.AddRequested -= OnBulkEditAddRequested;
        }
        // A value made with «+ новий» in the dialog already wrote under this step: the applied values join it.
        var addStep = _bulkEditStep;
        _bulkEditStep = null;
        if (result is null)
            return;

        // One undo step per applied dialog: every row's own capture joins the gesture.
        var undoLabel = string.Format(Localization.Get("Undo.Label.BulkEdit"),
            fieldLabels.TryGetValue(result.Key, out var fieldLabel) ? fieldLabel : result.Key, visibleRows.Count);
        using (UndoGesture.Continue(addStep, undoLabel))
            ApplyBulkEdit(visibleRows, result, request?.RosterDayIndex);

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

    /// <summary>The bulk-edit field key prefix of a discount checkbox column: <c>Discount:&lt;discountId&gt;</c>.</summary>
    public const string DiscountFieldKeyPrefix = "Discount:";

    // The fields the user can bulk-edit, in display order. Unique fields (number, chip) are excluded.
    // Context-sensitive fields are dropped when they don't apply: group (day mode only), team (only when
    // shown for the current mode), raised fee (only when enabled), bonus (only on a point-scoring day /
    // when the roster shows the block), discounts (one Bool field per discount column, the auto FSOU one
    // excluded), and one field per additional participant field (kind by the field's kind).
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
        fields.Add(F("BirthDate", Dialogs.BulkEditFieldKind.Date, "Participants.Col.BirthDate"));
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

        // The result-status override: the same choices as the row dropdown (the "auto" sentinel clears the
        // override); the choice value is the FinishStatus name, blank for auto. Per day: the roster fans it
        // out over the member days like the other per-day fields.
        var statusChoices = FinishStatusOptions.Build(Localization)
            .Select(o => new Dialogs.BulkEditChoice(o.Status?.ToString() ?? string.Empty, o.Label))
            .ToList();
        fields.Add(new Dialogs.BulkEditFieldOption("ResultStatus", Dialogs.BulkEditFieldKind.Choice,
            Localization.Get("Participants.Col.ResultStatus"), statusChoices));
        // «Бонус» exists only where the table shows it: a point-scoring day / any point-scoring day for the roster.
        if ((IsDayMode && ShowScoreColumn) || (IsRosterMode && RosterShowsScore))
            fields.Add(F("Bonus", Dialogs.BulkEditFieldKind.Text, "Participants.Col.Bonus"));

        // One flag per discount column, captioned like the column (the discount's name). The FSOU-member
        // discount follows «Член ФСОУ» and has no column of its own, so it is not offered either.
        foreach (var discount in Discounts.Where(d => !d.IsFsouMemberDiscount))
        {
            var label = string.IsNullOrWhiteSpace(discount.Name)
                ? Localization.Get("EntryFees.Discount.Unnamed")
                : discount.Name;
            fields.Add(new Dialogs.BulkEditFieldOption(DiscountFieldKeyPrefix + discount.Id, Dialogs.BulkEditFieldKind.Bool, label));
        }

        // The competition's additional fields, keyed like their columns ("field.<key>"); the editor follows
        // the field's kind (flag / choice list / text; a number is text, normalised on save like a typed edit).
        foreach (var field in CustomFields)
        {
            var key = CustomField.ColumnPrefix + field.Key;
            fields.Add(field.Kind switch
            {
                CustomFieldKind.Bool => new Dialogs.BulkEditFieldOption(key, Dialogs.BulkEditFieldKind.Bool, field.Label),
                CustomFieldKind.Choice => new Dialogs.BulkEditFieldOption(key, Dialogs.BulkEditFieldKind.Choice, field.Label,
                    field.Options.Select(o => new Dialogs.BulkEditChoice(o.Value, o.Value)).ToList()),
                _ => new Dialogs.BulkEditFieldOption(key, Dialogs.BulkEditFieldKind.Text, field.Label),
            });
        }
        return fields;
    }

    // The FinishStatus a status-choice result names; null = the "auto" sentinel (clear the override).
    private static FinishStatus? ParseStatusChoice(string? text) =>
        Enum.TryParse<FinishStatus>(text, out var status) && status != FinishStatus.None ? status : null;

    // Applies a bulk edit to a row's additional-field cell (shared by both row kinds): the flag setter for a
    // Bool field, the text setter otherwise; either goes through the cell's save callback, which normalises
    // and persists exactly like a typed edit. A field the row doesn't carry is an inert cell and is skipped.
    private static void ApplyBulkEditToField(ParticipantFieldsViewModel fields, string columnKey, BulkEditResult r)
    {
        var cell = fields[columnKey[CustomField.ColumnPrefix.Length..]];
        if (cell.Key.Length == 0)
            return;
        if (r.Bool is { } flag)
            cell.Checked = flag;
        else
            cell.Text = r.Text ?? string.Empty;
    }

    // Applies a discount flag ("Discount:<id>") to a row's DiscountFlags entry, which persists through the
    // row's own change callback like a click on the column's checkbox. The FSOU-member flag is never touched.
    private static void ApplyBulkEditToDiscount(IEnumerable<DiscountFlagViewModel> flags, string key, BulkEditResult r)
    {
        if (!Guid.TryParse(key[DiscountFieldKeyPrefix.Length..], out var id))
            return;
        var flag = flags.FirstOrDefault(f => f.DiscountId == id && !f.IsFsouMemberDiscount);
        if (flag is not null)
            flag.IsSelected = r.Bool ?? false;
    }

    // The undo step of the bulk edit whose dialog is open, taken by its first «+ новий» (the new region / club / ДЮСШ
    // is part of the action); null until then. The applied values join it (BulkEditAsync).
    private UndoEntry? _bulkEditStep;
    private int _bulkEditRowCount;

    // The step a «+ новий» in the bulk-edit dialog writes under: «Зміна «Регіон» (рядків: N)».
    private UndoEntry? BulkEditAddStep(string columnKey)
        => _bulkEditStep ??= CaptureUndoAction(string.Format(Localization.Get("Undo.Label.BulkEdit"),
            Localization.Get(columnKey), _bulkEditRowCount));

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
                : await RunUndoAsync(BulkEditAddStep("Participants.Col.Region"), () => _busy.RunAsync(() => _editor.AddRegionAsync(name)));
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
                : await RunUndoAsync(BulkEditAddStep("Participants.Col.Club"), () => _busy.RunAsync(() => _editor.AddClubAsync(name)));
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
                : await RunUndoAsync(BulkEditAddStep("Participants.Col.Dussh"), () => _busy.RunAsync(() => _editor.AddDusshAsync(name)));
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
    // With a roster day index (the selected cells of one day's column) a per-day field changes only that
    // day's cell of each row.
    private void ApplyBulkEdit(IReadOnlyList<object?> visibleRows, BulkEditResult result, int? rosterDayIndex = null)
    {
        foreach (var item in visibleRows)
        {
            switch (item)
            {
                case ParticipantDayRowViewModel d:
                    ApplyBulkEditToDayRow(d, result);
                    break;
                case ParticipantRosterRowViewModel r when rosterDayIndex is { } i:
                    if (i >= 0 && i < r.Days.Count)
                        ApplyBulkEditToRosterDay(r.Days[i], result);
                    break;
                case ParticipantRosterRowViewModel r:
                    ApplyBulkEditToRosterRow(r, result);
                    break;
            }
        }
    }

    // One roster day cell, the per-day fields only (what a day column of the roster edits). A closed day is
    // left alone; member-only values skip a day the participant does not run, as the fan-out does. A group
    // joins the day (like picking it in the cell); a relay runner's group goes through the page's question.
    private void ApplyBulkEditToRosterDay(RosterDayCellViewModel cell, BulkEditResult r)
    {
        if (cell.IsDayLocked)
            return;

        if (r.Key == "Group")
        {
            if (cell.GroupOptions.FirstOrDefault(o => o.Id == r.Id) is { } option)
            {
                if (cell.Relay.HasTeam)
                    cell.BulkGroup = option;
                else
                    cell.ComboGroup = option;
            }
            return;
        }

        if (!cell.IsMember)
            return;
        switch (r.Key)
        {
            case "Payment": cell.Payment = r.Text ?? string.Empty; break;
            case "PaysRaisedFee": cell.PaysRaisedFee = r.Bool ?? false; break;
            case "StartTime": cell.StartTimeText = r.Text ?? string.Empty; break;
            case "OutOfCompetition": cell.OutOfCompetition = r.Bool ?? false; break;
            case "ResultStatus":
                cell.SelectedStatus = FinishStatusOptions.Select(cell.StatusOptions, ParseStatusChoice(r.Text));
                break;
            case "Bonus": cell.BonusText = r.Text ?? string.Empty; break;
        }
    }

    private void ApplyBulkEditToDayRow(ParticipantDayRowViewModel row, BulkEditResult r)
    {
        switch (r.Key)
        {
            case "Group":
                var g = row.GroupOptions.FirstOrDefault(o => o.Id == r.Id);
                if (g is not null)
                    row.BulkGroup = g; // a relay runner keeps the team's group (Р-22)
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
                row.SelectedRank = row.RankOptions.FirstOrDefault(o => OrientPyx.Plugins.Sdk.Toolkit.TextKey.Same(o.Value, r.Text)) ?? row.RankOptions[0];
                break;
            case "Coach": row.Coach = r.Text ?? string.Empty; break;
            case "BirthDate": row.BirthDate = r.Date; break;
            case "Representative": row.Representative = r.Text ?? string.Empty; break;
            case "FsouCode": row.FsouCode = r.Text ?? string.Empty; break;
            // In per-day mode the day grid's «Оплата» is this day's own value, so bulk edit writes there.
            case "Payment":
                if (PaymentPerDay)
                    row.DayPayment = r.Text ?? string.Empty;
                else
                    row.Payment = r.Text ?? string.Empty;
                break;
            case "Note": row.Note = r.Text ?? string.Empty; break;
            case "Team": row.Team = r.Text ?? string.Empty; break;
            case "StartTime": row.StartTimeText = r.Text ?? string.Empty; break;
            case "IsFsouMember": row.IsFsouMember = r.Bool ?? false; break;
            case "PaysRaisedFee": row.PaysRaisedFee = r.Bool ?? false; break;
            case "OutOfCompetition": row.OutOfCompetition = r.Bool ?? false; break;
            // The status override goes through the row's dedicated status callback (persist + re-rank).
            case "ResultStatus":
                row.SelectedStatus = FinishStatusOptions.Select(row.StatusOptions, ParseStatusChoice(r.Text));
                break;
            case "Bonus": row.BonusText = r.Text ?? string.Empty; break;
            case var key when key.StartsWith(DiscountFieldKeyPrefix, StringComparison.Ordinal):
                ApplyBulkEditToDiscount(row.DiscountFlags, key, r);
                break;
            case var key when key.StartsWith(CustomField.ColumnPrefix, StringComparison.Ordinal):
                ApplyBulkEditToField(row.Fields, key, r);
                break;
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
                row.SelectedRank = row.RankOptions.FirstOrDefault(o => OrientPyx.Plugins.Sdk.Toolkit.TextKey.Same(o.Value, r.Text)) ?? row.RankOptions[0];
                break;
            case "Coach": row.Coach = r.Text ?? string.Empty; break;
            case "BirthDate": row.BirthDate = r.Date; break;
            case "Representative": row.Representative = r.Text ?? string.Empty; break;
            case "FsouCode": row.FsouCode = r.Text ?? string.Empty; break;
            // Per-day mode fans the payment out to the member days, like the collapsed roster cell does.
            case "Payment":
                if (PaymentPerDay)
                    row.SetPaymentForMemberDays(r.Text ?? string.Empty);
                else
                    row.Payment = r.Text ?? string.Empty;
                break;
            case "Note": row.Note = r.Text ?? string.Empty; break;
            case "Team": row.Team = r.Text ?? string.Empty; break;
            case "IsFsouMember": row.IsFsouMember = r.Bool ?? false; break;
            case "PaysRaisedFee":
                if (PaymentPerDay)
                    row.SetRaisedFeeForMemberDays(r.Bool ?? false);
                else
                    row.PaysRaisedFee = r.Bool ?? false;
                break;
            // Per-day fields fan out to the participant's member days (like the collapsed roster cells).
            case "StartTime": row.SetStartTimeForMemberDays(r.Text ?? string.Empty); break;
            case "OutOfCompetition": row.SetOutOfCompetitionForMemberDays(r.Bool ?? false); break;
            case "ResultStatus": row.SetResultStatusForMemberDays(ParseStatusChoice(r.Text)); break;
            case "Bonus": row.SetBonusForMemberDays(r.Text ?? string.Empty); break;
            case var key when key.StartsWith(DiscountFieldKeyPrefix, StringComparison.Ordinal):
                ApplyBulkEditToDiscount(row.DiscountFlags, key, r);
                break;
            case var key when key.StartsWith(CustomField.ColumnPrefix, StringComparison.Ordinal):
                ApplyBulkEditToField(row.Fields, key, r);
                break;
        }
    }

    // ── Roster block collapse/expand
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

    // ── Roster per-day group change
    // Picking a real group joins the day (or changes the group); picking "не участвує" (null) leaves
    // the day. Either way the write runs in the background and the cell's membership is updated.
    private void RequestCellGroupChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        var step = CaptureRoster(RunnerLabel("Participants.Col.Group", cell.ParticipantId));
        _pendingSaves.Track(ApplyCellGroupChangeAsync(cell, step));
    }

    // ── Roster per-day chip change
    // Editing a member day's chip (expanded cell, or a collapsed all-days edit) may collide with
    // another competitor on that day: confirm the reassignment, then persist (clearing the previous
    // holder) or revert. Only member days carry a chip, so there is no membership change to apply here.
    private void RequestCellChipChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        if (!cell.IsMember)
            return;
        var step = CaptureRoster(RunnerLabel("Participants.Col.Chip", cell.ParticipantId));
        _pendingSaves.Track(ResolveCellChipAsync(cell, step));
    }

    // Start time / out-of-competition have no uniqueness rule, so they persist directly in the
    // background (no confirm flow). Member-only — a non-member cell is ignored.
    private void RequestCellStartTimeChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        if (!cell.IsMember)
            return;
        var (participantId, dayId, startTime) = (cell.ParticipantId, cell.DayId, cell.StartTime);
        var step = CaptureRoster(RunnerLabel("Participants.Col.StartTime", participantId));
        // A start from the protocol is the runner's start when the chip has none: the result time and places move.
        _pendingSaves.Track(WriteThenRefreshAsync(step, dayId, () => _editor.SetParticipantDayStartTimeAsync(participantId, dayId, startTime)));
    }

    private void RequestCellOutOfCompetitionChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        if (!cell.IsMember)
            return;
        var (participantId, dayId, value) = (cell.ParticipantId, cell.DayId, cell.OutOfCompetition);
        var step = CaptureRoster(RunnerLabel("Participants.Col.OutOfCompetition", participantId));
        // «П/К» takes the runner out of the ranking: the others' places move.
        _pendingSaves.Track(WriteThenRefreshAsync(step, dayId, () => _editor.SetParticipantDayOutOfCompetitionAsync(participantId, dayId, value)));
    }

    // One background write of a step; a failure (a closed day, a lost connection) never crashes the UI — the next
    // reload shows what the store holds.
    private async Task WriteAsync(UndoEntry? step, Func<Task> write)
    {
        try
        {
            await RunUndoAsync(step, write);
        }
        catch
        {
            // see above
        }
    }

    // ── Result status override (day grid + roster)
    // The status dropdown persists a manual override on the participant-day, then re-ranks the day so
    // places re-number live across the visible rows. Day grid uses the session's current day; the roster
    // cell carries its own day.
    private void RequestDayRowResultStatusChange(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.ResultStatus", row.FullName));
        _pendingSaves.Track(ApplyResultStatusAsync(row.ParticipantId, row.DayId, row.ResultStatusOverride, step));
    }

    private void RequestCellResultStatusChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        if (!cell.IsMember)
            return;
        var step = CaptureRoster(RunnerLabel("Participants.Col.ResultStatus", cell.ParticipantId));
        _pendingSaves.Track(ApplyResultStatusAsync(cell.ParticipantId, cell.DayId, cell.ResultStatusOverride, step));
    }

    private Task ApplyResultStatusAsync(Guid participantId, Guid dayId, FinishStatus? status, UndoEntry? step)
        => WriteThenRefreshAsync(step, dayId, () => _editor.SetParticipantDayResultStatusAsync(participantId, dayId, status),
            evenWithoutReadouts: true);

    // ── Relay cells (day grid + roster): «Команда» / «Номер команди» / «Етап» typed on a runner's row («Номер етапу» is
    // read-only). The cell already shows the edit; the page saves it (asking first when it could change the team itself
    // or another runner holds the leg), says what else moved, and reloads — or puts the cell back and says why.
    // The step is taken at the edit (on the day table's history for a day row, the roster's for a roster cell); the
    // «Замінити» / «для всієї команди» answer comes later and its repeated call writes under the same step.
    private void RequestDayRowRelayPlace(RelayCellsViewModel cells, RelayPlacementRequest request)
        => RequestRelayPlace(cells, request, HistoryKey(UndoKey, cells.DayId));

    private void RequestCellRelayPlace(RelayCellsViewModel cells, RelayPlacementRequest request)
        => RequestRelayPlace(cells, request, HistoryKey(UndoKey, null));

    private void RequestDayRowRelayTeamEdit(RelayCellsViewModel cells, RelayTeamField field, string value)
        => RequestRelayTeamEdit(cells, field, value, HistoryKey(UndoKey, cells.DayId));

    private void RequestCellRelayTeamEdit(RelayCellsViewModel cells, RelayTeamField field, string value)
        => RequestRelayTeamEdit(cells, field, value, HistoryKey(UndoKey, null));

    private void RequestRelayPlace(RelayCellsViewModel cells, RelayPlacementRequest request, UndoHistoryKey key)
    {
        var step = CaptureUndo(key, RunnerLabel("Participants.Col.RelayLeg", cells.ParticipantId));
        _pendingSaves.Track(RunRelayEditAsync(cells, () => PlaceRelayAsync(request, step)));
    }

    private void RequestRelayTeamEdit(RelayCellsViewModel cells, RelayTeamField field, string value, UndoHistoryKey key)
    {
        var column = field == RelayTeamField.Number ? "Participants.Col.RelayTeam" : "Participants.Col.RelayTeamName";
        var step = CaptureUndo(key, RunnerLabel(column, cells.ParticipantId));
        _pendingSaves.Track(RunRelayEditAsync(cells, () => EditRelayTeamAsync(
            new RelayTeamCellEdit(cells.ParticipantId, cells.DayId, field, value), step)));
    }

    // One relay edit at a time (a fill-down paste must create a team once and see each leg the previous row took; a
    // question shows one at a time), then the common ending. True when the edit changed something.
    private async Task<bool> RunRelayEditAsync(RelayCellsViewModel cells, Func<Task<RelayPlacementOutcome>> edit)
    {
        await _relayGate.WaitAsync();
        try
        {
            var outcome = await edit();
            if (!outcome.Changed)
            {
                cells.Revert();
                if (RelayRefusal(outcome) is { } refusal)
                    _toasts.Show(Localization.Get("Participants.Relay.Title"), refusal);
                return false;
            }
            // The edited row shows its new place now (a team just created gets its id, so its leg can be typed straight
            // away); the rest follows on the reload.
            cells.Apply(outcome.Place);
            if (RelayNotice(outcome) is { } notice)
                _toasts.Show(Localization.Get("Participants.Relay.Title"), notice);
            // Other rows moved too (a swapped teammate, a replaced holder, a renamed team, the team's filled legs): their
            // relay cells follow at once — the full reload (results) comes a second later and waits while a cell is typed in.
            await ApplyRelayPlacementsAsync(cells.DayId);
            RequestReload();
            return true;
        }
        catch (DayLockedException locked)
        {
            cells.Revert();
            await ExplainDayLockAsync(locked.DayNumber);
            return false;
        }
        catch (Exception ex)
        {
            cells.Revert();
            _log.Error("Participants.Relay", ex);
            return false;
        }
        finally
        {
            _relayGate.Release();
        }
    }

    // Repaints the relay cells of every row on the day from the stored places (only the cells that changed raise).
    private async Task ApplyRelayPlacementsAsync(Guid dayId)
    {
        try
        {
            var places = await Task.Run(() => _editor.GetRelayPlacementsAsync(dayId));
            foreach (var row in Participants)
                if (row.Relay.DayId == dayId)
                    row.Relay.Apply(places.GetValueOrDefault(row.ParticipantId));
            foreach (var row in Roster)
                foreach (var cell in row.Days)
                    if (cell.DayId == dayId)
                        cell.Relay.Apply(places.GetValueOrDefault(row.ParticipantId));
        }
        catch (Exception ex)
        {
            // The edit itself is saved; the reload that follows repaints the rows anyway.
            _log.Error("Participants.Relay", ex);
        }
    }

    // A leg / team placement; a leg another runner holds is taken after «Замінити».
    private async Task<RelayPlacementOutcome> PlaceRelayAsync(RelayPlacementRequest request, UndoEntry? step)
    {
        var outcome = await RunUndoAsync(step, () => _editor.PlaceRelayRunnerAsync(request));
        if (outcome.Status != RelayPlacementStatus.LegTaken)
            return outcome;
        var replace = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization, "Participants.Relay.LegTaken.Title", "Participants.Relay.LegTaken.Message",
            confirmKey: "Participants.Relay.LegTaken.Replace")
        {
            MessageArgs = [outcome.TeamLabel, outcome.Leg, outcome.OtherRunner],
            IsDestructive = false
        });
        return replace
            ? await RunUndoAsync(step, () => _editor.PlaceRelayRunnerAsync(request with { ReplaceOccupant = true }))
            : new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
    }

    // «Команда» / «Номер команди»: when the edit could change the team itself, ask — change the team for all its runners,
    // or move only this one — then repeat it with the answer.
    private async Task<RelayPlacementOutcome> EditRelayTeamAsync(RelayTeamCellEdit change, UndoEntry? step)
    {
        var outcome = await RunUndoAsync(step, () => _editor.EditRunnerRelayTeamAsync(change));
        var choice = outcome.Status switch
        {
            RelayPlacementStatus.ConfirmTeamChange => await AskTeamChangeAsync(change.Field, outcome),
            RelayPlacementStatus.NumberTaken => await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                    Localization, "Participants.Relay.NumberTaken.Title", "Participants.Relay.NumberTaken.Message",
                    confirmKey: "Participants.Relay.MoveRunner")
                {
                    MessageArgs = [change.Value.Trim(), outcome.OtherTeam],
                    IsDestructive = false
                })
                ? RelayTeamChoice.MoveRunner : (RelayTeamChoice?)null,
            _ => RelayTeamChoice.Ask
        };
        if (choice == RelayTeamChoice.Ask)
            return outcome;
        return choice is { } answer
            ? await RunUndoAsync(step, () => _editor.EditRunnerRelayTeamAsync(change with { Choice = answer }))
            : new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
    }

    // «Змінити номер команди 101 на 105?» / «Перейменувати команду…?» — confirm changes the team, the alternate moves
    // only this runner; null when cancelled.
    private async Task<RelayTeamChoice?> AskTeamChangeAsync(RelayTeamField field, RelayPlacementOutcome outcome)
    {
        var cleared = outcome.NewValue.Length == 0;
        var (title, message, confirm, alternate) = field == RelayTeamField.Number
            ? cleared
                ? ("Participants.Relay.ClearNumber.Title", "Participants.Relay.ClearNumber.Message",
                    "Participants.Relay.ClearNumber.Confirm", "Participants.Relay.ReleaseRunner")
                : ("Participants.Relay.Renumber.Title", "Participants.Relay.Renumber.Message",
                    "Participants.Relay.Renumber.Confirm", "Participants.Relay.MoveToNewNumber")
            : ("Participants.Relay.Rename.Title", "Participants.Relay.Rename.Message",
                "Participants.Relay.Rename.Confirm", "Participants.Relay.MoveToNewName");
        var dialog = new ConfirmDialogViewModel(Localization, title, message, confirmKey: confirm)
        {
            AlternateKey = outcome.CanMoveRunner ? alternate : null,
            MessageArgs = [outcome.TeamLabel, outcome.NewValue, outcome.MemberCount],
            IsDestructive = false
        };
        return await _dialogs.ChooseAsync(dialog) switch
        {
            ConfirmDialogResult.Confirm => RelayTeamChoice.ChangeTeam,
            ConfirmDialogResult.Alternate => RelayTeamChoice.MoveRunner,
            _ => null
        };
    }

    // ── A relay runner's group (Р-22: on a relay day it is the team's). Another group picked in the group cell is asked:
    // take only this runner off the team and move them, or move the whole team (offered only into a group that runs a
    // relay too) — then, when the team's leg numbers hold courses of its group, whether to move it anyway; «не бере
    // участі» (roster) takes them off the team and the day. Cancelled, the cell shows the team's group again. A paste or
    // bulk edit is asked once for all its relay runners (ApplyRelayBulkGroupsAsync).
    private void RequestCellRelayGroupChange(RosterDayCellViewModel cell, GroupOption target)
    {
        var step = CaptureRoster(RunnerLabel("Participants.Col.Group", cell.ParticipantId));
        _pendingSaves.Track(ChangeRelayGroupAsync(cell.Relay, target, cell.ShowGroup, cell.SetGroupSilently,
            leaveDay: () => LeaveRelayDayAsync(cell, step), step));
    }

    private void RequestDayRowRelayGroupChange(ParticipantDayRowViewModel row, GroupOption target)
    {
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.Group", row.FullName));
        _pendingSaves.Track(ChangeRelayGroupAsync(row.Relay, target, row.ShowGroup, row.SetGroupSilently, leaveDay: null, step));
    }

    private async Task ChangeRelayGroupAsync(RelayCellsViewModel cells, GroupOption target, Action showGroup,
        Action<GroupOption> setGroup, Func<Task>? leaveDay, UndoEntry? step)
    {
        var name = RunnerName(cells.ParticipantId);
        var team = cells.Stored is { } place ? RelayTeamNumbers.Label(place.TeamNumber, place.TeamName) : string.Empty;
        if (target.Id is not { } groupId)
        {
            var leave = leaveDay is not null && await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(Localization,
                "Participants.Relay.GroupChange.Title", "Participants.Relay.GroupChange.LeaveMessage",
                confirmKey: "Participants.Relay.GroupChange.Leave")
            {
                MessageArgs = [name, team]
            });
            if (leave)
                await leaveDay!();
            else
                showGroup();
            return;
        }

        var changed = await RunRelayEditAsync(cells, async () =>
            await AskRelayGroupChangeAsync(name, team, target) is { } choice
            && (choice != RelayTeamChoice.ChangeTeam || cells.Stored is not { } stored
                || await ConfirmNumberCoursesAsync(cells.DayId, [stored.TeamId], target))
                ? await RunUndoAsync(step, () => _editor.ChangeRelayRunnerGroupAsync(
                    new RelayGroupEdit(cells.ParticipantId, cells.DayId, groupId, choice)))
                : new RelayPlacementOutcome(RelayPlacementStatus.Unchanged));
        if (changed)
            setGroup(target);
        else
            showGroup();
    }

    // «Змінити групу учасника команди?» — into a relay group: the whole team (confirm) or only this runner, off the team
    // (alternate); into any other group only the runner can go (confirm). Null when cancelled.
    private async Task<RelayTeamChoice?> AskRelayGroupChangeAsync(string name, string team, GroupOption target)
    {
        var dialog = target.IsRelay
            ? new ConfirmDialogViewModel(Localization, "Participants.Relay.GroupChange.Title",
                "Participants.Relay.GroupChange.Message", confirmKey: "Participants.Relay.GroupChange.WholeTeam")
            {
                AlternateKey = "Participants.Relay.GroupChange.OnlyRunner",
                MessageArgs = [name, team, target.Label],
                IsDestructive = false
            }
            : new ConfirmDialogViewModel(Localization, "Participants.Relay.GroupChange.Title",
                "Participants.Relay.GroupChange.NotRelayMessage", confirmKey: "Participants.Relay.GroupChange.ReleaseAndMove")
            {
                MessageArgs = [name, team, target.Label],
                IsDestructive = false
            };
        return await _dialogs.ChooseAsync(dialog) switch
        {
            ConfirmDialogResult.Confirm => target.IsRelay ? RelayTeamChoice.ChangeTeam : RelayTeamChoice.MoveRunner,
            ConfirmDialogResult.Alternate => RelayTeamChoice.MoveRunner,
            _ => null
        };
    }

    // ── A paste / bulk edit of the group column over relay runners. The rows hand their picks here; once the paste / bulk
    // edit has set every row (the next dispatcher turn) they are sorted by day, target group and team. Into a relay
    // group, a team all of whose runners on the day were picked moves as a whole without a question; the other teams are
    // asked once per target group, like the group cell asks for one runner: «Команди повністю» or «Лише цих учасників»
    // (off their teams); into a group that runs no relay only the runners can go. «Залишити в командах» leaves them. A team
    // moving whole whose leg numbers hold courses of its group is asked about once more (RelayNumberCoursesQuestion).
    // Step: the undo step the pick was captured under (the paste / bulk edit gesture); its writes run under it.
    private sealed record RelayBulkGroupPick(RelayCellsViewModel Cells, GroupOption Target, Action<GroupOption> SetGroup,
        UndoEntry? Step);

    private sealed record RelayBulkTeam(RelayPlacement Place, IReadOnlyList<RelayBulkGroupPick> Picks, int MemberCount)
    {
        public bool AllPicked => Picks.Count >= MemberCount;
    }

    private void RequestDayRowRelayBulkGroup(ParticipantDayRowViewModel row, GroupOption target)
        => QueueRelayBulkGroup(new RelayBulkGroupPick(row.Relay, target, row.SetGroupSilently,
            CaptureDayRow(row, UndoLabel("Participants.Col.Group", row.FullName))));

    private void RequestCellRelayBulkGroup(RosterDayCellViewModel cell, GroupOption target)
        => QueueRelayBulkGroup(new RelayBulkGroupPick(cell.Relay, target, cell.SetGroupSilently,
            CaptureRoster(RunnerLabel("Participants.Col.Group", cell.ParticipantId))));

    private void QueueRelayBulkGroup(RelayBulkGroupPick pick)
    {
        // A runner's last pick on a day wins.
        _relayBulkGroupPicks.RemoveAll(p => p.Cells.ParticipantId == pick.Cells.ParticipantId && p.Cells.DayId == pick.Cells.DayId);
        _relayBulkGroupPicks.Add(pick);
        if (_relayBulkGroupQueued)
            return;
        _relayBulkGroupQueued = true;
        // A paste / bulk edit sets its rows one after another in one go: answer once they are all in.
        Dispatcher.UIThread.Post(() => _pendingSaves.Track(ApplyRelayBulkGroupsAsync()), DispatcherPriority.Background);
    }

    private async Task ApplyRelayBulkGroupsAsync()
    {
        var picks = _relayBulkGroupPicks.ToList();
        _relayBulkGroupPicks.Clear();
        _relayBulkGroupQueued = false;
        if (picks.Count == 0)
            return;

        await _relayGate.WaitAsync();
        var notices = new List<string>();
        try
        {
            foreach (var day in picks.GroupBy(p => p.Cells.DayId))
            {
                // The stored places name each picked runner's team and all its runners on the day, shown or not.
                var places = await Task.Run(() => _editor.GetRelayPlacementsAsync(day.Key));
                foreach (var byTarget in day.GroupBy(p => p.Target.Id!.Value))
                {
                    var target = byTarget.First().Target;
                    var teams = byTarget
                        .Where(p => places.GetValueOrDefault(p.Cells.ParticipantId) is { TeamId: var id } && id != Guid.Empty)
                        .GroupBy(p => places[p.Cells.ParticipantId].TeamId)
                        .Select(t => new RelayBulkTeam(places[t.First().Cells.ParticipantId], t.ToList(),
                            places.Values.Count(p => p.TeamId == t.Key)))
                        .OrderBy(t => RelayTeamNumbers.SortKey(t.Place.TeamNumber))
                        .ThenBy(t => t.Place.TeamName, StringComparer.CurrentCulture)
                        .ToList();
                    var asked = teams.Where(t => !target.IsRelay || !t.AllPicked).ToList();
                    var choice = asked.Count == 0 ? (RelayTeamChoice?)null : await AskRelayBulkGroupAsync(asked, target);
                    var whole = teams.Where(t => target.IsRelay && (t.AllPicked || choice == RelayTeamChoice.ChangeTeam)).ToList();
                    // The whole teams whose leg numbers hold courses of their group are asked about once more; kept, they
                    // stay where they are.
                    var numberCourses = await NumberCoursesOfAsync(day.Key, whole.Select(t => t.Place.TeamId).ToList());
                    HashSet<Guid> kept = numberCourses.Count > 0 && !await _dialogs.ConfirmAsync(RelayNumberCoursesQuestion.Build(
                            Localization, numberCourses.SelectMany(c => c.Value), target.Label))
                        ? numberCourses.Keys.ToHashSet()
                        : [];

                    var movedTeams = 0;
                    var movedTeamRunners = 0;
                    var movedRunners = 0;
                    foreach (var team in teams)
                    {
                        var wholeTeam = whole.Contains(team);
                        if (!wholeTeam && (!asked.Contains(team) || choice != RelayTeamChoice.MoveRunner))
                            continue; // left in the team's group
                        if (wholeTeam)
                        {
                            if (kept.Contains(team.Place.TeamId))
                                continue;
                            var outcome = await RunUndoAsync(team.Picks[0].Step, () => _editor.ChangeRelayRunnerGroupAsync(new RelayGroupEdit(
                                team.Picks[0].Cells.ParticipantId, day.Key, target.Id!.Value, RelayTeamChoice.ChangeTeam)));
                            if (!outcome.Changed)
                                continue;
                            movedTeams++;
                            movedTeamRunners += outcome.MemberCount;
                            foreach (var pick in team.Picks)
                                pick.SetGroup(pick.Target);
                            continue;
                        }
                        foreach (var pick in team.Picks)
                        {
                            var outcome = await RunUndoAsync(pick.Step, () => _editor.ChangeRelayRunnerGroupAsync(new RelayGroupEdit(
                                pick.Cells.ParticipantId, day.Key, target.Id!.Value, RelayTeamChoice.MoveRunner)));
                            if (!outcome.Changed)
                                continue;
                            movedRunners++;
                            pick.SetGroup(pick.Target);
                        }
                    }
                    if (movedTeams > 0)
                        notices.Add(string.Format(CultureInfo.CurrentCulture,
                            Localization.Get("Participants.Relay.BulkGroup.TeamsMoved"), target.Label, movedTeams, movedTeamRunners));
                    if (movedRunners > 0)
                        notices.Add(string.Format(CultureInfo.CurrentCulture,
                            Localization.Get("Participants.Relay.BulkGroup.RunnersMoved"), target.Label, movedRunners));
                }
                await ApplyRelayPlacementsAsync(day.Key);
            }
        }
        catch (DayLockedException locked)
        {
            await ExplainDayLockAsync(locked.DayNumber);
        }
        catch (Exception ex)
        {
            _log.Error("Participants.Relay", ex);
        }
        finally
        {
            _relayGate.Release();
        }

        if (notices.Count == 0)
            return;
        foreach (var notice in notices)
            _log.Action(notice);
        _toasts.Show(Localization.Get("Participants.Relay.Title"), string.Join(" ", notices));
        RequestReload();
    }

    // «Змінити групу учасників команд?» for the teams a paste / bulk edit moves only partly (or, into a group that runs no
    // relay, at all): into a relay group the teams wholly (confirm) or only the picked runners, off their teams
    // (alternate); into any other group only the runners (confirm). Null when they are left as they were.
    private async Task<RelayTeamChoice?> AskRelayBulkGroupAsync(IReadOnlyList<RelayBulkTeam> teams, GroupOption target)
    {
        const int shown = 10;
        var list = string.Join(", ", teams.Take(shown).Select(t => string.Format(CultureInfo.CurrentCulture,
            Localization.Get("Participants.Relay.BulkGroup.TeamItem"),
            RelayTeamNumbers.Label(t.Place.TeamNumber, t.Place.TeamName), t.Picks.Count, t.MemberCount)));
        if (teams.Count > shown)
            list += " " + string.Format(CultureInfo.CurrentCulture,
                Localization.Get("Participants.Relay.BulkGroup.More"), teams.Count - shown);

        var dialog = target.IsRelay
            ? new ConfirmDialogViewModel(Localization, "Participants.Relay.BulkGroup.Title",
                "Participants.Relay.BulkGroup.Message", confirmKey: "Participants.Relay.BulkGroup.WholeTeams",
                cancelKey: "Participants.Relay.BulkGroup.Keep")
            {
                AlternateKey = "Participants.Relay.BulkGroup.OnlyRunners",
                MessageArgs = [list, target.Label],
                IsDestructive = false
            }
            : new ConfirmDialogViewModel(Localization, "Participants.Relay.BulkGroup.Title",
                "Participants.Relay.BulkGroup.NotRelayMessage", confirmKey: "Participants.Relay.BulkGroup.ReleaseAndMove",
                cancelKey: "Participants.Relay.BulkGroup.Keep")
            {
                MessageArgs = [list, target.Label],
                IsDestructive = false
            };
        return await _dialogs.ChooseAsync(dialog) switch
        {
            ConfirmDialogResult.Confirm => target.IsRelay ? RelayTeamChoice.ChangeTeam : RelayTeamChoice.MoveRunner,
            ConfirmDialogResult.Alternate => RelayTeamChoice.MoveRunner,
            _ => null
        };
    }

    // A whole team into another group while its leg numbers hold courses of its group («Варіанти за номерами»): there they
    // stop applying — asked once for all the teams (true = move them). The teams that hold none are not asked about.
    private async Task<bool> ConfirmNumberCoursesAsync(Guid dayId, IReadOnlyCollection<Guid> teamIds, GroupOption target)
    {
        var courses = await NumberCoursesOfAsync(dayId, teamIds);
        return courses.Count == 0
            || await _dialogs.ConfirmAsync(RelayNumberCoursesQuestion.Build(Localization, courses.SelectMany(c => c.Value), target.Label));
    }

    // The number courses (of their own group) of the given teams on the day; the teams without any are absent.
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RelayNumberCourse>>> NumberCoursesOfAsync(
        Guid dayId, IReadOnlyCollection<Guid> teamIds)
    {
        if (teamIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<RelayNumberCourse>>();
        var all = await Task.Run(() => _editor.GetRelayNumberCoursesAsync(dayId));
        return teamIds.Where(all.ContainsKey).Distinct().ToDictionary(id => id, id => all[id]);
    }

    // «не бере участі» for a relay runner, once confirmed: off the day — the link goes, and the leg with it.
    private async Task LeaveRelayDayAsync(RosterDayCellViewModel cell, UndoEntry? step)
    {
        try
        {
            if (!await LeaveCellDayAsync(cell, step))
            {
                cell.ShowGroup();
                return;
            }
            await ApplyRelayPlacementsAsync(cell.DayId);
            RequestReload();
        }
        catch (DayLockedException locked)
        {
            cell.ShowGroup();
            await ExplainDayLockAsync(locked.DayNumber);
        }
        catch (Exception ex)
        {
            cell.ShowGroup();
            _log.Error("Participants.Relay", ex);
        }
    }

    private string RunnerName(Guid participantId) =>
        Roster.FirstOrDefault(r => r.ParticipantId == participantId)?.FullName
        ?? Participants.FirstOrDefault(r => r.ParticipantId == participantId)?.FullName
        ?? string.Empty;

    // Why a relay edit did nothing; null when there is nothing to say (the runner is already there / a question was
    // cancelled).
    private string? RelayRefusal(RelayPlacementOutcome outcome) => outcome.Status switch
    {
        RelayPlacementStatus.TeamFull => string.Format(Localization.Get("Participants.Relay.TeamFull"),
            outcome.TeamLabel, outcome.LegCount),
        RelayPlacementStatus.LegOutOfRange => string.Format(Localization.Get("Participants.Relay.LegOutOfRange"),
            outcome.TeamLabel, outcome.LegCount, outcome.Leg),
        RelayPlacementStatus.NoRelayGroup => string.Format(Localization.Get("Participants.Relay.NoRelayGroup"), outcome.TeamLabel),
        RelayPlacementStatus.AmbiguousGroup => string.Format(Localization.Get("Participants.Relay.AmbiguousGroup"), outcome.TeamLabel),
        RelayPlacementStatus.AmbiguousTeam => string.Format(Localization.Get("Participants.Relay.AmbiguousTeam"),
            outcome.TeamLabel, outcome.MemberCount),
        _ => null
    };

    // What else a relay edit changed besides the edited cell; null when nothing.
    private string? RelayNotice(RelayPlacementOutcome outcome)
    {
        var parts = new List<string>();
        if (outcome.TeamCreated)
            parts.Add(string.Format(Localization.Get("Participants.Relay.TeamCreated"), outcome.TeamLabel));
        if (outcome.Status == RelayPlacementStatus.TeamChanged && outcome.MemberCount > 1)
            parts.Add(string.Format(Localization.Get("Participants.Relay.TeamChanged"), outcome.TeamLabel, outcome.MemberCount));
        if (outcome.Status == RelayPlacementStatus.Swapped)
            parts.Add(string.Format(Localization.Get("Participants.Relay.Swapped"), outcome.OtherRunner));
        else if (outcome.Status == RelayPlacementStatus.Placed && outcome.OtherRunner.Length > 0)
            parts.Add(string.Format(Localization.Get("Participants.Relay.Replaced"), outcome.OtherRunner));
        if (outcome.MovedToGroup.Length > 0)
            parts.Add(string.Format(Localization.Get("Participants.Relay.GroupMoved"), outcome.MovedToGroup, outcome.TeamLabel));
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    // ── Bonus (points correction) edit (day grid + roster)
    // The «бонус» cell persists a points correction on the participant-day, then recomputes the day so
    // «Бали» and places re-number live (a correction can re-rank individuals and rogaine teams). Day grid
    // uses the session's current day; the roster cell carries its own day.
    private void RequestDayRowBonusChange(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.Bonus", row.FullName));
        _pendingSaves.Track(ApplyBonusAsync(row.ParticipantId, row.DayId, row.Bonus, step));
    }

    private void RequestCellBonusChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        if (!cell.IsMember)
            return;
        var step = CaptureRoster(RunnerLabel("Participants.Col.Bonus", cell.ParticipantId));
        _pendingSaves.Track(ApplyBonusAsync(cell.ParticipantId, cell.DayId, cell.Bonus, step));
    }

    // The day grid's «Оплата» cell in per-day mode: persisted through its own writer, like the bonus, so
    // the debounced row save can't wipe it. No result recompute — a payment changes no standings.
    private void RequestDayRowPaymentChange(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.Payment", row.FullName));
        _pendingSaves.Track(ApplyDayPaymentAsync(row.ParticipantId, row.DayId, row.DayPayment, step));
    }

    private void RequestCellPaymentChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        if (!cell.IsMember)
            return;
        var step = CaptureRoster(RunnerLabel("Participants.Col.Payment", cell.ParticipantId));
        _pendingSaves.Track(ApplyDayPaymentAsync(cell.ParticipantId, cell.DayId, cell.Payment, step));
    }

    private Task ApplyDayPaymentAsync(Guid participantId, Guid dayId, string payment, UndoEntry? step)
        => WriteAsync(step, () => _editor.SetParticipantDayPaymentAsync(participantId, dayId, payment));

    // Per-day payment mode only: the raised-fee flag belongs to the day, so it is saved on the link.
    private void RequestCellRaisedFeeChange(RosterDayCellViewModel cell)
    {
        if (cell.IsApplying)
            return;
        if (!cell.IsMember)
            return;
        var step = CaptureRoster(RunnerLabel("Participants.Col.RaisedFee", cell.ParticipantId));
        _pendingSaves.Track(ApplyDayRaisedFeeAsync(cell.ParticipantId, cell.DayId, cell.PaysRaisedFee, step));
    }

    private Task ApplyDayRaisedFeeAsync(Guid participantId, Guid dayId, bool paysRaisedFee, UndoEntry? step)
        => WriteAsync(step, () => _editor.SetParticipantDayRaisedFeeAsync(participantId, dayId, paysRaisedFee));

    private Task ApplyBonusAsync(Guid participantId, Guid dayId, int? bonus, UndoEntry? step)
        => WriteThenRefreshAsync(step, dayId, () => _editor.SetParticipantDayBonusAsync(participantId, dayId, bonus),
            evenWithoutReadouts: true);

    // ── Live results
    // A write that can move a result — a status or a bonus, «П/К», a group, a chip, a start time, a team, a field a
    // plugin rule reads, a runner gone from the day — asks for the day's results again, and every row of the day takes
    // them (the others' places, «Бали» and «Очки» move too). Quick edits (typing down a column, a paste, a bulk edit)
    // ask many times over: the reads of a day run one at a time, an ask while one runs makes it read once more, and
    // only a read that started after the last ask is applied — an older read finishing late never lands over a newer
    // one (it did: two recomputes in flight could finish out of order and leave the places of a later edit unshown).
    private sealed class ResultsRead
    {
        public bool Again;
        public Task Task = Task.CompletedTask;
    }

    // Day → its read in flight. Touched on the UI thread only.
    private readonly Dictionary<Guid, ResultsRead> _resultsReads = new();

    // evenWithoutReadouts: the edit shows on a runner with no read-out too (a status, a bonus) or may bring one (a
    // chip). Otherwise a day nobody has read out on yet is left alone — nothing on it is placed or scored.
    private Task RefreshDayResultsAsync(Guid dayId, bool evenWithoutReadouts = false)
    {
        if (!evenWithoutReadouts && !DayHasReadouts(dayId))
            return Task.CompletedTask;
        if (_resultsReads.TryGetValue(dayId, out var running))
        {
            running.Again = true;
            return running.Task;
        }
        var read = new ResultsRead();
        _resultsReads[dayId] = read;
        read.Task = ReadDayResultsAsync(dayId, read);
        return read.Task;
    }

    private async Task ReadDayResultsAsync(Guid dayId, ResultsRead read)
    {
        try
        {
            do
            {
                read.Again = false;
                var results = await _editor.GetDayResultsByParticipantAsync(dayId);
                if (!read.Again)
                    RefreshResults(dayId, results);
            }
            while (read.Again);
        }
        catch
        {
            // never crash the UI over a recompute; the next reload shows the store
        }
        finally
        {
            _resultsReads.Remove(dayId);
        }
    }

    // The member days of the shown rows with a read-out on them.
    private bool DayHasReadouts(Guid dayId) => IsDayMode
        ? Participants.Any(r => r.DayId == dayId && r.HasReadout)
        : Roster.Any(r => r.Days.Any(c => c.DayId == dayId && c.IsMember && c.HasReadout));

    // The days a roster participant runs (each one's results may move with an edit of the person).
    private IReadOnlyList<Guid> MemberDaysOf(Guid participantId)
        => Roster.FirstOrDefault(r => r.ParticipantId == participantId) is { } row
            ? row.Days.Where(c => c.IsMember).Select(c => c.DayId).ToList()
            : [];

    private async Task RefreshDaysResultsAsync(IEnumerable<Guid> dayIds)
    {
        foreach (var dayId in dayIds.Distinct().ToList())
            await RefreshDayResultsAsync(dayId);
    }

    // One background write of a step, then the day's results (see RefreshDayResultsAsync). A failed write refreshes
    // nothing — the next reload shows what the store holds.
    private async Task WriteThenRefreshAsync(UndoEntry? step, Guid dayId, Func<Task> write, bool evenWithoutReadouts = false)
    {
        try
        {
            await RunUndoAsync(step, write);
        }
        catch
        {
            return;
        }
        await RefreshDayResultsAsync(dayId, evenWithoutReadouts);
    }

    // Re-applies recomputed day results onto the visible rows/cells so places re-number after a status
    // edit. Day grid: every row is the current day. Roster: the matching day's cell on each row.
    private void RefreshResults(Guid dayId, IReadOnlyDictionary<Guid, ParticipantDayResult> results)
    {
        // Values the service computed — never a change of the operator's.
        using var programmatic = UndoGesture.Programmatic();
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

    private async Task ResolveCellChipAsync(RosterDayCellViewModel cell, UndoEntry? step)
    {
        var newChip = (cell.Chip ?? string.Empty).Trim();
        var (participantId, dayId) = (cell.ParticipantId, cell.DayId);

        var written = false;
        await _chipGate.WaitAsync();
        try
        {
            var previousChip = (cell.CommittedChip ?? string.Empty).Trim();
            var choice = await ResolveChipReassignmentAsync(participantId, dayId, newChip, previousChip);
            if (choice == ChipConflictChoice.Cancel)
            {
                cell.SetChipSilently(previousChip);
                return;
            }

            cell.MarkChipCommitted(newChip);
            if (choice == ChipConflictChoice.Swap)
            {
                await RunUndoAsync(step, () => _editor.SwapParticipantDayChipsAsync(participantId, dayId, newChip, previousChip));
                SetChipOnConflictingRosterCell(participantId, dayId, newChip, previousChip);
            }
            else
            {
                await RunUndoAsync(step, () => _editor.ReassignParticipantDayChipAsync(participantId, dayId, newChip));
                // Mirror the DB: drop the chip from any other roster cell on the SAME day that showed it.
                SetChipOnConflictingRosterCell(participantId, dayId, newChip, string.Empty);
            }
            written = true;
        }
        catch
        {
            // never crash the UI over a chip edit; the next reload shows the store
        }
        finally
        {
            _chipGate.Release();
        }
        // As on the day grid: the chip decides the runner's read-out, so the day's results follow.
        if (written)
            await RefreshDayResultsAsync(dayId, evenWithoutReadouts: true);
    }

    // After a roster reassignment, replace the chip on the other participant's cell on the same day:
    // blank for a plain take-over, the traded chip for a swap.
    private void SetChipOnConflictingRosterCell(Guid keepParticipantId, Guid dayId, string chip, string handBack)
    {
        if (chip.Length == 0)
            return;
        // Program-made: the store already holds it (written by the step above).
        using var programmatic = UndoGesture.Programmatic();
        foreach (var row in Roster)
        {
            if (row.ParticipantId == keepParticipantId)
                continue;
            foreach (var cell in row.Days)
            {
                if (cell.DayId == dayId && cell.IsMember &&
                    OrientPyx.Plugins.Sdk.Toolkit.TextKey.Same((cell.Chip ?? string.Empty).Trim(), chip))
                {
                    cell.SetChipSilently(handBack);
                    cell.MarkChipCommitted(handBack);
                }
            }
        }
    }

    private async Task ApplyCellGroupChangeAsync(RosterDayCellViewModel cell, UndoEntry? step)
    {
        var participantId = cell.ParticipantId;
        var dayId = cell.DayId;
        var groupId = cell.SelectedGroup.Id;
        try
        {
            if (groupId is null)
                await LeaveCellDayAsync(cell, step);
            else
            {
                var linkId = await RunUndoAsync(step, () => _editor.SetParticipantDayGroupAsync(participantId, dayId, groupId));
                cell.ApplyMembership(isMember: true, linkId: linkId);
                // Joining a day may have copied a chip from another member day (see
                // ParticipantRosterRowViewModel.CopyChipOnJoin); ApplyMembership ran with saves
                // suppressed, so persist the (possibly copied) chip now — part of the same step.
                var chip = (cell.Chip ?? string.Empty).Trim();
                if (chip.Length > 0)
                    await RunUndoAsync(step, () => _editor.SetParticipantDayChipAsync(participantId, dayId, chip));
            }
        }
        catch
        {
            return;
        }
        // A runner who joined, left or changed group moves the places of both groups.
        await RefreshDayResultsAsync(dayId, evenWithoutReadouts: true);
    }

    // "не бере участі": takes the participant off the cell's day; nothing to do if not a member. Their last day deletes
    // the person — ask first; a «no» reloads the page (the cell shows their group again) and returns false.
    private async Task<bool> LeaveCellDayAsync(RosterDayCellViewModel cell, UndoEntry? step)
    {
        if (cell.LinkId is { } linkId)
        {
            if (!await ConfirmLastDayAsync(cell.ParticipantId))
            {
                await LoadAsync();
                return false;
            }
            var participantId = cell.ParticipantId;
            await RunUndoAsync(step, () => _editor.RemoveParticipantFromDayAsync(linkId, participantId));
        }
        cell.ApplyMembership(isMember: false, linkId: null);
        return true;
    }

    // ── Region options + per-row region edit
    // Rebuilds the shared region options list from the current DB. Called on reload and after a "+ new"
    // create, so the list is always: "(none)", regions A→Z, "+ new".
    private async Task RefreshRegionOptionsAsync(bool hasEvent)
    {
        var regions = hasEvent
            ? await _busy.RunAsync(() => _editor.GetRegionsAsync())
            : (IReadOnlyList<BusinessLogic.Entities.Region>)[];
        var options = BuildRegionOptions(regions);
        // A new list reference re-seeds every row's combo, so the old one is kept while nothing in it moved.
        if (!SameOptions(_regionOptions, options, o => (o.Id, o.IsAdd, o.Label)))
            _regionOptions = options;
    }

    // Value equality for a shared option list, by a per-option key (id + label).
    private static bool SameOptions<T, TKey>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, TKey> key)
        where TKey : notnull
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (!key(a[i]).Equals(key(b[i])))
                return false;
        return true;
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
    // Every per-row callback below ignores a row a live refresh is applying (IsApplying): those values came from the
    // store and must neither be written back nor open a dialog.
    private void RequestDayRowRegionChange(ParticipantDayRowViewModel row)
    {
        if (!row.IsApplying)
            SaveRegion(row.ParticipantId, row.SelectedRegion.Id, CaptureDayRow(row, UndoLabel("Participants.Col.Region", row.FullName)));
    }

    private void RequestRosterRowRegionChange(ParticipantRosterRowViewModel row)
    {
        if (!row.IsApplying)
            SaveRegion(row.ParticipantId, row.SelectedRegion.Id, CaptureRoster(UndoLabel("Participants.Col.Region", row.FullName)));
    }

    private void SaveRegion(Guid participantId, Guid? regionId, UndoEntry? step)
        => _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantRegionAsync(participantId, regionId)));

    // "+ new" was picked: prompt for a name, create the region, refresh every row's options, and
    // select the new region on the originating row. On cancel, revert the row to its previous region.
    // One undo step: the new region and the row pointing at it (undoing it removes both).
    private void RequestDayRowAddRegion(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.Region", row.FullName));
        _pendingSaves.Track(AddRegionForRowAsync(
            select: id => SelectRegionOnDayRow(row, id, step),
            revert: () => row.SetRegionSilently(row.CommittedRegion), step));
    }

    private void RequestRosterRowAddRegion(ParticipantRosterRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureRoster(UndoLabel("Participants.Col.Region", row.FullName));
        _pendingSaves.Track(AddRegionForRowAsync(
            select: id => SelectRegionOnRosterRow(row, id, step),
            revert: () => row.SetRegionSilently(row.CommittedRegion), step));
    }

    private async Task AddRegionForRowAsync(Action<Guid> select, Action revert, UndoEntry? step)
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

            var region = await RunUndoAsync(step, () => _busy.RunAsync(() => _editor.AddRegionAsync(name)));
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

    private void SelectRegionOnDayRow(ParticipantDayRowViewModel row, Guid regionId, UndoEntry? step)
    {
        var option = _regionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == regionId);
        if (option is not null)
        {
            row.SetRegionSilently(option);
            SaveRegion(row.ParticipantId, regionId, step);
        }
    }

    private void SelectRegionOnRosterRow(ParticipantRosterRowViewModel row, Guid regionId, UndoEntry? step)
    {
        var option = _regionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == regionId);
        if (option is not null)
        {
            row.SetRegionSilently(option);
            SaveRegion(row.ParticipantId, regionId, step);
        }
    }

    // ── Club options + per-row club edit (mirrors the region flow above)
    private async Task RefreshClubOptionsAsync(bool hasEvent)
    {
        var clubs = hasEvent
            ? await _busy.RunAsync(() => _editor.GetClubsAsync())
            : (IReadOnlyList<BusinessLogic.Entities.Club>)[];
        var options = BuildClubOptions(clubs);
        if (!SameOptions(_clubOptions, options, o => (o.Id, o.IsAdd, o.Label)))
            _clubOptions = options;
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
    {
        if (!row.IsApplying)
            SaveClub(row.ParticipantId, row.SelectedClub.Id, CaptureDayRow(row, UndoLabel("Participants.Col.Club", row.FullName)));
    }

    private void RequestRosterRowClubChange(ParticipantRosterRowViewModel row)
    {
        if (!row.IsApplying)
            SaveClub(row.ParticipantId, row.SelectedClub.Id, CaptureRoster(UndoLabel("Participants.Col.Club", row.FullName)));
    }

    private void SaveClub(Guid participantId, Guid? clubId, UndoEntry? step)
        => _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantClubAsync(participantId, clubId)));

    private void RequestDayRowAddClub(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.Club", row.FullName));
        _pendingSaves.Track(AddClubForRowAsync(
            select: id => SelectClubOnDayRow(row, id, step),
            revert: () => row.SetClubSilently(row.CommittedClub), step));
    }

    private void RequestRosterRowAddClub(ParticipantRosterRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureRoster(UndoLabel("Participants.Col.Club", row.FullName));
        _pendingSaves.Track(AddClubForRowAsync(
            select: id => SelectClubOnRosterRow(row, id, step),
            revert: () => row.SetClubSilently(row.CommittedClub), step));
    }

    private async Task AddClubForRowAsync(Action<Guid> select, Action revert, UndoEntry? step)
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

            var club = await RunUndoAsync(step, () => _busy.RunAsync(() => _editor.AddClubAsync(name)));
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

    private void SelectClubOnDayRow(ParticipantDayRowViewModel row, Guid clubId, UndoEntry? step)
    {
        var option = _clubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == clubId);
        if (option is not null)
        {
            row.SetClubSilently(option);
            SaveClub(row.ParticipantId, clubId, step);
        }
    }

    private void SelectClubOnRosterRow(ParticipantRosterRowViewModel row, Guid clubId, UndoEntry? step)
    {
        var option = _clubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == clubId);
        if (option is not null)
        {
            row.SetClubSilently(option);
            SaveClub(row.ParticipantId, clubId, step);
        }
    }

    // ── ДЮСШ options + per-row school edit (mirrors the region/club flow above)
    private async Task RefreshDusshOptionsAsync(bool hasEvent)
    {
        var dusshes = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDusshesAsync())
            : (IReadOnlyList<BusinessLogic.Entities.Dussh>)[];
        var options = BuildDusshOptions(dusshes);
        if (!SameOptions(_dusshOptions, options, o => (o.Id, o.IsAdd, o.Label)))
            _dusshOptions = options;
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
    {
        if (!row.IsApplying)
            SaveDussh(row.ParticipantId, row.SelectedDussh.Id, CaptureDayRow(row, UndoLabel("Participants.Col.Dussh", row.FullName)));
    }

    private void RequestRosterRowDusshChange(ParticipantRosterRowViewModel row)
    {
        if (!row.IsApplying)
            SaveDussh(row.ParticipantId, row.SelectedDussh.Id, CaptureRoster(UndoLabel("Participants.Col.Dussh", row.FullName)));
    }

    private void SaveDussh(Guid participantId, Guid? dusshId, UndoEntry? step)
        => _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantDusshAsync(participantId, dusshId)));

    private void RequestDayRowAddDussh(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.Dussh", row.FullName));
        _pendingSaves.Track(AddDusshForRowAsync(
            select: id => SelectDusshOnDayRow(row, id, step),
            revert: () => row.SetDusshSilently(row.CommittedDussh), step));
    }

    private void RequestRosterRowAddDussh(ParticipantRosterRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureRoster(UndoLabel("Participants.Col.Dussh", row.FullName));
        _pendingSaves.Track(AddDusshForRowAsync(
            select: id => SelectDusshOnRosterRow(row, id, step),
            revert: () => row.SetDusshSilently(row.CommittedDussh), step));
    }

    private async Task AddDusshForRowAsync(Action<Guid> select, Action revert, UndoEntry? step)
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

            var dussh = await RunUndoAsync(step, () => _busy.RunAsync(() => _editor.AddDusshAsync(name)));
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

    private void SelectDusshOnDayRow(ParticipantDayRowViewModel row, Guid dusshId, UndoEntry? step)
    {
        var option = _dusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == dusshId);
        if (option is not null)
        {
            row.SetDusshSilently(option);
            SaveDussh(row.ParticipantId, dusshId, step);
        }
    }

    private void SelectDusshOnRosterRow(ParticipantRosterRowViewModel row, Guid dusshId, UndoEntry? step)
    {
        var option = _dusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == dusshId);
        if (option is not null)
        {
            row.SetDusshSilently(option);
            SaveDussh(row.ParticipantId, dusshId, step);
        }
    }

    // ── Rank options (application-level, no "+ new")
    // Rebuilds the shared rank options from the app database: "(none)" then the ranks in order. Rank is
    // stored as text, so there is no create flow here — ranks are managed on their own page.
    private async Task RefreshRankOptionsAsync()
    {
        var ranks = await _busy.RunAsync(() => _appStore.GetRanksAsync());
        var options = new List<RankOption>(ranks.Count + 1) { RankOption.None(Localization) };
        foreach (var r in ranks)
            if (!string.IsNullOrWhiteSpace(r.Name))
                options.Add(new RankOption(r.Name, Localization));
        if (!SameOptions(_rankOptions, options, o => o.Value))
            _rankOptions = options;
    }

    // ── Entry-fee data (discounts + the recompute snapshot)
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
            PaymentPerDay = false;
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
        PaymentPerDay = info?.PaymentPerDay ?? false;
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

    // A row's raised-fee flag toggled. Per-day payment puts the flag on the day the grid is showing;
    // otherwise it is competition-level and covers every day the participant runs.
    private void RequestRowRaisedFeeChange(ParticipantDayRowViewModel row)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, UndoLabel("Participants.Col.RaisedFee", row.FullName));
        if (PaymentPerDay)
        {
            _pendingSaves.Track(ApplyDayRaisedFeeAsync(row.ParticipantId, row.DayId, row.PaysRaisedFee, step));
            return;
        }

        var (participantId, value) = (row.ParticipantId, row.PaysRaisedFee);
        _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantPaysRaisedFeeAsync(participantId, value)));
    }

    // ── «Мандатку пройдено» (roster only; F8 / the cell menu)

    /// <summary>
    /// The label the roster table shows for its row action over <paramref name="rows"/>, or null when the
    /// action does not apply (no roster rows among them). The action is a toggle: with any row not yet
    /// checked in it reads «Мандатку пройдено», otherwise «Зняти позначку»; several rows add the count.
    /// </summary>
    public string? CheckedInActionLabel(IReadOnlyList<object> rows)
    {
        var items = RosterRowsOf(rows);
        if (items.Count == 0)
            return null;
        var label = Localization.Get(items.Any(r => !r.CheckedIn)
            ? "Participants.CheckedIn.Mark"
            : "Participants.CheckedIn.Unmark");
        return items.Count > 1
            ? string.Format(Localization.Get("Participants.CheckedIn.Many"), label, items.Count)
            : label;
    }

    /// <summary>
    /// Toggles «Мандатку пройдено» on the roster rows: if any of them is not checked in yet, every one is
    /// marked; if all are, every one is cleared. The rows update at once; the write runs in the background
    /// through its own service call (the debounced row save never touches the flag).
    /// </summary>
    public void ToggleCheckedIn(IReadOnlyList<object> rows)
    {
        var items = RosterRowsOf(rows);
        if (items.Count == 0)
            return;
        var on = items.Any(r => !r.CheckedIn);
        var changed = items.Where(r => r.CheckedIn != on).ToList();
        // One undo step for the rows it marks: «Мандатку пройдено — Іваненко Іван» / «… (рядків: 3)».
        var action = Localization.Get(on ? "Participants.CheckedIn.Mark" : "Participants.CheckedIn.Unmark");
        var step = CaptureUndoAction(HistoryKey(UndoKey, null), changed.Count == 1
            ? string.Format(Localization.Get("Undo.Label.Cell"), action, changed[0].FullName)
            : string.Format(Localization.Get("Undo.Label.RowAction"), action, changed.Count));
        foreach (var row in changed)
            row.CheckedIn = on;
        var ids = changed.Select(r => r.ParticipantId).ToList();
        _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantsCheckedInAsync(ids, on)));
        _log.Action(string.Format(
            Localization.Get(on ? "Participants.CheckedIn.Log.Marked" : "Participants.CheckedIn.Log.Unmarked"),
            changed.Count));
        RecomputeStatusInfo();
    }

    private static List<ParticipantRosterRowViewModel> RosterRowsOf(IReadOnlyList<object> rows)
        => rows.OfType<ParticipantRosterRowViewModel>().ToList();

    // The roster's row-level checkbox only exists outside per-day mode (in it, the flag is a per-day
    // block whose cells save themselves through RequestCellRaisedFeeChange).
    private void RequestRosterRaisedFeeChange(ParticipantRosterRowViewModel row)
    {
        if (row.IsApplying)
            return;
        if (PaymentPerDay)
            return;

        var (participantId, value) = (row.ParticipantId, row.PaysRaisedFee);
        var step = CaptureRoster(UndoLabel("Participants.Col.RaisedFee", row.FullName));
        _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantPaysRaisedFeeAsync(participantId, value)));
    }

    // A row's discount checkbox toggled: persist the link add/remove in the background.
    private void RequestRowDiscountChange(ParticipantDayRowViewModel row, Guid discountId, bool on)
    {
        if (row.IsApplying)
            return;
        var step = CaptureDayRow(row, DiscountLabel(discountId, row.FullName));
        var participantId = row.ParticipantId;
        _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantDiscountAsync(participantId, discountId, on)));
    }

    private void RequestRosterDiscountChange(ParticipantRosterRowViewModel row, Guid discountId, bool on)
    {
        if (row.IsApplying)
            return;
        var step = CaptureRoster(DiscountLabel(discountId, row.FullName));
        var participantId = row.ParticipantId;
        _pendingSaves.Track(WriteAsync(step, () => _editor.SetParticipantDiscountAsync(participantId, discountId, on)));
    }

    // «<discount name> — <row>»: a discount column is captioned with the discount's name.
    private string DiscountLabel(Guid discountId, string? row)
    {
        var name = Discounts.FirstOrDefault(d => d.Id == discountId)?.Name;
        return string.Format(Localization.Get("Undo.Label.Cell"),
            string.IsNullOrWhiteSpace(name) ? Localization.Get("EntryFees.Discount.Unnamed") : name, (row ?? string.Empty).Trim());
    }

    // ── Rental-chip highlight + toggle
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

        // A write to the rental base, made from this page: a step of its own here («Прокатний чіп — 777»).
        var step = CaptureUndoAction(string.Format(Localization.Get("Undo.Label.Cell"),
            Localization.Get("Undo.Entity.RentalChip"), number));
        _pendingSaves.Track(ToggleRentalChipAndRecomputeAsync(number, step));
    }

    // Persists the rental toggle, then rebuilds the shared fee snapshot from the updated rental set and
    // pushes it onto every live row so the «Стартовий внесок» column recomputes (a chip in the rental
    // pool is charged rental; one removed from it is the participant's own, so no rental fee).
    private async Task ToggleRentalChipAndRecomputeAsync(string number, UndoEntry? step)
    {
        await WriteAsync(step, () => _editor.ToggleRentalChipAsync(number));
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
        // Registration progress («Мандатку пройдено») is competition-level, so only the roster shows it.
        if (IsRosterMode)
            parts.Add(string.Format(Localization.Get("Participants.Status.CheckedIn"),
                Roster.Count(r => r.CheckedIn), Roster.Count));

        StatusInfo = string.Join("    •    ", parts);
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
        _saveSteps.Clear();
    }
}

/// <summary>
/// The argument for the bulk-edit command: the shown (filtered + sorted) rows to change, plus an
/// optional <see cref="PreselectKey"/> — the bulk-edit field key the dialog should open on. The view
/// sets it from the focused column (toolbar button) or the right-clicked header column (column menu);
/// null ⇒ the dialog opens on its first field. An unknown/unavailable key is ignored.
/// </summary>
public sealed record BulkEditRequest(IReadOnlyList<object?> Rows, string? PreselectKey = null)
{
    /// <summary>The rows are the selected cells of one column: the dialog offers only the
    /// <see cref="PreselectKey"/> field (nothing when that field is not available here).</summary>
    public bool SelectionOnly { get; init; }

    /// <summary>The roster day (index into the row's <c>Days</c>) whose cell a per-day field changes, instead of
    /// fanning out over the participant's days — the selection lies in one day's column.</summary>
    public int? RosterDayIndex { get; init; }

    /// <summary>The field's label in the dialog and the log instead of its own (e.g. «Групи — День 2»).</summary>
    public string? FieldLabel { get; init; }
}
