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
    private static readonly IReadOnlyList<ScatterVariantData> NoScatterVariants = [];

    public static (FinishStatusResult Result, DateTimeOffset? ResolvedStart) Evaluate(
        IDisciplineStrategyProvider strategies,
        FinishReadout readout,
        ParticipantDay link,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        IReadOnlySet<string> startFinishCodes,
        IReadOnlySet<string> disabledCodes,
        DisciplineId dayDefault,
        IReadOnlyDictionary<Guid, IReadOnlyList<ScatterVariantData>> variantsByGroup)
    {
        GroupDaySettings? gs = link.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
        var strategy = strategies.For(gs?.DisciplineOverride ?? dayDefault);

        // Expected = course-order codes minus the day's start/finish controls AND its disabled («проблемні»)
        // controls, so missing a disabled control is never an MP. Punched keeps disabled codes — a chip may
        // still have punched one; for a set course it just reads as an extra, for a scored one it's ignored
        // because it's no longer in the allowed set. We only drop the start/finish boxes from the punches.
        var expected = CourseCodes.CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var punched = CourseCodes.SplitCodes(readout.Punches).Where(c => !startFinishCodes.Contains(c)).ToList();

        // Start = chip read-out start when present, else the assigned start (a time-of-day) paired with
        // the finish's date so finish − start is a meaningful duration — the discipline decides (Р-5).
        var start = strategy.ResolveStart(new StartContext
        {
            ChipStart = readout.StartTime,
            AssignedStart = link.StartTime,
            FinishTime = readout.FinishTime
        });

        var context = new FinishContext
        {
            ExpectedControls = expected,
            CourseOrderText = gs?.CourseOrder ?? string.Empty,
            PunchedControls = punched,
            StartTime = start,
            FinishTime = readout.FinishTime,
            TimeLimit = gs?.TimeLimitSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            RequiredControlCount = gs?.RequiredControlCount,
            ScatterVariants = link.GroupId is { } vgid && variantsByGroup.TryGetValue(vgid, out var vs)
                ? vs
                : NoScatterVariants
        };

        return (strategy.EvaluateFinish(context), start);
    }

    /// <summary>
    /// The day's scatter variants keyed by group id, ready for the strategy: only groups whose discipline uses
    /// course variants, each variant's order reduced to its required controls the same way ExpectedControls is
    /// (start/finish + disabled dropped), preserving the A-before-B order.
    /// </summary>
    public static Dictionary<Guid, IReadOnlyList<ScatterVariantData>> BuildScatterVariants(
        IDisciplineStrategyProvider strategies,
        IReadOnlyList<ScatterVariant> rows,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        DisciplineId dayDefault,
        IReadOnlySet<string> startFinishCodes,
        IReadOnlySet<string> disabledCodes)
    {
        var result = new Dictionary<Guid, IReadOnlyList<ScatterVariantData>>();
        foreach (var byGroup in rows.GroupBy(v => v.GroupId))
        {
            if (!settingsByGroup.TryGetValue(byGroup.Key, out var gs)
                || !strategies.For(gs.DisciplineOverride ?? dayDefault).UsesCourseVariants)
                continue;

            result[byGroup.Key] = byGroup
                .OrderBy(v => v.Order)
                .Select(v => new ScatterVariantData(
                    v.Code, CourseCodes.CourseControls(v.CourseOrder, startFinishCodes, disabledCodes)))
                .ToList();
        }
        return result;
    }

    /// <summary>True when any group on the day uses course variants (so the variants are worth loading).</summary>
    public static bool AnyUsesCourseVariants(
        IDisciplineStrategyProvider strategies,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        DisciplineId dayDefault) =>
        settingsByGroup.Values.Any(s => strategies.For(s.DisciplineOverride ?? dayDefault).UsesCourseVariants);
}
