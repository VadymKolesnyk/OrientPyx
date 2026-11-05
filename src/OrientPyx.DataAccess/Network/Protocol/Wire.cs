using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.DataAccess.Network.Protocol;

/// <summary>
/// The wire contract between a host and its stations (see <c>docs/network-protocol.md</c>). Every body is JSON
/// serialised with <see cref="BusinessLogic.Serialization.RpcJson.Options"/>. Shared by the host endpoints and the
/// station proxies so the two sides cannot drift apart.
/// </summary>
public static class Wire
{
    /// <summary>The HTTP header carrying the station's session token: <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    public const string AuthorizationHeader = "Authorization";

    /// <summary>A GUID per write call; the host answers a repeat of the same key with the stored response.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>SSE reconnect: the number of the last event the station received.</summary>
    public const string LastEventIdHeader = "Last-Event-ID";

    public const string PairPath = "/pair";
    public const string PingPath = "/ping";
    public const string RpcPathPrefix = "/rpc";
    public const string ChangesPath = "/changes";
    public const string VersionPath = "/version";
    public const string JobsPathPrefix = "/jobs";
    public const string ArchiveDownloadPath = "/files/archive";
    public const string ArchiveUploadPath = "/files/import";
    public const string ArchivePreviewPath = "/files/preview";
    public const string LogPath = "/log";

    /// <summary>Media types used on the wire.</summary>
    public const string JsonContentType = "application/json";
    public const string EventStreamContentType = "text/event-stream";
    public const string ZipContentType = "application/zip";
    public const string OctetStreamContentType = "application/octet-stream";

    /// <summary>The status the host answers a call cancelled by the station with (client closed request).</summary>
    public const int ClientClosedRequestStatus = 499;

    /// <summary>The version string this build reports (the entry assembly's version; <c>0.0.0</c> when unknown).</summary>
    public static string AppVersion { get; } = ReadAppVersion();

    private static string ReadAppVersion()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }

    /// <summary>The interface names in <c>/rpc/{iface}/{method}</c> and the interfaces they stand for.</summary>
    public static readonly IReadOnlyDictionary<string, Type> Interfaces = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["EventStore"] = typeof(IEventStore),
        ["FolderScanner"] = typeof(IEventFolderScanner),
        ["Catalog"] = typeof(IEventCatalogService),
        ["EventFiles"] = typeof(IEventFiles),
        ["Archive"] = typeof(IEventArchiveService),
        ["AppData"] = typeof(IResultsAppData),
    };

    /// <summary>The name of an interface in the RPC path.</summary>
    public static string NameOf(Type iface)
        => Interfaces.First(kv => kv.Value == iface).Key;

    /// <summary>SSE event names.</summary>
    public const string ChangeEvent = "change";
    public const string FullRefreshEvent = "full-refresh";

    /// <summary>The discovery request a station broadcasts and the prefix of the host's answer.</summary>
    public const string DiscoveryRequest = "OPYX?";
    public const string DiscoveryResponsePrefix = "OPYX ";
}

/// <summary>POST /pair request.</summary>
public sealed record PairRequest(
    string Pin,
    string StationName,
    string AppVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Plugins,
    /// <summary>A previously issued token to re-validate instead of the PIN (reconnect after a restart).</summary>
    string? Token = null);

/// <summary>POST /pair response.</summary>
public sealed record PairResponse(
    string Token,
    string HostId,
    string HostName,
    string AppVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Plugins,
    DateTimeOffset ServerTimeUtc,
    /// <summary>The host's app-level «Тип відмітки» (0 = SportIdent, 1 = Sport Time), for the station's warning.</summary>
    int ReadoutType);

/// <summary>GET /ping response (no token needed).</summary>
public sealed record PingResponse(
    string HostId,
    string HostName,
    string AppVersion,
    int ProtocolVersion,
    DateTimeOffset Time);

/// <summary>
/// An error response body (any 4xx/5xx of /rpc). <see cref="Type"/> is the full name of the exception thrown on the
/// host; the station re-throws the same BCL type for the ones callers catch (<see cref="ArgumentException"/>,
/// <see cref="InvalidOperationException"/>, <see cref="KeyNotFoundException"/>, <see cref="OperationCanceledException"/>,
/// <c>DayLockedException</c>…) and <c>RemoteCallException</c> for the rest. <see cref="Kind"/> is a short machine tag:
/// <c>unauthorized</c>, <c>bad-request</c>, <c>not-found</c>, <c>conflict</c>, <c>error</c>.
/// </summary>
public sealed record RpcError(string Type, string Message, string Kind, int? DayNumber = null);

/// <summary>One event of GET /changes (the <c>data:</c> line).</summary>
public sealed record ChangeEvent(string EventPath, DataArea Area, long Version, string Origin);

/// <summary>GET /version?event= response.</summary>
public sealed record VersionResponse(string EventPath, long Version, long Sequence);

/// <summary>The state of a background job (GET /jobs/{id}); <see cref="Result"/> is the method's JSON result once finished.</summary>
public sealed record JobStatus(
    string Id,
    bool Finished,
    string? Stage,
    int Current,
    int Total,
    JsonElement? Result,
    RpcError? Error);

/// <summary>The immediate answer of a job method (<c>/rpc</c> returns this instead of the result).</summary>
public sealed record JobStarted(string JobId);

/// <summary>One batch of a station's log lines (POST /log).</summary>
public sealed record LogBatch(string EventPath, IReadOnlyList<LogLine> Lines);

public sealed record LogLine(DateTimeOffset At, string Level, string Message);

/// <summary>The error kinds of <see cref="RpcError.Kind"/>.</summary>
public static class RpcErrorKinds
{
    public const string BadRequest = "bad-request";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not-found";
    public const string Conflict = "conflict";
    public const string Protocol = "protocol";
    public const string Locked = "locked";
    public const string Cancelled = "cancelled";
    public const string Error = "error";
}

/// <summary>The host's answer to the UDP discovery broadcast.</summary>
public sealed record DiscoveryResponse(
    string HostId,
    string Name,
    string Version,
    IReadOnlyList<string> Ip,
    int Port,
    string? Competition);
