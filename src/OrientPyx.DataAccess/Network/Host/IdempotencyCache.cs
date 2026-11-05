using System.Collections.Concurrent;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>
/// Remembers the answer of every write call by its <c>Idempotency-Key</c> for ten minutes, so a station that lost the
/// answer and retries gets the stored one and the write runs once. A concurrent duplicate waits on the first call's
/// lock and gets its answer too.
/// </summary>
internal sealed class IdempotencyCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public RpcOutcome? Outcome;
        public readonly DateTime CreatedAt = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private DateTime _lastPrune = DateTime.UtcNow;

    public async Task<RpcOutcome> RunAsync(string key, Func<Task<RpcOutcome>> execute, CancellationToken cancellationToken)
    {
        Prune();
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Outcome is { } stored)
                return stored;
            var outcome = await execute();
            entry.Outcome = outcome;
            return outcome;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPrune < TimeSpan.FromMinutes(1))
            return;
        _lastPrune = now;
        foreach (var (key, entry) in _entries)
            if (now - entry.CreatedAt > Ttl && entry.Outcome is not null)
                _entries.TryRemove(key, out _);
    }
}

/// <summary>The HTTP answer of one RPC call: a status and a JSON body (null ⇒ an empty 204).</summary>
internal sealed record RpcOutcome(int Status, byte[]? Body);
