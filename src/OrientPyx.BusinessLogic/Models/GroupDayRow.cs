using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Flat read/write model for one group on one day: joins a <c>Group</c> (Name) with its
/// <c>GroupDaySettings</c> (course order, distance, discipline override, type-specific fields) so the
/// UI handles a single type per grid row. <see cref="DisciplineOverride"/> null means "inherit the
/// day default"; the effective discipline is <c>DisciplineOverride ?? DayDefaultDiscipline</c>.
/// </summary>
public sealed record GroupDayRow(
    Guid SettingsId,
    Guid GroupId,
    int Order,
    string Name,
    string CourseOrder,
    decimal? DistanceKm,
    DisciplineType? DisciplineOverride,
    DisciplineType DayDefaultDiscipline,
    int? TimeLimitSeconds,
    int? RequiredControlCount,
    decimal? PenaltyPerMinute,
    /// <summary>Per-group course-setter (начальник дистанції) override; blank = inherit the competition
    /// default. Printed in the group's protocol caption.</summary>
    string CourseSetter = "",
    /// <summary>Optional judge category for the per-group course-setter override; blank = none.</summary>
    string CourseSetterCategory = "",
    /// <summary>Per-group points-rule override (app-level PointsRule id); null = inherit the competition
    /// default (<c>CompetitionInfo.DefaultPointsRuleId</c>).</summary>
    Guid? PointsRuleId = null,
    /// <summary>Which sports-rank level this group awards on the day (Додаток 89). None = no ranks.</summary>
    GroupRankLevel RankLevel = GroupRankLevel.None,
    /// <summary>How many «Майстер спорту» titles to award to the top placed runners (adult groups);
    /// null = none.</summary>
    int? MasterCount = null,
    /// <summary>Earliest allowed birth year, inclusive ("не старше" — born this year or later); null = no
    /// lower bound. Group-level, editable from any day.</summary>
    int? MinBirthYear = null,
    /// <summary>Latest allowed birth year, inclusive ("не молодше" — born this year or earlier); null = no
    /// upper bound. Group-level, editable from any day.</summary>
    int? MaxBirthYear = null,
    /// <summary>How many participants are in this group on this day (read-only, auto-counted).</summary>
    int ParticipantCount = 0,
    /// <summary>How many scatter («розсіювання») variants this group has on this day. 0 for a non-scatter
    /// group. Drives the grid's «N варіантів дистанції» course-order cell and the bottom variants editor.</summary>
    int ScatterVariantCount = 0);
