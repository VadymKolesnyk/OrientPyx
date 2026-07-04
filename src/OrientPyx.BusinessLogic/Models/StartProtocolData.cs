namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Which start protocol is being built. Both share the document shape (header + sections + table) but group
/// and order differently.
/// </summary>
public enum StartProtocolKind
{
    /// <summary>Regular start protocol: one section per group, members ordered by start time within the group.</summary>
    Regular,

    /// <summary>Judges' start protocol: one section per start minute, members of that minute (across all groups) under it.</summary>
    Judges
}

/// <summary>
/// Raw start-protocol data for one day, gathered by the editor service: every group that runs on the day
/// with its members carrying their drawn start time and identity fields. The layer-neutral
/// <c>IStartProtocolBuilder</c> turns this — plus the user's settings + localized labels — into a renderable
/// <see cref="ResultProtocolDocument"/> (the same document model the results protocol and its .docx writer
/// use), grouping/ordering it by the chosen <see cref="StartProtocolKind"/>.
/// </summary>
public sealed record StartProtocolData(
    IReadOnlyList<StartProtocolGroup> Groups,
    ProtocolOfficialsData Officials)
{
    /// <summary>Back-compat overload: no officials configured.</summary>
    public StartProtocolData(IReadOnlyList<StartProtocolGroup> Groups)
        : this(Groups, ProtocolOfficialsData.None) { }
}

/// <summary>One group's section of raw start data: its name, its members (with start times), and the group's
/// effective course-setter (override, else competition default) printed in the section caption.</summary>
public sealed record StartProtocolGroup(
    string Name,
    /// <summary>Display order within the day (mirrors the day grid order).</summary>
    int Order,
    IReadOnlyList<StartProtocolRow> Rows,
    /// <summary>Effective course-setter (начальник дистанції) for this group; blank when none.</summary>
    string CourseSetter = "",
    /// <summary>Optional judge category for the effective course-setter; blank when none.</summary>
    string CourseSetterCategory = "");

/// <summary>
/// One participant's raw row in a start protocol: identity fields plus the drawn start time (null when the
/// participant has no assigned start minute yet — such rows sort last). <see cref="GroupName"/> lets the
/// judges' (per-minute) protocol show which group each runner is from.
/// </summary>
public sealed record StartProtocolRow(
    TimeSpan? StartTime,
    string Number,
    string FullName,
    DateTimeOffset? BirthDate,
    string ClubName,
    string RegionName,
    string DusshName,
    string Coach,
    string Rank,
    string Chip,
    string GroupName,
    /// <summary>Team / команда of the runner; blank when none. Shown in the judges' protocol.</summary>
    string Team = "");
