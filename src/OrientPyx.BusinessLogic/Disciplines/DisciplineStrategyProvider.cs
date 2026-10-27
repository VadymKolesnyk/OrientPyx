using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Indexes the DI-registered strategies by their <see cref="IDisciplineStrategy.Id"/>. Adding a
/// new discipline only requires registering its strategy; this provider needs no changes. An id with
/// no registered strategy (e.g. a missing plugin) — or one of a plugin that is switched off — resolves to a cached
/// <see cref="UnknownDisciplineStrategy"/>. Switching a plugin on or off takes effect on the next lookup.
/// </summary>
public sealed class DisciplineStrategyProvider : IDisciplineStrategyProvider
{
    private readonly IReadOnlyDictionary<DisciplineId, IDisciplineStrategy> _byId;
    private readonly ICourseDistanceCalculator _distance;
    private readonly IPluginCatalog? _plugins;
    private readonly IReadOnlyList<IDisciplineStrategy> _all;
    private readonly Dictionary<DisciplineId, IDisciplineStrategy> _unknown = [];
    private readonly Lock _gate = new();

    public DisciplineStrategyProvider(IEnumerable<IDisciplineStrategy> strategies, ICourseDistanceCalculator distance)
        : this(strategies, distance, plugins: null, errors: null, log: null)
    {
    }

    /// <summary>
    /// Built-ins plus every loaded plugin's disciplines (after the built-ins, in plugin order); only the switched-on
    /// plugins' ones are offered (<see cref="All"/>) and resolved (<see cref="For"/>). A plugin discipline is wrapped
    /// in a <see cref="PluginDisciplineStrategy"/> over its <see cref="Discipline.BasedOn"/> built-in.
    /// </summary>
    public DisciplineStrategyProvider(
        IEnumerable<IDisciplineStrategy> strategies, ICourseDistanceCalculator distance,
        IPluginCatalog? plugins, PluginRuntimeErrors? errors, IActivityLog? log)
    {
        _distance = distance;
        _plugins = plugins;
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
        if (plugins is not null && errors is not null)
        {
            var builtins = byId.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var plugin in plugins.Plugins)
                foreach (var discipline in plugin.Disciplines)
                {
                    var id = new DisciplineId(discipline.Id);
                    var basedOnId = new DisciplineId(discipline.BasedOn.ToString());
                    if (byId.ContainsKey(id) || !builtins.TryGetValue(basedOnId, out var basedOn))
                    {
                        log?.Info($"Plugin '{plugin.Info.Id}': discipline '{id.Value}' skipped (duplicate id or no built-in '{basedOnId.Value}').");
                        continue;
                    }
                    var strategy = new PluginDisciplineStrategy(discipline, plugin.Info.Id, basedOn, errors);
                    byId[id] = strategy;
                    all.Add(strategy);
                }
        }

        _byId = byId;
        _all = all;
    }

    public IReadOnlyList<IDisciplineStrategy> All => _all.Where(IsAvailable).ToList();

    public IDisciplineStrategy For(DisciplineId id)
    {
        if (_byId.TryGetValue(id, out var strategy) && IsAvailable(strategy))
            return strategy;

        lock (_gate)
        {
            if (!_unknown.TryGetValue(id, out var unknown))
                _unknown[id] = unknown = new UnknownDisciplineStrategy(id, _distance);
            return unknown;
        }
    }

    private bool IsAvailable(IDisciplineStrategy strategy) =>
        strategy.PluginId is not { } pluginId || _plugins?.IsEnabled(pluginId) != false;
}
