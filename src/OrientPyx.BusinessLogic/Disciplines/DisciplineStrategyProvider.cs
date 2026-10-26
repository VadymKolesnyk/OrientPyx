using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Indexes the DI-registered strategies by their <see cref="IDisciplineStrategy.Id"/>. Adding a
/// new discipline only requires registering its strategy; this provider needs no changes. An id with
/// no registered strategy (e.g. a missing plugin) resolves to a cached <see cref="UnknownDisciplineStrategy"/>.
/// </summary>
public sealed class DisciplineStrategyProvider : IDisciplineStrategyProvider
{
    private readonly IReadOnlyDictionary<DisciplineId, IDisciplineStrategy> _byId;
    private readonly ICourseDistanceCalculator _distance;
    private readonly Dictionary<DisciplineId, IDisciplineStrategy> _unknown = [];
    private readonly Lock _gate = new();

    public DisciplineStrategyProvider(IEnumerable<IDisciplineStrategy> strategies, ICourseDistanceCalculator distance)
    {
        _distance = distance;
        // The first registration wins on a duplicate id, so a plugin can never shadow a built-in (the
        // built-ins are registered first; plugin ids are namespaced anyway).
        var byId = new Dictionary<DisciplineId, IDisciplineStrategy>();
        var all = new List<IDisciplineStrategy>();
        foreach (var s in strategies)
        {
            if (byId.ContainsKey(s.Id))
                continue;
            byId[s.Id] = s;
            all.Add(s);
        }
        _byId = byId;
        All = all;
    }

    public IReadOnlyList<IDisciplineStrategy> All { get; }

    public IDisciplineStrategy For(DisciplineId id)
    {
        if (_byId.TryGetValue(id, out var strategy))
            return strategy;

        lock (_gate)
        {
            if (!_unknown.TryGetValue(id, out var unknown))
                _unknown[id] = unknown = new UnknownDisciplineStrategy(id, _distance);
            return unknown;
        }
    }
}
