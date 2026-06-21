using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

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
    string CourseSetterCategory = "");
