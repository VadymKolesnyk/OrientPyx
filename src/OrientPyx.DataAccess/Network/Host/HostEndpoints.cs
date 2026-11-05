using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.BusinessLogic.Services.Store;
using OrientPyx.DataAccess.FileSystem;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>
/// The host's HTTP surface: a hand-written router over the request path (no MVC, no endpoint routing — the empty
/// Kestrel builder carries nothing but the server). Every body is JSON with <see cref="RpcJson.Options"/>; every
/// path but <c>/ping</c> and <c>/pair</c> needs a bearer token issued by <c>/pair</c>.
/// </summary>
internal sealed class HostEndpoints
{
    private readonly IServiceProvider _services;
    private readonly StationRegistry _stations;
    private readonly RpcDispatcher _dispatcher;
    private readonly IdempotencyCache _idempotency;
    private readonly JobRegistry _jobs;
    private readonly ChangeStream _changes;
    private readonly EventPathMapper _mapper;
    private readonly IDataChangeBus _bus;
    private readonly IActivityLog _log;
    private readonly Func<string> _hostId;
    private readonly Func<string> _hostName;
    private readonly Action _changed;

    public HostEndpoints(
        IServiceProvider services,
        StationRegistry stations,
        RpcDispatcher dispatcher,
        IdempotencyCache idempotency,
        JobRegistry jobs,
        ChangeStream changes,
        EventPathMapper mapper,
        IDataChangeBus bus,
        IActivityLog log,
        Func<string> hostId,
        Func<string> hostName,
        Action changed)
    {
        _services = services;
        _stations = stations;
        _dispatcher = dispatcher;
        _idempotency = idempotency;
        _jobs = jobs;
        _changes = changes;
        _mapper = mapper;
        _bus = bus;
        _log = log;
        _hostId = hostId;
        _hostName = hostName;
        _changed = changed;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;
        var cancellationToken = context.RequestAborted;
        try
        {
            if (method == HttpMethods.Get && path == Wire.PingPath)
            {
                await WriteJsonAsync(context, 200, new PingResponse(_hostId(), _hostName(), Wire.AppVersion, NetworkProtocol.Version, DateTimeOffset.UtcNow));
                return;
            }

            if (method == HttpMethods.Post && path == Wire.PairPath)
            {
                await PairAsync(context);
                return;
            }

            var station = _stations.Authenticate(BearerToken(context), context.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
            if (station is null)
            {
                await WriteOutcomeAsync(context, RpcErrors.Outcome(401, RpcErrorKinds.Unauthorized, "Unknown or revoked station token."));
                return;
            }

            if (method == HttpMethods.Post && path.StartsWith(Wire.RpcPathPrefix + "/", StringComparison.Ordinal))
            {
                await RpcAsync(context, station, path[(Wire.RpcPathPrefix.Length + 1)..]);
            }
            else if (method == HttpMethods.Get && path == Wire.ChangesPath)
            {
                await _changes.HandleAsync(context);
            }
            else if (method == HttpMethods.Get && path == Wire.VersionPath)
            {
                await VersionAsync(context, station);
            }
            else if (method == HttpMethods.Get && path.StartsWith(Wire.JobsPathPrefix + "/", StringComparison.Ordinal))
            {
                var status = _jobs.Get(path[(Wire.JobsPathPrefix.Length + 1)..]);
                if (status is null)
                    await WriteOutcomeAsync(context, RpcErrors.Outcome(404, RpcErrorKinds.NotFound, "Unknown job.", typeof(KeyNotFoundException).FullName));
                else
                    await WriteJsonAsync(context, 200, status);
            }
            else if (method == HttpMethods.Post && path == Wire.LogPath)
            {
                await LogAsync(context, station);
            }
            else if (method == HttpMethods.Get && path == Wire.ArchiveDownloadPath)
            {
                await ArchiveDownloadAsync(context, station);
            }
            else if (method == HttpMethods.Post && path == Wire.ArchivePreviewPath)
            {
                await ArchivePreviewAsync(context);
            }
            else if (method == HttpMethods.Post && path == Wire.ArchiveUploadPath)
            {
                await ArchiveImportAsync(context, station);
            }
            else
            {
                await WriteOutcomeAsync(context, RpcErrors.Outcome(404, RpcErrorKinds.NotFound, $"No endpoint {method} {path}.", typeof(KeyNotFoundException).FullName));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The station went away mid-request.
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            _log.Error($"Network host: {method} {path} failed", ex);
            await WriteOutcomeAsync(context, RpcErrors.Outcome(ex));
        }
    }

    // ── /pair

    private async Task PairAsync(HttpContext context)
    {
        var request = await ReadJsonAsync<PairRequest>(context);
        if (request is null)
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(400, RpcErrorKinds.BadRequest, "Missing pair request."));
            return;
        }
        if (request.ProtocolVersion != NetworkProtocol.Version)
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(409, RpcErrorKinds.Protocol,
                $"Protocol version mismatch: host {NetworkProtocol.Version}, station {request.ProtocolVersion}."));
            return;
        }

        var address = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var stationName = string.IsNullOrWhiteSpace(request.StationName) ? address : request.StationName.Trim();
        var cancellationToken = context.RequestAborted;

        string token;
        if (!string.IsNullOrEmpty(request.Token) && await _stations.RevalidateAsync(request.Token, stationName, address, cancellationToken))
        {
            token = request.Token;
            _log.Info($"Network host: station '{stationName}' ({address}) reconnected with its token");
        }
        else
        {
            if (_stations.IsLocked)
            {
                await WriteOutcomeAsync(context, RpcErrors.Outcome(429, RpcErrorKinds.Locked, "Too many wrong PINs; try again in 30 seconds."));
                return;
            }
            if (!_stations.CheckPin(request.Pin))
            {
                _log.Info($"Network host: station '{stationName}' ({address}) sent a wrong PIN");
                await WriteOutcomeAsync(context, _stations.IsLocked
                    ? RpcErrors.Outcome(429, RpcErrorKinds.Locked, "Too many wrong PINs; try again in 30 seconds.")
                    : RpcErrors.Outcome(401, RpcErrorKinds.Unauthorized, "Wrong PIN."));
                return;
            }
            token = (await _stations.IssueAsync(stationName, address, cancellationToken)).Token;
            _log.Action($"Network host: station '{stationName}' ({address}) paired");
        }

        var plugins = _services.GetRequiredService<IPluginCatalog>();
        var enabled = plugins.Plugins.Where(p => plugins.IsEnabled(p.Info.Id)).Select(p => p.Info.Id).Order(StringComparer.Ordinal).ToList();
        var readoutType = await _services.GetRequiredService<IAppStore>().GetReadoutTypeAsync(cancellationToken) ?? 0;
        _changed();
        await WriteJsonAsync(context, 200, new PairResponse(
            token, _hostId(), _hostName(), Wire.AppVersion, NetworkProtocol.Version, enabled, DateTimeOffset.UtcNow, readoutType));
    }

    // ── /rpc/{iface}/{method}

    private async Task RpcAsync(HttpContext context, StationContext station, string route)
    {
        var slash = route.IndexOf('/');
        if (slash <= 0 || slash == route.Length - 1)
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(404, RpcErrorKinds.NotFound, "Expected /rpc/{iface}/{method}.", typeof(KeyNotFoundException).FullName));
            return;
        }
        var iface = route[..slash];
        var method = route[(slash + 1)..];
        var cancellationToken = context.RequestAborted;

        JsonElement[] args;
        try
        {
            args = await ReadJsonAsync<JsonElement[]>(context) ?? [];
        }
        catch (JsonException ex)
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(400, RpcErrorKinds.BadRequest, $"Malformed argument array: {ex.Message}", typeof(ArgumentException).FullName));
            return;
        }

        Task<RpcOutcome> Execute() => _dispatcher.InvokeAsync(iface, method, args, station,
            identifier => _stations.Touch(station.Token, identifier), cancellationToken);

        // Any call that carries a key (the station sends one per write) is answered once per key.
        var key = context.Request.Headers[Wire.IdempotencyKeyHeader].ToString();
        var outcome = !string.IsNullOrEmpty(key)
            ? await _idempotency.RunAsync($"{station.Token}:{key}", Execute, cancellationToken)
            : await Execute();

        _changed();
        await WriteOutcomeAsync(context, outcome);
    }

    // ── /version?event=

    private async Task VersionAsync(HttpContext context, StationContext station)
    {
        var identifier = context.Request.Query["event"].ToString();
        if (string.IsNullOrEmpty(identifier))
        {
            await WriteJsonAsync(context, 200, new VersionResponse(string.Empty, 0, _bus.Sequence));
            return;
        }
        if (!EventIdentifier.IsValid(identifier))
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(400, RpcErrorKinds.BadRequest, "Invalid identifier.", typeof(ArgumentException).FullName));
            return;
        }
        _stations.Touch(station.Token, identifier);
        var path = EventPathMapper.ToPath(await _mapper.EventsPathAsync(context.RequestAborted), identifier);
        await WriteJsonAsync(context, 200, new VersionResponse(identifier, _bus.CurrentVersion(path), _bus.Sequence));
    }

    // ── /log

    private async Task LogAsync(HttpContext context, StationContext station)
    {
        var batch = await ReadJsonAsync<LogBatch>(context);
        if (batch is not null)
        {
            foreach (var line in batch.Lines)
            {
                var text = $"[{station.Name}] {line.Message}";
                if (string.Equals(line.Level, "ACTION", StringComparison.OrdinalIgnoreCase))
                    _log.Action(text);
                else
                    _log.Info(text);
            }
        }
        context.Response.StatusCode = 204;
    }

    // ── /files/*

    private EventArchiveService LocalArchive => _services.GetRequiredService<EventArchiveService>();

    private async Task ArchiveDownloadAsync(HttpContext context, StationContext station)
    {
        var identifier = context.Request.Query["identifier"].ToString();
        var cancellationToken = context.RequestAborted;
        var temp = TempFile(".opyx");
        try
        {
            await LocalArchive.ExportAsync(identifier, temp, cancellationToken);
            _stations.Touch(station.Token, identifier);
            context.Response.StatusCode = 200;
            context.Response.ContentType = Wire.ZipContentType;
            await using var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            context.Response.ContentLength = stream.Length;
            await stream.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(ex));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task ArchivePreviewAsync(HttpContext context)
    {
        var temp = await SaveBodyAsync(context, ".opyx");
        try
        {
            var preview = await LocalArchive.PreviewImportAsync(temp, context.RequestAborted);
            await WriteJsonAsync(context, 200, preview);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(ex));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task ArchiveImportAsync(HttpContext context, StationContext station)
    {
        var identifier = context.Request.Query["identifier"].ToString();
        var overwrite = bool.TryParse(context.Request.Query["overwrite"], out var o) && o;
        var temp = await SaveBodyAsync(context, ".opyx");
        try
        {
            var result = await LocalArchive.ImportAsync(temp, identifier, overwrite, context.RequestAborted);
            _stations.Touch(station.Token, identifier);
            _log.Action($"Network host: station '{station.Name}' imported competition '{identifier}'");
            _changed();
            await WriteJsonAsync(context, 200, result with { Competition = EventPathMapper.Outgoing(result.Competition) });
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await WriteOutcomeAsync(context, RpcErrors.Outcome(ex));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // ── helpers

    private static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers[Wire.AuthorizationHeader].ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context)
    {
        if (context.Request.ContentLength == 0)
            return default;
        return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, RpcJson.Options, context.RequestAborted);
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, int status, T value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, RpcJson.Options);
        await WriteOutcomeAsync(context, new RpcOutcome(status, body));
    }

    private static async Task WriteOutcomeAsync(HttpContext context, RpcOutcome outcome)
    {
        context.Response.StatusCode = outcome.Status;
        if (outcome.Body is null)
            return;
        context.Response.ContentType = Wire.JsonContentType;
        await context.Response.Body.WriteAsync(outcome.Body, context.RequestAborted);
    }

    private static async Task<string> SaveBodyAsync(HttpContext context, string extension)
    {
        var temp = TempFile(extension);
        await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await context.Request.Body.CopyToAsync(file, context.RequestAborted);
        return temp;
    }

    private static string TempFile(string extension)
        => Path.Combine(Path.GetTempPath(), $"opyx-host-{Guid.NewGuid():N}{extension}");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A leftover temp file is harmless.
        }
    }
}
