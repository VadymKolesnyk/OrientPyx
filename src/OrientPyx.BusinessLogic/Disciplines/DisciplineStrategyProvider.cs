using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Indexes the DI-registered strategies by their <see cref="IDisciplineStrategy.Type"/>. Adding a
/// new discipline only requires registering its strategy; this provider needs no changes.
/// </summary>
public sealed class DisciplineStrategyProvider : IDisciplineStrategyProvider
{
    private readonly IReadOnlyDictionary<DisciplineType, IDisciplineStrategy> _byType;

    public DisciplineStrategyProvider(IEnumerable<IDisciplineStrategy> strategies)
    {
        All = strategies.ToList();
        _byType = All.ToDictionary(s => s.Type);
    }

    public IReadOnlyList<IDisciplineStrategy> All { get; }

    public IDisciplineStrategy For(DisciplineType type) =>
        _byType.TryGetValue(type, out var strategy)
            ? strategy
            : throw new InvalidOperationException($"No discipline strategy registered for '{type}'.");
}
