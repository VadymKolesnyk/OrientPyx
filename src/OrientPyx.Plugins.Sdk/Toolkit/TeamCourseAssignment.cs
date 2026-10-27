namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>
/// Distributes a team's set of courses over its legs (relay «набір на команду», Р-41…Р-43): every leg takes a
/// distinct slot of the set so that the summed penalty is the smallest (a leg's penalty on a course is 0 when it
/// ran that course cleanly, else the number of controls it missed). Among equally good assignments the one that
/// gives the lowest-numbered leg the course standing earliest in the set wins, then the next leg, and so on (Р-42).
/// Small teams (≤ 8 slots) are searched exhaustively; larger ones use the Hungarian algorithm.
/// </summary>
public static class TeamCourseAssignment
{
    /// <summary>The search space up to which every injection is tried (8! = 40 320 orders).</summary>
    public const int ExhaustiveLimit = 8;

    /// <summary>
    /// Assigns each row (a leg, in leg order) a distinct column (a slot of the set, in set order) minimising the
    /// total of <paramref name="penalties"/>[row][column]. Returns the column per row, or null for a row left
    /// without a slot (more legs than slots). Rows may differ only in count from the columns; every row must have
    /// one penalty per column.
    /// </summary>
    public static int?[] Assign(IReadOnlyList<IReadOnlyList<int>> penalties)
    {
        var rows = penalties.Count;
        if (rows == 0)
            return [];
        var slots = penalties[0].Count;
        if (penalties.Any(r => r.Count != slots))
            throw new ArgumentException("Every row needs one penalty per slot.", nameof(penalties));

        // Rows beyond the slots get a «no course» slot that costs more than any real one, placed after the real
        // slots so a real slot always wins a tie.
        var unassignedCost = (long)penalties.Sum(r => r.Count == 0 ? 0L : r.Max(p => (long)p)) + 1;
        var width = Math.Max(slots, rows);
        var cost = new long[rows, width];
        for (var i = 0; i < rows; i++)
            for (var j = 0; j < width; j++)
                cost[i, j] = j < slots ? penalties[i][j] : unassignedCost;

        var chosen = width <= ExhaustiveLimit ? Exhaustive(cost, rows, width) : Lexicographic(cost, rows, width);
        return chosen.Select(j => j < slots ? j : (int?)null).ToArray();
    }

    // Depth-first over injections in lexicographic order; only a strictly cheaper complete assignment replaces the
    // incumbent, so the first optimum found — the lexicographically smallest — is kept (Р-42).
    private static int[] Exhaustive(long[,] cost, int rows, int width)
    {
        var used = new bool[width];
        var current = new int[rows];
        var best = new int[rows];
        var bestCost = long.MaxValue;

        void Walk(int row, long sum)
        {
            if (sum >= bestCost)
                return;
            if (row == rows)
            {
                bestCost = sum;
                Array.Copy(current, best, rows);
                return;
            }
            for (var j = 0; j < width; j++)
            {
                if (used[j])
                    continue;
                used[j] = true;
                current[row] = j;
                Walk(row + 1, sum + cost[row, j]);
                used[j] = false;
            }
        }

        Walk(0, 0);
        return best;
    }

    // Hungarian optimum, then each row in turn is fixed to the earliest slot that still allows the optimum (Р-42).
    private static int[] Lexicographic(long[,] cost, int rows, int width)
    {
        var target = Hungarian(cost, Enumerable.Range(0, rows).ToList(), Enumerable.Range(0, width).ToList());
        var freeCols = Enumerable.Range(0, width).ToList();
        var result = new int[rows];
        long fixedSum = 0;
        for (var i = 0; i < rows; i++)
        {
            var restRows = Enumerable.Range(i + 1, rows - i - 1).ToList();
            foreach (var j in freeCols)
            {
                var restCols = freeCols.Where(c => c != j).ToList();
                var rest = restRows.Count == 0 ? 0 : Hungarian(cost, restRows, restCols);
                if (fixedSum + cost[i, j] + rest == target)
                {
                    result[i] = j;
                    fixedSum += cost[i, j];
                    freeCols.Remove(j);
                    break;
                }
            }
        }
        return result;
    }

    // Minimum total of a rows × cols sub-matrix (rows ≤ cols) — the classic potentials formulation, O(n²·m).
    private static long Hungarian(long[,] cost, IReadOnlyList<int> rowIds, IReadOnlyList<int> colIds)
    {
        var n = rowIds.Count;
        var m = colIds.Count;
        var u = new long[n + 1];
        var v = new long[m + 1];
        var p = new int[m + 1];
        var way = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(long.MaxValue, m + 1).ToArray();
            var used = new bool[m + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                var delta = long.MaxValue;
                for (var j = 1; j <= m; j++)
                {
                    if (used[j])
                        continue;
                    var cur = cost[rowIds[i0 - 1], colIds[j - 1]] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }
                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }
                for (var j = 0; j <= m; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                        minv[j] -= delta;
                }
                j0 = j1;
            } while (p[j0] != 0);
            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        long total = 0;
        for (var j = 1; j <= m; j++)
            if (p[j] != 0)
                total += cost[rowIds[p[j] - 1], colIds[j - 1]];
        return total;
    }
}
