using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Disciplines.Plugins;

/// <summary>
/// Collects exceptions thrown by plugin code at run time (Р-3): every one goes to the activity log; the first one
/// per plugin in a session raises <see cref="FirstFailure"/> so the UI can show a single banner.
/// </summary>
public sealed class PluginRuntimeErrors
{
    private readonly IActivityLog _log;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PluginRuntimeErrors(IActivityLog log) => _log = log;

    /// <summary>Raised once per plugin per session: (plugin id, discipline id, built-in fallback id).</summary>
    public event Action<string, DisciplineId, DisciplineId>? FirstFailure;

    public void Report(string pluginId, DisciplineId discipline, DisciplineId fallback, string member, Exception exception)
    {
        _log.Error($"Plugin '{pluginId}' ({discipline.Value}.{member}) failed; using '{fallback.Value}'", exception);

        bool first;
        lock (_gate)
            first = _reported.Add(pluginId);
        if (first)
            FirstFailure?.Invoke(pluginId, discipline, fallback);
    }
}
