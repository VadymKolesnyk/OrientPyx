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
    private readonly bool _initialized;

    [ObservableProperty]
    private string _surname;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _number;

    [ObservableProperty]
    private string _rank;

    [ObservableProperty]
    private string _coach;

    [ObservableProperty]
    private DateTimeOffset? _birthDate;

    public ParticipantRosterRowViewModel(
        ParticipantRosterRow row,
        IReadOnlyList<RosterDayCellViewModel> dayCells,
        ILocalizationService localization,
        Action<ParticipantRosterRowViewModel> requestSave)
    {
        _participantId = row.ParticipantId;
        _requestSave = requestSave;
        Localization = localization;

        Days = new ObservableCollection<RosterDayCellViewModel>(dayCells);
        // The collapsed merged cells aggregate over the day cells, so refresh them whenever a cell's
        // group/chip/membership changes (e.g. an expanded edit, or a join/leave).
        foreach (var cell in Days)
            cell.PropertyChanged += OnDayCellChanged;

        _surname = row.Surname;
        _name = row.Name;
        _number = row.Number;
        _rank = row.Rank;
        _coach = row.Coach;
        _birthDate = row.BirthDate;

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid ParticipantId => _participantId;

    /// <summary>Per-day cells, in day order. Bound by the runtime per-day columns in the view.</summary>
    public ObservableCollection<RosterDayCellViewModel> Days { get; }

    partial void OnSurnameChanged(string value) => QueueSave();
    partial void OnNameChanged(string value) => QueueSave();
    partial void OnNumberChanged(string value) => QueueSave();
    partial void OnRankChanged(string value) => QueueSave();
    partial void OnCoachChanged(string value) => QueueSave();
    partial void OnBirthDateChanged(DateTimeOffset? value) => QueueSave();

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
        get => GroupValuesDiffer || Days.Count == 0 ? null : Days[0].SelectedGroup;
        set
        {
            if (value is not null)
                SetGroupForAllDays(value);
        }
    }

    /// <summary>True when the days do not all share one group (by id).</summary>
    public bool GroupValuesDiffer =>
        Days.Select(d => d.SelectedGroup.Id).Distinct().Count() > 1;

    /// <summary>True when the collapsed Groups cell should show the editable combo (all days share one).</summary>
    public bool GroupShowsInput => !GroupValuesDiffer;

    /// <summary>Sets every day's group to <paramref name="value"/> (each cell persists itself).</summary>
    public void SetGroupForAllDays(GroupOption value)
    {
        foreach (var cell in Days)
            cell.SelectedGroup = value;
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
            case nameof(RosterDayCellViewModel.IsMember):
                // Membership shifts which cells are relevant for every aggregate.
                RaiseGroupAggregates();
                RaiseChipAggregates();
                break;
        }
    }

    private void RaiseGroupAggregates()
    {
        OnPropertyChanged(nameof(CollapsedGroupValue));
        OnPropertyChanged(nameof(GroupValuesDiffer));
        OnPropertyChanged(nameof(GroupShowsInput));
    }

    private void RaiseChipAggregates()
    {
        OnPropertyChanged(nameof(CollapsedChipValue));
        OnPropertyChanged(nameof(ChipValuesDiffer));
        OnPropertyChanged(nameof(HasAnyChipMember));
        OnPropertyChanged(nameof(ChipShowsInput));
        OnPropertyChanged(nameof(ChipShowsDifferent));
    }
}
