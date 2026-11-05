using System.Collections.Concurrent;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Services.Network;

/// <summary>
/// Default <see cref="IDataChangeBus"/>: a version counter per competition, one global monotonic event number and a ring
/// buffer of the last events (so a reconnecting station can ask «what did I miss since event N»).
/// </summary>
public sealed class DataChangeBus : IDataChangeBus
{
    /// <summary>The event path of app-level changes (<see cref="DataArea.AppData"/>) — they concern every competition.</summary>
    public const string AnyEvent = "*";

    private const int BufferSize = 512;

    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Queue<DataChange> _buffer = new();
    private long _sequence;

    public long Sequence => Interlocked.Read(ref _sequence);

    public event EventHandler<DataChange>? Changed;

    public long CurrentVersion(string eventFolderPath)
        => _versions.TryGetValue(eventFolderPath ?? string.Empty, out var v) ? v : 0;

    public DataChange Publish(string eventFolderPath, DataArea area, string origin)
    {
        eventFolderPath ??= string.Empty;
        var version = area == DataArea.AppData
            ? 0
            : _versions.AddOrUpdate(eventFolderPath, 1, (_, v) => v + 1);
        var change = new DataChange(eventFolderPath, area, version, origin ?? string.Empty, Interlocked.Increment(ref _sequence));
        Remember(change);
        Changed?.Invoke(this, change);
        return change;
    }

    public void Replay(DataChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Area != DataArea.AppData && change.Area != DataArea.Any)
            _versions[change.EventFolderPath] = change.Version;
        var sequence = Interlocked.Increment(ref _sequence);
        var local = change with { Sequence = sequence };
        Remember(local);
        Changed?.Invoke(this, local);
    }

    public IReadOnlyList<DataChange>? Since(long sequence)
    {
        lock (_gate)
        {
            if (sequence >= _sequence)
                return [];
            var oldest = _buffer.Count == 0 ? _sequence : _buffer.Peek().Sequence;
            // The caller's last event must still be in the buffer (or be the one just before it), else we cannot
            // prove nothing was lost.
            if (sequence < oldest - 1)
                return null;
            return _buffer.Where(c => c.Sequence > sequence).ToList();
        }
    }

    private void Remember(DataChange change)
    {
        lock (_gate)
        {
            _buffer.Enqueue(change);
            while (_buffer.Count > BufferSize)
                _buffer.Dequeue();
        }
    }
}

/// <summary>
/// The name of the machine that caused the write currently in progress, flowing with the async call: the host's RPC
/// dispatcher sets it to the calling station's name for the duration of a call, so the change the write publishes is
/// stamped with that station and not with the host. Null ⇒ the local <c>IWorkspaceMode.SelfOrigin</c>.
/// </summary>
public static class DataChangeOrigin
{
    private static readonly AsyncLocal<string?> Origin = new();

    public static string? Current
    {
        get => Origin.Value;
        set => Origin.Value = value;
    }
}
