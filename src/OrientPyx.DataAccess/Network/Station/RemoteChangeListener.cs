using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// The station's end of <c>GET /changes</c>: reads the host's Server-Sent Events and replays each one on the local
/// <see cref="IDataChangeBus"/> (so pages reload exactly as they do on the host). Two break detectors: 30 s of silence
/// on the stream (the host heartbeats every 10 s) and a failed <c>GET /version</c> poll (every 15 s, which also
/// catches a version the stream missed → a full refresh). Reconnects with a 1/2/5/10 s back-off without limit,
/// resuming from the last event id; after a reconnect every page refreshes once (<see cref="DataArea.Any"/>).
/// </summary>
public sealed class RemoteChangeListener : IDisposable
{
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VersionPoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly RpcChannel _channel;
    private readonly IDataChangeBus _bus;
    private readonly IActivityLog _log;
    private readonly string _hostName;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, long> _knownVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _cts;
    private long _lastEventId;
    private bool _streamUp;
    private bool _pollUp = true;
    private bool _reported = true;

    public RemoteChangeListener(RpcChannel channel, IDataChangeBus bus, IActivityLog log, string hostName)
    {
        _channel = channel;
        _bus = bus;
        _log = log;
        _hostName = hostName;
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            BaseAddress = RpcChannel.BaseUri(channel.Endpoint),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The identifier of the competition the station works on (the one <c>/version</c> is polled for).</summary>
    public string? CurrentEvent { get; set; }

    /// <summary>Raised (on a pool thread) when the stream came up (true) or was lost (false).</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Raised when the host answered 401 — the token is no longer valid.</summary>
    public event Action? Unauthorized;

    /// <summary>The id of the last event received (sent as <c>Last-Event-ID</c> on reconnect).</summary>
    public long LastEventId => Interlocked.Read(ref _lastEventId);

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _ = StreamLoopAsync(_cts.Token);
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task StreamLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Wire.ChangesPath);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _channel.Token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(Wire.EventStreamContentType));
                var last = LastEventId;
                if (last > 0)
                    request.Headers.TryAddWithoutValidation(Wire.LastEventIdHeader, last.ToString());

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Unauthorized?.Invoke();
                    return;
                }
                response.EnsureSuccessStatusCode();

                SetStream(true);
                if (!first)
                    _bus.Replay(new DataChange(DataChangeBus.AnyEvent, DataArea.Any, 0, _hostName));
                first = false;
                attempt = 0;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                await ReadEventsAsync(reader, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_streamUp)
                    _log.Info($"Network: change stream lost ({ex.GetType().Name}: {ex.Message})");
            }

            SetStream(false);
            try
            {
                await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Parses SSE blocks: "id:", "event:", "data:" lines, a blank line ends the event; ": hb" comments keep it alive.
    private async Task ReadEventsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? eventName = null;
        string? data = null;
        long? id = null;
        while (true)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            silence.CancelAfter(Silence);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(silence.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("No heartbeat from the host for 30 s.");
            }
            if (line is null)
                throw new IOException("The host closed the change stream.");

            if (line.Length == 0)
            {
                Dispatch(eventName, data, id);
                eventName = null;
                data = null;
                id = null;
                continue;
            }
            if (line[0] == ':')
                continue;
            if (line.StartsWith("id:", StringComparison.Ordinal) && long.TryParse(line[3..].Trim(), out var parsed))
                id = parsed;
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data = data is null ? line[5..].Trim() : data + "\n" + line[5..].Trim();
        }
    }

    private void Dispatch(string? eventName, string? data, long? id)
    {
        if (id is { } seq)
            Interlocked.Exchange(ref _lastEventId, Math.Max(LastEventId, seq));
        if (eventName is null)
            return;

        if (eventName == Wire.FullRefreshEvent)
        {
            _bus.Replay(new DataChange(DataChangeBus.AnyEvent, DataArea.Any, 0, _hostName));
            return;
        }
        if (eventName != Wire.ChangeEvent || string.IsNullOrEmpty(data))
            return;

        ChangeEvent? change;
        try
        {
            change = JsonSerializer.Deserialize<ChangeEvent>(data, RpcJson.Options);
        }
        catch (JsonException)
        {
            return;
        }
        if (change is null)
            return;

        if (change.Area is not (DataArea.AppData or DataArea.Any) && change.EventPath != DataChangeBus.AnyEvent)
            _knownVersions[change.EventPath] = change.Version;
        _bus.Replay(new DataChange(change.EventPath, change.Area, change.Version, change.Origin));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(VersionPoll, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var current = CurrentEvent;
            if (string.IsNullOrEmpty(current))
                continue;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{Wire.VersionPath}?event={Uri.EscapeDataString(current)}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _channel.Token);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(RpcChannel.ReadTimeout);
                using var response = await _http.SendAsync(request, cts.Token);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Unauthorized?.Invoke();
                    return;
                }
                response.EnsureSuccessStatusCode();
                var version = await response.Content.ReadFromJsonAsync<VersionResponse>(RpcJson.Options, cts.Token);
                SetPoll(true);
                if (version is null)
                    continue;

                if (_knownVersions.TryGetValue(current, out var known) && known != version.Version)
                {
                    _log.Info($"Network: version of '{current}' moved from {known} to {version.Version} without an event — refreshing");
                    _bus.Replay(new DataChange(current, DataArea.Any, version.Version, _hostName));
                }
                _knownVersions[current] = version.Version;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                SetPoll(false);
            }
        }
    }

    private void SetStream(bool up)
    {
        lock (_stateGate)
        {
            _streamUp = up;
            Report();
        }
    }

    private void SetPoll(bool up)
    {
        lock (_stateGate)
        {
            _pollUp = up;
            Report();
        }
    }

    // Reports the combined state only when it changed.
    private void Report()
    {
        var up = _streamUp && _pollUp;
        if (up == _reported)
            return;
        _reported = up;
        ConnectionChanged?.Invoke(up);
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
