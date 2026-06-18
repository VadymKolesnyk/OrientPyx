namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Raw protocol data for one day, gathered by the editor service from the computed results: every group
/// that runs on the day, each with its course metadata and its participant rows (already carrying the
/// computed <see cref="ResultProtocolRow.Result"/>). The layer-neutral <c>IResultProtocolBuilder</c> turns
/// this — plus the user's settings + localized labels — into a renderable <see cref="ResultProtocolDocument"/>.
/// </summary>
public sealed record ResultProtocolData(IReadOnlyList<ResultProtocolGroup> Groups);

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
    IReadOnlyList<ResultProtocolRow> Rows);

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
