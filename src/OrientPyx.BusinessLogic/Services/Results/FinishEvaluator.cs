using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Results;

/// <summary>
/// Judges one read-out against its holder's group: builds the <see cref="FinishContext"/> and asks the group's
/// discipline strategy for the start (<see cref="IDisciplineStrategy.ResolveStart"/>) and the status. Shared by
/// the day-results calculator and the read-out log so both judge a run identically.
/// </summary>
internal static class FinishEvaluator
{
    private static readonly IReadOnlyList<CourseVariantData> NoCourseVariants = [];

    public static (FinishStatusResult Result, DateTimeOffset? ResolvedStart) Evaluate(
        IDisciplineStrategyProvider strategies,
        FinishReadout readout,
        ParticipantDay link,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        IReadOnlySet<string> startFinishCodes,
        IReadOnlySet<string> disabledCodes,
        DisciplineId dayDefault,
        IReadOnlyDictionary<Guid, IReadOnlyList<CourseVariantData>> variantsByGroup,
        Func<IDisciplineStrategy, SettingValues>? settingsOf = null,
        RelayLegStart? relayLeg = null,
        Guid? groupId = null)
    {
        // A relay leg is judged in its team's group (Р-22), which the caller passes explicitly.
        var effectiveGroup = groupId ?? link.GroupId;
        GroupDaySettings? gs = effectiveGroup is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
        var strategy = strategies.For(gs?.DisciplineOverride ?? dayDefault);

        // Expected = course-order codes minus the day's start/finish controls AND its disabled («проблемні»)
        // controls, so missing a disabled control is never an MP. Punched keeps disabled codes — a chip may
        // still have punched one; for a set course it just reads as an extra, for a scored one it's ignored
        // because it's no longer in the allowed set. We only drop the start/finish boxes from the punches.
        // A relay leg with a named course (Р-40, Р-41) is judged against that course instead of the group's.
        var legCourse = relayLeg?.Course;
        var courseOrder = legCourse?.CourseOrderText ?? gs?.CourseOrder;
        var expected = legCourse?.Controls ?? CourseCodes.CourseControls(courseOrder, startFinishCodes, disabledCodes);
        var punched = CourseCodes.SplitCodes(readout.Punches).Where(c => !startFinishCodes.Contains(c)).ToList();

        // Start = chip read-out start when present, else the assigned start (a time-of-day) paired with
        // the finish's date so finish − start is a meaningful duration — the discipline decides (Р-5).
        // A relay leg's start is decided by the leg sequence (previous finish / restart, Р-28, Р-29), not by the
        // runner's own punches.
        var start = relayLeg is { } rl
            ? rl.Start
            : strategy.ResolveStart(new StartContext
            {
                ChipStart = readout.StartTime,
                AssignedStart = link.StartTime,
                FinishTime = readout.FinishTime
            });

        var context = new FinishContext
        {
            ExpectedControls = expected,
            CourseOrderText = courseOrder ?? string.Empty,
            PunchedControls = punched,
            StartTime = start,
            FinishTime = readout.FinishTime,
            // A relay's time limit applies to the team (Р-31), never to one leg.
            TimeLimit = relayLeg is null && gs?.TimeLimitSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            RequiredControlCount = gs?.RequiredControlCount,
            CourseVariants = effectiveGroup is { } vgid && variantsByGroup.TryGetValue(vgid, out var vs)
                ? vs
                : NoCourseVariants,
            Settings = settingsOf?.Invoke(strategy) ?? SettingValues.Empty
        };

        // Р-41: a leg whose course could not be assigned is MP («дистанцію не призначено») once it finished.
        if (relayLeg is { CourseUnassigned: true } && readout.FinishTime is not null)
            return (new FinishStatusResult(FinishStatus.Mp, relayLeg.CourseDetail, FinishDetailKind.CourseNotAssigned), start);

        return (strategy.EvaluateFinish(context), start);
    }

    /// <summary>
    /// A relay leg as decided by the leg sequence (see <see cref="Evaluate"/>): its start, and — when the group
    /// names its leg courses (Р-41) — the leg's course, or that none could be assigned (<paramref name="CourseDetail"/>
    /// names the missing course code, «—» when there is none).
    /// </summary>
    public sealed record RelayLegStart(
        DateTimeOffset? Start, CourseVariantData? Course = null, bool CourseUnassigned = false, string CourseDetail = "");

    /// <summary>
    /// The day's scatter variants keyed by group id, ready for the strategy: only groups whose discipline uses
    /// course variants, each variant's order reduced to its required controls the same way ExpectedControls is
    /// (start/finish + disabled dropped), preserving the A-before-B order.
    /// </summary>
    public static Dictionary<Guid, IReadOnlyList<CourseVariantData>> BuildCourseVariants(
        IDisciplineStrategyProvider strategies,
        IReadOnlyList<GroupCourseVariant> rows,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        DisciplineId dayDefault,
        IReadOnlySet<string> startFinishCodes,
        IReadOnlySet<string> disabledCodes)
    {
        var result = new Dictionary<Guid, IReadOnlyList<CourseVariantData>>();
        foreach (var byGroup in rows.GroupBy(v => v.GroupId))
        {
            if (!settingsByGroup.TryGetValue(byGroup.Key, out var gs)
                || !HasCourseVariants(strategies.For(gs.DisciplineOverride ?? dayDefault)))
                continue;

            result[byGroup.Key] = byGroup
                .OrderBy(v => v.Order)
                .Select(v => new CourseVariantData(
                    v.Code, CourseCodes.CourseControls(v.CourseOrder, startFinishCodes, disabledCodes), v.CourseOrder))
                .ToList();
        }
        return result;
    }

    /// <summary>True when any group on the day uses course variants (so the variants are worth loading).</summary>
    public static bool AnyUsesCourseVariants(
        IDisciplineStrategyProvider strategies,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        DisciplineId dayDefault) =>
        settingsByGroup.Values.Any(s => HasCourseVariants(strategies.For(s.DisciplineOverride ?? dayDefault)));

    // Scatter variants, and a relay group's named leg courses (Р-40), which the leg sequence picks from.
    private static bool HasCourseVariants(IDisciplineStrategy strategy) =>
        strategy.UsesCourseVariants || strategy.Competitors == CompetitorModel.TeamWithLegs;
}
