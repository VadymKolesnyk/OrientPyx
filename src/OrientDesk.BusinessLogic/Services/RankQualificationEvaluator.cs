using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Evaluates the awarded sports rank («виконаний розряд», Додаток 89) for a single result, against the
/// editable application-level qualification table. Pure logic (no I/O): the caller loads the table rows
/// from <c>IAppStore</c> and supplies the already-computed course rank, the group's level, whether the
/// discipline is point-scoring, and the runner's result as a percentage of the group leader's.
/// </summary>
public static class RankQualificationEvaluator
{
    // Canonical rank names (must match the seeded SportRank names). КМС is "КМСУ", master is "МСУ".
    public const string Master = "МСУ";
    public const string Kms = "КМСУ";
    public const string First = "I";
    public const string Second = "II";
    public const string Third = "III";
    public const string FirstJunior = "I-ю";
    public const string SecondJunior = "II-ю";
    public const string ThirdJunior = "III-ю";

    /// <summary>
    /// The awarded rank for a result, or null when none qualifies. <paramref name="rank"/> is the computed
    /// course rank; the row with the largest <see cref="RankQualificationRow.Rank"/> ≤ it applies. For a
    /// time-based discipline a cell is the maximum % of the leader's time (lower <paramref name="percent"/>
    /// qualifies); for a point-scoring one it is the minimum % of the leader's score (higher qualifies).
    /// The highest attainable rank is returned. Junior groups never offer КМС/I and remap the lower three
    /// columns to I-ю / II-ю / III-ю.
    /// </summary>
    public static string? Award(
        IReadOnlyList<RankQualificationRow> rows,
        int rank,
        GroupRankLevel level,
        bool pointsBased,
        double percent)
    {
        if (level == GroupRankLevel.None || rows.Count == 0)
            return null;

        // The applicable row: the largest rank threshold ≤ the computed course rank.
        RankQualificationRow? row = null;
        foreach (var r in rows)
            if (r.Rank <= rank && (row is null || r.Rank > row.Rank))
                row = r;
        if (row is null)
            return null;

        // Candidate (cell, name) pairs from the highest rank to the lowest, per level. The cell value
        // comes from the time half or the points half depending on the discipline.
        foreach (var (cell, name) in Candidates(row, level, pointsBased))
        {
            if (cell is not { } threshold)
                continue;
            var qualifies = pointsBased ? percent >= threshold : percent <= threshold;
            if (qualifies)
                return name;
        }

        return null;
    }

    /// <summary>
    /// The applicable qualification row for a computed course <paramref name="rank"/> (the largest threshold
    /// ≤ it), or null when none applies / the level offers no ranks. Exposed so the protocol can show the
    /// derivation (course class + per-rank thresholds) using the same bracket the award uses.
    /// </summary>
    public static RankQualificationRow? ApplicableRow(IReadOnlyList<RankQualificationRow> rows, int rank, GroupRankLevel level)
    {
        if (level == GroupRankLevel.None || rows.Count == 0)
            return null;
        RankQualificationRow? row = null;
        foreach (var r in rows)
            if (r.Rank <= rank && (row is null || r.Rank > row.Rank))
                row = r;
        return row;
    }

    /// <summary>
    /// The attainable (rank name, threshold %) pairs for a bracket row, highest rank first, with the
    /// non-attainable cells (null) dropped — the percentage is a max % of the leader's time for a time
    /// discipline, a min % of the leader's score for a point-scoring one. Used both to award a rank and to
    /// print the derivation line, so the two stay in lock-step.
    /// </summary>
    public static IReadOnlyList<(string Name, int Percent)> AttainableRanks(
        RankQualificationRow row, GroupRankLevel level, bool pointsBased) =>
        Candidates(row, level, pointsBased)
            .Where(c => c.Cell is not null)
            .Select(c => (c.Name, c.Cell!.Value))
            .ToList();

    private static IEnumerable<(int? Cell, string Name)> Candidates(
        RankQualificationRow row, GroupRankLevel level, bool pointsBased) =>
        level == GroupRankLevel.Junior
            ? // МС/КМС/I are not available to juniors; the lower three columns map to the junior ranks.
            [
                (pointsBased ? row.PointsSecond : row.TimeSecond, FirstJunior),
                (pointsBased ? row.PointsThird : row.TimeThird, SecondJunior),
                (pointsBased ? row.PointsThirdJunior : row.TimeThirdJunior, ThirdJunior),
            ]
            :
            [
                (pointsBased ? row.PointsKms : row.TimeKms, Kms),
                (pointsBased ? row.PointsFirst : row.TimeFirst, First),
                (pointsBased ? row.PointsSecond : row.TimeSecond, Second),
                (pointsBased ? row.PointsThird : row.TimeThird, Third),
            ];
}
