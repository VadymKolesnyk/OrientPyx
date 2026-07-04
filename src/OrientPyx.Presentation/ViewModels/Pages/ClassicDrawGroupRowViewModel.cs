using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One row on the classic draw page: a single group with a "take part" checkbox, an editable start time of
/// its first competitor and an editable interval, plus the computed first FREE start minute after the group
/// (the minute the next group could start). Each group is drawn independently from its own start/interval —
/// there is no shared lane layout here (that is the other, lane-based draw page).
/// </summary>
public sealed partial class ClassicDrawGroupRowViewModel : ObservableObject
{
    public ClassicDrawGroupRowViewModel(DrawGroup group, string start, string interval)
    {
        Group = group;
        _start = start;
        _interval = interval;
    }

    /// <summary>The underlying group with its members.</summary>
    public DrawGroup Group { get; }

    public Guid GroupId => Group.GroupId;
    public string Name => Group.Name;
    public int MemberCount => Group.Members.Count;

    /// <summary>"КП 31"-style first-control label, blank when the course order had no control.</summary>
    public string FirstControlLabel =>
        string.IsNullOrEmpty(Group.FirstControl) ? string.Empty : $"КП {Group.FirstControl}";

    /// <summary>"×12" member-count badge.</summary>
    public string CountLabel => $"×{MemberCount}";

    /// <summary>Whether this group takes part in the draw (only checked groups are drawn). Empty groups stay
    /// off by default but can be toggled on harmlessly.</summary>
    [ObservableProperty]
    private bool _selected;

    /// <summary>Editable "hh:mm:ss" start time of the group's first competitor.</summary>
    [ObservableProperty]
    private string _start;

    /// <summary>Editable "hh:mm:ss" interval between consecutive competitors in this group.</summary>
    [ObservableProperty]
    private string _interval;

    /// <summary>
    /// The first FREE start minute after this group — the minute the next group could start on the same lane,
    /// i.e. group start + members × interval. Recomputed by the page whenever the start/interval/membership
    /// changes; blank when the start or interval can't be parsed.
    /// </summary>
    [ObservableProperty]
    private string _freeMinute = string.Empty;

    partial void OnStartChanged(string value) => FreeMinuteChanged?.Invoke();

    partial void OnIntervalChanged(string value) => FreeMinuteChanged?.Invoke();

    /// <summary>Raised when the start or interval text changes so the page can recompute <see cref="FreeMinute"/>.</summary>
    public event Action? FreeMinuteChanged;
}
