using OrientPyx.BusinessLogic.Disciplines.CoursePattern;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// The full list of missed controls for an MP run (Р-52a): the required controls left out of the
/// <b>maximum</b> in-order match with the punches. The finish status itself keeps coming from the greedy
/// checks in the strategies (an in-order match exists ⇔ the maximum match is complete, so OK/MP agree);
/// this only answers "which ones, all of them" for rules that count missed controls.
/// </summary>
internal static class MissedControls
{
    /// <summary>
    /// Missed controls of a prescribed order (set course, a scatter variant): the expected controls outside a
    /// longest common subsequence with the punches, in course order. Among equally long alignments the earliest
    /// punch of each control is taken, like the splits alignment.
    /// </summary>
    public static IReadOnlyList<string> ForOrder(IReadOnlyList<string> expected, IReadOnlyList<string> punched)
    {
        var n = punched.Count;
        var m = expected.Count;
        if (m == 0)
            return [];

        var codes = punched.Select(p => p.Trim()).ToArray();
        var course = expected.Select(c => c.Trim()).ToArray();

        bool Same(int i, int j) =>
            codes[i].Length > 0 && string.Equals(codes[i], course[j], StringComparison.OrdinalIgnoreCase);

        // best[i, j] = most prescribed controls takeable from punch i onwards against course position j onwards.
        var best = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                best[i, j] = Same(i, j)
                    ? Math.Max(best[i + 1, j + 1] + 1, Math.Max(best[i + 1, j], best[i, j + 1]))
                    : Math.Max(best[i + 1, j], best[i, j + 1]);

        var missed = new List<string>();
        int pi = 0, cj = 0;
        while (cj < m)
        {
            if (pi < n && Same(pi, cj) && best[pi, cj] == best[pi + 1, cj + 1] + 1)
            {
                pi++;
                cj++;
            }
            else if (pi < n && best[pi + 1, cj] == best[pi, cj])
                pi++;   // an extra punch
            else
            {
                missed.Add(course[cj]);   // a prescribed control left out
                cj++;
            }
        }
        return missed;
    }

    /// <summary>
    /// Missed controls of a «mixed» pattern: the required controls left out of the maximum satisfaction of the
    /// pattern by the punches. An ordered run is aligned like a set course; an <c>[N: …]</c> block in which only
    /// k &lt; N options were taken contributes the first N − k untaken options in writing order.
    /// <paramref name="ignored"/> (the day's disabled controls) count as satisfied and are never missed.
    /// </summary>
    public static IReadOnlyList<string> ForPattern(
        CoursePattern.CoursePattern pattern, IReadOnlyList<string> punched, IReadOnlySet<string> ignored)
    {
        var solver = new PatternSolver(punched, ignored);
        var n = punched.Count;
        var missed = new List<string>();
        solver.Reconstruct(pattern.Root, 0, n, missed);
        return missed;
    }

    /// <summary>
    /// Dynamic programme over (item, start, end): <c>Row(item, i)[j]</c> is the most required controls the item
    /// can satisfy using only the punches in <c>[i, j)</c>. Rows are memoised per (item, start).
    /// </summary>
    private sealed class PatternSolver
    {
        // Blocks with at most this many options are solved exactly over option subsets; wider blocks of nested
        // items fall back to taking the options in writing order (a plain-control block is always exact).
        private const int ExactSubsetLimit = 6;

        private readonly string[] _punches;
        private readonly IReadOnlySet<string> _ignored;
        private readonly int _n;
        private readonly Dictionary<IPatternItem, int[]?[]> _memo = new(ReferenceEqualityComparer.Instance);

        public PatternSolver(IReadOnlyList<string> punches, IReadOnlySet<string> ignored)
        {
            _punches = punches.Select(p => p.Trim()).ToArray();
            _ignored = ignored;
            _n = _punches.Length;
        }

        private bool IsIgnored(string code) => _ignored.Contains(code.Trim());

        private bool Matches(int index, string code) =>
            string.Equals(_punches[index], code, StringComparison.OrdinalIgnoreCase);

        public int[] Row(IPatternItem item, int i)
        {
            if (!_memo.TryGetValue(item, out var rows))
                _memo[item] = rows = new int[]?[_n + 1];
            return rows[i] ??= Compute(item, i);
        }

        private int[] Compute(IPatternItem item, int i) => item switch
        {
            ControlItem c => ControlRow(c, i),
            SequenceItem s => SequenceRows(s.Items, i)[^1],
            AnyOfItem a => AnyOfRow(a, i),
            _ => new int[_n + 1]
        };

        private int[] ControlRow(ControlItem c, int i)
        {
            var row = new int[_n + 1];
            if (IsIgnored(c.Code))
            {
                Array.Fill(row, 1);
                return row;
            }

            var k = FirstMatch(c.Code, i);
            if (k >= 0)
                for (var j = k + 1; j <= _n; j++)
                    row[j] = 1;
            return row;
        }

        private int FirstMatch(string code, int from)
        {
            for (var k = from; k < _n; k++)
                if (Matches(k, code))
                    return k;
            return -1;
        }

