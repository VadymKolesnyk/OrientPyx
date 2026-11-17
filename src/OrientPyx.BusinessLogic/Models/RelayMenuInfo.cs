namespace OrientPyx.BusinessLogic.Models;

/// <summary>What the competition has for the «Змагання → Естафета» menu (over all its days).</summary>
/// <param name="HasRelayGroups">Some group runs a relay on some day (the day's type or the group's own).</param>
/// <param name="HasNumberedVariants">Some number was handed a course variant (<c>LegCourseAssignment</c> with a code) —
/// a relay, or a scatter whose variants OCAD distributed between the numbers.</param>
public sealed record RelayMenuInfo(bool HasRelayGroups, bool HasNumberedVariants)
{
    public static RelayMenuInfo None { get; } = new(false, false);
}
