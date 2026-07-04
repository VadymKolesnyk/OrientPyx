namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// One row of the rank qualification table (Додаток 89, кваліфікаційна таблиця) — an application-level,
/// editable lookup seeded on first run. Each row is keyed by a course-rank threshold (<see cref="Rank"/>):
/// for a computed course rank R, the row with the largest <see cref="Rank"/> ≤ R applies.
///
/// The cells are thresholds against the runner's result expressed as a percentage of the group leader's:
/// the <c>Time*</c> columns are a <b>maximum</b> % of the leader's time (lower result % qualifies — used by
/// time-based disciplines), the <c>Points*</c> columns are a <b>minimum</b> % of the leader's score (higher
/// % qualifies — used by point-scoring disciplines). A null cell means that rank is not attainable at this
/// course rank. The five columns are КМС / I / II / III / III-junior; the junior group remaps the lower
/// three (II→I-ю, III→II-ю, III-junior→III-ю) and never offers КМС/I.
/// </summary>
public class RankQualificationRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Course-rank threshold this row applies from (e.g. 1200, 500, 1).</summary>
    public int Rank { get; set; }

    // Time-based half: maximum % of the leader's time to earn each rank (null = not attainable).
    public int? TimeKms { get; set; }
    public int? TimeFirst { get; set; }
    public int? TimeSecond { get; set; }
    public int? TimeThird { get; set; }
    public int? TimeThirdJunior { get; set; }

    // Points-based half: minimum % of the leader's score to earn each rank (null = not attainable).
    public int? PointsKms { get; set; }
    public int? PointsFirst { get; set; }
    public int? PointsSecond { get; set; }
    public int? PointsThird { get; set; }
    public int? PointsThirdJunior { get; set; }

    /// <summary>Display order; seeded rows keep their canonical high-to-low rank order.</summary>
    public int Order { get; set; }
}
