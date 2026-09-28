using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The application-level (app.db) data a competition refers to, packed into a competition archive so the
/// competition keeps working on a machine that doesn't have it: the points rules its days/groups point at
/// (by id) and the sports ranks its participants carry (by name).
/// </summary>
public sealed record EventSharedData(
    IReadOnlyList<SharedPointsRule> PointsRules,
    IReadOnlyList<SharedRank> Ranks)
{
    public static EventSharedData Empty { get; } = new([], []);
}

/// <summary>A points rule as stored in the archive.</summary>
public sealed record SharedPointsRule(Guid Id, string Name, PointsRuleKind Kind, string? TableJson, string? Formula);

/// <summary>A sports rank as stored in the archive.</summary>
public sealed record SharedRank(string Name, double Points);

/// <summary>What importing an archive's shared data changed locally.</summary>
public sealed record EventSharedDataImportResult(int PointsRulesAdded, int PointsRulesMatched, int RanksAdded);
