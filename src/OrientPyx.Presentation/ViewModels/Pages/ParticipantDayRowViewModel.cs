using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the day-mode participants grid. Wraps a single <see cref="ParticipantDayRow"/>
/// (a participant joined with its link on the current day). Identity fields (surname, name, number,
/// rank, coach, birth date) are competition-level — editing them affects every day; group, chip and
/// team are this day's only. Edits do not save directly: each change invokes the page-supplied
/// <c>requestSave</c> callback, which debounces and persists in the background.
///
/// Whether the discipline-specific Team column is relevant is decided by the day's default
/// discipline via <see cref="IDisciplineStrategy"/> — no competition rules live here.
/// </summary>
public sealed partial class ParticipantDayRowViewModel : ObservableObject
{
    private readonly Guid _linkId;
    private readonly Guid _dayId;
    private readonly Guid _participantId;
    private int _order;
    private DisciplineId _dayDefaultDiscipline;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly Action<ParticipantDayRowViewModel> _requestSave;
    private readonly Action<ParticipantDayRowViewModel> _requestLeaveDay;
    private readonly Action<ParticipantDayRowViewModel, GroupOption> _requestRelayGroupChange;
    private readonly Action<ParticipantDayRowViewModel> _requestChipChange;
    private readonly Action<ParticipantDayRowViewModel> _requestRegionChange;
    private readonly Action<ParticipantDayRowViewModel> _requestAddRegion;
    private readonly Action<ParticipantDayRowViewModel> _requestClubChange;
    private readonly Action<ParticipantDayRowViewModel> _requestAddClub;
    private readonly Action<ParticipantDayRowViewModel> _requestDusshChange;
    private readonly Action<ParticipantDayRowViewModel> _requestAddDussh;
    private readonly Action<ParticipantDayRowViewModel> _requestRaisedFeeChange;
    private readonly Action<ParticipantDayRowViewModel, Guid, bool> _requestDiscountChange;
    private readonly Action<ParticipantDayRowViewModel> _requestResultStatusChange;
    private readonly Action<ParticipantDayRowViewModel> _requestBonusChange;
    private readonly Action<ParticipantDayRowViewModel> _requestDayPaymentChange;
    // Whether the competition charges per day: decides which payment field the «Оплата» column edits and
    // which fee the value is compared against. Fixed for the row's lifetime (a mode change reloads the page).
    private bool _paymentPerDay;
    private EntryFeeContext _fees;
    private IReadOnlyList<ParticipantFeeDay> _otherDays;
    // The page's shared rank list the row's own list was derived from (an "unknown" option may be prepended).
    private IReadOnlyList<RankOption> _sharedRankOptions;

    // Suppresses save requests while the constructor seeds initial values, and while a rejected
    // reassignment reverts the chip.
    private bool _initialized;

    /// <summary>Whether this participant is charged the raised (late) fee. Competition-level.</summary>
    [ObservableProperty]
    private bool _paysRaisedFee;

    [ObservableProperty]
    private string _fullName;

    [ObservableProperty]
    private string _number;

    [ObservableProperty]
    private string _rank;

    [ObservableProperty]
    private string _coach;

