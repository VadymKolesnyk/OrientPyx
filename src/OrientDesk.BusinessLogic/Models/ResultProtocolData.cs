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
    string CourseSetterCategory = "",
    /// <summary>The rank-award calculation summary for this group (Додаток 89), or null when the group awards
    /// no rank (level None, or the validity conditions fail). When present and the awarded-rank column is
    /// shown, the builder prints it as an explanatory line under the group's table.</summary>
    GroupRankCalculation? RankCalculation = null);

/// <summary>
/// The rank-award calculation for one group, surfaced under its results table so the printed protocol shows
/// <i>how</i> the «виконаний розряд» values were derived (Додаток 89). Mirrors the on-sheet line
/// "Клас дистанції: КМС ; Ранг змагань: 790 балів ; КМСУ 120% 00:24:08 ; I 135% 00:27:09 …": the course class
/// (the highest rank attainable in the applicable qualification bracket for the group's level), the computed
/// course rank, and one entry per attainable rank with its qualifying percentage and the result cut-off it
/// implies (the leader's time × % for a time discipline, the leader's score × % for a point-scoring one).
/// Raw values; the builder formats them into the printed line using localized labels.
/// </summary>
public sealed record GroupRankCalculation(
    /// <summary>The course-class label — the highest rank attainable at this course rank for the group's level
    /// (e.g. "КМС"). Blank when nothing is attainable.</summary>
    string CourseClass,
    /// <summary>The computed course rank (sum of the up-to-12 highest member rank point values).</summary>
    int Rank,
    /// <summary>The attainable ranks, highest first — each with its qualifying percentage and the result it
    /// implies against the group leader's.</summary>
    IReadOnlyList<RankCalculationEntry> Entries);

/// <summary>
/// One attainable rank within a <see cref="GroupRankCalculation"/>: the rank name, the qualifying percentage
/// of the leader's result, and the implied cut-off — a <see cref="CutoffTimeSeconds"/> for a time discipline
/// (result no slower than this) or a <see cref="CutoffScore"/> for a point-scoring one (score no lower).
/// Exactly one cut-off is set per the group's discipline; the other is null.
/// </summary>
public sealed record RankCalculationEntry(
    string RankName,
    int Percent,
    double? CutoffTimeSeconds,
    int? CutoffScore);

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
