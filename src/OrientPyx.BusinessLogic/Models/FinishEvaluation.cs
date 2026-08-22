using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Everything a discipline needs to derive a participant's finish status from one read-out, all as
/// layer-neutral data. Times are optional (a read-out / assignment may lack them). The expected
/// controls are the group's prescribed course already reduced to the codes that must be visited
/// (start/finish markers removed by the caller using the day's control-point types).
/// </summary>
public sealed class FinishContext
{
    /// <summary>Control codes that must be visited, in the prescribed order (no start/finish markers, and
    /// with any disabled «проблемні» controls already removed by the caller).</summary>
    public IReadOnlyList<string> ExpectedControls { get; init; } = [];

    /// <summary>
    /// The group's raw course-order text as entered, needed by the «mixed» discipline to parse the
    /// order <b>pattern</b> (<c>&lt;…&gt;</c> / <c>[N …]</c> blocks) that <see cref="ExpectedControls"/>
    /// flattens away. Other disciplines ignore it. Empty when there is no course order.
    /// </summary>
    public string CourseOrderText { get; init; } = string.Empty;

    /// <summary>Control codes the chip actually punched, in read order (start/finish already excluded).</summary>
    public IReadOnlyList<string> PunchedControls { get; init; } = [];

    /// <summary>Start time used for the time-limit check (caller picks chip-then-assigned). Null = unknown.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Finish time from the read-out. Null = no finish punch.</summary>
    public DateTimeOffset? FinishTime { get; init; }

    /// <summary>The group's time limit (контрольний час) for the day, when set; null = no limit.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>
    /// The minimum number of allowed controls a runner must take to be classified — the group's
    /// «мін. к-сть КП» for a free-choice-by-count day. Null when the group set none, which the
    /// <see cref="Enums.DisciplineType.ScoreByCount"/> strategy reads as "every allowed control is
    /// required". Ignored by the other disciplines.
    /// </summary>
    public int? RequiredControlCount { get; init; }

    /// <summary>
    /// The scatter («розсіювання») course variants for the runner's group — each a valid order reduced to
    /// its required controls (start/finish and disabled controls already removed, like
    /// <see cref="ExpectedControls"/>). Populated only for a scatter group; empty otherwise. The scatter
    /// strategy picks the best-matching variant from these and judges the runner against it.
    /// </summary>
    public IReadOnlyList<ScatterVariantData> ScatterVariants { get; init; } = [];
}

/// <summary>
/// A computed finish status plus a short human-readable detail (e.g. which control is missing), shown
/// as a tooltip next to the status. <see cref="Detail"/> is empty when there is nothing to add.
/// <see cref="DetailKind"/> tells the UI how to word that detail — the default
/// <see cref="FinishDetailKind.MissingControl"/> reads as «бракує КП …», while
/// <see cref="FinishDetailKind.ControlCount"/> reads as «взято N з M» for the free-choice-by-count
/// format, where nothing is "missing" in particular and no order was judged.
/// </summary>
public readonly record struct FinishStatusResult(
    FinishStatus Status, string Detail, FinishDetailKind DetailKind = FinishDetailKind.MissingControl)
{
    public static FinishStatusResult Of(FinishStatus status) => new(status, string.Empty);
}

/// <summary>How a <see cref="FinishStatusResult.Detail"/> should be worded by the UI.</summary>
public enum FinishDetailKind
{
    /// <summary>The detail names the control that is missing or out of order (set course, mixed, scatter).</summary>
    MissingControl,

    /// <summary>The detail is a "taken/required" control tally (за вибором по кількості КП).</summary>
    ControlCount
}