    [ObservableProperty]
    private DateTimeOffset? _birthDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoGroup))]
    [NotifyPropertyChangedFor(nameof(GroupLabel))]
    [NotifyPropertyChangedFor(nameof(NoGroupTooltip))]
    [NotifyPropertyChangedFor(nameof(ComboGroup))]
    private GroupOption _selectedGroup;

    [ObservableProperty]
    private RegionOption _selectedRegion;

    [ObservableProperty]
    private ClubOption _selectedClub;

    [ObservableProperty]
    private DusshOption _selectedDussh;

    [ObservableProperty]
    private RankOption _selectedRank;

    [ObservableProperty]
    private string _representative;

    [ObservableProperty]
    private string _fsouCode;

    [ObservableProperty]
    private bool _isFsouMember;

    [ObservableProperty]
    private string _payment;

    /// <summary>
    /// This day's own payment («Оплата» in per-day payment mode). The «Оплата» column binds here instead of
    /// <see cref="Payment"/> while the competition charges per day; persisted through its own callback so
    /// the debounced row save can't wipe it.
    /// </summary>
    [ObservableProperty]
    private string _dayPayment;

    [ObservableProperty]
    private string _note;

    [ObservableProperty]
    private string _chip;

    [ObservableProperty]
    private string _team;

    [ObservableProperty]
    private TimeSpan? _startTime;

    [ObservableProperty]
    private bool _outOfCompetition;

    // The computed run result for this day (read-only except the status override below).
    private ParticipantDayResult _result;

    // The judge's points correction («бонус») for this day; null = none. Edited via BonusText, persisted
    // through its own callback (like the status override) so the debounced row save can't wipe it.
    private int? _bonus;

    /// <summary>The selected finish-status option: an "(автоматично)" sentinel (no override, the computed
    /// status shows through) or a settable status. Editing it persists the override on the participant-day.</summary>
    [ObservableProperty]
    private FinishStatusOption _selectedStatus;

    public ParticipantDayRowViewModel(
        ParticipantDayRow row,
        IReadOnlyList<GroupOption> groupOptions,
        IReadOnlyList<RegionOption> regionOptions,
        IReadOnlyList<ClubOption> clubOptions,
        IReadOnlyList<DusshOption> dusshOptions,
        IReadOnlyList<RankOption> rankOptions,
        IReadOnlyList<EntryFeeDiscount> discounts,
        EntryFeeContext fees,
        ILocalizationService localization,
        IDisciplineStrategyProvider strategies,
        Action<ParticipantDayRowViewModel> requestSave,
        Action<ParticipantDayRowViewModel> requestLeaveDay,
        Action<ParticipantDayRowViewModel> requestChipChange,
        Action<ParticipantDayRowViewModel> requestRegionChange,
        Action<ParticipantDayRowViewModel> requestAddRegion,
        Action<ParticipantDayRowViewModel> requestClubChange,
        Action<ParticipantDayRowViewModel> requestAddClub,
        Action<ParticipantDayRowViewModel> requestDusshChange,
        Action<ParticipantDayRowViewModel> requestAddDussh,
        Action<ParticipantDayRowViewModel> requestRaisedFeeChange,
        Action<ParticipantDayRowViewModel, Guid, bool> requestDiscountChange,
        Action<ParticipantDayRowViewModel> requestResultStatusChange,
        Action<ParticipantDayRowViewModel> requestBonusChange,
        Action<ParticipantDayRowViewModel> requestDayPaymentChange,
        Action<RelayCellsViewModel, RelayPlacementRequest> requestRelayPlace,
        Action<RelayCellsViewModel, RelayTeamField, string> requestRelayTeamEdit,
        Action<ParticipantDayRowViewModel, GroupOption> requestRelayGroupChange)
    {
        _requestRelayGroupChange = requestRelayGroupChange;
        _linkId = row.LinkId;
        _dayId = row.DayId;
        _participantId = row.ParticipantId;
        _order = row.Order;
        _dayDefaultDiscipline = row.DayDefaultDiscipline;
        _strategies = strategies;
        _requestSave = requestSave;
        _requestLeaveDay = requestLeaveDay;
        _requestChipChange = requestChipChange;
        _requestRegionChange = requestRegionChange;
        _requestAddRegion = requestAddRegion;
        _requestClubChange = requestClubChange;
        _requestAddClub = requestAddClub;
        _requestDusshChange = requestDusshChange;
        _requestAddDussh = requestAddDussh;
        _requestRaisedFeeChange = requestRaisedFeeChange;
        _requestDiscountChange = requestDiscountChange;
        _requestResultStatusChange = requestResultStatusChange;
        _requestBonusChange = requestBonusChange;
        _requestDayPaymentChange = requestDayPaymentChange;
        _paymentPerDay = row.PaymentPerDay;
        _fees = fees;
        _otherDays = row.OtherDays;
        Localization = localization;

        _groupOptions = groupOptions;
        RegionOptions = regionOptions;
        ClubOptions = clubOptions;
        DusshOptions = dusshOptions;

        _fullName = row.FullName;
        _number = row.Number;
        _rank = row.Rank;
        // Rank stores text; resolve the dropdown selection by matching the stored name (case-insensitive).
        // A stored value not in the list gets a one-off "unknown" option prepended so it still shows.
        var (rankOpts, rankSel) = RankOptionResolver.Resolve(rankOptions, row.Rank, localization);
        _sharedRankOptions = rankOptions;
        _rankOptions = rankOpts;
        _selectedRank = rankSel;
        _coach = row.Coach;
        _birthDate = row.BirthDate;
        _representative = row.Representative;
        _fsouCode = row.FsouCode;
        _note = row.Note;
        _isFsouMember = row.IsFsouMember;
        _payment = row.Payment;
        _dayPayment = row.DayPayment;
        _chip = row.Chip;
        _committedChip = row.Chip;
        _team = row.Team;
        _startTime = row.StartTime;
        _outOfCompetition = row.OutOfCompetition;
        _bonus = row.Bonus;

        // Computed result + the status-override dropdown. The resting cell shows the effective status
        // text (ResultStatusText); the dropdown's "auto" sentinel reads "(<computed> — автоматично)".
        _result = row.Result;
        // The "(… — автоматично)" sentinel reflects what auto would compute (override cleared), NOT the
        // effective status (which already folds the override in).
        _statusOptions = FinishStatusOptions.Build(localization, row.Result.Computed);
        _selectedStatus = FinishStatusOptions.Select(StatusOptions, row.Result.Override);

        // Match by id; a group that is unset or not on the day's list resolves to "no group" (see ResolveGroup).
        _selectedGroup = ResolveGroup(groupOptions, row.GroupId);
        // Region/Club match by id across their shared lists; fall back to "(none)" (the first option).
        _selectedRegion = regionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.RegionId) ?? regionOptions[0];
        _committedRegion = _selectedRegion;
        _selectedClub = clubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.ClubId) ?? clubOptions[0];
        _committedClub = _selectedClub;
        _selectedDussh = dusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.DusshId) ?? dusshOptions[0];
        _committedDussh = _selectedDussh;

        // Entry-fee state: one DiscountFlags entry per discount column, in build order (FSOU first).
        _paysRaisedFee = row.PaysRaisedFee;
        var selected = row.SelectedDiscountIds.ToHashSet();
        DiscountFlags = new ObservableCollection<DiscountFlagViewModel>();
        foreach (var d in discounts)
        {
            var on = d.IsFsouMemberDiscount ? row.IsFsouMember : selected.Contains(d.Id);
            DiscountFlags.Add(new DiscountFlagViewModel(d.Id, d.IsFsouMemberDiscount, on, OnDiscountFlagChanged));
        }
        _totalEntryFee = row.TotalEntryFee;

        // The relay cells (team / leg / «100.2» / team name) edit the stored place through their own page callbacks.
        Relay = new RelayCellsViewModel(row.ParticipantId, row.DayId, row.Relay, localization, requestRelayPlace, requestRelayTeamEdit);
        Relay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RelayCellsViewModel.HasTeam))
                OnPropertyChanged(nameof(GroupFollowsTeam));
        };

        _initialized = true;

        // Seed the breakdown tooltip (and reconcile the total with live UI state) so the hover text is
        // present on first render.
        RecomputeTotal();
    }

    // The last region/club/ДЮСШ the page accepted, so a cancelled "+ new" can revert the selection.
    private RegionOption _committedRegion;
    private ClubOption _committedClub;
    private DusshOption _committedDussh;

    /// <summary>Region choices for the competition (shared list): "(none)", regions A→Z, "+ new".</summary>
    [ObservableProperty]
    private IReadOnlyList<RegionOption> _regionOptions;

    /// <summary>Club choices for the competition (shared list): "(none)", clubs A→Z, "+ new".</summary>
    [ObservableProperty]
    private IReadOnlyList<ClubOption> _clubOptions;

    /// <summary>ДЮСШ choices for the competition (shared list): "(none)", schools A→Z, "+ new".</summary>
    [ObservableProperty]
    private IReadOnlyList<DusshOption> _dusshOptions;

    /// <summary>
    /// Rank choices (shared list): "(none)", then the application-level ranks. Rank is stored as text,
    /// so picking an option writes its name into <see cref="Rank"/> (saved with the row); there is no
    /// "+ new" option. When the participant's stored rank is not in the list, a one-off "unknown" option
    /// carrying that value is prepended for this row so the dropdown can still show it.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<RankOption> _rankOptions;

    /// <summary>
    /// Swaps in a rebuilt shared options list (e.g. after a "+ new" added a region) while keeping the
    /// current selection by id. Done silently so it doesn't re-fire the region-change callback.
    /// </summary>
    public void ResetRegionOptions(IReadOnlyList<RegionOption> options)
    {
        var keepId = SelectedRegion?.Id;
        var wasInitialized = _initialized;
        _initialized = false;
        RegionOptions = options;
        SelectedRegion = options.FirstOrDefault(o => !o.IsAdd && o.Id == keepId) ?? options[0];
        _committedRegion = SelectedRegion;
        _initialized = wasInitialized;
    }

    /// <summary>Swaps in a rebuilt shared club options list while keeping the selection by id (silent).</summary>
    public void ResetClubOptions(IReadOnlyList<ClubOption> options)
    {
        var keepId = SelectedClub?.Id;
        var wasInitialized = _initialized;
        _initialized = false;
        ClubOptions = options;
        SelectedClub = options.FirstOrDefault(o => !o.IsAdd && o.Id == keepId) ?? options[0];
        _committedClub = SelectedClub;
        _initialized = wasInitialized;
    }

    /// <summary>Swaps in a rebuilt shared ДЮСШ options list while keeping the selection by id (silent).</summary>
    public void ResetDusshOptions(IReadOnlyList<DusshOption> options)
    {
        var keepId = SelectedDussh?.Id;
        var wasInitialized = _initialized;
        _initialized = false;
        DusshOptions = options;
        SelectedDussh = options.FirstOrDefault(o => !o.IsAdd && o.Id == keepId) ?? options[0];
        _committedDussh = SelectedDussh;
        _initialized = wasInitialized;
    }

    // The last chip value the page accepted/persisted, so a rejected reassignment can revert to it.
    private string _committedChip;

    /// <summary>The previously committed chip (to restore after a rejected reassignment).</summary>
    public string CommittedChip => _committedChip;

    /// <summary>Records the chip the page has accepted (after a successful save/reassign).</summary>
    public void MarkChipCommitted(string value) => _committedChip = value;

    public ILocalizationService Localization { get; }

    /// <summary>Per-discount checkboxes, one per discount column in build order (FSOU-member first).</summary>
    public ObservableCollection<DiscountFlagViewModel> DiscountFlags { get; }

    private decimal _totalEntryFee;

    /// <summary>The computed total start-entry fee across all the participant's days. Sortable numeric value.</summary>
    public decimal TotalEntryFee
    {
        get => _totalEntryFee;
        private set
        {
            if (SetProperty(ref _totalEntryFee, value))
            {
                OnPropertyChanged(nameof(FormattedTotalFee));
                // The total is the baseline the payment cell compares against (in whole-competition payment
                // mode) — re-tint when it moves (discount / raised-fee / group / chip edits recompute it).
                OnPropertyChanged(nameof(PaymentBase));
                OnPropertyChanged(nameof(PaymentStatus));
                OnPropertyChanged(nameof(PaymentStatusKey));
            }
        }
    }

    /// <summary>The total fee formatted for display (no currency symbol, trims trailing zeros).</summary>
    public string FormattedTotalFee => TotalEntryFee.ToString("0.##", CultureInfo.InvariantCulture);

    private decimal _dayEntryFee;

    /// <summary>
    /// This day's share of the total entry fee — what the payment is measured against in per-day payment
    /// mode. Recomputed alongside <see cref="TotalEntryFee"/>; equal to it on a one-day competition.
    /// </summary>
    public decimal DayEntryFee
    {
        get => _dayEntryFee;
        private set
        {
            if (SetProperty(ref _dayEntryFee, value))
            {
                OnPropertyChanged(nameof(FormattedDayFee));
                OnPropertyChanged(nameof(PaymentBase));
                OnPropertyChanged(nameof(PaymentStatus));
                OnPropertyChanged(nameof(PaymentStatusKey));
            }
        }
    }

    /// <summary>This day's fee share formatted for display (no currency symbol, trims trailing zeros).</summary>
    public string FormattedDayFee => DayEntryFee.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The fee the «Оплата» cell is compared against: this day's share when the competition charges per day,
    /// else the whole-competition total. Also what the status bar's "still owed" line sums.
    /// </summary>
    public decimal PaymentBase => _paymentPerDay ? DayEntryFee : TotalEntryFee;

    /// <summary>The payment value in force: this day's own in per-day mode, else the competition-level one.</summary>
    public string EffectivePayment => _paymentPerDay ? DayPayment : Payment;

    /// <summary>How «Оплата» compares to the fee it is measured against — drives the cell tint + filter.</summary>
    public PaymentStatus PaymentStatus => PaymentStatusExtensions.Classify(EffectivePayment, PaymentBase);

    /// <summary>The payment status as a stable token, used as the payment column's filter value.</summary>
    public string PaymentStatusKey => PaymentStatus.ToString();

    private string _feeBreakdown = string.Empty;

    /// <summary>
    /// A multi-line, localized explanation of how <see cref="TotalEntryFee"/> was reached (per-day
    /// entry + chip rental, discounts applied). Shown as the total-fee cell's hover tooltip.
    /// </summary>
    public string FeeBreakdown
    {
        get => _feeBreakdown;
        private set => SetProperty(ref _feeBreakdown, value);
    }

    private string _dayFeeBreakdown = string.Empty;

    /// <summary>
    /// Like <see cref="FeeBreakdown"/> but for this day's share only (<see cref="DayEntryFee"/>) — the
    /// tooltip of the day grid's fee column, which shows the current day's sum.
    /// </summary>
    public string DayFeeBreakdown
    {
        get => _dayFeeBreakdown;
        private set => SetProperty(ref _dayFeeBreakdown, value);
    }

    // Recomputes the total from this day's live group/chip plus the other days' fixed contributions,
    // using the shared fee context — no DB round-trip. A participant shown in the day grid is always a
    // member of the current day, so this day always contributes. Also refreshes the breakdown tooltip.
    private void RecomputeTotal()
    {
        var selected = DiscountFlags
            .Where(f => !f.IsFsouMemberDiscount && f.IsSelected)
            .Select(f => f.DiscountId)
            .ToList();
        // In per-day payment mode this row's checkbox is THIS day's flag; the other days keep the flags
        // they were loaded with. Outside that mode the context ignores the per-day values entirely.
        var memberDays = new List<EntryFeeDay>(_otherDays.Count + 1)
        {
            new(_dayId, SelectedGroup.Id, Chip ?? string.Empty, PaysRaisedFee)
        };
        foreach (var d in _otherDays)
            memberDays.Add(new EntryFeeDay(d.DayId, d.GroupId, d.Chip, d.PaysRaisedFee));
        var breakdown = _fees.Describe(PaysRaisedFee, IsFsouMember, selected, memberDays);
        TotalEntryFee = breakdown.Total;
        DayEntryFee = breakdown.DayTotal(_dayId);
        FeeBreakdown = EntryFeeBreakdownFormatter.Format(breakdown, Localization);
        DayFeeBreakdown = EntryFeeBreakdownFormatter.FormatDay(breakdown, _dayId, Localization);
    }

    /// <summary>
    /// Swaps in a rebuilt fee snapshot (e.g. after a rental chip was toggled, which changes which chips
    /// are charged rental) and recomputes the total from it. The new context replaces the one captured
    /// at construction; no DB round-trip.
    /// </summary>
    public void RefreshFees(EntryFeeContext fees)
    {
        _fees = fees;
        RecomputeTotal();
    }

    partial void OnPaysRaisedFeeChanged(bool value)
    {
        if (!_initialized)
            return;
        _requestRaisedFeeChange(this);
        RecomputeTotal();
    }

    // A manual discount checkbox toggled (the FSOU-member flag is read-only, so it never fires here).
    private void OnDiscountFlagChanged(DiscountFlagViewModel flag)
    {
        if (!_initialized)
            return;
        _requestDiscountChange(this, flag.DiscountId, flag.IsSelected);
        RecomputeTotal();
    }

    /// <summary>Key used by the page for debounce timers, delete and as the row identity.</summary>
    public Guid Id => _linkId;

    /// <summary>Parent participant id; needed for the cascade-delete check on removal.</summary>
    public Guid ParticipantId => _participantId;

    /// <summary>The participant's additional fields (Р-13), bound by the <c>field.&lt;key&gt;</c> columns as
    /// <c>Fields[key].Text</c> etc. Set by the page right after the row is built.</summary>
    public ParticipantFieldsViewModel Fields { get; set; } = ParticipantFieldsViewModel.Empty;

    /// <summary>Group choices for this day (id + name). The day grid offers only real groups — no
    /// "не участвує" sentinel — since a participant shown here is always a day member (leave the day
    /// via the row's delete button instead).</summary>
    [ObservableProperty]
    private IReadOnlyList<GroupOption> _groupOptions;

    /// <summary>True while <see cref="Apply"/> copies fresh values onto the row; the page skips its callbacks then.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>
    /// Copies a freshly-read row onto this one without triggering any save or dialog. Only the cells whose value
    /// changed raise a property change; the shared option lists are swapped only when the page rebuilt them, so an
    /// open combo keeps its items and its selected instance. The additional-field cells are applied by the page.
    /// </summary>
    public void Apply(
        ParticipantDayRow row,
        IReadOnlyList<GroupOption> groupOptions,
        IReadOnlyList<RegionOption> regionOptions,
        IReadOnlyList<ClubOption> clubOptions,
        IReadOnlyList<DusshOption> dusshOptions,
        IReadOnlyList<RankOption> rankOptions,
        IReadOnlyList<EntryFeeDiscount> discounts,
        EntryFeeContext fees,
        IReadOnlyList<ResultColumnDef> resultColumns)
    {
        IsApplying = true;
        _initialized = false;
        try
        {
            _order = row.Order;
            _otherDays = row.OtherDays;
            _fees = fees;
            if (_dayDefaultDiscipline != row.DayDefaultDiscipline)
            {
                _dayDefaultDiscipline = row.DayDefaultDiscipline;
                OnPropertyChanged(nameof(UsesTeam));
                OnPropertyChanged(nameof(UsesScore));
            }
            if (_paymentPerDay != row.PaymentPerDay)
            {
                _paymentPerDay = row.PaymentPerDay;
                OnPropertyChanged(nameof(PaymentBase));
                OnPropertyChanged(nameof(EffectivePayment));
                OnPropertyChanged(nameof(PaymentStatus));
                OnPropertyChanged(nameof(PaymentStatusKey));
            }

            if (!ReferenceEquals(GroupOptions, groupOptions))
                GroupOptions = groupOptions;
            SelectedGroup = ResolveGroup(groupOptions, row.GroupId);

            if (!ReferenceEquals(RegionOptions, regionOptions))
                RegionOptions = regionOptions;
            SelectedRegion = regionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.RegionId) ?? regionOptions[0];
            _committedRegion = SelectedRegion;

            if (!ReferenceEquals(ClubOptions, clubOptions))
                ClubOptions = clubOptions;
            SelectedClub = clubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.ClubId) ?? clubOptions[0];
            _committedClub = SelectedClub;

            if (!ReferenceEquals(DusshOptions, dusshOptions))
                DusshOptions = dusshOptions;
            SelectedDussh = dusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.DusshId) ?? dusshOptions[0];
            _committedDussh = SelectedDussh;

            // The rank list is re-resolved only when the page rebuilt it or the stored value moved (a stray value
            // gets its one-off "unknown" option), so the combo keeps its items otherwise.
            var rankChanged = !string.Equals(Rank, row.Rank, StringComparison.Ordinal);
            Rank = row.Rank;
            if (rankChanged || !ReferenceEquals(_sharedRankOptions, rankOptions))
            {
                _sharedRankOptions = rankOptions;
                var (rankOpts, rankSel) = RankOptionResolver.Resolve(rankOptions, row.Rank, Localization);
                if (!ReferenceEquals(RankOptions, rankOpts))
                    RankOptions = rankOpts;
                SelectedRank = rankSel;
            }

            FullName = row.FullName;
            Number = row.Number;
            Coach = row.Coach;
            BirthDate = row.BirthDate;
            Representative = row.Representative;
            FsouCode = row.FsouCode;
            Note = row.Note;
            IsFsouMember = row.IsFsouMember;
            Payment = row.Payment;
            DayPayment = row.DayPayment;
            Chip = row.Chip;
            _committedChip = row.Chip;
            Team = row.Team;
            StartTime = row.StartTime;
            OutOfCompetition = row.OutOfCompetition;
            PaysRaisedFee = row.PaysRaisedFee;

            if (_bonus != row.Bonus)
            {
                _bonus = row.Bonus;
                OnPropertyChanged(nameof(BonusText));
                OnPropertyChanged(nameof(BonusSort));
            }

            ApplyDiscountFlags(discounts, row);

            if (!ReferenceEquals(_resultColumns, resultColumns))
                SetResultColumns(resultColumns);
            ApplyResult(row.Result);
            Relay.Apply(row.Relay);

            RecomputeTotal();
        }
        finally
        {
            _initialized = true;
            IsApplying = false;
        }
    }

    // One flag per discount column, in build order. The collection is rebuilt only when the discount set moved
    // (the page then rebuilds the columns too); otherwise each flag is set silently.
    private void ApplyDiscountFlags(IReadOnlyList<EntryFeeDiscount> discounts, ParticipantDayRow row)
    {
        var selected = row.SelectedDiscountIds.ToHashSet();
        var same = DiscountFlags.Count == discounts.Count;
        for (var i = 0; same && i < discounts.Count; i++)
            same = DiscountFlags[i].DiscountId == discounts[i].Id
                && DiscountFlags[i].IsFsouMemberDiscount == discounts[i].IsFsouMemberDiscount;

        if (!same)
            DiscountFlags.Clear();
        for (var i = 0; i < discounts.Count; i++)
        {
            var d = discounts[i];
            var on = d.IsFsouMemberDiscount ? row.IsFsouMember : selected.Contains(d.Id);
            if (same)
                DiscountFlags[i].SetSilently(on);
            else
                DiscountFlags.Add(new DiscountFlagViewModel(d.Id, d.IsFsouMemberDiscount, on, OnDiscountFlagChanged));
        }
    }

    public bool UsesTeam => _strategies.For(_dayDefaultDiscipline).UsesParticipantColumn(ParticipantColumn.Team);

    /// <summary>True when this day's discipline scores points (rogaine) — drives the «Бали» column.</summary>
    public bool UsesScore => _strategies.For(_dayDefaultDiscipline).UsesControlPointPoints;

    /// <summary>The finish-status choices: a descriptive "(<computed> — автоматично)" sentinel, then the
    /// settable statuses. Rebuilt when the result changes so the sentinel tracks the computed status.</summary>
    [ObservableProperty]
    private IReadOnlyList<FinishStatusOption> _statusOptions;

    /// <summary>The effective status code shown on the resting status cell (OK/MP/…); blank when no result.</summary>
    public string ResultStatusText => ResultText.Status(_result);

    /// <summary>True when the status is a problem code (anything but OK / blank) — the cell shows it in red.</summary>
    public bool StatusIsProblem => _result.StatusIsProblem;

    /// <summary>Always true on the day grid: a participant shown here is a day member, so a judge can set
    /// the status — overriding the computed one when there's a read-out, or marking DNS/DNF/… when there
    /// isn't (picking OK then leaves it blank).</summary>
    public bool CanEditStatus => true;

    // ── Read-only computed result columns
    /// <summary>Actual start (from the chip read-out only) as "HH:mm:ss"; blank when none.</summary>
    public string ActualStartText => ResultText.ActualStart(_result);
    /// <summary>Finish time as "HH:mm:ss"; blank when none.</summary>
    public string FinishText => ResultText.Finish(_result);
    /// <summary>Result time (finish − start) as "H:mm:ss"; blank unless a valid OK result.</summary>
    public string ResultText_ => ResultText.Result(_result);
    /// <summary>Place within the group on the day; blank when unplaced.</summary>
    public string PlaceText => ResultText.Place(_result);
    /// <summary>Score / «Бали» (rogaine); blank for non-scoring disciplines.</summary>
    public string ScoreText => ResultText.Score(_result);
    /// <summary>Per-control «Бали» breakdown for the score column's hover tooltip; null when no score.</summary>
    public string? ScoreTooltip => ResultText.ScoreTooltip(_result, Localization);
    /// <summary>Ranking points / «Очки» (from the group's points rule); blank when none awarded.</summary>
    public string PointsText => ResultText.Points(_result);
    /// <summary>Awarded sports rank / «Виконаний розряд» (Додаток 89); blank when none.</summary>
    public string AwardedRankText => ResultText.AwardedRank(_result);
    /// <summary>Raw place for sorting (max when unplaced, so OK results sort to the top).</summary>
    /// <summary>A plugin discipline's result values as display text, by column key (empty when none).</summary>
    public ResultExtrasView ResultExtras => new(_result, _resultColumns);

    /// <summary>Whether a plugin flags a result value, by column key (the cell is tinted red, e.g. «K не визначено»).</summary>
    public ResultProblemsView ResultProblems => new(_result, _resultColumns);

    // The plugin result columns that format ResultExtras (set by the page for the shown day).
    private IReadOnlyList<ResultColumnDef> _resultColumns = [];

    /// <summary>Sets the plugin result columns used to format <see cref="ResultExtras"/>.</summary>
    public void SetResultColumns(IReadOnlyList<ResultColumnDef> columns)
    {
        _resultColumns = columns;
        OnPropertyChanged(nameof(ResultExtras));
        OnPropertyChanged(nameof(ResultProblems));
    }

    public int PlaceSort => _result.Place ?? int.MaxValue;

    // ── Relay (Р-21…Р-23): the runner's team, leg, leg number «100.2» and team name, as stored — editable here.
    /// <summary>The relay cells of this runner on the day (blank outside a relay).</summary>
    public RelayCellsViewModel Relay { get; }

    /// <summary>True for a relay runner: their group on the day is the team's (Р-22) and follows it — picking another
    /// one is asked by the page (see <see cref="ComboGroup"/>), a paste or bulk edit leaves it.</summary>
    public bool GroupFollowsTeam => Relay.HasTeam;
    public int ScoreSort => _result.Score ?? -1;
    /// <summary>Raw points for sorting (min when none, so awarded points sort together).</summary>
    public decimal PointsSort => _result.Points ?? decimal.MinValue;

    /// <summary>
    /// The judge's points correction («бонус») as editable signed-integer text; empty clears it. A valid
    /// integer persists through the page's bonus callback (which recomputes «Бали» live); an unparseable
    /// entry reverts on the next notification. Shown only on point-scoring days (<see cref="UsesScore"/>).
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
            if (_initialized)
                _requestBonusChange(this);
        }
    }

    /// <summary>The parsed bonus the user entered (null = none). Read by the page's bonus callback.</summary>
    public int? Bonus => _bonus;
    /// <summary>Raw bonus for sorting (min when unset, so entered corrections sort together).</summary>
    public int BonusSort => _bonus ?? int.MinValue;

    public ParticipantDayRow ToRow() => new(
        LinkId: _linkId,
        DayId: _dayId,
        ParticipantId: _participantId,
        Order: _order,
        FullName: (FullName ?? string.Empty).Trim(),
        Number: (Number ?? string.Empty).Trim(),
        Rank: (Rank ?? string.Empty).Trim(),
        Coach: (Coach ?? string.Empty).Trim(),
        BirthDate: BirthDate,
        RegionId: SelectedRegion.Id,
        RegionName: SelectedRegion.Label,
        ClubId: SelectedClub.Id,
        ClubName: SelectedClub.Label,
        DusshId: SelectedDussh.Id,
        DusshName: SelectedDussh.Label,
        Representative: (Representative ?? string.Empty).Trim(),
        FsouCode: (FsouCode ?? string.Empty).Trim(),
        IsFsouMember: IsFsouMember,
        Payment: (Payment ?? string.Empty).Trim(),
        DayPayment: (DayPayment ?? string.Empty).Trim(),
        PaymentPerDay: _paymentPerDay,
        Note: (Note ?? string.Empty).Trim(),
        // Fee fields are persisted through their own callbacks, not the row save; carry them so the
        // record round-trips unchanged (the editor's row-save ignores them).
        PaysRaisedFee: PaysRaisedFee,
        SelectedDiscountIds: DiscountFlags.Where(f => !f.IsFsouMemberDiscount && f.IsSelected).Select(f => f.DiscountId).ToList(),
        TotalEntryFee: TotalEntryFee,
        OtherDays: _otherDays,
        // No group picked: keep what is stored (null, or a group no longer on the day) — never the list's first entry.
        GroupId: HasNoGroup ? _unlistedGroupId : SelectedGroup.Id,
        GroupName: SelectedGroup.Label,
        Chip: (Chip ?? string.Empty).Trim(),
        Team: (Team ?? string.Empty).Trim(),
        StartTime: StartTime,
        OutOfCompetition: OutOfCompetition,
        Bonus: _bonus,
        DayDefaultDiscipline: _dayDefaultDiscipline,
        // Result is computed, not part of the row save; carry it so the record round-trips unchanged.
        Result: _result);

    /// <summary>
    /// The start time as editable "hh:mm:ss" text (mirrors <see cref="RosterDayCellViewModel.StartTimeText"/>).
    /// Empty clears it; a partial entry is padded and out-of-range minute/second is clamped to 59 (see
    /// <see cref="StartTimeFormat"/>); a truly unparseable value reverts on the next notification.
    /// </summary>
    public string StartTimeText
    {
        get => StartTimeFormat.Format(StartTime);
        set
        {
            if (StartTimeFormat.TryParse(value, out var parsed))
                StartTime = parsed;
            else
                OnPropertyChanged();
        }
    }

    partial void OnStartTimeChanged(TimeSpan? value)
    {
        OnPropertyChanged(nameof(StartTimeText));
        QueueSave();
    }
    partial void OnOutOfCompetitionChanged(bool value) => QueueSave();

    partial void OnFullNameChanged(string value) => QueueSave();
    partial void OnNumberChanged(string value) => QueueSave();
    partial void OnRankChanged(string value) => QueueSave();
    partial void OnCoachChanged(string value) => QueueSave();
    partial void OnBirthDateChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(BirthDateViolatesAge));
        OnPropertyChanged(nameof(AgeViolationTooltip));
        QueueSave();
    }
    partial void OnSelectedGroupChanged(GroupOption value)
    {
        // The age window comes from the selected group, so a group change can flip the highlight.
        OnPropertyChanged(nameof(BirthDateViolatesAge));
        OnPropertyChanged(nameof(AgeViolationTooltip));
        // So does the course length the chip must hold.
        OnPropertyChanged(nameof(ChipTooSmall));
        OnPropertyChanged(nameof(ChipCapacityTooltip));

        if (!_initialized)
            return;
        // A relay runner's group is the team's (Р-22): it moves only through the page's question (ComboGroup).
        if (GroupFollowsTeam)
            return;

        // Selecting "не участвує" (the null sentinel) removes the participant from this day; any other
        // choice is a normal save. The page handles the removal (drops the row, deletes the link).
        if (value.Id is null)
            _requestLeaveDay(this);
        else
        {
            QueueSave();
            RecomputeTotal();
        }
    }

    // The "(none)" option of a row with no group, kept outside the day's list (see ResolveGroup).
    private GroupOption? _noGroupOption;

    // The stored group of a row whose group is not on the day's list, written back unchanged on a row save.
    private Guid? _unlistedGroupId;

    // The day grid lists only the day's groups (no "(none)" option), so a member with no group on the day — none set,
    // or theirs no longer on the day — resolves to a "(none)" option of its own outside the list: the cell reads
    // «без групи» and the combo shows no choice. Falling back to the list's first entry instead showed (and, on the
    // next save of any other field, wrote) the day's first group. The stored id is kept for the row save.
    private GroupOption ResolveGroup(IReadOnlyList<GroupOption> options, Guid? groupId)
    {
        _unlistedGroupId = null;
        if (options.FirstOrDefault(o => o.Id == groupId) is { } listed)
            return listed;
        _unlistedGroupId = groupId;
        return options.FirstOrDefault(o => o.Id is null)
               ?? (_noGroupOption ??= new GroupOption(null, string.Empty, Localization));
    }

    /// <summary>
    /// A row of the day with no group on it (none was set, or theirs is no longer on the day's list). Every row here
    /// runs the day, so the cell shows amber «без групи», not «не бере участі».
    /// </summary>
    public bool HasNoGroup => SelectedGroup?.Id is null;

    /// <summary>The group cell's resting text — the group or «без групи»; also the column's sort and filter value.</summary>
    public string GroupLabel => HasNoGroup
        ? Localization.Get("Participants.Group.Missing")
        : SelectedGroup.Label;

    /// <summary>The amber group cell's tooltip; null otherwise, which hides it.</summary>
    public string? NoGroupTooltip => HasNoGroup ? Localization.Get("Participants.Group.Missing.Tooltip") : null;

    /// <summary>
    /// What the group combo selects: <see cref="SelectedGroup"/>, except nothing while <see cref="HasNoGroup"/> — the
    /// "(none)" option would read as the current value, and picking it would change nothing. With no selection,
    /// picking «не бере участі» takes the participant off the day. A relay runner's pick is handed to the page to ask
    /// (their group is the team's, Р-22); the selection moves only once that is answered.
    /// </summary>
    public GroupOption? ComboGroup
    {
        get => HasNoGroup ? null : SelectedGroup;
        set
        {
            if (value is null)
                return; // the combo dropping its own selection (a filtered list) is not a choice
            if (GroupFollowsTeam)
            {
                if (!ReferenceEquals(value, SelectedGroup) && _initialized)
                    _requestRelayGroupChange(this, value);
                return;
            }
            if (!ReferenceEquals(value, SelectedGroup))
                SelectedGroup = value;
            else if (HasNoGroup && _initialized)
                _requestLeaveDay(this); // «не бере участі» on a row with no group: SelectedGroup does not move
        }
    }

    /// <summary>
    /// The group set by a paste or a bulk edit — never asked, so a relay runner keeps the team's group (Р-22); the
    /// change is made one row at a time through the combo, which asks.
    /// </summary>
    public GroupOption? BulkGroup
    {
        get => SelectedGroup;
        set
        {
            if (value is not null && !GroupFollowsTeam)
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
        _unlistedGroupId = null;
        _initialized = wasInitialized;
    }

    /// <summary>
    /// True when this participant's birth year falls outside their day-group's allowed age window
    /// (<c>SelectedGroup.MinBirthYear</c>/<c>MaxBirthYear</c>, both inclusive). Drives the red tint on the
    /// birth-date cell. False when the date or group is unset, or the group has no bounds.
    /// </summary>
    public bool BirthDateViolatesAge =>
        Group.ViolatesAgeWindow(BirthDate?.Year, SelectedGroup?.MinBirthYear, SelectedGroup?.MaxBirthYear);

    /// <summary>
    /// Localized explanation of the age-window breach (names the group + the breached bound), shown as the
    /// birth-date cell's tooltip; null when there is no violation, which suppresses the tooltip.
    /// </summary>
    public string? AgeViolationTooltip
    {
        get
        {
            var reason = SelectedGroup?.AgeViolationReason(BirthDate?.Year);
            return string.IsNullOrEmpty(reason) ? null : reason;
        }
    }
    /// <summary>
    /// True when the chip is a known SportIdent card model that holds fewer punches than this day's group
    /// course has controls (<see cref="GroupOption.ControlCount"/>). Drives the amber tint on the chip cell.
    /// False for a blank/unknown chip or a group without a course.
    /// </summary>
    public bool ChipTooSmall => SiCard.TooSmall(Chip, SelectedGroup?.ControlCount ?? 0) is not null;

    /// <summary>Why the chip is too small (model, capacity, group, control count); null when it fits, which hides the tooltip.</summary>
    public string? ChipCapacityTooltip
    {
        get
        {
            var reason = SelectedGroup?.ChipCapacityReason(Chip);
            return string.IsNullOrEmpty(reason) ? null : reason;
        }
    }

    // The chip is NOT part of the debounced row save: a chip edit may collide with another competitor
    // on the day and must be resolved (confirm + reassign, or revert) before it is persisted, so the
    // page owns it via a dedicated callback.
    partial void OnChipChanged(string value)
    {
        // The capacity check reads the chip, so re-evaluate on every change — a silent (reverted /
        // scanned-in) one included, since the cell shows the new number either way.
        OnPropertyChanged(nameof(ChipTooSmall));
        OnPropertyChanged(nameof(ChipCapacityTooltip));
        if (_initialized)
        {
            _requestChipChange(this);
            RecomputeTotal();
        }
    }
    partial void OnTeamChanged(string value) => QueueSave();

    // Region is competition-level and NOT part of the debounced row save: picking "+ new" has a modal
    // side effect, so the page owns it. Picking a real region / "(none)" persists via its own callback.
    partial void OnSelectedRegionChanged(RegionOption value)
    {
        if (!_initialized || value is null)
            return;

        if (value.IsAdd)
            _requestAddRegion(this);
        else
        {
            _committedRegion = value;
            _requestRegionChange(this);
        }
    }

    /// <summary>Restores the chip to a value without re-triggering the chip-change callback (used to revert a rejected reassignment).</summary>
    public void SetChipSilently(string value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        Chip = value;
        _initialized = wasInitialized;
    }

    /// <summary>The previously committed region (to restore after a cancelled "+ new").</summary>
    public RegionOption CommittedRegion => _committedRegion;

    /// <summary>Sets the region without re-triggering the change callback (revert after a cancelled "+ new").</summary>
    public void SetRegionSilently(RegionOption value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        SelectedRegion = value;
        _committedRegion = value;
        _initialized = wasInitialized;
    }

    // Club mirrors Region: NOT part of the debounced save (the "+ new" sentinel opens a modal).
    partial void OnSelectedClubChanged(ClubOption value)
    {
        if (!_initialized || value is null)
            return;

        if (value.IsAdd)
            _requestAddClub(this);
        else
        {
            _committedClub = value;
            _requestClubChange(this);
        }
    }

    /// <summary>The previously committed club (to restore after a cancelled "+ new").</summary>
    public ClubOption CommittedClub => _committedClub;

    /// <summary>Sets the club without re-triggering the change callback (revert after a cancelled "+ new").</summary>
    public void SetClubSilently(ClubOption value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        SelectedClub = value;
        _committedClub = value;
        _initialized = wasInitialized;
    }

    // ДЮСШ mirrors Region/Club: NOT part of the debounced save (the "+ new" sentinel opens a modal).
    partial void OnSelectedDusshChanged(DusshOption value)
    {
        if (!_initialized || value is null)
            return;

        if (value.IsAdd)
            _requestAddDussh(this);
        else
        {
            _committedDussh = value;
            _requestDusshChange(this);
        }
    }

    /// <summary>The previously committed ДЮСШ (to restore after a cancelled "+ new").</summary>
    public DusshOption CommittedDussh => _committedDussh;

    /// <summary>Sets the ДЮСШ without re-triggering the change callback (revert after a cancelled "+ new").</summary>
    public void SetDusshSilently(DusshOption value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        SelectedDussh = value;
        _committedDussh = value;
        _initialized = wasInitialized;
    }

    // Rank is stored as text: picking an option writes its name into Rank, which persists through the
    // debounced row save (OnRankChanged). "(none)" writes blank. No modal side effect, so no special
    // routing — unlike region/club.
    partial void OnSelectedRankChanged(RankOption value)
    {
        if (!_initialized || value is null)
            return;
        Rank = value.Value;
    }

    /// <summary>
    /// Swaps in a rebuilt rank list (e.g. after the Ranks page changed) while keeping the current
    /// selection by value. Done silently so it doesn't re-fire the rank change / a save.
    /// </summary>
    public void ResetRankOptions(IReadOnlyList<RankOption> options)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        var (rankOpts, rankSel) = RankOptionResolver.Resolve(options, Rank, Localization);
        RankOptions = rankOpts;
        SelectedRank = rankSel;
        _initialized = wasInitialized;
    }

    // The competition-level text/bool fields persist through the debounced row save like the identity.
    partial void OnRepresentativeChanged(string value) => QueueSave();
    partial void OnFsouCodeChanged(string value) => QueueSave();
    partial void OnNoteChanged(string value) => QueueSave();
    partial void OnIsFsouMemberChanged(bool value)
    {
        QueueSave();
        // The FSOU-member discount auto-applies, so mirror the flag onto its (read-only) checkbox and
        // recompute. The flag's own callback is suppressed (it is never persisted per-row).
        foreach (var flag in DiscountFlags)
            if (flag.IsFsouMemberDiscount)
                flag.SetSilently(value);
        if (_initialized)
            RecomputeTotal();
    }
    partial void OnPaymentChanged(string value)
    {
        // Re-tint the payment cell (and re-evaluate the status filter) as the user types.
        OnPropertyChanged(nameof(EffectivePayment));
        OnPropertyChanged(nameof(PaymentStatus));
        OnPropertyChanged(nameof(PaymentStatusKey));
        QueueSave();
    }

    partial void OnDayPaymentChanged(string value)
    {
        OnPropertyChanged(nameof(EffectivePayment));
        OnPropertyChanged(nameof(PaymentStatus));
        OnPropertyChanged(nameof(PaymentStatusKey));
        // Its own writer (the row save leaves the per-day payment column alone), like the bonus.
        if (_initialized)
            _requestDayPaymentChange(this);
    }

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    /// <summary>The status override the user picked (null = "auto", clears it). Read by the page callback.</summary>
    public FinishStatus? ResultStatusOverride => SelectedStatus?.Status;

    // The status dropdown is NOT part of the debounced row save (which would wipe the override); the page
    // owns it via a dedicated callback that persists the override and re-ranks the affected group.
    partial void OnSelectedStatusChanged(FinishStatusOption value)
    {
        if (_initialized && value is not null)
            _requestResultStatusChange(this);
    }

    /// <summary>
    /// Applies a freshly recomputed result (e.g. after a status edit re-ranked the group) without
    /// re-triggering the status callback. Refreshes the read-only result texts and the status selection.
    /// </summary>
    public void ApplyResult(ParticipantDayResult result)
    {
        if (ReferenceEquals(_result, result))
            return;

        var before = ResultSnapshot();
        var computedChanged = _result.Computed != result.Computed;
        _result = result;
        var wasInitialized = _initialized;
        _initialized = false;
        // Rebuild the options so the "auto" sentinel reflects the new computed status (only when it moved, so an
        // open dropdown keeps its items), then select by the raw override (auto when none).
        if (computedChanged)
            StatusOptions = FinishStatusOptions.Build(Localization, result.Computed);
        SelectedStatus = FinishStatusOptions.Select(StatusOptions, result.Override);
        _initialized = wasInitialized;

        // Raise only the display properties whose value moved, so a live refresh repaints exactly the changed cells.
        foreach (var (name, value) in ResultSnapshot())
            if (!Equals(before[name], value))
                OnPropertyChanged(name);
        // The plugin views wrap the result object, so they always follow a new result.
        OnPropertyChanged(nameof(ResultExtras));
        OnPropertyChanged(nameof(ResultProblems));
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
        [nameof(PlaceSort)] = PlaceSort,
        [nameof(ScoreSort)] = ScoreSort,
        [nameof(PointsSort)] = PointsSort,
    };
}
