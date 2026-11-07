namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// Whether this station can reach its host, judged from every exchange with it — an RPC answer, a version poll, the
/// change stream connecting or a single heartbeat line all count as a success; a timeout or a refused/reset connection as
/// a failure. The station is <see cref="IsOffline"/> only when something failed <b>and</b> nothing has succeeded for
/// <see cref="OfflineGrace"/>: a mobile router that drops one long-lived connection (the change stream) while new ones
/// work, or a Wi-Fi blip of a few seconds, never blocks editing. Any later success brings it back at once.
/// <para>
/// It also counts the writes in flight, so the UI can say «зберігається…» about the ones that have waited longer than
/// <see cref="SlowWrite"/> (a write keeps retrying through a short outage instead of failing). Evaluated once a second
/// (<see cref="Start"/>) and right after each success; <see cref="Changed"/> fires when either answer moved.
/// </para>
/// </summary>
public sealed class ConnectionHealth : IDisposable
{
    /// <summary>How long the host may stay silent, with at least one failure, before the station counts as offline.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(15);

    /// <summary>A write in flight for longer than this is shown as «зберігається…».</summary>
    public static readonly TimeSpan SlowWrite = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _writes = [];
    private Timer? _timer;
    private long _nextWrite;
    private DateTimeOffset _lastSuccess;
    private DateTimeOffset? _failingSince;
    private bool _offline;
    private int _delayedWrites;

    public ConnectionHealth(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lastSuccess = _clock();
    }

    /// <summary>True when the host has not answered anything for <see cref="OfflineGrace"/> and an attempt failed.</summary>
    public bool IsOffline
    {
        get { lock (_gate) return _offline; }
    }

    /// <summary>The writes that have been waiting for the host longer than <see cref="SlowWrite"/>.</summary>
    public int DelayedWrites
    {
        get { lock (_gate) return _delayedWrites; }
    }

    /// <summary>What failed last (for the log and the page), or null.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>When the host last answered anything.</summary>
    public DateTimeOffset LastSuccess
    {
        get { lock (_gate) return _lastSuccess; }
    }

    /// <summary>Raised (on a pool thread) when <see cref="IsOffline"/> or <see cref="DelayedWrites"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Starts the once-a-second evaluation (the station's connection is up).</summary>
    public void Start()
    {
        lock (_gate)
        {
            _lastSuccess = _clock();
            _failingSince = null;
            _timer ??= new Timer(_ => Evaluate(), null, Tick, Tick);
        }
    }

    /// <summary>The host answered (any HTTP status counts — it is there).</summary>
    public void ReportSuccess()
    {
        bool wasOffline;
        lock (_gate)
        {
            _lastSuccess = _clock();
            _failingSince = null;
            wasOffline = _offline;
        }
        if (wasOffline)
            Evaluate();
    }

    /// <summary>An exchange failed at the transport level (timeout, refused, reset, silent stream).</summary>
    public void ReportFailure(string reason)
    {
        lock (_gate)
        {
            _failingSince ??= _clock();
            LastFailure = reason;
        }
    }

    /// <summary>Registers a write in flight; pass the id to <see cref="EndWrite"/> when it finished either way.</summary>
    public long BeginWrite()
    {
        lock (_gate)
        {
            var id = ++_nextWrite;
            _writes[id] = _clock();
            return id;
        }
    }

    public void EndWrite(long id)
    {
        bool changed;
        lock (_gate)
        {
            _writes.Remove(id);
            changed = _delayedWrites > 0;
        }
        if (changed)
            Evaluate();
    }

    /// <summary>Recomputes both answers and raises <see cref="Changed"/> when one moved (the timer calls it; tests too).</summary>
    public void Evaluate()
    {
        bool changed;
        lock (_gate)
        {
            var now = _clock();
            var offline = _failingSince is not null && now - _lastSuccess >= OfflineGrace;
            var delayed = _writes.Values.Count(started => now - started >= SlowWrite);
            changed = offline != _offline || delayed != _delayedWrites;
            _offline = offline;
            _delayedWrites = delayed;
        }
        if (changed)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
