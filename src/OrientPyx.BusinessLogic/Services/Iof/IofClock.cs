using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Iof;

/// <summary>
/// Puts a read-out on the IOF time line (plan §3.2). A stamp's clock is its own <c>TimeOfDay</c> — the chip's time as
/// read, never converted (Sport Time stamps carry a synthetic 2001-01-01 date and a winter offset). The runner's anchor
/// (resolved start, else the finish) lands on the day's date <c>D</c> at the day's offset <c>Z</c>; the finish follows
/// at its distance from the anchor, so a run past midnight finishes on D+1 and the machine's time zone never matters.
/// Every value is cut to whole seconds.
/// </summary>
internal static class IofClock
{
    /// <summary>The day's date at <paramref name="clock"/>, in the day's offset, cut to whole seconds.</summary>
    public static DateTimeOffset OnDay(IofRaceData race, TimeSpan clock) =>
        Truncate(Midnight(race) + clock);

    /// <summary>
    /// The IOF start / finish of a run: <paramref name="anchor"/> (null ⇒ the finish anchors, and no start is written)
    /// and <paramref name="finish"/>, both as read.
    /// </summary>
    public static (DateTimeOffset? Start, DateTimeOffset? Finish) Stamps(
        IofRaceData race, DateTimeOffset? anchor, DateTimeOffset? finish)
    {
        if ((anchor ?? finish) is not { } a)
            return (null, null);
        var start = Midnight(race) + a.TimeOfDay;
        return (
            anchor is null ? null : Truncate(start),
            finish is { } f ? Truncate(start + (f - a)) : null);
    }

    /// <summary>Whole seconds of a duration (cut, as <c>TimeFormat.Result</c> prints it).</summary>
    public static int Seconds(TimeSpan span) => (int)(span.Ticks / TimeSpan.TicksPerSecond);

    public static DateTimeOffset Truncate(DateTimeOffset t) => t.AddTicks(-(t.Ticks % TimeSpan.TicksPerSecond));

    private static DateTimeOffset Midnight(IofRaceData race) =>
        new(race.Date.ToDateTime(TimeOnly.MinValue), race.Offset);
}
