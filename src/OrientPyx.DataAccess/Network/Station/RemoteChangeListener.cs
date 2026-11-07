using System.Collections.Concurrent;
using System.Diagnostics;
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
/// <see cref="IDataChangeBus"/> (so pages reload exactly as they do on the host).
/// <para>
/// The stream is only a notification channel, so losing it is not «offline»: a mobile router can drop this one
/// long-lived connection while new ones work. When nothing (not even a heartbeat) arrives for about three heartbeat
/// intervals (<see cref="Wire.HeartbeatHeader"/>; 30 s for an older host), the stream is reopened quietly with a
/// 0.5/1/2/5 s back-off and resumes from <c>Last-Event-ID</c> — the host replays what was missed or answers
/// <c>full-refresh</c>. Every line read and every connect counts as a success in the channel's
/// <see cref="ConnectionHealth"/>, every break as a failure; the health alone decides whether the station is offline.
/// </para>
/// <para>
/// A <c>GET /version</c> poll every 5 s (on a fresh connection, one immediate retry) is the safety net: it keeps the
/// health informed while the stream is down and catches a version the stream missed (→ a refresh of that competition).
/// </para>
/// </summary>
public sealed class RemoteChangeListener : IDisposable
{
    /// <summary>The silence before a reopen when the host does not announce its heartbeat (an older host: 10 s).</summary>
    private static readonly TimeSpan LegacySilence = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinSilence = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VersionPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A version seen ahead of the stream is re-checked after this pause before it forces a refresh: the
    /// event may simply still be on its way.</summary>
    private static readonly TimeSpan VersionGrace = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly RpcChannel _channel;
    private readonly IDataChangeBus _bus;
    private readonly IActivityLog _log;
    private readonly string _hostName;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, long> _knownVersions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private long _lastEventId;
    private string? _epoch;
    private bool _pollFailing;

    public RemoteChangeListener(RpcChannel channel, IDataChangeBus bus, IActivityLog log, string hostName)
    {
        _channel = channel;
        _bus = bus;
        _log = log;
        _hostName = hostName;
        _http = new HttpClient(RpcChannel.CreateHandler())
        {
            BaseAddress = RpcChannel.BaseUri(channel.Endpoint),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The identifier of the competition the station works on (the one <c>/version</c> is polled for).</summary>
    public string? CurrentEvent { get; set; }

    /// <summary>Raised when the host answered 401 — the token is no longer valid.</summary>
    public event Action? Unauthorized;

    /// <summary>The id of the last event received (sent as <c>Last-Event-ID</c> on reconnect).</summary>
    public long LastEventId => Interlocked.Read(ref _lastEventId);

    private ConnectionHealth Health => _channel.Health;

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
        long? lostAt = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var opened = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Wire.ChangesPath);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _channel.Token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(Wire.EventStreamContentType));
                if (!first)
                {
                    // Always on a reconnect (even 0): the host then replays everything it has since that number.
                    request.Headers.TryAddWithoutValidation(Wire.LastEventIdHeader, LastEventId.ToString());
                    if (_epoch is not null)
                        request.Headers.TryAddWithoutValidation(Wire.StreamEpochHeader, _epoch);
                }

                HttpResponseMessage response;
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // Only the wait for the headers is bounded; the stream itself is watched by the silence timer.
                    connect.CancelAfter(ConnectTimeout);
                    try
                    {
                        response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No answer to /changes within {ConnectTimeout.TotalSeconds:0} s.", ex);
                    }
                }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Unauthorized?.Invoke();
                        return;
                    }
                    response.EnsureSuccessStatusCode();

                    Health.ReportSuccess();
                    opened = true;
                    var epoch = Header(response, Wire.StreamEpochHeader);
                    if (!first)
                    {
                        // A host of this build resumes (or says full-refresh) by itself; an older one cannot be trusted
                        // to have replayed what was missed — refresh every page once.
                        if (epoch is null)
                            _bus.Replay(new DataChange(DataChangeBus.AnyEvent, DataArea.Any, 0, _hostName));
                        var away = lostAt is { } t ? Stopwatch.GetElapsedTime(t).TotalSeconds : 0;
                        _log.Info($"Network: change stream reopened after {away:0.0} s (from event #{LastEventId})");
                    }
                    _epoch = epoch;
                    first = false;
                    attempt = 0;
                    lostAt = null;

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);
                    await ReadEventsAsync(reader, SilenceOf(response), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var reason = ex is TimeoutException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                Health.ReportFailure($"change stream: {reason}");
                if (opened || (lostAt is null && !first))
                    _log.Info($"Network: change stream lost ({reason}) — reopening");
                lostAt ??= Stopwatch.GetTimestamp();
            }

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

    // About three heartbeats of silence (10 s for the 3 s heartbeat); the old fixed 30 s for a host that does not say.
    private static TimeSpan SilenceOf(HttpResponseMessage response)
    {
        if (!int.TryParse(Header(response, Wire.HeartbeatHeader), out var seconds) || seconds <= 0)
            return LegacySilence;
        var silence = TimeSpan.FromSeconds(seconds * 3 + 1);
        return silence < MinSilence ? MinSilence : silence;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    // Parses SSE blocks: "id:", "event:", "data:" lines, a blank line ends the event; ": hb" comments keep it alive.
    private async Task ReadEventsAsync(StreamReader reader, TimeSpan silence, CancellationToken cancellationToken)
    {
        string? eventName = null;
        string? data = null;
        long? id = null;
        while (true)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(silence);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(timer.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No heartbeat from the host for {silence.TotalSeconds:0} s.");
            }
            if (line is null)
                throw new IOException("The host closed the change stream.");

            Health.ReportSuccess();
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
        if (eventName == Wire.FullRefreshEvent)
        {
            // The host could not replay what was missed (or restarted): numbering starts over from the id it sends next.
            Interlocked.Exchange(ref _lastEventId, 0);
            _knownVersions.Clear();
            _bus.Replay(new DataChange(DataChangeBus.AnyEvent, DataArea.Any, 0, _hostName));
        }
        if (id is { } seq)
            Interlocked.Exchange(ref _lastEventId, Math.Max(LastEventId, seq));
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

            VersionResponse? version = null;
            Exception? failure = null;
            // One immediate retry: the first attempt's connection is gone by then, the second one is new.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    version = await PollOnceAsync(current, cancellationToken);
                    failure = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    Unauthorized?.Invoke();
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            if (failure is not null)
            {
                var reason = $"version poll: {failure.Message}";
                Health.ReportFailure(reason);
                if (!_pollFailing)
                    _log.Info($"Network: {reason}");
                _pollFailing = true;
                continue;
            }

            Health.ReportSuccess();
            _pollFailing = false;
            if (version is null)
                continue;

            if (_knownVersions.TryGetValue(current, out var known) && known != version.Version)
            {
                // The change event may still be on its way down the stream; refresh only if it does not arrive.
                try
                {
                    await Task.Delay(VersionGrace, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (_knownVersions.TryGetValue(current, out var now) && now == version.Version)
                    continue;
                _log.Info($"Network: version of '{current}' moved from {known} to {version.Version} without an event — refreshing");
                _bus.Replay(new DataChange(current, DataArea.Any, version.Version, _hostName));
            }
            _knownVersions[current] = version.Version;
        }
    }

    private async Task<VersionResponse?> PollOnceAsync(string identifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Wire.VersionPath}?event={Uri.EscapeDataString(identifier)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _channel.Token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(VersionTimeout);
        try
        {
            using var response = await _http.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException();
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<VersionResponse>(RpcJson.Options, cts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer within {VersionTimeout.TotalSeconds:0} s", ex);
        }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