        // Prefix rows of an ordered run: result[k][j] = best over the first k items within [i, j).
        private List<int[]> SequenceRows(IReadOnlyList<IPatternItem> items, int i)
        {
            var prefixes = new List<int[]>(items.Count + 1) { new int[_n + 1] };
            foreach (var item in items)
                prefixes.Add(Then(prefixes[^1], item, i));
            return prefixes;
        }

        // Extends a partial row (best within [i, m)) by one more item placed in [m, j).
        private int[] Then(int[] before, IPatternItem item, int i)
        {
            var next = new int[_n + 1];
            for (var m = i; m <= _n; m++)
            {
                var row = Row(item, m);
                var b = before[m];
                for (var j = m; j <= _n; j++)
                    if (b + row[j] > next[j])
                        next[j] = b + row[j];
            }
            return next;
        }

        private int[] AnyOfRow(AnyOfItem block, int i)
        {
            var options = block.Items;
            if (options.All(o => o is ControlItem))
            {
                var row = new int[_n + 1];
                for (var j = i; j <= _n; j++)
                    row[j] = Math.Min(block.Amount, TakenControlOptions(options, i, j).Count);
                return row;
            }

            return options.Count <= ExactSubsetLimit ? SubsetRows(block, i).Best : OrderedRows(block, i)[^1][^1];
        }

        // The plain-control options satisfied within [i, j): each punch serves at most one option, a disabled
        // option is always satisfied. Returned as option indices in writing order.
        private List<int> TakenControlOptions(IReadOnlyList<IPatternItem> options, int i, int j)
        {
            var available = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var k = i; k < j; k++)
                if (_punches[k].Length > 0)
                    available[_punches[k]] = available.GetValueOrDefault(_punches[k]) + 1;

            var taken = new List<int>();
            for (var o = 0; o < options.Count; o++)
            {
                var code = ((ControlItem)options[o]).Code;
                if (IsIgnored(code))
                    taken.Add(o);
                else if (available.TryGetValue(code, out var left) && left > 0)
                {
                    available[code] = left - 1;
                    taken.Add(o);
                }
            }
            return taken;
        }

