using OrientPyx.BusinessLogic.Disciplines;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// How many control punches a runner of a group may have to make on a day — the number the chip must be able
/// to hold (<see cref="SiCard.TooSmall"/>). Shared by the participant tables' chip highlight and the chip-scan
/// warning so both judge a chip the same way.
/// </summary>
public static class ChipCapacity
{
    /// <summary>
    /// The longest run a member of <paramref name="group"/> can make on the day: the discipline's control count
    /// of the group's own course order, or — when the group has named courses / scatter variants
    /// (<see cref="GroupDayRow.ScatterMaxControlCount"/>) — the longest of those, whichever is larger. 0 when
    /// the group has no course yet (nothing to judge).
    /// </summary>
    public static int NeededPunches(GroupDayRow group, IDisciplineStrategy strategy) =>
        Math.Max(group.ScatterMaxControlCount, strategy.ControlCount(group.CourseOrder));
}
