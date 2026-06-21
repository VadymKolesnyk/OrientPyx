namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Which participant attribute the draw keeps apart, so two competitors sharing it do not start
/// consecutively (the standard orienteering rule: if drawn back-to-back, the next is moved on). Picked
/// by the user on the draw page; <see cref="None"/> turns the constraint off (pure random order).
/// </summary>
public enum DrawSeparationField
{
    /// <summary>No separation — participants are drawn in a purely random order.</summary>
    None = 0,

    /// <summary>Keep participants from the same region (Region) off consecutive start slots.</summary>
    Region = 1,

    /// <summary>Keep participants from the same club (Club) off consecutive start slots.</summary>
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
/// <param name="FirstControl">First control-point code parsed from the course order, "" when none.</param>
/// <param name="CourseControls">The full ordered control sequence parsed from the course order (start/finish
/// markers stripped), used to detect groups that run an identical distance. Empty when the order is blank.</param>
/// <param name="Members">The group's competitors on this day, draw order is decided by the page.</param>
public sealed record DrawGroup(
    Guid GroupId,
    string Name,
    string FirstControl,
    IReadOnlyList<string> CourseControls,
    IReadOnlyList<DrawParticipant> Members);

/// <summary>
/// One competitor in the draw, carrying just what the algorithm needs: the participant-day link id (so the
/// assigned start time can be written back), display fields, and the separation keys (region/club/team).
/// </summary>
public sealed record DrawParticipant(
    Guid LinkId,
    Guid ParticipantId,
    string FullName,
    string Number,
    string RegionName,
    string ClubName,
    string Team);

/// <summary>One assigned start time, to be written back onto a participant-day link.</summary>
public readonly record struct DrawStartAssignment(Guid LinkId, TimeSpan StartTime);

/// <summary>
/// A single group for the classic draw, where every group is drawn independently with its own start time
/// and interval (unlike the lane-based <see cref="DrawGroup"/>, which shares one global start/interval).
/// </summary>
/// <param name="Group">The group with its members.</param>
/// <param name="Start">Start time of the group's first competitor.</param>
/// <param name="Interval">Gap between consecutive competitors within the group.</param>
public sealed record ClassicDrawGroup(DrawGroup Group, TimeSpan Start, TimeSpan Interval);
