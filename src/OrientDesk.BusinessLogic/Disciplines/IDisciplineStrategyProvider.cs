using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Disciplines;

/// <summary>
/// Resolves the <see cref="IDisciplineStrategy"/> for a competition type. Shared code depends on
/// this provider rather than on the concrete strategies, so the set of disciplines can grow without
/// touching the call sites.
/// </summary>
public interface IDisciplineStrategyProvider
{
    /// <summary>Returns the strategy for the given type. Throws if no strategy is registered.</summary>
    IDisciplineStrategy For(DisciplineType type);

    /// <summary>All registered strategies (e.g. to build the discipline selection list).</summary>
    IReadOnlyList<IDisciplineStrategy> All { get; }
}
