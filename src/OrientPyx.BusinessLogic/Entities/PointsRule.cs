namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// How a <see cref="PointsRule"/> turns a result into points (очки).
/// </summary>
public enum PointsRuleKind
{
    /// <summary>A fixed placement table: 1st place → N points, 2nd → M, … (see <see cref="PointsRule.TableJson"/>).</summary>
    Table = 0,

    /// <summary>A custom formula over the allowed variables (see <see cref="PointsRule.Formula"/>).</summary>
    Formula = 1,
}

/// <summary>
/// An application-level rule for awarding "очки" (ranking points), shared across every competition
/// (it lives in the app database, not a per-event one). Later it will be linked to days or groups to
/// compute each runner's points; for now this is just the editable catalogue of rules.
///
/// A rule is one of two kinds (<see cref="PointsRuleKind"/>):
/// <list type="bullet">
/// <item>a <b>placement table</b> — an ordered list of point values, index 0 = 1st place
/// (stored as JSON in <see cref="TableJson"/>);</item>
/// <item>a <b>formula</b> — an arithmetic expression over the allowed variables
/// (e.g. <c>100*(2 - T_у/T_л)</c>), stored in <see cref="Formula"/>.</item>
/// </list>
///
/// All point values are decimals with two fractional digits.
/// </summary>
public class PointsRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name (e.g. "45 42 40…" or "100*(2-T_у/T_л)"); unique (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which kind of rule this is.</summary>
    public PointsRuleKind Kind { get; set; } = PointsRuleKind.Table;

    /// <summary>
    /// For <see cref="PointsRuleKind.Table"/>: the placement points as a JSON array of decimals,
    /// index 0 = 1st place (e.g. <c>[45.00,42.00,40.00]</c>). A place beyond the list scores 0.
    /// Empty/null for a formula rule.
    /// </summary>
    public string? TableJson { get; set; }

    /// <summary>
    /// For <see cref="PointsRuleKind.Formula"/>: the expression text over the allowed variables.
    /// Empty/null for a table rule.
    /// </summary>
    public string? Formula { get; set; }

    /// <summary>Display order; seeded rules keep their canonical order.</summary>
    public int Order { get; set; }
}
