namespace OrientPyx.BusinessLogic.Disciplines.CoursePattern;

/// <summary>
/// Combinatorial helpers used to enumerate (and count) the concrete passage orders a pattern allows: an
/// <c>[N: …]</c> block is every N-sized choice of its options × every order those N can be run in. Counting
/// saturates instead of overflowing, so a deliberately wide block reports "very many" rather than wrapping
/// to a negative number.
/// </summary>
internal static class PatternMath
{
    /// <summary><paramref name="a"/> × <paramref name="b"/>, clamped to <see cref="long.MaxValue"/>.</summary>
    public static long MultiplySaturating(long a, long b)
    {
        if (a == 0 || b == 0) return 0;
        return a > long.MaxValue / b ? long.MaxValue : a * b;
    }

    /// <summary><paramref name="a"/> + <paramref name="b"/>, clamped to <see cref="long.MaxValue"/>.</summary>
    public static long AddSaturating(long a, long b) =>
        a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary><paramref name="n"/>!, clamped to <see cref="long.MaxValue"/>.</summary>
    public static long Factorial(int n)
    {
        var result = 1L;
        for (var i = 2; i <= n; i++)
            result = MultiplySaturating(result, i);
        return result;
    }

    /// <summary>Every <paramref name="k"/>-sized index combination of <c>0..n-1</c>, in ascending order.</summary>
    public static IEnumerable<int[]> Combinations(int n, int k)
    {
        if (k < 0 || k > n)
            yield break;
        if (k == 0)
        {
            yield return [];
            yield break;
        }

        var indices = new int[k];
        for (var i = 0; i < k; i++)
            indices[i] = i;

        while (true)
        {
            yield return (int[])indices.Clone();

            // Advance the rightmost index that still has room, then repack the ones after it.
            var pos = k - 1;
            while (pos >= 0 && indices[pos] == n - k + pos)
                pos--;
            if (pos < 0)
                yield break;

            indices[pos]++;
            for (var i = pos + 1; i < k; i++)
                indices[i] = indices[i - 1] + 1;
        }
    }

    /// <summary>
    /// Every ordering of <paramref name="items"/>, in lexicographic order of the item values — so the
    /// first order listed is the block as written, and the rest follow in a stable, readable progression.
    /// </summary>
    public static IEnumerable<int[]> Permutations(IReadOnlyList<int> items)
    {
        var current = items.OrderBy(x => x).ToArray();
        if (current.Length == 0)
        {
            yield return [];
            yield break;
        }

        while (true)
        {
            yield return (int[])current.Clone();

            // Next lexicographic permutation: find the rightmost ascent, swap it with the smallest larger
            // value to its right, then reverse the suffix. No ascent = the last permutation.
            var pivot = current.Length - 2;
            while (pivot >= 0 && current[pivot] >= current[pivot + 1])
                pivot--;
            if (pivot < 0)
                yield break;

            var successor = current.Length - 1;
            while (current[successor] <= current[pivot])
                successor--;
            (current[pivot], current[successor]) = (current[successor], current[pivot]);
            Array.Reverse(current, pivot + 1, current.Length - pivot - 1);
        }
    }
}
