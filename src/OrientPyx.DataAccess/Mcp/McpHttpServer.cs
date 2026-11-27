using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services.Mcp;

namespace OrientPyx.DataAccess.Mcp;

/// <summary>Test / embedding knobs of the server.</summary>
public sealed class McpHostOptions
{
    /// <summary>Listen on this port instead of the remembered one (0 = any free port; tests).</summary>
    public int? PortOverride { get; init; }

    /// <summary>The discovery file (default: in the data root).</summary>
    public string? EndpointPath { get; init; }

    /// <summary>How often a window that waits behind another one checks whether it may take over.</summary>
    public TimeSpan TakeoverEvery { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The local MCP server (plan §3.1): MCP Streamable HTTP on <c>http://127.0.0.1:&lt;port&gt;/mcp</c> in the window's own
/// process, through the official C# SDK on a <see cref="WebApplication"/> of its own (no Generic Host in the app), the
/// tools served from shared collections so a mode change reaches open sessions as <c>notifications/tools/list_changed</c>.
/// One server per computer: a second window of the same data root waits («обслуговує інше вікно») and takes over when
/// the first one closes (М11).
/// </summary>
public sealed class McpHttpServer : IMcpServer, IAsyncDisposable
{
    public const int DefaultPort = 47833;
    private const int PortProbeRange = 20;

    private readonly IAppStore _appStore;
    private readonly McpToolDispatcher _dispatcher;
    private readonly McpRuntime _runtime;
    private readonly McpConfirmations _confirmations;
    private readonly ISessionService _session;
    private readonly IServiceProvider _services;
    private readonly IActivityLog? _log;
    private readonly McpHostOptions _options;
    private readonly string _endpointPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly McpServerPrimitiveCollection<McpServerTool> _tools = new();
    private readonly McpServerPrimitiveCollection<McpServerPrompt> _prompts = new();
    private readonly McpServerResourceCollection _resources = new();
    private readonly Dictionary<string, McpServerTool> _toolObjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clients = new();
    private WebApplication? _app;
    private Timer? _takeover;
    private McpSettings? _settings;

    public McpHttpServer(
        IAppStore appStore,
        McpToolDispatcher dispatcher,
        McpRuntime runtime,
        McpConfirmations confirmations,
        ISessionService session,
        IServiceProvider services,
        IActivityLog? log = null,
        McpHostOptions? options = null)
    {
        _appStore = appStore;
        _dispatcher = dispatcher;
        _runtime = runtime;
        _confirmations = confirmations;
        _session = session;
        _services = services;
        _log = log;
        _options = options ?? new McpHostOptions();
        _endpointPath = _options.EndpointPath ?? McpEndpointFile.DefaultPath;

        foreach (var info in McpToolCatalog.All)
            _toolObjects[info.Name] = new SdkTool(info, this);
        foreach (var prompt in McpPrompts.All)
            _prompts.Add(new SdkPrompt(prompt, this));
        _resources.Add(McpServerResource.Create(
            async (CancellationToken ct) => (await _dispatcher.CallAsync("describe_data", McpArgs.Empty, McpCall.None, ct)).Text,
            new McpServerResourceCreateOptions
            {
                UriTemplate = "orientpyx://schema",
                Name = "schema",
                Title = "OrientPyx data schema",
                Description = "The analytics tables of the open competition (as describe_data).",
                MimeType = "application/json",
            }));

        _runtime.ModeChanged += (_, _) => RefreshTools();
        _session.SessionChanged += (_, _) => RewriteEndpoint();
        RefreshTools();
    }

    public McpServerState State { get; private set; } = McpServerState.Off;
    public int? Port { get; private set; }
    public string? Url => State == McpServerState.Running && Port is { } p ? $"http://127.0.0.1:{p}/mcp" : null;
    public McpMode Mode => _runtime.Mode;
    public string? ServedBy { get; private set; }
    public string? Error { get; private set; }
    public int ConnectedClients => _clients.Count(c => DateTimeOffset.Now - c.Value < TimeSpan.FromMinutes(10));

    public event EventHandler? Changed;

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ApplyCoreAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State = McpServerState.Failed;
            Error = ex.Message;
            _log?.Error("MCP: applying the settings", ex);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyCoreAsync(CancellationToken cancellationToken)
    {
        var settings = await _appStore.GetMcpSettingsAsync(cancellationToken);
        _settings = settings;
        if (!settings.Enabled)
        {
            await StopServerAsync();
            StopTakeoverWatch();
            _confirmations.CancelAll();
            _runtime.ResetSession();
            _runtime.SetMode(settings.Mode);
            if (McpEndpointFile.Read(_endpointPath) is { } e && e.Pid == McpEndpointFile.Self().Pid)
                McpEndpointFile.Delete(_endpointPath);
            State = McpServerState.Off;
            ServedBy = null;
            Error = null;
            return;
        }

        _runtime.SetMode(settings.Mode);
        if (_app is not null)
        {
            RewriteEndpoint();
            return;
        }

        if (string.IsNullOrEmpty(settings.Token))
        {
            settings.Token = NewToken();
            await _appStore.SaveMcpSettingsAsync(settings, cancellationToken);
        }

        if (McpEndpointFile.Read(_endpointPath) is { } other && other.Pid != McpEndpointFile.Self().Pid && McpEndpointFile.IsAlive(other))
        {
            State = McpServerState.ServedElsewhere;
            ServedBy = other.Competition;
            StartTakeoverWatch();
            return;
        }

        if (_options.PortOverride is null && settings.Port == 0)
        {
            settings.Port = PickPort();
            await _appStore.SaveMcpSettingsAsync(settings, cancellationToken);
        }

        var port = _options.PortOverride ?? settings.Port;
        var app = Build(port, settings.Token);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            await app.DisposeAsync();
            State = McpServerState.PortBusy;
            Port = port;
            Error = $"Port {port} is taken by another program: {ex.Message}";
            _log?.Info("MCP: " + Error);
            return;
        }

        _app = app;
        Port = port == 0 ? BoundPort(app) : port;
        State = McpServerState.Running;
        ServedBy = null;
        Error = null;
        StopTakeoverWatch();
        RewriteEndpoint();
        _log?.Info($"MCP: serving on http://127.0.0.1:{Port}/mcp (mode {settings.Mode})");
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            StopTakeoverWatch();
            _confirmations.CancelAll();
            await StopServerAsync();
            State = McpServerState.Off;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task StopServerAsync()
    {
        if (_app is not { } app)
            return;
        _app = null;
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(stop.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        await app.DisposeAsync();
        _clients.Clear();
    }

    // ── tools by mode

    private void RefreshTools()
    {
        var listed = _dispatcher.ListedTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        lock (_toolObjects)
        {
            foreach (var (name, tool) in _toolObjects)
            {
                var present = _tools.TryGetPrimitive(name, out _);
                if (listed.Contains(name) && !present)
                    _tools.Add(tool);
                else if (!listed.Contains(name) && present)
                    _tools.Remove(tool);
            }
        }
    }

    // ── discovery file and takeover

    private void RewriteEndpoint()
    {
        if (_app is null || Port is not { } port || _settings is not { } s)
            return;
        try
        {
            var (pid, start) = McpEndpointFile.Self();
            McpEndpointFile.Write(_endpointPath, new McpEndpoint(port, s.Token, pid, start, _runtime.Mode, McpContext.AppVersion, _session.CurrentEvent?.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Error("MCP: writing the discovery file", ex);
        }
    }

    private void StartTakeoverWatch()
    {
        _takeover ??= new Timer(_ => _ = TryTakeOverAsync(), null, _options.TakeoverEvery, _options.TakeoverEvery);
    }

    private void StopTakeoverWatch()
    {
        _takeover?.Dispose();
        _takeover = null;
    }

    private async Task TryTakeOverAsync()
    {
        if (State != McpServerState.ServedElsewhere)
            return;
        var other = McpEndpointFile.Read(_endpointPath);
        if (other is not null && McpEndpointFile.IsAlive(other))
        {
            if (other.Competition != ServedBy)
            {
                ServedBy = other.Competition;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        _log?.Info("MCP: the serving window is gone; taking over");
        await ApplySettingsAsync();
    }

    // ── HTTP

    private WebApplication Build(int port, string token)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port);
            k.AddServerHeader = false;
        });
        builder.Services.AddRouting();
        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = McpInstructions.ServerName, Title = McpInstructions.ServerTitle, Version = McpContext.AppVersion };
                o.ServerInstructions = McpInstructions.Text;
                o.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = true },
                    Prompts = new PromptsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { ListChanged = false },
                };
            })
            .WithHttpTransport(t =>
            {
                // Sessions on purpose: every client today (Claude Desktop / Code, Gemini CLI, VS Code) uses the initialize
                // handshake, and only a session gets tools/list_changed after a mode switch, progress and the per-session tool
                // list. The SDK marks the idle timeout obsolete only because sessionless 2026-07-28 clients have none (MCP9006);
                // 12 h keeps a chat left open for a competition day from losing its session (the default is 2 h).
                t.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
#pragma warning disable MCP9006
                t.IdleTimeout = TimeSpan.FromHours(12);
#pragma warning restore MCP9006
                t.ConfigureSessionOptions = (http, options, ct) =>
                {
                    options.ToolCollection = _tools;
                    options.PromptCollection = _prompts;
                    options.ResourceCollection = _resources;
                    return Task.CompletedTask;
                };
            });

        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            if (Reject(http, token) is { } status)
            {
                http.Response.StatusCode = status;
                if (status == StatusCodes.Status401Unauthorized)
                    http.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            _clients[http.Request.Headers["Mcp-Session-Id"].ToString() is { Length: > 0 } sid ? sid : http.Request.Headers.UserAgent.ToString()] = DateTimeOffset.Now;
            await next();
        });
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>
    /// The request checks of plan §8 / М14: a browser page (an Origin that is not this computer) gets 403, a request
    /// for another host name (DNS rebinding) 421, no / a wrong bearer token 401. CLI clients send no Origin — allowed.
    /// </summary>
    internal static int? Reject(HttpContext http, string token)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsLoopbackOrigin(origin))
            return StatusCodes.Status403Forbidden;
        var host = http.Request.Host.Host;
        if (!(host.Equals("127.0.0.1", StringComparison.Ordinal) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
              || host.Equals("[::1]", StringComparison.Ordinal) || host.Equals("::1", StringComparison.Ordinal)))
            return StatusCodes.Status421MisdirectedRequest;
        var auth = http.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth[bearer.Length..].Trim()), Encoding.UTF8.GetBytes(token)))
            return StatusCodes.Status401Unauthorized;
        return null;
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && (uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1");

    private static int BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (var address in addresses ?? [])
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                return uri.Port;
        throw new InvalidOperationException("Kestrel reported no bound address.");
    }

    /// <summary>47833, else the next free port (chosen once and remembered — plan §3.1).</summary>
    private static int PickPort()
    {
        for (var port = DefaultPort; port < DefaultPort + PortProbeRange; port++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }
        return DefaultPort;
    }

    /// <summary>32 random bytes, base64url.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── SDK adapters

    private IMcpUi? Ui => _services.GetService<IMcpUi>();

    private async Task<CallToolResult> InvokeAsync(McpToolInfo info, RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var p = context.Params;
        var args = p?.Arguments is { } a
            ? new McpArgs(System.Text.Json.JsonSerializer.SerializeToElement(a))
            : McpArgs.Empty;
        var server = context.Server;
        var token = p?.ProgressToken;
        var step = 0;
        var call = new McpCall
        {
            Client = server.ClientInfo?.Name,
            Progress = async message =>
            {
                if (token is { } t)
                    await server.NotifyProgressAsync(t, new ModelContextProtocol.ProgressNotificationValue { Progress = ++step, Message = message }, cancellationToken: cancellationToken);
            },
        };
        var result = await _dispatcher.CallAsync(info.Name, args, call, cancellationToken);
        return new CallToolResult { Content = [new TextContentBlock { Text = result.Text }], IsError = result.IsError };
    }

    private sealed class SdkTool(McpToolInfo info, McpHttpServer server) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = info.Name,
            Title = info.Title,
            Description = info.Description,
            InputSchema = System.Text.Json.JsonSerializer.SerializeToElement(info.InputSchema),
            Annotations = new ToolAnnotations
            {
                Title = info.Title,
                ReadOnlyHint = info.ReadOnly,
                DestructiveHint = info.Destructive,
                IdempotentHint = info.Idempotent,
                OpenWorldHint = false,
            },
        };

        public override IReadOnlyList<object> Metadata { get; } = [];

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
            await server.InvokeAsync(info, request, cancellationToken);
    }

    private sealed class SdkPrompt(McpPromptInfo info, McpHttpServer server) : McpServerPrompt
    {
        public override Prompt ProtocolPrompt => new()
        {
            Name = info.Name,
            Title = server.Ui?.Text(info.TitleKey) ?? info.Description,
            Description = info.Description,
            Arguments = info.Arguments.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required }).ToList(),
        };

        public override IReadOnlyList<object> Metadata { get; } = [];

        public override ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> request, CancellationToken cancellationToken = default)
        {
            var text = info.Template;
            foreach (var (name, _, _) in info.Arguments)
            {
                var value = request.Params?.Arguments is { } a && a.TryGetValue(name, out var v) ? v.ToString() : string.Empty;
                text = text.Replace("{" + name + "}", string.IsNullOrWhiteSpace(value) ? "(ask me)" : value, StringComparison.Ordinal);
            }
            return ValueTask.FromResult(new GetPromptResult
            {
                Description = info.Description,
                Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
            });
        }
    }
}
