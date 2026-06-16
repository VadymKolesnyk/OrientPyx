using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

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
    private bool _initialized;

    [ObservableProperty]
    private bool _isMember;

    [ObservableProperty]
    private GroupOption _selectedGroup;

    [ObservableProperty]
    private string _chip;

    [ObservableProperty]
    private TimeSpan? _startTime;

    [ObservableProperty]
    private bool _outOfCompetition;

    public RosterDayCellViewModel(
        Guid participantId,
        RosterDayCell cell,
        IReadOnlyList<GroupOption> groupOptions,
        ILocalizationService localization,
        Action<RosterDayCellViewModel> requestGroupChange,
        Action<RosterDayCellViewModel> requestChipChange,
        Action<RosterDayCellViewModel> requestStartTimeChange,
        Action<RosterDayCellViewModel> requestOutOfCompetitionChange)
    {
        _participantId = participantId;
        DayId = cell.DayId;
        DayNumber = cell.DayNumber;
        LinkId = cell.LinkId;
        _isMember = cell.IsMember;
        _requestGroupChange = requestGroupChange;
        _requestChipChange = requestChipChange;
        _requestStartTimeChange = requestStartTimeChange;
        _requestOutOfCompetitionChange = requestOutOfCompetitionChange;
        Localization = localization;

        GroupOptions = groupOptions;
        _selectedGroup = groupOptions.FirstOrDefault(o => o.Id == cell.GroupId) ?? groupOptions[0];
        _chip = cell.Chip;
        _committedChip = cell.Chip;
        _startTime = cell.StartTime;
        _outOfCompetition = cell.OutOfCompetition;

        _initialized = true;
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
    public int DayNumber { get; }

    /// <summary>The participant's link id for this day, or null when not a member.</summary>
    public Guid? LinkId { get; private set; }

    public Guid ParticipantId => _participantId;

    /// <summary>Group choices for this specific day (id + name), with a leading "не участвує" sentinel.</summary>
    public IReadOnlyList<GroupOption> GroupOptions { get; }

    partial void OnSelectedGroupChanged(GroupOption value)
    {
        if (_initialized)
            _requestGroupChange(this);
    }

    partial void OnChipChanged(string value)
    {
        if (_initialized)
            _requestChipChange(this);
    }

    partial void OnStartTimeChanged(TimeSpan? value)
    {
        // Keep the editable text in sync, then persist (no uniqueness rule, so a plain save).
        OnPropertyChanged(nameof(StartTimeText));
        if (_initialized)
            _requestStartTimeChange(this);
    }

    partial void OnOutOfCompetitionChanged(bool value)
    {
        if (_initialized)
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
            StartTime = null;
            OutOfCompetition = false;
        }
        _initialized = true;
    }
}
