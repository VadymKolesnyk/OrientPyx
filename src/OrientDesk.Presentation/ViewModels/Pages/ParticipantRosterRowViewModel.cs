using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One row in the roster ("Мандатка") grid: a single competition participant with a
/// <see cref="RosterDayCellViewModel"/> per day. Identity fields are competition-level and editable
/// here (affecting every day); the per-day cells carry membership and group. Identity edits invoke
/// the page-supplied <c>requestSave</c> callback (debounced); membership/group edits are handled by
/// the cells via their own callback.
/// </summary>
public sealed partial class ParticipantRosterRowViewModel : ObservableObject
{
    private readonly Guid _participantId;
    private readonly Action<ParticipantRosterRowViewModel> _requestSave;
    private readonly Action<ParticipantRosterRowViewModel> _requestRegionChange;
    private readonly Action<ParticipantRosterRowViewModel> _requestAddRegion;
    private readonly Action<ParticipantRosterRowViewModel> _requestClubChange;
    private readonly Action<ParticipantRosterRowViewModel> _requestAddClub;
    private readonly Action<ParticipantRosterRowViewModel> _requestDusshChange;
    private readonly Action<ParticipantRosterRowViewModel> _requestAddDussh;
    private bool _initialized;

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

    public ParticipantRosterRowViewModel(
        ParticipantRosterRow row,
        IReadOnlyList<RosterDayCellViewModel> dayCells,
        IReadOnlyList<RegionOption> regionOptions,
        IReadOnlyList<ClubOption> clubOptions,
        IReadOnlyList<DusshOption> dusshOptions,
        IReadOnlyList<RankOption> rankOptions,
        ILocalizationService localization,
        Action<ParticipantRosterRowViewModel> requestSave,
        Action<ParticipantRosterRowViewModel> requestRegionChange,
        Action<ParticipantRosterRowViewModel> requestAddRegion,
        Action<ParticipantRosterRowViewModel> requestClubChange,
        Action<ParticipantRosterRowViewModel> requestAddClub,
        Action<ParticipantRosterRowViewModel> requestDusshChange,
        Action<ParticipantRosterRowViewModel> requestAddDussh)
    {
        _participantId = row.ParticipantId;
        _requestSave = requestSave;
        _requestRegionChange = requestRegionChange;
        _requestAddRegion = requestAddRegion;
        _requestClubChange = requestClubChange;
        _requestAddClub = requestAddClub;
        _requestDusshChange = requestDusshChange;
        _requestAddDussh = requestAddDussh;
        Localization = localization;

        Days = new ObservableCollection<RosterDayCellViewModel>(dayCells);
        // The collapsed merged cells aggregate over the day cells, so refresh them whenever a cell's
        // group/chip/membership changes (e.g. an expanded edit, or a join/leave).
        foreach (var cell in Days)
            cell.PropertyChanged += OnDayCellChanged;

        RegionOptions = regionOptions;
        ClubOptions = clubOptions;
        DusshOptions = dusshOptions;

        _fullName = row.FullName;
        _number = row.Number;
        _rank = row.Rank;
        // Rank stores text; resolve the dropdown selection by matching the stored name (case-insensitive).
        var (rankOpts, rankSel) = RankOptionResolver.Resolve(rankOptions, row.Rank, localization);
        _rankOptions = rankOpts;
        _selectedRank = rankSel;
        _coach = row.Coach;
        _birthDate = row.BirthDate;
        _representative = row.Representative;
        _fsouCode = row.FsouCode;
        _isFsouMember = row.IsFsouMember;
        _payment = row.Payment;
        // Region/Club match by id across their shared lists; fall back to "(none)" (the first option).
        _selectedRegion = regionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.RegionId) ?? regionOptions[0];
        _committedRegion = _selectedRegion;
        _selectedClub = clubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.ClubId) ?? clubOptions[0];
        _committedClub = _selectedClub;
        _selectedDussh = dusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == row.DusshId) ?? dusshOptions[0];
        _committedDussh = _selectedDussh;

        _initialized = true;
    }

    // The last region/club/ДЮСШ the page accepted, so a cancelled "+ new" can revert the selection.
    private RegionOption _committedRegion;
    private ClubOption _committedClub;
    private DusshOption _committedDussh;

    public ILocalizationService Localization { get; }

    public Guid ParticipantId => _participantId;

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
    /// so picking an option writes its name into <see cref="Rank"/> (saved with the row); no "+ new".
    /// A stored value not in the list is preserved as a one-off "unknown" option on this row.
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

    /// <summary>Per-day cells, in day order. Bound by the runtime per-day columns in the view.</summary>
    public ObservableCollection<RosterDayCellViewModel> Days { get; }

    partial void OnFullNameChanged(string value) => QueueSave();
    partial void OnNumberChanged(string value) => QueueSave();
    partial void OnRankChanged(string value) => QueueSave();
    partial void OnCoachChanged(string value) => QueueSave();
    partial void OnBirthDateChanged(DateTimeOffset? value) => QueueSave();

    // Region is competition-level and NOT part of the debounced identity save: "+ new" has a modal
    // side effect, so the page owns it. A real region / "(none)" persists via its own callback.
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
    // debounced identity save (OnRankChanged). "(none)" writes blank.
    partial void OnSelectedRankChanged(RankOption value)
    {
        if (!_initialized || value is null)
            return;
        Rank = value.Value;
    }

    /// <summary>
    /// Swaps in a rebuilt rank list while keeping the current selection by value (silent — no save).
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

    // The competition-level text/bool fields persist through the debounced identity save.
    partial void OnRepresentativeChanged(string value) => QueueSave();
    partial void OnFsouCodeChanged(string value) => QueueSave();
    partial void OnIsFsouMemberChanged(bool value) => QueueSave();
    partial void OnPaymentChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    // ── Collapsed-block aggregates ───────────────────────────────────────────────────────────────
    // The roster groups its per-day columns into collapsible blocks. When a block is collapsed it
    // shows ONE merged cell per row whose value is computed from the relevant day cells:
    //   • Groups span ALL days; Chips span only the days the participant runs (IsMember).
    //   • If the relevant cells share one value, the merged cell edits and writes it to all of them.
    //   • If they differ (>1 distinct value), the merged cell shows a read-only "різні" label.
    // These are computed getters; OnDayCellChanged raises change notifications to keep them live.

    /// <summary>
    /// The shared group across all days (null when they differ/none). Setting it fans out to every day.
    /// Bound TwoWay by the collapsed Groups cell, so an edit there writes to all days.
    /// </summary>
    public GroupOption? CollapsedGroupValue
    {
        get => GroupShowsInput && Days.Count > 0 ? Days[0].SelectedGroup : null;
        set
        {
            if (value is not null)
                SetGroupForAllDays(value);
        }
    }

    // The collapsed Groups cell has three states, decided by the distinct ACTUAL (non-null) groups
    // across all days — a day with no group / where the participant doesn't run is the null sentinel
    // and is ignored when counting "real" groups:
    //   • input  — every day shares one value (all the same real group, or all "(none)"): editable combo.
    //   • single — exactly one real group, but not on every day (the rest are "(none)"): read-only
    //              "<group> (<n> днів)" summary, where n is how many days use that group. No input.
    //   • differ — two or more distinct real groups across days: read-only "різні" label.
    private IReadOnlyList<RosterDayCellViewModel> RealGroupDays =>
        Days.Where(d => d.SelectedGroup.Id is not null).ToList();

    private int DistinctRealGroupCount =>
        Days.Select(d => d.SelectedGroup.Id).Where(id => id is not null).Distinct().Count();

    /// <summary>True when every day shares one value (one real group on all, or "(none)" on all).</summary>
    public bool GroupShowsInput =>
        Days.Select(d => d.SelectedGroup.Id).Distinct().Count() <= 1;

    /// <summary>True when one real group is used on some (not all) days, the rest having none.</summary>
    public bool GroupShowsSingle => !GroupShowsInput && DistinctRealGroupCount == 1;

    /// <summary>True when the collapsed Groups cell should show the read-only "різні" label.</summary>
    public bool GroupShowsDifferent => DistinctRealGroupCount > 1;

    /// <summary>"&lt;group&gt; (&lt;n&gt; днів)" for the single-group summary state (empty otherwise).</summary>
    public string GroupSingleSummary
    {
        get
        {
            if (!GroupShowsSingle)
                return string.Empty;
            var days = RealGroupDays;
            return Localization.Get("Participants.Roster.GroupSomeDays")
                .Replace("{0}", days[0].SelectedGroup.Label)
                .Replace("{1}", days.Count.ToString());
        }
    }

    /// <summary>
    /// Sort key for the collapsed Groups column. Rows are grouped by their FIRST real group — the group
    /// on the earliest day the participant runs — so a person who runs one group sorts under it whether
    /// they run it every day, one day, or alongside others. Within one such group the order is, in turn:
    ///   1. rows where every day is that one group,
    ///   2. rows where it is a single day's group (earlier day first),
    ///   3. genuinely "різні" rows, at the end of that group's block.
    /// The key is "&lt;firstGroupLabel&gt;&lt;tier&gt;&lt;dayIndex&gt;": the label drives the outer
    /// (natural) order; the tier digit then the day index break ties. A control-char separator keeps a
    /// label that ends in digits (e.g. "Група 10") from merging with the tier digit under natural
    /// comparison. Empty for a participant with no group on any day, so those clump together as before.
    /// </summary>
    public string CollapsedGroupSortKey
    {
        get
        {
            // Earliest participating day index that carries a real group (Days is in day-number order).
            int firstIndex = -1;
            for (int i = 0; i < Days.Count; i++)
                if (Days[i].SelectedGroup.Id is not null)
                {
                    firstIndex = i;
                    break;
                }
            if (firstIndex < 0)
                return string.Empty;

            var label = Days[firstIndex].SelectedGroup.Label;
            // tier: 0 = same on every day, 1 = single day, 2 = genuinely different.
            int tier = GroupShowsDifferent ? 2 : GroupShowsInput ? 0 : 1;
            return $"{label}{tier}{firstIndex:D4}";
        }
    }

    /// <summary>Sets every day's group to <paramref name="value"/> (each cell persists itself).</summary>
    public void SetGroupForAllDays(GroupOption value)
    {
        // Each day owns a distinct GroupOptions list, and the combo matches its selection by reference.
        // Resolve the equivalent option (by group id) from each day's own list so its per-day combo can
        // find the selected item — assigning the shared instance directly leaves other days' combos blank.
        foreach (var cell in Days)
            cell.SelectedGroup =
                cell.GroupOptions.FirstOrDefault(o => o.Id == value.Id) ?? cell.SelectedGroup;
    }

    /// <summary>True when the participant runs at least one day (so a chip can be set).</summary>
    public bool HasAnyChipMember => Days.Any(d => d.IsMember);

    /// <summary>
    /// The shared chip across the member days (empty when they differ/none). Setting it fans out to
    /// every member day. Bound TwoWay by the collapsed Chips cell, so an edit there writes to all
    /// member days.
    /// </summary>
    public string CollapsedChipValue
    {
        get
        {
            var members = Days.Where(d => d.IsMember).ToList();
            return members.Count == 0 || ChipValuesDiffer ? string.Empty : members[0].Chip;
        }
        set => SetChipForMemberDays(value ?? string.Empty);
    }

    /// <summary>True when the member days do not all share one chip (by trimmed value).</summary>
    public bool ChipValuesDiffer =>
        Days.Where(d => d.IsMember).Select(d => (d.Chip ?? string.Empty).Trim()).Distinct().Count() > 1;

    /// <summary>True when the collapsed Chips cell should show the editable input (a member day, all equal).</summary>
    public bool ChipShowsInput => HasAnyChipMember && !ChipValuesDiffer;

    /// <summary>True when the collapsed Chips cell should show the read-only "різні" label.</summary>
    public bool ChipShowsDifferent => HasAnyChipMember && ChipValuesDiffer;

    /// <summary>Sets the chip on every member day to <paramref name="value"/> (each cell persists itself).</summary>
    public void SetChipForMemberDays(string value)
    {
        foreach (var cell in Days.Where(d => d.IsMember))
            cell.Chip = value;
    }

    // ── Start time (member-only, like Chips) ─────────────────────────────────────────────────────
    /// <summary>
    /// The shared start time across member days as "HH:mm" text (empty when they differ/none). Setting
    /// it fans out to every member day. Bound TwoWay by the collapsed Start-times cell.
    /// </summary>
    public string CollapsedStartTimeText
    {
        get
        {
            var members = Days.Where(d => d.IsMember).ToList();
            return members.Count == 0 || StartTimeValuesDiffer ? string.Empty : members[0].StartTimeText;
        }
        set => SetStartTimeForMemberDays(value ?? string.Empty);
    }

    /// <summary>True when the member days do not all share one start time.</summary>
    public bool StartTimeValuesDiffer =>
        Days.Where(d => d.IsMember).Select(d => d.StartTime).Distinct().Count() > 1;

    /// <summary>True when the collapsed Start-times cell should show the editable input.</summary>
    public bool StartTimeShowsInput => HasAnyChipMember && !StartTimeValuesDiffer;

    /// <summary>True when the collapsed Start-times cell should show the read-only "різні" label.</summary>
    public bool StartTimeShowsDifferent => HasAnyChipMember && StartTimeValuesDiffer;

    /// <summary>Sets the start-time text on every member day (each cell persists itself).</summary>
    public void SetStartTimeForMemberDays(string value)
    {
        foreach (var cell in Days.Where(d => d.IsMember))
            cell.StartTimeText = value;
    }

    // ── Out of competition (member-only, like Chips) ─────────────────────────────────────────────
    /// <summary>
    /// The shared "out of competition" flag across member days (null when they differ/none). Setting
    /// it fans out to every member day. Bound TwoWay by the collapsed cell's CheckBox.
    /// </summary>
    public bool? CollapsedOutOfCompetition
    {
        get
        {
            var members = Days.Where(d => d.IsMember).ToList();
            return members.Count == 0 || OutOfCompetitionValuesDiffer ? null : members[0].OutOfCompetition;
        }
        set
        {
            if (value is { } v)
                SetOutOfCompetitionForMemberDays(v);
        }
    }

    /// <summary>True when the member days do not all share one flag value.</summary>
    public bool OutOfCompetitionValuesDiffer =>
        Days.Where(d => d.IsMember).Select(d => d.OutOfCompetition).Distinct().Count() > 1;

    /// <summary>True when the collapsed cell should show the editable CheckBox (a member day, all equal).</summary>
    public bool OutOfCompetitionShowsInput => HasAnyChipMember && !OutOfCompetitionValuesDiffer;

    /// <summary>True when the collapsed cell should show the read-only "різні" label.</summary>
    public bool OutOfCompetitionShowsDifferent => HasAnyChipMember && OutOfCompetitionValuesDiffer;

    /// <summary>Sets the flag on every member day (each cell persists itself).</summary>
    public void SetOutOfCompetitionForMemberDays(bool value)
    {
        foreach (var cell in Days.Where(d => d.IsMember))
            cell.OutOfCompetition = value;
    }

    private void OnDayCellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RosterDayCellViewModel.SelectedGroup):
                RaiseGroupAggregates();
                break;
            case nameof(RosterDayCellViewModel.Chip):
                RaiseChipAggregates();
                break;
            case nameof(RosterDayCellViewModel.StartTime):
                RaiseStartTimeAggregates();
                break;
            case nameof(RosterDayCellViewModel.OutOfCompetition):
                RaiseOutOfCompetitionAggregates();
                break;
            case nameof(RosterDayCellViewModel.IsMember):
                // A participant who just joined a day inherits the chip they already use on their
                // other days, so the same card carries across the competition without re-typing.
                if (sender is RosterDayCellViewModel joined)
                    CopyChipOnJoin(joined);
                // Membership shifts which cells are relevant for every aggregate.
                RaiseGroupAggregates();
                RaiseChipAggregates();
                RaiseStartTimeAggregates();
                RaiseOutOfCompetitionAggregates();
                break;
        }
    }

    // When a day cell flips to "member" with no chip of its own, copy a chip the participant already
    // uses on another member day. Setting Chip on the (now initialized) cell fires its save callback,
    // so the copied chip persists like any edit. No-op when the cell already has a chip or when no
    // other day carries one — and only acts on a join (IsMember just turned true).
    private void CopyChipOnJoin(RosterDayCellViewModel joined)
    {
        if (!joined.IsMember || !string.IsNullOrWhiteSpace(joined.Chip))
            return;

        var source = Days.FirstOrDefault(d =>
            !ReferenceEquals(d, joined) && d.IsMember && !string.IsNullOrWhiteSpace(d.Chip));
        if (source is not null)
            joined.Chip = source.Chip;
    }

    private void RaiseGroupAggregates()
    {
        OnPropertyChanged(nameof(CollapsedGroupValue));
        OnPropertyChanged(nameof(GroupShowsInput));
        OnPropertyChanged(nameof(GroupShowsSingle));
        OnPropertyChanged(nameof(GroupShowsDifferent));
        OnPropertyChanged(nameof(GroupSingleSummary));
    }

    private void RaiseChipAggregates()
    {
        OnPropertyChanged(nameof(CollapsedChipValue));
        OnPropertyChanged(nameof(ChipValuesDiffer));
        OnPropertyChanged(nameof(HasAnyChipMember));
        OnPropertyChanged(nameof(ChipShowsInput));
        OnPropertyChanged(nameof(ChipShowsDifferent));
    }

    private void RaiseStartTimeAggregates()
    {
        OnPropertyChanged(nameof(CollapsedStartTimeText));
        OnPropertyChanged(nameof(StartTimeValuesDiffer));
        OnPropertyChanged(nameof(StartTimeShowsInput));
        OnPropertyChanged(nameof(StartTimeShowsDifferent));
    }

    private void RaiseOutOfCompetitionAggregates()
    {
        OnPropertyChanged(nameof(CollapsedOutOfCompetition));
        OnPropertyChanged(nameof(OutOfCompetitionValuesDiffer));
        OnPropertyChanged(nameof(OutOfCompetitionShowsInput));
        OnPropertyChanged(nameof(OutOfCompetitionShowsDifferent));
    }
}
