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
    private bool _initialized;

    [ObservableProperty]
    private bool _isMember;

    [ObservableProperty]
    private GroupOption _selectedGroup;

    public RosterDayCellViewModel(
        Guid participantId,
        RosterDayCell cell,
        IReadOnlyList<GroupOption> groupOptions,
        ILocalizationService localization,
        Action<RosterDayCellViewModel> requestGroupChange)
    {
        _participantId = participantId;
        DayId = cell.DayId;
        DayNumber = cell.DayNumber;
        LinkId = cell.LinkId;
        _isMember = cell.IsMember;
        _requestGroupChange = requestGroupChange;
        Localization = localization;

        GroupOptions = groupOptions;
        _selectedGroup = groupOptions.FirstOrDefault(o => o.Id == cell.GroupId) ?? groupOptions[0];

        _initialized = true;
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

    /// <summary>
    /// Updates the cell after a membership change persisted (joined/left), without re-triggering the
    /// save callback. Called by the page once the background write has applied.
    /// </summary>
    public void ApplyMembership(bool isMember, Guid? linkId)
    {
        _initialized = false;
        IsMember = isMember;
        LinkId = linkId;
        if (!isMember)
            SelectedGroup = GroupOptions[0];
        _initialized = true;
    }
}
