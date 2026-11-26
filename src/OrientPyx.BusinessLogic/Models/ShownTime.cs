namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// How a result time is SHOWN — one rule for every output: fractions of a second are dropped, never rounded. It
/// touches only the text: every stored and computed time (the result, a time the discipline changed, a team or a
/// summary total) keeps its full precision, and places, points and sums are worked out from that. A result that shows
/// the same whole second (and score) as a result with another place gets the tenths — or hundredths, thousandths —
/// that tell them apart (<see cref="ParticipantDayResult.TimeDecimals"/>).
/// </summary>
public static class ShownTime
{
    /// <summary>The most fractional digits a tie is broken with (a read-out gives milliseconds).</summary>
    public const int MaxDecimals = 3;

    /// <summary><paramref name="time"/> as shown with <paramref name="decimals"/> fractional digits: everything past
    /// them dropped. For display only — never feed it back into a calculation.</summary>
    public static TimeSpan Truncate(TimeSpan time, int decimals = 0)
    {
        var unit = TimeSpan.TicksPerSecond / Pow10(Math.Clamp(decimals, 0, MaxDecimals));
        return TimeSpan.FromTicks(time.Ticks - time.Ticks % unit);
    }

    /// <summary>"h:mm:ss" (<see cref="TimeFormat.Result"/>) with <paramref name="decimals"/> fractional digits;
    /// empty for null or negative.</summary>
    public static string Result(TimeSpan? time, int decimals = 0) =>
        time is { } t && t >= TimeSpan.Zero ? TimeFormat.Result(Truncate(t)) + Fraction(t, decimals) : string.Empty;

    /// <summary>"m:ss" under an hour, "h:mm:ss" above (<see cref="TimeFormat.Span"/>) with <paramref name="decimals"/>
    /// fractional digits; empty for null.</summary>
    public static string Span(TimeSpan? time, int decimals = 0) =>
        time is { } t ? TimeFormat.Span(Truncate(t)) + Fraction(t, decimals) : string.Empty;

    /// <summary>"00:10:30" — always two-digit total hours (<see cref="TimeFormat.TotalHours"/>, the summary sheet) with
    /// <paramref name="decimals"/> fractional digits.</summary>
    public static string TotalHours(TimeSpan time, int decimals = 0) =>
        TimeFormat.TotalHours(Truncate(time)) + Fraction(time, decimals);

    /// <summary>The loss to the leader as the two times are shown: the difference of the two shown values, so the
    /// printed gap always matches the printed times (17:57.3 behind 17:56.4 shows as 17:57 − 17:56 = +1 s). Text
    /// only, like the times themselves.</summary>
    public static TimeSpan Gap(TimeSpan time, TimeSpan leader, int decimals = 0) =>
        Truncate(time, decimals) - Truncate(leader, decimals);

    /// <summary>«Відставання» as "+h:mm:ss" (<see cref="Result"/>) — <see cref="Gap"/> with the row's digits; empty
    /// when the shown times are equal or the row is ahead.</summary>
    public static string ResultGap(TimeSpan time, TimeSpan leader, int decimals = 0) =>
        Gap(time, leader, decimals) is var gap && gap > TimeSpan.Zero ? "+" + Result(gap, decimals) : string.Empty;

    /// <summary>«Відставання» as "+m:ss" (<see cref="Span"/>); empty when the shown times are equal or the row is ahead.</summary>
    public static string SpanGap(TimeSpan time, TimeSpan leader, int decimals = 0) =>
        Gap(time, leader, decimals) is var gap && gap > TimeSpan.Zero ? "+" + Span(gap, decimals) : string.Empty;

    /// <summary>
    /// The fractional digits each item's time is shown with. Items whose times show the same whole second under
    /// the same key (the score, in a scoring format) form one cluster; when two of its placed items have different
    /// places and different times, all its items get the fewest digits (up to <see cref="MaxDecimals"/>) that tell
    /// every such pair apart. Unplaced items (поза конкурсом, not ranked) take their cluster's digits but never cause
    /// them; equal times placed apart by another rule (a tie-break) need no digits. Items not listed show whole seconds.
    /// </summary>
    public static Dictionary<TId, int> Decimals<T, TId>(
        IEnumerable<T> items, Func<T, TId> id, Func<T, TimeSpan?> time, Func<T, int?> place, Func<T, int?> score)
        where TId : notnull
    {
        var result = new Dictionary<TId, int>();
        var clusters = items
            .Select(i => (Item: i, Time: time(i)))
            .Where(x => x.Time is { } t && t >= TimeSpan.Zero)
            .GroupBy(x => (Score: score(x.Item), Second: Truncate(x.Time!.Value)));
        foreach (var cluster in clusters)
        {
            var placed = cluster
                .Where(x => place(x.Item) is not null)
                .Select(x => (Place: place(x.Item)!.Value, Time: x.Time!.Value))
                .ToList();
            var pairs = (from a in placed
                         from b in placed
                         where a.Place < b.Place && a.Time != b.Time
                         select (A: a.Time, B: b.Time)).ToList();
            if (pairs.Count == 0)
                continue;

            var decimals = 1;
            while (decimals < MaxDecimals && pairs.Any(p => Truncate(p.A, decimals) == Truncate(p.B, decimals)))
                decimals++;
            foreach (var (item, _) in cluster)
                result[id(item)] = decimals;
        }
        return result;
    }

    // ".4" / ".45" / ".456" — the dropped fraction's leading digits; empty for none.
    private static string Fraction(TimeSpan time, int decimals)
    {
        if (decimals <= 0)
            return string.Empty;
        decimals = Math.Min(decimals, MaxDecimals);
        var fraction = (Truncate(time, decimals) - Truncate(time)).Ticks / (TimeSpan.TicksPerSecond / Pow10(decimals));
        return "." + fraction.ToString(new string('0', decimals), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long Pow10(int n) => n switch { 0 => 1, 1 => 10, 2 => 100, _ => 1000 };
}
