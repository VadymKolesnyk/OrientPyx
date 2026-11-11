using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One participant's standing on one day in the roster ("Мандатка") grid. Holds the day's group
/// choices and the current selection. Picking a group on a non-member cell joins the participant to
/// that day; choosing "не участвує" (the null sentinel) leaves the day entirely. Selecting either
/// invokes the page-supplied callback, which persists in the background.
/// </summary>
public sealed partial class RosterDayCellViewModel : ObservableObject
{
    private readonly Guid _participantId;
    private readonly Action<RosterDayCellViewModel> _requestGroupChange;
    private readonly Action<RosterDayCellViewModel> _requestChipChange;
    private readonly Action<RosterDayCellViewModel> _requestStartTimeChange;
    private readonly Action<RosterDayCellViewModel> _requestOutOfCompetitionChange;
    private readonly Action<RosterDayCellViewModel> _requestResultStatusChange;
    private readonly Action<RosterDayCellViewModel> _requestBonusChange;
    private readonly Action<RosterDayCellViewModel> _requestPaymentChange;
    private readonly Action<RosterDayCellViewModel> _requestRaisedFeeChange;
    private readonly Action<RosterDayCellViewModel, GroupOption> _requestRelayGroupChange;
    private ParticipantDayResult _result;
    // The judge's points correction («бонус») for this day; null = none. Edited via BonusText.
    private int? _bonus;
    private bool _initialized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoGroup))]
    [NotifyPropertyChangedFor(nameof(GroupLabel))]
    [NotifyPropertyChangedFor(nameof(NoGroupTooltip))]
    [NotifyPropertyChangedFor(nameof(ComboGroup))]
    private bool _isMember;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoGroup))]
    [NotifyPropertyChangedFor(nameof(GroupLabel))]
    [NotifyPropertyChangedFor(nameof(NoGroupTooltip))]
    [NotifyPropertyChangedFor(nameof(ComboGroup))]
    private GroupOption _selectedGroup;

    [ObservableProperty]
    private string _chip;

    /// <summary>
    /// This day's payment («Оплата»), edited in the roster's per-day payment block; only in force while the
    /// competition charges per day. Persisted through its own page callback, like the chip.
    /// </summary>
    [ObservableProperty]
    private string _payment;

    /// <summary>
    /// Whether the raised (late) entry fee is charged for this day, edited in the roster's per-day
    /// raised-fee block; only in force while the competition charges per day. Persisted through its own
    /// page callback, like the payment.
    /// </summary>
    [ObservableProperty]
    private bool _paysRaisedFee;

    [ObservableProperty]
    private TimeSpan? _startTime;

    [ObservableProperty]
    private bool _outOfCompetition;

    /// <summary>The selected finish-status option (auto sentinel = no override). Editing persists the override.</summary>
    [ObservableProperty]
    private FinishStatusOption _selectedStatus;

    public RosterDayCellViewModel(
        Guid participantId,
        RosterDayCell cell,
        IReadOnlyList<GroupOption> groupOptions,
        ILocalizationService localization,
        Action<RosterDayCellViewModel> requestGroupChange,
        Action<RosterDayCellViewModel> requestChipChange,
        Action<RosterDayCellViewModel> requestStartTimeChange,
        Action<RosterDayCellViewModel> requestOutOfCompetitionChange,
        Action<RosterDayCellViewModel> requestResultStatusChange,
        Action<RosterDayCellViewModel> requestBonusChange,
        Action<RosterDayCellViewModel> requestPaymentChange,
        Action<RosterDayCellViewModel> requestRaisedFeeChange,
        Action<RelayCellsViewModel, RelayPlacementRequest> requestRelayPlace,
        Action<RelayCellsViewModel, RelayTeamField, string> requestRelayTeamEdit,
        Action<RosterDayCellViewModel, GroupOption> requestRelayGroupChange)
    {
        _participantId = participantId;
        _requestRelayGroupChange = requestRelayGroupChange;
        DayId = cell.DayId;
        DayNumber = cell.DayNumber;
        LinkId = cell.LinkId;
        _isMember = cell.IsMember;
        _requestGroupChange = requestGroupChange;
        _requestChipChange = requestChipChange;
        _requestStartTimeChange = requestStartTimeChange;
        _requestOutOfCompetitionChange = requestOutOfCompetitionChange;
        _requestResultStatusChange = requestResultStatusChange;
        _requestBonusChange = requestBonusChange;
        _requestPaymentChange = requestPaymentChange;
        _requestRaisedFeeChange = requestRaisedFeeChange;
        Localization = localization;

        _groupOptions = groupOptions;
        _selectedGroup = groupOptions.FirstOrDefault(o => o.Id == cell.GroupId) ?? groupOptions[0];
        _chip = cell.Chip;
        _committedChip = cell.Chip;
        _payment = cell.Payment;
        _paysRaisedFee = cell.PaysRaisedFee;
        _startTime = cell.StartTime;
        _outOfCompetition = cell.OutOfCompetition;
        _bonus = cell.Bonus;

        _result = cell.Result;
        // The "(… — автоматично)" sentinel reflects what auto would compute (override cleared), NOT the
        // effective status (which already folds the override in).
        _statusOptions = FinishStatusOptions.Build(localization, cell.Result.Computed);
        _selectedStatus = FinishStatusOptions.Select(StatusOptions, cell.Result.Override);

        _isRelayDay = cell.IsRelayDay;
        Relay = new RelayCellsViewModel(participantId, cell.DayId, cell.Relay, localization, requestRelayPlace, requestRelayTeamEdit);
        // The row's merged relay cells, the leg cell's lock and the group cell's lock follow the relay place.
        Relay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RelayCellsViewModel.HasTeam))
            {
                OnPropertyChanged(nameof(CanEditGroup));
                OnPropertyChanged(nameof(CanEditRelayLeg));
            }
            else if (e.PropertyName is nameof(RelayCellsViewModel.BibText) or nameof(RelayCellsViewModel.TeamName)
                     or nameof(RelayCellsViewModel.TeamText) or nameof(RelayCellsViewModel.LegText))
            {
                OnPropertyChanged(nameof(Relay));
            }
        };

        _initialized = true;
    }

    /// <summary>This day's relay cells («Команда», «Номер команди», «Етап», «Номер етапу»); live only on a relay day.
    /// A change of any of them is raised as <c>Relay</c> too, so the row's merged cells follow.</summary>
    public RelayCellsViewModel Relay { get; }

    /// <summary>The day runs a relay (a relay group or at least one team) — the relay cells are live on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditRelay))]
    [NotifyPropertyChangedFor(nameof(CanEditRelayLeg))]
    private bool _isRelayDay;

    /// <summary>The team cells accept an edit: a relay day that is still open. A runner who does not run the day yet
    /// may be put on a team too — they join the day in the team's group.</summary>
    public bool CanEditRelay => IsRelayDay && !IsDayLocked;

    /// <summary>The «Етап» cell accepts an edit: as <see cref="CanEditRelay"/>, on a team.</summary>
    public bool CanEditRelayLeg => CanEditRelay && Relay.HasTeam;

    /// <summary>The group cell accepts an edit: an open day. A relay runner's group is the team's (Р-22), so picking
    /// another one for them is asked by the page (see <see cref="ComboGroup"/>).</summary>
    public bool CanEditGroup => CanEdit;

    /// <summary>The finish-status choices: a descriptive "(<computed> — автоматично)" sentinel, then the
    /// settable statuses. Rebuilt when the result changes.</summary>
    [ObservableProperty]
    private IReadOnlyList<FinishStatusOption> _statusOptions;

    /// <summary>The status override the user picked (null = "auto"). Read by the page callback.</summary>
    public FinishStatus? ResultStatusOverride => SelectedStatus?.Status;

    /// <summary>The effective status code shown on the resting status cell (OK/MP/…); blank when no result.</summary>
    public string ResultStatusText => ResultText.Status(_result);

    /// <summary>True when the status is a problem code (anything but OK / blank) — the cell shows it in red.</summary>
    public bool StatusIsProblem => _result.StatusIsProblem;

    /// <summary>
    /// True when this cell's day is closed for editing. Set by the page from the day list; a locked
    /// day's cells rest read-only in the roster exactly as its own grid does.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanEditGroup))]
    [NotifyPropertyChangedFor(nameof(CanEditRelay))]
    [NotifyPropertyChangedFor(nameof(CanEditRelayLeg))]
    [NotifyPropertyChangedFor(nameof(CanEditStatus))]
    [NotifyPropertyChangedFor(nameof(CanEditMemberFields))]
    [NotifyPropertyChangedFor(nameof(LockedDayNumber))]
    private bool _isDayLocked;

    /// <summary>True when this day's cells accept edits at all (an open day).</summary>
    public bool CanEdit => !IsDayLocked;

    /// <summary>
    /// This cell's day number while it is closed, else 0. Bound by the cell so a refused click can name
    /// the day — in the roster each column is a different day, so "the current day" would be wrong.
    /// </summary>
    public int LockedDayNumber => IsDayLocked ? DayNumber : 0;

    /// <summary>
    /// True when the per-day member fields (chip, start time, bonus, поза конкурсом) are editable here:
    /// the participant runs this day AND the day is still open. The cells bind their enabled state to
    /// this, so a closed day reads exactly like a non-member day — flat, greyed, not clickable.
    /// </summary>
    public bool CanEditMemberFields => IsMember && !IsDayLocked;

    /// <summary>True for any day the participant runs: a judge can override the computed status (with a
    /// read-out) or mark DNS/DNF/… without one (picking OK then leaves it blank). Non-members can't,
    /// and neither can a day that is closed for editing.</summary>
    public bool CanEditStatus => IsMember && !IsDayLocked;

    // ── Read-only computed result columns
    public string ActualStartText => ResultText.ActualStart(_result);
    public string FinishText => ResultText.Finish(_result);
    public string ResultText_ => ResultText.Result(_result);
    public string PlaceText => ResultText.Place(_result);
    public string ScoreText => ResultText.Score(_result);
    /// <summary>Per-control «Бали» breakdown for the score column's hover tooltip; null when no score.</summary>
    public string? ScoreTooltip => ResultText.ScoreTooltip(_result, Localization);
    /// <summary>Ranking points / «Очки» (from the group's points rule); blank when none awarded.</summary>
    public string PointsText => ResultText.Points(_result);
    /// <summary>Awarded sports rank / «Виконаний розряд» (Додаток 89); blank when none.</summary>
    public string AwardedRankText => ResultText.AwardedRank(_result);

    /// <summary>
    /// The judge's points correction («бонус») as editable signed-integer text; empty clears it. Editable
    /// only on days the participant runs; persists through the page callback (which recomputes «Бали» live).
    /// An unparseable entry reverts on the next notification.
    /// </summary>
    public string BonusText
    {
        get => BonusFormat.Format(_bonus);
        set
        {
            if (!BonusFormat.TryParse(value, out var parsed))
            {
                OnPropertyChanged(); // unparseable — revert the box to the stored value
                return;
            }
            if (parsed == _bonus)
                return;
            _bonus = parsed;
            OnPropertyChanged();
            if (_initialized && IsMember && !IsDayLocked)
                _requestBonusChange(this);
        }
    }

    /// <summary>The parsed bonus the user entered (null = none). Read by the page's bonus callback.</summary>
    public int? Bonus => _bonus;

    // CanEditStatus / CanEditMemberFields fold in membership (and the day lock), so re-raise both
    // whenever membership flips.
    partial void OnIsMemberChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditStatus));
        OnPropertyChanged(nameof(CanEditMemberFields));
        OnPropertyChanged(nameof(PaymentStatus));
        OnPropertyChanged(nameof(PaymentStatusKey));
    }

    // The status dropdown is owned by the page (persists the override + re-ranks); member-only.
    partial void OnSelectedStatusChanged(FinishStatusOption value)
    {
        if (_initialized && IsMember && !IsDayLocked && value is not null)
            _requestResultStatusChange(this);
    }

    /// <summary>Applies a recomputed result (after a status edit re-ranked the day) without re-firing the callback.
    /// Only the display properties whose value moved raise a change.</summary>
    public void ApplyResult(ParticipantDayResult result)
    {
        if (ReferenceEquals(_result, result))
            return;

        var before = ResultSnapshot();
        var computedChanged = _result.Computed != result.Computed;
        _result = result;
        var wasInitialized = _initialized;
        _initialized = false;
        if (computedChanged)
            StatusOptions = FinishStatusOptions.Build(Localization, result.Computed);
        SelectedStatus = FinishStatusOptions.Select(StatusOptions, result.Override);
        _initialized = wasInitialized;
        foreach (var (name, value) in ResultSnapshot())
            if (!Equals(before[name], value))
                OnPropertyChanged(name);
    }

    // Every result-derived bound property with its current value (see ApplyResult).
    private Dictionary<string, object?> ResultSnapshot() => new()
    {
        [nameof(ResultStatusText)] = ResultStatusText,
        [nameof(StatusIsProblem)] = StatusIsProblem,
        [nameof(CanEditStatus)] = CanEditStatus,
        [nameof(ActualStartText)] = ActualStartText,
        [nameof(FinishText)] = FinishText,
        [nameof(ResultText_)] = ResultText_,
        [nameof(PlaceText)] = PlaceText,
        [nameof(ScoreText)] = ScoreText,
        [nameof(ScoreTooltip)] = ScoreTooltip,
        [nameof(PointsText)] = PointsText,
        [nameof(AwardedRankText)] = AwardedRankText,
    };

    /// <summary>True while <see cref="Apply"/> copies fresh values onto the cell; the page skips its callbacks then.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>
    /// Copies a freshly-read day cell onto this one without triggering any save or dialog. Only the values that
    /// changed raise a property change; the day's group list is swapped only when the page rebuilt it.
    /// </summary>
    public void Apply(RosterDayCell cell, IReadOnlyList<GroupOption> groupOptions)
    {
        IsApplying = true;
        _initialized = false;
        try
        {
            if (DayNumber != cell.DayNumber)
            {
                DayNumber = cell.DayNumber;
                OnPropertyChanged(nameof(DayNumber));
                OnPropertyChanged(nameof(LockedDayNumber));
            }
            LinkId = cell.LinkId;
            if (!ReferenceEquals(GroupOptions, groupOptions))
                GroupOptions = groupOptions;
            SelectedGroup = groupOptions.FirstOrDefault(o => o.Id == cell.GroupId) ?? groupOptions[0];
            IsMember = cell.IsMember;
            Chip = cell.Chip;
            _committedChip = cell.Chip;
            Payment = cell.Payment;
            PaysRaisedFee = cell.PaysRaisedFee;
            StartTime = cell.StartTime;
            OutOfCompetition = cell.OutOfCompetition;
            if (_bonus != cell.Bonus)
            {
                _bonus = cell.Bonus;
                OnPropertyChanged(nameof(BonusText));
            }
            ApplyResult(cell.Result);
            IsRelayDay = cell.IsRelayDay;
            Relay.Apply(cell.Relay);
        }
        finally
        {
            _initialized = true;
            IsApplying = false;
        }
    }

    /// <summary>
    /// The start time as editable "hh:mm:ss" text. Empty clears it; a partial entry is padded and any
    /// out-of-range minute/second is clamped to 59 (see <see cref="StartTimeFormat"/>); a truly
    /// unparseable value is ignored (the cell reverts on the next notification). Kept as a string so the
    /// cell reuses a plain text editor like the chip cell, without a converter or masked-time control.
    /// </summary>
    public string StartTimeText
    {
        get => StartTimeFormat.Format(StartTime);
        set
        {
            if (StartTimeFormat.TryParse(value, out var parsed))
                StartTime = parsed;
            else
                // Unparseable — keep the stored value and re-raise so the box reverts to it.
                OnPropertyChanged();
        }
    }

    // The last chip value the page accepted/persisted, so a rejected reassignment can revert to it.
    private string _committedChip;

    /// <summary>The previously committed chip (to restore after a rejected reassignment).</summary>
    public string CommittedChip => _committedChip;

    /// <summary>Records the chip the page has accepted (after a successful save/reassign).</summary>
    public void MarkChipCommitted(string value) => _committedChip = value;

    /// <summary>Restores the chip without re-triggering the chip-change callback (revert / external clear).</summary>
    public void SetChipSilently(string value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        Chip = value;
        _initialized = wasInitialized;
    }

    public ILocalizationService Localization { get; }

    /// <summary>The day this cell belongs to.</summary>
    public Guid DayId { get; }

    /// <summary>1-based day number (for the column header).</summary>
    public int DayNumber { get; private set; }

    /// <summary>The participant's link id for this day, or null when not a member.</summary>
    public Guid? LinkId { get; private set; }

    public Guid ParticipantId => _participantId;

    /// <summary>Group choices for this specific day (id + name), with a leading "не участвує" sentinel.</summary>
    [ObservableProperty]
    private IReadOnlyList<GroupOption> _groupOptions;

    partial void OnSelectedGroupChanged(GroupOption value)
    {
        // The course length the chip must hold comes from the group, so a group change can flip the flag.
        OnPropertyChanged(nameof(ChipTooSmall));
        OnPropertyChanged(nameof(ChipCapacityTooltip));
        // A relay runner's group is the team's (Р-22): the cell is read-only, so a paste never moves them either.
        if (_initialized && !IsDayLocked && !Relay.HasTeam)
            _requestGroupChange(this);
    }

    /// <summary>
    /// The participant runs this day but has no group on it: none was set (a UOF «3Е» with no relay group, a file row
    /// with no group), or theirs is no longer on the day's list and the cell fell back to the "(none)" option. Shown
    /// as amber «без групи» — never as «не бере участі», which means "not on this day".
    /// </summary>
    public bool HasNoGroup => IsMember && SelectedGroup?.Id is null;

    /// <summary>The group cell's resting text: the group, «без групи» (<see cref="HasNoGroup"/>) or «не бере участі».
    /// Also the column's sort and filter value.</summary>
    public string GroupLabel => HasNoGroup
        ? Localization.Get("Participants.Group.Missing")
        : SelectedGroup?.Label ?? string.Empty;

    /// <summary>The amber group cell's tooltip; null otherwise, which hides it.</summary>
    public string? NoGroupTooltip => HasNoGroup ? Localization.Get("Participants.Group.Missing.Tooltip") : null;

    /// <summary>
    /// What the group combo selects: <see cref="SelectedGroup"/>, except nothing while <see cref="HasNoGroup"/> — the
    /// "(none)" option would read as the current value there, and picking it would change nothing. With no
    /// selection, picking «не бере участі» takes the participant off the day. A relay runner's pick is handed to the page
    /// to ask (their group is the team's, Р-22); the selection moves only once that is answered.
    /// </summary>
    public GroupOption? ComboGroup
    {
        get => HasNoGroup ? null : SelectedGroup;
        set
        {
            if (value is null)
                return; // the combo dropping its own selection (a filtered list) is not a choice
            if (Relay.HasTeam)
            {
                if (!ReferenceEquals(value, SelectedGroup) && _initialized && !IsDayLocked)
                    _requestRelayGroupChange(this, value);
                return;
            }
            if (!ReferenceEquals(value, SelectedGroup))
                SelectedGroup = value;
            else if (HasNoGroup && _initialized && !IsDayLocked)
                _requestGroupChange(this); // «не бере участі» on a member with no group: SelectedGroup does not move
        }
    }

    /// <summary>
    /// The group set by a paste or a bulk edit — never asked, so a relay runner keeps the team's group (Р-22); the
    /// change is made one cell at a time through the combo, which asks.
    /// </summary>
    public GroupOption? BulkGroup
    {
        get => SelectedGroup;
        set
        {
            if (value is not null && !Relay.HasTeam)
                SelectedGroup = value;
        }
    }

    /// <summary>Shows the stored group again in the combo — a relay group change was cancelled or refused.</summary>
    public void ShowGroup() => OnPropertyChanged(nameof(ComboGroup));

    /// <summary>Sets the group a relay group change just saved, without asking or saving again.</summary>
    public void SetGroupSilently(GroupOption group)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        SelectedGroup = group;
        _initialized = wasInitialized;
    }

    /// <summary>
    /// True when this day's chip is a known SportIdent card model that holds fewer punches than the day's group
    /// course has controls (<see cref="GroupOption.ControlCount"/>). Drives the amber tint on the per-day chip
    /// cell. False for a non-member, a blank/unknown chip, or a group without a course.
    /// </summary>
    public bool ChipTooSmall => IsMember && SiCard.TooSmall(Chip, SelectedGroup?.ControlCount ?? 0) is not null;

    /// <summary>Why the chip is too small (model, capacity, group, control count); null when it fits, which hides the tooltip.</summary>
    public string? ChipCapacityTooltip
    {
        get
        {
            var reason = IsMember ? SelectedGroup?.ChipCapacityReason(Chip) : null;
            return string.IsNullOrEmpty(reason) ? null : reason;
        }
    }

    partial void OnChipChanged(string value)
    {
        // Re-evaluate on every change, a silent (reverted / scanned-in) one included — the cell shows the
        // new number either way.
        OnPropertyChanged(nameof(ChipTooSmall));
        OnPropertyChanged(nameof(ChipCapacityTooltip));
        if (_initialized && !IsDayLocked)
            _requestChipChange(this);
    }

    partial void OnPaysRaisedFeeChanged(bool value)
    {
        if (_initialized && IsMember && !IsDayLocked)
            _requestRaisedFeeChange(this);
    }

    partial void OnPaymentChanged(string value)
    {
        // The tint compares against this day's own share of the fee, so it moves with the typed value.
        OnPropertyChanged(nameof(PaymentStatus));
        OnPropertyChanged(nameof(PaymentStatusKey));
        if (_initialized && IsMember && !IsDayLocked)
            _requestPaymentChange(this);
    }

    private decimal _dayEntryFee;

    /// <summary>
    /// This day's share of the participant's total entry fee — the baseline «Оплата» is compared against in
    /// per-day payment mode. Written by the row whenever it recomputes the total (a group / chip / discount
    /// edit moves it), not by this cell.
    /// </summary>
    public decimal DayEntryFee
    {
        get => _dayEntryFee;
        set
        {
            if (SetProperty(ref _dayEntryFee, value))
            {
                OnPropertyChanged(nameof(FormattedDayFee));
                OnPropertyChanged(nameof(PaymentStatus));
                OnPropertyChanged(nameof(PaymentStatusKey));
            }
        }
    }

    /// <summary>This day's fee share formatted for display (no currency symbol, trims trailing zeros).</summary>
    public string FormattedDayFee => DayEntryFee.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>How this day's «Оплата» compares to this day's fee share — drives the cell tint + filter.</summary>
    public PaymentStatus PaymentStatus => IsMember
        ? PaymentStatusExtensions.Classify(Payment, DayEntryFee)
        : PaymentStatus.Empty;

    /// <summary>The payment status as a stable token, used as the per-day payment column's filter value.</summary>
    public string PaymentStatusKey => PaymentStatus.ToString();

    /// <summary>Sets the payment without re-triggering the change callback (a merged write already persisted).</summary>
    public void SetPaymentSilently(string value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        Payment = value;
        _initialized = wasInitialized;
    }

    partial void OnStartTimeChanged(TimeSpan? value)
    {
        // Keep the editable text in sync, then persist (no uniqueness rule, so a plain save).
        OnPropertyChanged(nameof(StartTimeText));
        if (_initialized && !IsDayLocked)
            _requestStartTimeChange(this);
    }

    partial void OnOutOfCompetitionChanged(bool value)
    {
        if (_initialized && !IsDayLocked)
            _requestOutOfCompetitionChange(this);
    }

    /// <summary>Sets the start time without re-triggering the change callback (external clear / leave-day).</summary>
    public void SetStartTimeSilently(TimeSpan? value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        StartTime = value;
        _initialized = wasInitialized;
    }

    /// <summary>
    /// Updates the cell after a membership change persisted (joined/left), without re-triggering the
    /// save callback. Called by the page once the background write has applied. Leaving a day also
    /// clears the chip (it is per-day and meaningless for a non-member).
    /// </summary>
    public void ApplyMembership(bool isMember, Guid? linkId)
    {
        _initialized = false;
        IsMember = isMember;
        LinkId = linkId;
        if (!isMember)
        {
            SelectedGroup = GroupOptions[0];
            Chip = string.Empty;
            Payment = string.Empty;
            StartTime = null;
            OutOfCompetition = false;
            _bonus = null;
            OnPropertyChanged(nameof(BonusText));
            ApplyResult(ParticipantDayResult.Empty);
        }
        _initialized = true;
    }
}
