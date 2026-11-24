using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Builds <see cref="GroupDayRow"/>s from the stored rows — one place, so «Групи», the participant tables' group options
/// (chip capacity) and the day readiness check see the same row, named courses included.
/// </summary>
internal static class GroupDayRows
{
    /// <summary>Each group's named courses (scatter variants / relay leg courses) on the day: how many, and the control
    /// count of the longest one.</summary>
    public static Dictionary<Guid, (int Count, int MaxControls)> VariantStats(IEnumerable<GroupCourseVariant> variants) =>
        variants
            .GroupBy(v => v.GroupId)
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), MaxControls: g.Max(v => CourseOrder.CountCourseControls(v.CourseOrder))));

    public static GroupDayRow Create(
        GroupDaySettings s,
        Group group,
        DisciplineId dayDefault,
        int participantCount = 0,
        int variantCount = 0,
        int maxVariantControls = 0) => new(
        SettingsId: s.Id,
        GroupId: s.GroupId,
        Order: s.Order,
        Name: group.Name,
        CourseOrder: s.CourseOrder,
        DistanceKm: s.DistanceKm,
        DisciplineOverride: s.DisciplineOverride,
        DayDefaultDiscipline: dayDefault,
        TimeLimitSeconds: s.TimeLimitSeconds,
        RequiredControlCount: s.RequiredControlCount,
        PenaltyPerMinute: s.PenaltyPerMinute,
        CourseSetter: s.CourseSetter,
        CourseSetterCategory: s.CourseSetterCategory,
        PointsRuleId: s.PointsRuleId,
        RankLevel: s.RankLevel,
        MasterCount: s.MasterCount,
        // Age window lives on the group itself (shared across days), so it comes from the group, not the
        // per-day settings.
        MinBirthYear: group.MinBirthYear,
        MaxBirthYear: group.MaxBirthYear,
        ParticipantCount: participantCount,
        ScatterVariantCount: variantCount,
        ScatterMaxControlCount: maxVariantControls,
        MaxLateSeconds: s.MaxLateSeconds,
        LegCount: Math.Max(1, s.LegCount ?? 1),
        CourseMode: s.CourseAssignmentMode);
}
