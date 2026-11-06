namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Which participant attribute the draw keeps apart, so two competitors sharing it do not start
/// consecutively (the standard orienteering rule: if drawn back-to-back, the next is moved on). Picked
/// by the user on the draw page; <see cref="None"/> turns the constraint off (pure random order).
/// </summary>
public enum DrawSeparationField
{
    /// <summary>No separation — participants are drawn in a purely random order.</summary>
    None = 0,

    Region = 1,

    Club = 2,

    /// <summary>Keep participants from the same team (Team) off consecutive start slots.</summary>
    Team = 3,
}

/// <summary>
/// Everything the draw page needs for one day, read from the database: the day's groups (each with its
/// first control point, parsed from the course order, used to colour/cluster start groups) and every
/// member of each group with the attributes the draw separates on. The page assigns each member a start
/// minute and writes it back via <see cref="DrawStartAssignment"/>.
/// </summary>
public sealed record DrawPrepData(IReadOnlyList<DrawGroup> Groups);

/// <summary>One group on the day, with its members and the first control point of its course.</summary>
/// <param name="GroupId">The competition-level group id.</param>
/// <param name="Name">The group's display name (e.g. "Ч21").</param>
/// <param name="FirstControl">First control-point code parsed from the course order, "" when none. The
/// representative opening control used to cluster start groups into lanes; for scatter it is the first
/// control of the first variant. Clash detection uses <see cref="FirstControls"/> instead.</param>
/// <param name="CourseControls">The full ordered control sequence parsed from the course order (start/finish
/// and disabled markers stripped), used to detect groups that run an identical distance. Empty when the order
/// is blank. For scatter this is empty — a scatter group has several valid orders, so "identical full course"
/// is judged variant-set to variant-set on the page, not on one flat sequence.</param>
/// <param name="ChecksClash">True when this group's course is a fixed order (set course / mixed / scatter) so
/// the draw should warn when it starts on the same minute as another group sharing an opening control. False
/// for the free-order formats (за вибором / рогейн / score-by-time), where runners pick their own route and
/// there is no meaningful "first control" — the draw ignores clash checks for them entirely.</param>
/// <param name="FirstControls">The set of opening controls to test for a shared-first-control clash: one entry
/// for a fixed course, one per variant for scatter (deduplicated). Empty when <see cref="ChecksClash"/> is
/// false or the course order is blank.</param>
/// <param name="Variants">For a scatter group, each variant's full ordered control sequence (start/finish and
/// disabled markers stripped), used to flag two scatter groups that run the identical set of variants as a
/// same-course clash. Empty for non-scatter groups.</param>
public sealed record DrawGroup(
    Guid GroupId,
    string Name,
    string FirstControl,
    IReadOnlyList<string> CourseControls,
    IReadOnlyList<DrawParticipant> Members,
    bool ChecksClash = false,
    IReadOnlyList<string>? FirstControls = null,
    IReadOnlyList<IReadOnlyList<string>>? Variants = null);

/// <summary>
/// One competitor in the draw, carrying just what the algorithm needs: the participant-day link id (so the
/// assigned start time can be written back), display fields, the separation keys (region/club/team) and the
/// competitor's start positions on the previous days (see <see cref="DrawHistoryEntry"/>).
/// </summary>
/// <param name="HistoryKey">Stable key of the draw unit across days: the participant id for an individual,
/// <c>team:&lt;groupId&gt;:&lt;number&gt;</c> for a relay team (its composition may change). Blank = no key,
/// the unit is never matched against history (its own or as somebody's predecessor).</param>
/// <param name="History">This unit's start positions on the previous days, oldest first; empty when it is new
/// on this day or no earlier day has start times.</param>
public sealed record DrawParticipant(
    Guid LinkId,
    Guid ParticipantId,
    string FullName,
    string Number,
    string RegionName,
    string ClubName,
    string Team,
    string? HistoryKey = null,
    IReadOnlyList<DrawHistoryEntry>? History = null)
{
    /// <summary>The cross-day key; defaults to the participant id.</summary>
    public string HistoryKey { get; init; } = HistoryKey ?? ParticipantId.ToString("N");

    /// <summary>Start positions on the previous days, oldest first (never null).</summary>
    public IReadOnlyList<DrawHistoryEntry> History { get; init; } = History ?? [];
}

/// <summary>
/// Where a draw unit started within its group on one earlier day of the competition: used by the draw to keep
/// the same competitor from opening (or closing) the group two days running and from reproducing yesterday's
/// order in a small group.
/// </summary>
/// <param name="DayNumber">The earlier day's number.</param>
/// <param name="Position">0-based start position inside the group that day (0 = started first).</param>
/// <param name="GroupSize">How many units the group had that day.</param>
/// <param name="PredecessorKey">The <see cref="DrawParticipant.HistoryKey"/> of the unit that started right
/// before this one that day; null for the group's first starter.</param>
public sealed record DrawHistoryEntry(int DayNumber, int Position, int GroupSize, string? PredecessorKey)
{
    /// <summary>The position normalised to 0…1 (0 = first, 1 = last); 0.5 for a group of one.</summary>
    public double Normalized => GroupSize > 1 ? (double)Position / (GroupSize - 1) : 0.5;
}

/// <summary>One assigned start time, to be written back onto a participant-day link.</summary>
public readonly record struct DrawStartAssignment(Guid LinkId, TimeSpan StartTime);

/// <summary>
/// Data for manually re-ordering the start sequence within a group on one day: every group that runs on
/// the day with its members carrying their current drawn start time (from the start protocol). The user
/// re-orders the members within a group; the set of start minutes stays fixed and is re-handed out in the
/// new order (member i takes the i-th smallest start time), written back via <see cref="DrawStartAssignment"/>.
/// </summary>
public sealed record StartOrderData(IReadOnlyList<StartOrderGroup> Groups);

/// <summary>One group on the day with its members, for manual start-order editing.</summary>
/// <param name="GroupId">The competition-level group id.</param>
/// <param name="Name">The group's display name (e.g. "Ч21").</param>
/// <param name="Members">The group's competitors on this day, ordered by their current start time (unset last).</param>
public sealed record StartOrderGroup(Guid GroupId, string Name, IReadOnlyList<StartOrderMember> Members);

/// <summary>
/// One competitor in the start-order editor: the participant-day link id (so the reassigned start time can
/// be written back), the current start time, and the display fields shown in the reorder list.
/// </summary>
public sealed record StartOrderMember(
    Guid LinkId,
    TimeSpan? StartTime,
    string Number,
    string FullName,
    string RegionName,
    string ClubName);

/// <summary>
/// A single group for the classic draw, where every group is drawn independently with its own start time
/// and interval (unlike the lane-based <see cref="DrawGroup"/>, which shares one global start/interval).
/// </summary>
/// <param name="Group">The group with its members.</param>
/// <param name="Start">Start time of the group's first competitor.</param>
/// <param name="Interval">Gap between consecutive competitors within the group.</param>
public sealed record ClassicDrawGroup(DrawGroup Group, TimeSpan Start, TimeSpan Interval);
