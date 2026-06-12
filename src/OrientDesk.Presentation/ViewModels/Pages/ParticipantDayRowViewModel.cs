using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Disciplines;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Models;
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

    // Suppresses save requests while the constructor seeds initial values, and while a rejected
    // reassignment reverts the chip.
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
    private GroupOption _selectedGroup;

    [ObservableProperty]
    private string _chip;

    [ObservableProperty]
    private string _team;

    public ParticipantDayRowViewModel(
        ParticipantDayRow row,
        IReadOnlyList<GroupOption> groupOptions,
        ILocalizationService localization,
        IDisciplineStrategyProvider strategies,
        Action<ParticipantDayRowViewModel> requestSave,
        Action<ParticipantDayRowViewModel> requestLeaveDay,
        Action<ParticipantDayRowViewModel> requestChipChange)
    {
        _linkId = row.LinkId;
        _participantId = row.ParticipantId;
        _order = row.Order;
        _dayDefaultDiscipline = row.DayDefaultDiscipline;
        _strategies = strategies;
        _requestSave = requestSave;
        _requestLeaveDay = requestLeaveDay;
        _requestChipChange = requestChipChange;
        Localization = localization;

        GroupOptions = groupOptions;

        _fullName = row.FullName;
        _number = row.Number;
        _rank = row.Rank;
        _coach = row.Coach;
        _birthDate = row.BirthDate;
        _chip = row.Chip;
        _committedChip = row.Chip;
        _team = row.Team;
        // Match by id; fall back to the "(none)" option (the first) when the group is unset/missing.
        _selectedGroup = groupOptions.FirstOrDefault(o => o.Id == row.GroupId) ?? groupOptions[0];

        _initialized = true;
    }

    // The last chip value the page accepted/persisted, so a rejected reassignment can revert to it.
    private string _committedChip;

    /// <summary>The previously committed chip (to restore after a rejected reassignment).</summary>
    public string CommittedChip => _committedChip;

    /// <summary>Records the chip the page has accepted (after a successful save/reassign).</summary>
    public void MarkChipCommitted(string value) => _committedChip = value;

    public ILocalizationService Localization { get; }

    /// <summary>Key used by the page for debounce timers, delete and as the row identity.</summary>
    public Guid Id => _linkId;

    /// <summary>Parent participant id; needed for the cascade-delete check on removal.</summary>
    public Guid ParticipantId => _participantId;

    /// <summary>Group choices for this day (id + name), with a leading "не участвує" sentinel that leaves the day.</summary>
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
        GroupId: SelectedGroup.Id,
        GroupName: SelectedGroup.Label,
        Chip: (Chip ?? string.Empty).Trim(),
        Team: (Team ?? string.Empty).Trim(),
        DayDefaultDiscipline: _dayDefaultDiscipline);

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
            QueueSave();
    }
    // The chip is NOT part of the debounced row save: a chip edit may collide with another competitor
    // on the day and must be resolved (confirm + reassign, or revert) before it is persisted, so the
    // page owns it via a dedicated callback.
    partial void OnChipChanged(string value)
    {
        if (_initialized)
            _requestChipChange(this);
    }
    partial void OnTeamChanged(string value) => QueueSave();

    /// <summary>Restores the chip to a value without re-triggering the chip-change callback (used to revert a rejected reassignment).</summary>
    public void SetChipSilently(string value)
    {
        var wasInitialized = _initialized;
        _initialized = false;
        Chip = value;
        _initialized = wasInitialized;
    }

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }
}
