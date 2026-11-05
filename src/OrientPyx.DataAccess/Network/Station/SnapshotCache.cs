using System.Collections.Concurrent;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Network;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// Caches <c>GetDaySnapshotAsync</c> on the station — the heaviest call, made on every page load and result
/// computation (П12). An entry is dropped when the host's change stream reports a change to that competition
/// (or a full refresh) and after any write of this station to it. The cached record is immutable, so handing the
/// same instance out twice is safe.
/// </summary>
public sealed class SnapshotCache : IDisposable
{
    private readonly IDataChangeBus _bus;
    private readonly ConcurrentDictionary<(string EventPath, Guid DayId), EventDaySnapshot> _entries = new();

    public SnapshotCache(IDataChangeBus bus)
    {
        _bus = bus;
        _bus.Changed += OnChanged;
    }

    public async Task<EventDaySnapshot> GetAsync(string eventPath, Guid dayId, Func<Task<EventDaySnapshot>> load)
    {
        var key = (Normalize(eventPath), dayId);
        if (_entries.TryGetValue(key, out var cached))
            return cached;
        var snapshot = await load().ConfigureAwait(false);
        _entries[key] = snapshot;
        return snapshot;
    }

    /// <summary>Drops every cached day of a competition (null ⇒ of every competition).</summary>
    public void Invalidate(string? eventPath)
    {
        if (eventPath is null || eventPath == DataChangeBus.AnyEvent)
        {
            _entries.Clear();
            return;
        }
        var path = Normalize(eventPath);
        foreach (var key in _entries.Keys)
            if (key.EventPath == path)
                _entries.TryRemove(key, out _);
    }

    private void OnChanged(object? sender, DataChange change)
    {
        if (change.Area == DataArea.AppData)
            return; // app-level tables are not part of the snapshot
        Invalidate(change.Area == DataArea.Any ? null : change.EventFolderPath);
    }

    private static string Normalize(string eventPath) => (eventPath ?? string.Empty).ToUpperInvariant();

    public void Dispose() => _bus.Changed -= OnChanged;
}
