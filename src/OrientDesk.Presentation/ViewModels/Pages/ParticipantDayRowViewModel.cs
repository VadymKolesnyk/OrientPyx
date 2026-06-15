using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Disciplines;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.BusinessLogic.Services;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

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
    private readonly Guid _participantId;
    private readonly int _order;
    private readonly DisciplineType _dayDefaultDiscipline;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly Action<ParticipantDayRowViewModel> _requestSave;
    private readonly Action<ParticipantDayRowViewModel> _requestLeaveDay;
    private readonly Action<ParticipantDayRowViewModel> _requestChipChange;
    private readonly Action<ParticipantDayRowViewModel> _requestRegionChange;
    private readonly Action<ParticipantDayRowViewModel> _requestAddRegion;
    private readonly Action<ParticipantDayRowViewModel> _requestClubChange;
    private readonly Action<ParticipantDayRowViewModel> _requestAddClub;
    private readonly Action<ParticipantDayRowViewModel> _requestDusshChange;
    private readonly Action<ParticipantDayRowViewModel> _requestAddDussh;
    private readonly Action<ParticipantDayRowViewModel> _requestRaisedFeeChange;
    private readonly Action<ParticipantDayRowViewModel, Guid, bool> _requestDiscountChange;
    private EntryFeeContext _fees;
    private readonly IReadOnlyList<ParticipantFeeDay> _otherDays;

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

    [ObservableProperty]
    private string _chip;

    [ObservableProperty]
    private string _team;

    [ObservableProperty]
    private TimeSpan? _startTime;

    [ObservableProperty]
    private bool _outOfCompetition;

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
        Action<ParticipantDayRowViewModel, Guid, bool> requestDiscountChange)
    {
        _linkId = row.LinkId;
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
        _fees = fees;
        _otherDays = row.OtherDays;
        Localization = localization;

        GroupOptions = groupOptions;
        RegionOptions = regionOptions;
        ClubOptions = clubOptions;
        DusshOptions = dusshOptions;

        _fullName = row.FullName;
        _number = row.Number;
        _rank = row.Rank;
        // Rank stores text; resolve the dropdown selection by matching the stored name (case-insensitive).
        // A stored value not in the list gets a one-off "unknown" option prepended so it still shows.
        var (rankOpts, rankSel) = RankOptionResolver.Resolve(rankOptions, row.Rank, localization);
        _rankOptions = rankOpts;
        _selectedRank = rankSel;
        _coach = row.Coach;
        _birthDate = row.BirthDate;
        _representative = row.Representative;
        _fsouCode = row.FsouCode;
        _isFsouMember = row.IsFsouMember;
        _payment = row.Payment;
        _chip = row.Chip;
        _committedChip = row.Chip;
        _team = row.Team;
        _startTime = row.StartTime;
        _outOfCompetition = row.OutOfCompetition;
        // Match by id; fall back to the "(none)" option (the first) when the group is unset/missing.
        _selectedGroup = groupOptions.FirstOrDefault(o => o.Id == row.GroupId) ?? groupOptions[0];
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
                OnPropertyChanged(nameof(FormattedTotalFee));
        }
    }

    /// <summary>The total fee formatted for display (no currency symbol, trims trailing zeros).</summary>
    public string FormattedTotalFee => TotalEntryFee.ToString("0.##", CultureInfo.InvariantCulture);

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

    // Recomputes the total from this day's live group/chip plus the other days' fixed contributions,
    // using the shared fee context — no DB round-trip. A participant shown in the day grid is always a
    // member of the current day, so this day always contributes. Also refreshes the breakdown tooltip.
    private void RecomputeTotal()
    {
        var selected = DiscountFlags
            .Where(f => !f.IsFsouMemberDiscount && f.IsSelected)
            .Select(f => f.DiscountId)
            .ToList();
        var memberDays = new List<(Guid?, string)>(_otherDays.Count + 1)
        {
            (SelectedGroup.Id, Chip ?? string.Empty)
        };
        foreach (var d in _otherDays)
            memberDays.Add((d.GroupId, d.Chip));
        var breakdown = _fees.Describe(PaysRaisedFee, IsFsouMember, selected, memberDays);
        TotalEntryFee = breakdown.Total;
        FeeBreakdown = EntryFeeBreakdownFormatter.Format(breakdown, Localization);
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

    /// <summary>Group choices for this day (id + name). The day grid offers only real groups — no
    /// "не участвує" sentinel — since a participant shown here is always a day member (leave the day
    /// via the row's delete button instead).</summary>
    public IReadOnlyList<GroupOption> GroupOptions { get; }

    /// <summary>True when this day's discipline uses the team column (rogaine).</summary>
    public bool UsesTeam => _strategies.For(_dayDefaultDiscipline).UsesParticipantColumn(ParticipantColumn.Team);

    public ParticipantDayRow ToRow() => new(
        LinkId: _linkId,
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
        // Fee fields are persisted through their own callbacks, not the row save; carry them so the
        // record round-trips unchanged (the editor's row-save ignores them).
        PaysRaisedFee: PaysRaisedFee,
        SelectedDiscountIds: DiscountFlags.Where(f => !f.IsFsouMemberDiscount && f.IsSelected).Select(f => f.DiscountId).ToList(),
        TotalEntryFee: TotalEntryFee,
        OtherDays: _otherDays,
        GroupId: SelectedGroup.Id,
        GroupName: SelectedGroup.Label,
        Chip: (Chip ?? string.Empty).Trim(),
        Team: (Team ?? string.Empty).Trim(),
        StartTime: StartTime,
        OutOfCompetition: OutOfCompetition,
        DayDefaultDiscipline: _dayDefaultDiscipline);

    /// <summary>
    /// The start time as editable "HH:mm" text (mirrors <see cref="RosterDayCellViewModel.StartTimeText"/>).
    /// Empty clears it; an unparseable value is ignored and the box reverts on the next notification.
    /// </summary>
    public string StartTimeText
    {
        get => StartTime is { } t ? t.ToString(@"hh\:mm") : string.Empty;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                StartTime = null;
            else if (TimeSpan.TryParseExact(trimmed, [@"hh\:mm", @"h\:mm"], System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                     || TimeSpan.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out parsed))
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
    partial void OnBirthDateChanged(DateTimeOffset? value) => QueueSave();
    partial void OnSelectedGroupChanged(GroupOption value)
    {
        if (!_initialized)
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
    // The chip is NOT part of the debounced row save: a chip edit may collide with another competitor
    // on the day and must be resolved (confirm + reassign, or revert) before it is persisted, so the
    // page owns it via a dedicated callback.
    partial void OnChipChanged(string value)
    {
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
    partial void OnPaymentChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }
}