        // Exact: every subset of at most Amount options, taken in any order. Rows[mask][j] = best using exactly
        // the options in mask (each placed in its own consecutive range) within [i, j).
        private (int[] Best, int[][] Rows) SubsetRows(AnyOfItem block, int i)
        {
            var options = block.Items;
            var count = 1 << options.Count;
            var rows = new int[count][];
            for (var mask = 0; mask < count; mask++)
                rows[mask] = Enumerable.Repeat(int.MinValue, _n + 1).ToArray();
            for (var j = i; j <= _n; j++)
                rows[0][j] = 0;

            for (var mask = 0; mask < count; mask++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) >= block.Amount)
                    continue;
                for (var o = 0; o < options.Count; o++)
                {
                    if ((mask & (1 << o)) != 0)
                        continue;
                    var target = rows[mask | (1 << o)];
                    for (var m = i; m <= _n; m++)
                    {
                        if (rows[mask][m] == int.MinValue)
                            continue;
                        var row = Row(options[o], m);
                        for (var j = m; j <= _n; j++)
                            if (rows[mask][m] + row[j] > target[j])
                                target[j] = rows[mask][m] + row[j];
                    }
                }
            }

            var best = new int[_n + 1];
            for (var mask = 0; mask < count; mask++)
                if (System.Numerics.BitOperations.PopCount((uint)mask) <= block.Amount)
                    for (var j = i; j <= _n; j++)
                        best[j] = Math.Max(best[j], rows[mask][j]);
            return (best, rows);
        }

        // Approximation for a wide block of nested options: options are considered in writing order, each either
        // skipped or placed after the previous one, at most Amount placed. rows[o][k][j] = best over the first o
        // options with k of them placed, within [i, j).
        private int[][][] OrderedRows(AnyOfItem block, int i)
        {
            var options = block.Items;
            var amount = Math.Min(block.Amount, options.Count);
            var rows = new int[options.Count + 1][][];
            for (var o = 0; o <= options.Count; o++)
            {
                rows[o] = new int[amount + 1][];
                for (var k = 0; k <= amount; k++)
                    rows[o][k] = Enumerable.Repeat(int.MinValue, _n + 1).ToArray();
            }
            for (var j = i; j <= _n; j++)
                rows[0][0][j] = 0;

            for (var o = 0; o < options.Count; o++)
                for (var k = 0; k <= amount; k++)
                {
                    var cur = rows[o][k];
                    // skip option o
                    for (var j = i; j <= _n; j++)
                        rows[o + 1][k][j] = Math.Max(rows[o + 1][k][j], cur[j]);
                    if (k == amount)
                        continue;
                    // place option o
                    var target = rows[o + 1][k + 1];
                    for (var m = i; m <= _n; m++)
                    {
                        if (cur[m] == int.MinValue)
                            continue;
                        var row = Row(options[o], m);
                        for (var j = m; j <= _n; j++)
                            if (cur[m] + row[j] > target[j])
                                target[j] = cur[m] + row[j];
                    }
                }

            // Collapse the "placed count" dimension into the last slot so callers read rows[^1][^1].
            var last = rows[options.Count];
            var best = new int[_n + 1];
            for (var j = i; j <= _n; j++)
                for (var k = 0; k <= amount; k++)
                    best[j] = Math.Max(best[j], last[k][j]);
            rows[options.Count] = [.. last, best];
            return rows;
        }

        // ── Reconstruction: which required controls the best solution of `item` within [i, j) leaves out.

        public void Reconstruct(IPatternItem item, int i, int j, List<string> missed)
        {
            switch (item)
            {
                case ControlItem c:
                    if (Row(c, i)[j] == 0)
                        missed.Add(c.Code);
                    break;
                case SequenceItem s:
                    ReconstructSequence(s.Items, i, j, missed);
                    break;
                case AnyOfItem a:
                    ReconstructAnyOf(a, i, j, missed);
                    break;
            }
        }

        private void ReconstructSequence(IReadOnlyList<IPatternItem> items, int i, int j, List<string> missed)
        {
            var prefixes = SequenceRows(items, i);
            var parts = new List<(IPatternItem Item, int From, int To)>(items.Count);
            var end = j;
            for (var k = items.Count; k >= 1; k--)
            {
                var target = prefixes[k][end];
                var from = end;
                for (var m = end; m >= i; m--)
                    if (prefixes[k - 1][m] + Row(items[k - 1], m)[end] == target)
                    {
                        from = m;
                        break;
                    }
                parts.Add((items[k - 1], from, end));
                end = from;
            }
            parts.Reverse();
            foreach (var (item, from, to) in parts)
                Reconstruct(item, from, to, missed);
        }

        private void ReconstructAnyOf(AnyOfItem block, int i, int j, List<string> missed)
        {
            var options = block.Items;

            // The options that actually scored, each with its own range; the rest of the N slots are filled by
            // the first untaken options in writing order (all of their controls missed).
            var scored = new List<(int Option, int From, int To)>();
            if (options.All(o => o is ControlItem))
            {
                foreach (var o in TakenControlOptions(options, i, j).Take(block.Amount))
                    scored.Add((o, j, j));   // a plain control taken: nothing missed inside it
            }
            else if (options.Count <= ExactSubsetLimit)
                scored.AddRange(TraceSubset(block, i, j));
            else
                scored.AddRange(TraceOrdered(block, i, j));

            var used = new HashSet<int>(scored.Select(s => s.Option));
            foreach (var (option, from, to) in scored)
                if (options[option] is not ControlItem)
                    Reconstruct(options[option], from, to, missed);

            var slots = block.Amount - scored.Count;
            for (var o = 0; o < options.Count && slots > 0; o++)
            {
                if (used.Contains(o))
                    continue;
                Reconstruct(options[o], _n, _n, missed);   // an empty range: every required control missed
                slots--;
            }
        }

        private List<(int Option, int From, int To)> TraceSubset(AnyOfItem block, int i, int j)
        {
            var options = block.Items;
            var (best, rows) = SubsetRows(block, i);
            var target = best[j];
            var mask = -1;
            for (var candidate = 0; candidate < rows.Length && mask < 0; candidate++)
                if (System.Numerics.BitOperations.PopCount((uint)candidate) <= block.Amount && rows[candidate][j] == target)
                    mask = candidate;

            var picked = new List<(int Option, int From, int To)>();
            var end = j;
            while (mask > 0)
            {
                var found = false;
                for (var o = 0; o < options.Count && !found; o++)
                {
                    if ((mask & (1 << o)) == 0)
                        continue;
                    var rest = rows[mask & ~(1 << o)];
                    for (var m = end; m >= i; m--)
                    {
                        if (rest[m] == int.MinValue || rest[m] + Row(options[o], m)[end] != rows[mask][end])
                            continue;
                        if (Row(options[o], m)[end] > 0)
                            picked.Add((o, m, end));
                        mask &= ~(1 << o);
                        end = m;
                        found = true;
                        break;
                    }
                }
                if (!found)
                    break;
            }
            return picked;
        }

        private List<(int Option, int From, int To)> TraceOrdered(AnyOfItem block, int i, int j)
        {
            var options = block.Items;
            var rows = OrderedRows(block, i);
            var amount = Math.Min(block.Amount, options.Count);
            var last = rows[options.Count];
            var k = -1;
            for (var c = 0; c <= amount && k < 0; c++)
                if (last[c][j] == last[^1][j])
                    k = c;

            var picked = new List<(int Option, int From, int To)>();
            var end = j;
            for (var o = options.Count - 1; o >= 0 && k >= 0; o--)
            {
                var value = rows[o + 1][k][end];
                if (rows[o][k][end] == value)
                    continue;   // option o skipped
                if (k == 0)
                    break;
                for (var m = end; m >= i; m--)
                {
                    var prev = rows[o][k - 1][m];
                    if (prev == int.MinValue || prev + Row(options[o], m)[end] != value)
                        continue;
                    if (Row(options[o], m)[end] > 0)
                        picked.Add((o, m, end));
                    end = m;
                    k--;
                    break;
                }
            }
            picked.Reverse();
            return picked;
        }
    }
}
