using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>How a call is treated on the wire: its timeout, whether it carries an idempotency key, whether it is
/// polled as a job.</summary>
public enum RpcCallKind
{
    Read,
    Write,
    Job,
}

/// <summary>
/// How long a call keeps trying: the timeout of each attempt (the first one short, so a request stuck on a connection
/// the router silently dropped is abandoned quickly; later ones longer, for a host that is merely slow), the pauses
/// between attempts, and the window after which the call gives up (counted from the moment it was made).
/// </summary>
public sealed record RpcRetryPolicy(IReadOnlyList<TimeSpan> AttemptTimeouts, IReadOnlyList<TimeSpan> Delays, TimeSpan Window)
{
    /// <summary>Reads: a page load waits through a blip of up to ~20 s instead of failing.</summary>
    public static readonly RpcRetryPolicy Read = new(
        [TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20)],
        [TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)],
        TimeSpan.FromSeconds(20));

    /// <summary>Writes: retried with the same idempotency key for up to a minute, so a short outage loses nothing.</summary>
    public static readonly RpcRetryPolicy Write = new(
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)],
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)],
        TimeSpan.FromSeconds(60));

    public TimeSpan TimeoutOf(int attempt) => AttemptTimeouts[Math.Min(attempt, AttemptTimeouts.Count - 1)];

    public TimeSpan DelayBefore(int attempt) => Delays[Math.Min(Math.Max(attempt - 1, 0), Delays.Count - 1)];
}

/// <summary>
/// The station's HTTP channel to the host: one <see cref="HttpClient"/> with the session token, the RPC call with its
/// retries (<see cref="RpcRetryPolicy"/>: on connection failures and timeouts, until the call's window runs out — writes
/// carry one <c>Idempotency-Key</c> for all attempts, so a retry never repeats the write), the error mapping back to the
/// exceptions local code throws, and the job polling of <c>/jobs/{id}</c>.
/// <para>
/// Built for a network that drops connections silently (a mobile router resetting its connection table): an idle pooled
/// connection is closed after <see cref="IdleConnectionTimeout"/> rather than reused, a timed-out attempt is retried on
/// a fresh connection, and every exchange is reported to <see cref="Health"/>, which decides whether the station is
/// offline. Writes go out one at a time in the order they were made, so a retried write never lands after a later one.
/// A write that still fails after its window raises <see cref="WriteAbandoned"/> and throws
/// <see cref="HostUnreachableException"/>; a 401 fires <see cref="Unauthorized"/>.
/// </para>
/// </summary>
public sealed class RpcChannel : IDisposable
{
    /// <summary>The timeout of the single-shot calls (<see cref="SendOnceAsync"/> callers, the version poll).</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A pooled connection idle for longer is closed instead of reused: after a router dropped its connection
    /// table, an old connection hangs while a new one goes through. A LAN handshake costs next to nothing.</summary>
    public static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http;
    private readonly RpcRetryPolicy _readPolicy;
    private readonly RpcRetryPolicy _writePolicy;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public RpcChannel(string endpoint, string token = "")
        : this(endpoint, CreateHandler(), token)
    {
    }

    /// <summary>A channel over the given handler and retry policies (the tests pass a fake handler and short windows).</summary>
    public RpcChannel(
        string endpoint,
        HttpMessageHandler handler,
        string token = "",
        RpcRetryPolicy? readPolicy = null,
        RpcRetryPolicy? writePolicy = null,
        ConnectionHealth? health = null)
    {
        Endpoint = endpoint;
        Token = token;
        _readPolicy = readPolicy ?? RpcRetryPolicy.Read;
        _writePolicy = writePolicy ?? RpcRetryPolicy.Write;
        Health = health ?? new ConnectionHealth();
        _http = new HttpClient(handler)
        {
            BaseAddress = BaseUri(endpoint),
            Timeout = Timeout.InfiniteTimeSpan, // per-attempt timeouts below
        };
    }

    /// <summary>The handler every station connection uses: short idle lifetime, 5 s connect timeout.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = IdleConnectionTimeout,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary><c>ip:port</c> of the host.</summary>
    public string Endpoint { get; }

    /// <summary>The bearer token (set after pairing).</summary>
    public string Token { get; set; }

    /// <summary>Whether the host is reachable, fed by every exchange of this channel and of the change listener.</summary>
    public ConnectionHealth Health { get; }

