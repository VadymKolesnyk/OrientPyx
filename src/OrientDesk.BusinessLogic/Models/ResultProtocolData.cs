namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Raw protocol data for one day, gathered by the editor service from the computed results: every group
/// that runs on the day, each with its course metadata and its participant rows (already carrying the
/// computed <see cref="ResultProtocolRow.Result"/>). The layer-neutral <c>IResultProtocolBuilder</c> turns
/// this — plus the user's settings + localized labels — into a renderable <see cref="ResultProtocolDocument"/>.
/// </summary>
public sealed record ResultProtocolData(
    IReadOnlyList<ResultProtocolGroup> Groups,
    ProtocolOfficialsData Officials)
{
    /// <summary>Back-compat overload: no officials configured.</summary>
    public ResultProtocolData(IReadOnlyList<ResultProtocolGroup> Groups)
        : this(Groups, ProtocolOfficialsData.None) { }
}

/// <summary>
/// The raw officials gathered from the competition metadata, shared by the results and both start protocols:
/// the chief judge, chief secretary (each a name + optional judge category) and the jury (free multi-line
/// text, one member per line). The builders turn this into the document's trailing signature block. The
/// course-setter is NOT here — it is per-group (see <see cref="ResultProtocolGroup.CourseSetter"/>).
/// </summary>
public sealed record ProtocolOfficialsData(
    string ChiefJudge,
    string ChiefJudgeCategory,
    string ChiefSecretary,
    string ChiefSecretaryCategory,
    string Jury)
{
    public static readonly ProtocolOfficialsData None =
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>One group's section of raw protocol data: its name, course metadata, and participant rows.</summary>
public sealed record ResultProtocolGroup(
    string Name,
    /// <summary>Display order within the day (mirrors the day grid order).</summary>
    int Order,
    /// <summary>Course length in km, or null when unknown.</summary>
    decimal? DistanceKm,
    /// <summary>Number of running control points on the course, or null when unknown.</summary>
    int? ControlCount,
    /// <summary>Time limit (контрольний час) in seconds, or null when none.</summary>
    int? TimeLimitSeconds,
    /// <summary>True for a team discipline (rogaine): the builder groups the rows by team and shows a team
    /// sub-row (team place / score) above its members, instead of a flat per-person ranking.</summary>
    bool IsTeam,
    IReadOnlyList<ResultProtocolRow> Rows,
    /// <summary>Effective course-setter (начальник дистанції) for this group: the group's per-day override,
    /// else the competition default. Blank when none. Printed in the group caption.</summary>
    string CourseSetter = "",
    /// <summary>Optional judge category for the effective course-setter. Blank when none.</summary>
    string CourseSetterCategory = "");

/// <summary>
/// One participant's raw row in a protocol group: the identity fields plus the computed day result. The
/// builder formats these into the configured columns; ordering (placed finishers first by place, then the
/// rest) is the builder's job.
/// </summary>
public sealed record ResultProtocolRow(
    string Number,
    string FullName,
    DateTimeOffset? BirthDate,
    string ClubName,
    string RegionName,
    string DusshName,
    string Coach,
    string Rank,
    /// <summary>Team name (rogaine); blank for a personal discipline or a teamless runner.</summary>
    string Team,
    ParticipantDayResult Result);
