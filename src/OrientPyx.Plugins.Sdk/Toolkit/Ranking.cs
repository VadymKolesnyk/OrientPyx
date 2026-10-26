namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>
/// Places competitors (Р-8): sorted by a comparer, equal results share a place and the next place skips
/// (1, 1, 3). Out-of-competition items are left out entirely, so the others rank as if they were absent.
/// </summary>
public static class Ranking
{
    /// <summary>
    /// Returns every placeable item with its place, best first. The sort is stable, so items that compare
    /// equal keep their input order (and share a place).
    /// </summary>
    public static IReadOnlyList<(T Item, int Place)> AssignPlaces<T>(
        IEnumerable<T> items, IComparer<T> order, Func<T, bool>? isOutOfCompetition = null)
    {
        var ordered = items.Where(i => isOutOfCompetition is null || !isOutOfCompetition(i))
            .OrderBy(i => i, order)
            .ToList();

        var placed = new List<(T, int)>(ordered.Count);
        var place = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i == 0 || order.Compare(ordered[i - 1], ordered[i]) != 0)
                place = i + 1;   // a new (worse) result takes the running position; ties keep the prior place
            placed.Add((ordered[i], place));
        }
        return placed;
    }

    /// <summary>Faster time first; a missing time sorts last.</summary>
    public static IComparer<T> ByTime<T>(Func<T, TimeSpan?> time) =>
        Comparer<T>.Create((a, b) => (time(a) ?? TimeSpan.MaxValue).CompareTo(time(b) ?? TimeSpan.MaxValue));

    /// <summary>Higher score first, then faster time; a missing time sorts last.</summary>
    public static IComparer<T> ByScoreThenTime<T>(Func<T, int> score, Func<T, TimeSpan?> time) =>
        Comparer<T>.Create((a, b) =>
        {
            var byScore = score(b).CompareTo(score(a));
            return byScore != 0
                ? byScore
                : (time(a) ?? TimeSpan.MaxValue).CompareTo(time(b) ?? TimeSpan.MaxValue);
        });
}
