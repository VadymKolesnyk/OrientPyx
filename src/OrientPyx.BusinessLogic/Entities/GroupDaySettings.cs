using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// A group's distance parameters for one specific day. The mere existence of this row means
/// "this group runs on this day"; deleting it removes the group from the day only.
/// </summary>
public class GroupDaySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    /// <summary>Owning <see cref="Group"/> id (foreign key by convention; no navigation).</summary>
    public Guid GroupId { get; set; }

    /// <summary>Stable display/sort order within the day's grid.</summary>
    public int Order { get; set; }

    /// <summary>Control-point order as a free string, e.g. "S1 31 32 33 F". Parsed later.</summary>
    public string CourseOrder { get; set; } = string.Empty;

    /// <summary>Course length in kilometres; optional. Fractional (e.g. 4.5).</summary>
    public decimal? DistanceKm { get; set; }

    /// <summary>
    /// Overrides the day's <see cref="EventDay.DefaultDiscipline"/> for this group when set;
    /// null means the group inherits the day default. Persisted as a string in the database.
    /// </summary>
    public DisciplineType? DisciplineOverride { get; set; }

    /// <summary>Time limit (контрольний час) in seconds; optional. Applies to every discipline.</summary>
    public int? TimeLimitSeconds { get; set; }

    /// <summary>
    /// Overrides the competition-wide course-setter (<see cref="CompetitionInfo.CourseSetter"/>) for this
    /// group on this day; blank means the group inherits the competition default. Printed in the group's
    /// protocol caption.
    /// </summary>
    public string CourseSetter { get; set; } = string.Empty;

    /// <summary>Optional judge category for the per-group course-setter override. Blank = none.</summary>
    public string CourseSetterCategory { get; set; } = string.Empty;

    /// <summary>
    /// Minimum number of control points required to avoid disqualification; optional. Used by the
    /// score-by-count discipline.
    /// </summary>
    public int? RequiredControlCount { get; set; }

    /// <summary>
    /// Points deducted per minute of finishing late; optional. Used by the score-by-time discipline.
    /// </summary>
    public decimal? PenaltyPerMinute { get; set; }

    /// <summary>
    /// Overrides the competition-wide default points rule (<see cref="CompetitionInfo.DefaultPointsRuleId"/>)
    /// for this group on this day; null means the group inherits the competition default. The id references
    /// an application-level <c>PointsRule</c> (app.db). Scoring with this rule is a later feature.
    /// </summary>
    public Guid? PointsRuleId { get; set; }

    /// <summary>
    /// Which sports-rank level this group awards on this day (Додаток 89). <see cref="GroupRankLevel.None"/>
    /// means no ranks are computed for the group. Persisted as a string in the database.
    /// </summary>
    public GroupRankLevel RankLevel { get; set; } = GroupRankLevel.None;

    /// <summary>
    /// How many «Майстер спорту» titles to award in this group on this day (simplified МС rule): the top
    /// N placed runners with an OK result get «МСУ». Null/0 = none. Only meaningful when
    /// <see cref="RankLevel"/> is <see cref="GroupRankLevel.Adult"/> (juniors cannot earn МС).
    /// </summary>
    public int? MasterCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
