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
/// The station's HTTP channel to the host: one <see cref="HttpClient"/> with the session token, the RPC call with
/// its retries (3 attempts, 0.5/1/2 s, on connection failures and timeouts — writes carry an <c>Idempotency-Key</c>
/// so a retry never repeats the write), the error mapping back to the exceptions local code throws, and the job
/// polling of <c>/jobs/{id}</c>. After the retries are exhausted <see cref="Unreachable"/> fires and
/// <see cref="HostUnreachableException"/> is thrown; a 401 fires <see cref="Unauthorized"/>.
/// </summary>
public sealed class RpcChannel : IDisposable
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http;

    public RpcChannel(string endpoint, string token = "")
    {
        Endpoint = endpoint;
        Token = token;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = BaseUri(endpoint),
            Timeout = Timeout.InfiniteTimeSpan, // per-call timeouts below
        };
    }

    /// <summary><c>ip:port</c> of the host.</summary>
    public string Endpoint { get; }

    /// <summary>The bearer token (set after pairing).</summary>
    public string Token { get; set; }

    /// <summary>One best-effort authenticated POST without a body and without retries (e.g. <c>/unpair</c> on leaving).</summary>
    public async Task<bool> TryPostAsync(string path, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
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

    /// <summary>Raised (on the calling thread) when a call failed after every retry.</summary>
    public event Action<Exception>? Unreachable;

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
        var timeout = kind == RpcCallKind.Read ? ReadTimeout : WriteTimeout;

        using var response = await SendWithRetriesAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Wire.RpcPathPrefix}/{iface}/{method}")
            {
                Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(Wire.JsonContentType) } },
            };
            if (key is not null)
                request.Headers.TryAddWithoutValidation(Wire.IdempotencyKeyHeader, key);
            return request;
        }, timeout, cancellationToken);

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

    /// <summary>One GET of a JSON endpoint (with the read timeout and retries).</summary>
    public async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetriesAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ReadTimeout, cancellationToken);
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
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The host did not answer within {timeout.TotalSeconds:0} s.");
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

    private async Task<HttpResponseMessage> SendWithRetriesAsync(Func<HttpRequestMessage> request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelays[attempt - 1], cancellationToken);

            var message = request();
            Authenticate(message);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                return await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cts.Token);
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
        }

        var unreachable = new HostUnreachableException($"The host at {Endpoint} is not reachable: {last?.Message}", last);
        Unreachable?.Invoke(unreachable);
        throw unreachable;
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

    public void Dispose() => _http.Dispose();
}