    /// <summary>Where the channel writes its diagnostics (a failed attempt, a call that went through only on a retry).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>One best-effort authenticated POST without a body and without retries (e.g. <c>/unpair</c> on leaving).</summary>
    public async Task<bool> TryPostAsync(string path, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The underlying client, for the non-RPC endpoints (<c>/ping</c>, <c>/pair</c>, <c>/files/*</c>).</summary>
    public HttpClient Http => _http;

    /// <summary>Raised when a write could not reach the host within its window — it was not saved. Carries the call
    /// (<c>iface.method</c>) and the last failure.</summary>
    public event Action<string, Exception>? WriteAbandoned;

    /// <summary>Raised when the host no longer accepts the token.</summary>
    public event Action? Unauthorized;

    public static Uri BaseUri(string endpoint) => new($"http://{endpoint.Trim().TrimEnd('/')}/");

    /// <summary>
    /// Calls <c>POST /rpc/{iface}/{method}</c> with the (already stripped) argument array and returns the result
    /// deserialised as <paramref name="resultType"/> (null for a <see cref="Task"/> without a value). A job call is
    /// polled to its end, forwarding progress to <paramref name="progress"/>.
    /// </summary>
    public async Task<object?> CallAsync(
        string iface,
        string method,
        object?[] args,
        RpcCallKind kind,
        Type resultType,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(args, RpcJson.Options);
        var key = kind == RpcCallKind.Read ? null : idempotencyKey ?? Guid.NewGuid().ToString("N");
        var label = $"{iface}.{method}";

        HttpRequestMessage Build()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Wire.RpcPathPrefix}/{iface}/{method}")
            {
                Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(Wire.JsonContentType) } },
            };
            if (key is not null)
                request.Headers.TryAddWithoutValidation(Wire.IdempotencyKeyHeader, key);
            return request;
        }

        using var response = kind == RpcCallKind.Read
            ? await SendWithRetriesAsync(Build, _readPolicy, label, Stopwatch.GetTimestamp(), cancellationToken)
            : await SendWriteAsync(Build, label, cancellationToken);

        await ThrowOnErrorAsync(response, cancellationToken);

        if (kind == RpcCallKind.Job)
        {
            var started = await response.Content.ReadFromJsonAsync<JobStarted>(RpcJson.Options, cancellationToken)
                          ?? throw new RemoteCallException("JobStarted", "The host did not return a job id.");
            return await PollJobAsync(started.JobId, resultType, progress, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NoContent || resultType == typeof(void))
            return null;
        return await response.Content.ReadFromJsonAsync(resultType, RpcJson.Options, cancellationToken);
    }

    /// <summary>One GET of a JSON endpoint (with the read policy).</summary>
    public async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetriesAsync(() => new HttpRequestMessage(HttpMethod.Get, path), _readPolicy,
            $"GET {path}", Stopwatch.GetTimestamp(), cancellationToken);
        await ThrowOnErrorAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return default;
        return await response.Content.ReadFromJsonAsync<T>(RpcJson.Options, cancellationToken);
    }

    /// <summary>Sends a request once, authenticated, with the given timeout and no retries (file transfers, pairing).</summary>
    public async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Authenticate(request);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Health.ReportSuccess();
            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Health.ReportFailure($"{request.RequestUri}: no answer within {timeout.TotalSeconds:0} s");
            throw new TimeoutException($"The host did not answer within {timeout.TotalSeconds:0} s.", ex);
        }
        catch (HttpRequestException ex)
        {
            Health.ReportFailure($"{request.RequestUri}: {ex.Message}");
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            // The station tore this channel down (disconnect / revoked token) while a caller still held it.
            throw new HostUnreachableException($"The connection to {Endpoint} was closed.", ex);
        }
    }

    /// <summary>Reads an error body and throws the exception it stands for (a 2xx returns normally).</summary>
    public async Task ThrowOnErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        RpcError? error = null;
        try
        {
            if (response.Content.Headers.ContentLength != 0)
                error = await response.Content.ReadFromJsonAsync<RpcError>(RpcJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            // Not one of ours — a proxy page, perhaps.
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Unauthorized?.Invoke();
            throw new PairingRefusedException("Unauthorized", error?.Message ?? "The host rejected the station token.");
        }

        error ??= new RpcError($"Http{(int)response.StatusCode}", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", RpcErrorKinds.Error);
        throw ToException(error);
    }

    /// <summary>The local exception an <see cref="RpcError"/> stands for (see <c>docs/network-protocol.md</c>).</summary>
    public static Exception ToException(RpcError error)
    {
        var type = error.Type;
        if (type == typeof(DayLockedException).FullName)
            return new DayLockedException(error.DayNumber ?? 0);
        if (type == typeof(EventArchiveFormatException).FullName)
            return new EventArchiveFormatException(error.Message);
        if (type == typeof(EventFolderInUseException).FullName)
            return new EventFolderInUseException(error.Message, new RemoteCallException(type, error.Message));
        if (type == typeof(ArgumentException).FullName || type == typeof(ArgumentNullException).FullName
            || type == typeof(ArgumentOutOfRangeException).FullName)
            return new ArgumentException(error.Message);
        if (type == typeof(KeyNotFoundException).FullName)
            return new KeyNotFoundException(error.Message);
        if (type == typeof(InvalidOperationException).FullName)
            return new InvalidOperationException(error.Message);
        if (type == typeof(OperationCanceledException).FullName || type == typeof(TaskCanceledException).FullName)
            return new OperationCanceledException(error.Message);
        return new RemoteCallException(type, error.Message);
    }

    // A write waits for the writes made before it (in order), then retries within its window — counted from the moment
    // it was made, so a queue behind a long outage still ends in bounded time. Counted in Health while in flight.
    private async Task<HttpResponseMessage> SendWriteAsync(Func<HttpRequestMessage> request, string label, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var writeId = Health.BeginWrite();
        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                return await SendWithRetriesAsync(request, _writePolicy, label, started, cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (HostUnreachableException ex)
        {
            Log?.Invoke($"Network: {label} NOT saved — the host did not answer for {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s ({ex.InnerException?.Message ?? ex.Message})");
            WriteAbandoned?.Invoke(label, ex);
            throw;
        }
        finally
        {
            Health.EndWrite(writeId);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<HttpRequestMessage> request, RpcRetryPolicy policy, string label, long startedAt, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                var delay = policy.DelayBefore(attempt);
                if (Stopwatch.GetElapsedTime(startedAt) + delay >= policy.Window)
                    break;
                await Task.Delay(delay, cancellationToken);
            }

            // The first attempt is short (a dead connection is given up quickly); a later one never shorter than that,
            // never past the window by more than that.
            var remaining = policy.Window - Stopwatch.GetElapsedTime(startedAt);
            var timeout = attempt == 0
                ? policy.TimeoutOf(0)
                : TimeSpan.FromTicks(Math.Min(policy.TimeoutOf(attempt).Ticks, Math.Max(remaining.Ticks, policy.TimeoutOf(0).Ticks)));

            var message = request();
            Authenticate(message);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cts.Token);
                Health.ReportSuccess();
                if (attempt > 0)
                    Log?.Invoke($"Network: {label} went through on attempt {attempt + 1} after {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:0.0} s");
                return response;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = new TimeoutException($"The host did not answer within {timeout.TotalSeconds:0} s.", ex);
            }
            catch (ObjectDisposedException ex)
            {
                // The channel was torn down meanwhile (the station disconnected): no retries, just "unreachable".
                throw new HostUnreachableException($"The connection to {Endpoint} was closed.", ex);
            }
            finally
            {
                message.Dispose();
            }

            Health.ReportFailure($"{label}: {last.Message}");
            if (attempt == 0)
                Log?.Invoke($"Network: {label} failed ({last.GetType().Name}: {last.Message}) — retrying");
        }

        throw new HostUnreachableException(
            $"The host at {Endpoint} is not reachable ({label}, {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:0} s): {last?.Message}", last);
    }

    private async Task<object?> PollJobAsync(string jobId, Type resultType, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        string? lastStage = null;
        var lastCurrent = -1;
        while (true)
        {
            var status = await GetJsonAsync<JobStatus>($"{Wire.JobsPathPrefix}/{jobId}", cancellationToken)
                         ?? throw new RemoteCallException("JobStatus", "The host lost the job.");

            if (status.Stage is { } stage && (stage != lastStage || status.Current != lastCurrent))
            {
                lastStage = stage;
                lastCurrent = status.Current;
                if (progress is not null && Enum.TryParse<ImportStage>(stage, out var parsed))
                    progress.Report(new ImportProgress(parsed, status.Current, status.Total));
            }

            if (status.Finished)
            {
                if (status.Error is { } error)
                    throw ToException(error);
                if (resultType == typeof(void) || status.Result is not { } result)
                    return null;
                return result.Deserialize(resultType, RpcJson.Options);
            }

            await Task.Delay(JobPollInterval, cancellationToken);
        }
    }

    private void Authenticate(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public void Dispose()
    {
        Health.Dispose();
        _http.Dispose();
    }
}
