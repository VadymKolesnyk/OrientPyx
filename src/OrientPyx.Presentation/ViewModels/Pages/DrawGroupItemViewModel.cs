using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One group placed inside a start group (column) on the draw page. Wraps the domain
/// <see cref="DrawGroup"/> and carries the live, computed first start time of the group within its
/// column (shown next to the member count). Pure display state — the actual draw is run from the
/// underlying <see cref="DrawGroup"/>.
/// </summary>
public sealed partial class DrawGroupItemViewModel : ObservableObject
{
    public DrawGroupItemViewModel(DrawGroup group)
    {
        Group = group;
    }

    /// <summary>The underlying group with its members and first control point.</summary>
    public DrawGroup Group { get; }

    public Guid GroupId => Group.GroupId;
    public string Name => Group.Name;
    public string FirstControl => Group.FirstControl;
    public IReadOnlyList<string> CourseControls => Group.CourseControls;
    public int MemberCount => Group.Members.Count;

    /// <summary>True when this group's discipline runs a prescribed order, so it takes part in the draw's
    /// shared-first-control / identical-course clash checks. False for the free-order formats (за вибором /
    /// рогейн / score-by-time), which the page skips entirely.</summary>
    public bool ChecksClash => Group.ChecksClash;

    /// <summary>The opening controls to test for a shared-first-control clash: one for a fixed course, one per
    /// variant for scatter (deduplicated). Empty when the group opts out of clash checks or has no course.</summary>
    public IReadOnlyList<string> FirstControls => Group.FirstControls ?? [];

    /// <summary>For a scatter group, every variant's full ordered controls; empty for non-scatter groups.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Variants => Group.Variants ?? [];

    /// <summary>"КП 31"-style first-control label, blank when the course order had no control.</summary>
    public string FirstControlLabel => string.IsNullOrEmpty(FirstControl) ? string.Empty : $"КП {FirstControl}";

    /// <summary>
    /// True when this group shares its first control with another group that starts on the same minute in a
    /// different start group — the View renders the first-control label (КП N) in bold red as a warning.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClash))]
    private bool _firstControlClash;

    /// <summary>
    /// True when another group running the IDENTICAL full course starts on the same minute in a different
    /// start group — the View tints the whole chip red as a stronger warning than a shared first control.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClash))]
    private bool _courseClash;

    /// <summary>True when the chip is highlighted red for any reason — drives the on-chip «?» explain button.</summary>
    public bool HasClash => FirstControlClash || CourseClash;

    /// <summary>
    /// The groups this one overlaps with (same start minutes, different lane), recorded by the page when it
    /// recomputes clashes so the on-chip «?» dialog can name them, their lane, the shared control(s) and the
    /// overlapping start times. Empty when there is no clash. Presentation-only detail — never persisted.
    /// </summary>
    public IReadOnlyList<DrawClashPeer> ClashPeers { get; set; } = [];

    /// <summary>"×12" member-count badge.</summary>
    public string CountLabel => $"×{MemberCount}";

    /// <summary>The computed time the group's first competitor starts within its column.</summary>
    [ObservableProperty]
    private string _startLabel = string.Empty;

    /// <summary>
    /// True for small groups (fewer than 4 members) whose chip is tight and may clip its text — the View
    /// attaches a hover tooltip showing the full info (<see cref="TooltipText"/>). Only set in proportional
    /// mode (the page clears it otherwise), since normal content-sized chips never clip. Re-raises
    /// <see cref="TooltipText"/> so the tooltip attaches/detaches when the mode toggles.
    /// </summary>
    [ObservableProperty]
    private bool _showTooltip;

    partial void OnShowTooltipChanged(bool value) => OnPropertyChanged(nameof(TooltipText));

    /// <summary>
    /// Full chip info as a single line for the hover tooltip — name, first control, member count and start
    /// time — so nothing is lost when a small chip clips its content. <c>null</c> for larger groups so no
    /// tooltip is attached (an unset ToolTip.Tip shows nothing). Re-raised when the start time changes.
    /// </summary>
    public string? TooltipText
    {
        get
        {
            if (!ShowTooltip)
                return null;
            var parts = new List<string> { Name };
            if (!string.IsNullOrEmpty(FirstControlLabel))
                parts.Add(FirstControlLabel);
            parts.Add(CountLabel);
            if (!string.IsNullOrEmpty(StartLabel))
                parts.Add(StartLabel);
            return string.Join("  •  ", parts);
        }
    }

    partial void OnStartLabelChanged(string value) => OnPropertyChanged(nameof(TooltipText));

    /// <summary>
    /// When proportional mode is on, the chip's pixel height — proportional to the member count so a column
    /// reads like a timeline and overlapping groups across columns line up vertically. <c>double.NaN</c>
    /// (the default) means "size to content", used in normal mode.
    /// </summary>
    [ObservableProperty]
    private double _proportionalHeight = double.NaN;

    /// <summary>
    /// True when the chip is too short (a small group in proportional mode) to fit two text rows, so the
    /// View collapses the КП / count / time detail onto the same line as the group name.
    /// </summary>
    [ObservableProperty]
    private bool _compact;

    /// <summary>Zero-based start slot of this group's first competitor within its column (set by the page);
    /// the group occupies slots [FirstSlot, FirstSlot + MemberCount). Used for cross-column collision checks.</summary>
    public int FirstSlot { get; set; }

    /// <summary>True while this chip is the one being dragged — the View renders it semi-transparent.</summary>
    [ObservableProperty]
    private bool _isDragging;

    /// <summary>True while a drag would drop immediately BEFORE this chip — the View shows an insertion line above it.</summary>
    [ObservableProperty]
    private bool _showDropLineBefore;
}

/// <summary>
/// One group that clashes with another on the draw page: the colliding group, the (1-based) start lane it
/// sits in, whether the whole course matches (vs just the opening control) and the inclusive window of start
/// times the two overlap on. Built by the page's clash pass so the «?» explain dialog can spell out the why.
/// </summary>
/// <param name="Group">The overlapping group.</param>
/// <param name="LaneNumber">1-based start-group (lane) number the overlapping group sits in.</param>
/// <param name="SameCourse">True when the two run an identical full course; false when only the first КП matches.</param>
/// <param name="SharedControls">The opening control(s) the two groups share, comma-separated — one code for a
/// fixed course, possibly several across scatter variants. What the «?» dialog names as the shared control.</param>
/// <param name="OverlapFrom">First overlapping start time (formatted), or "" when times are unavailable.</param>
/// <param name="OverlapTo">Last overlapping start time (formatted), or "" when times are unavailable.</param>
public sealed record DrawClashPeer(
    DrawGroupItemViewModel Group,
    int LaneNumber,
    bool SameCourse,
    string SharedControls,
    string OverlapFrom,
    string OverlapTo);
