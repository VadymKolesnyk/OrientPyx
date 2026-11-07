namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The current day's finish-read log together with the finish desk's head count, both built from one read of
/// the day (so the counts never disagree with the rows they sit under).
/// </summary>
/// <param name="Rows">The log rows, ordered by sequence (see <see cref="FinishReadoutRow"/>).</param>
/// <param name="Counts">How many runners have read out and how many are still out.</param>
public sealed record FinishReadoutLog(IReadOnlyList<FinishReadoutRow> Rows, FinishReadoutCounts Counts);

/// <summary>
/// The finish desk's head count for the current day.
/// </summary>
/// <param name="ReadOut">Day members with at least one read-out — each runner once, however often they read.</param>
/// <param name="OnCourse">Day members still out: no read-out, and no actual start, finish or status — the
/// dashboard's «На дистанції» test (a runner who has read is never counted here).</param>
/// <param name="Unknown">Distinct chips in the log that no day member holds.</param>
public sealed record FinishReadoutCounts(int ReadOut, int OnCourse, int Unknown)
{
    /// <summary>No day selected / nothing to count.</summary>
    public static FinishReadoutCounts Empty { get; } = new(0, 0, 0);
}
