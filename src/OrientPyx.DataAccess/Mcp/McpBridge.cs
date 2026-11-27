using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services.Mcp;

namespace OrientPyx.DataAccess.Mcp;

/// <summary>
/// <c>OrientPyx --mcp-bridge</c> (plan §4.2, М8/М9): an MCP stdio server for the clients that start a server as a command
/// (Claude Desktop) — it opens no database and no window, reads the discovery file on every request and forwards each
/// request to the running window's HTTP server (progress and <c>list_changed</c> come back the same way). While no window
/// serves, it lists the same tools (they are the same binary's) for the mode the window last had and answers every call
/// «OrientPyx не запущено» / «MCP вимкнено». It never starts the program itself.
/// </summary>
public static class McpBridge
{
    public const string Argument = "--mcp-bridge";

    public const string NotRunning =
        "OrientPyx is not running (or the assistant is switched off in it). Ask the user to open OrientPyx and switch the " +
        "assistant on in «Налаштування → Асистент (MCP)», then try again.";

    public const string SwitchedOff =
        "The assistant is switched off in OrientPyx. The user can switch it on in «Налаштування → Асистент (MCP)».";

    /// <summary>Runs on the process's stdin / stdout until the client closes them. Returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> endpointPaths)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            RunAsync(stdin, stdout, endpointPaths, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("OrientPyx MCP bridge: " + ex);
            return 1;
        }
    }

    public static async Task RunAsync(Stream input, Stream output, IReadOnlyList<string> endpointPaths, CancellationToken cancellationToken)
    {
        await using var upstream = new Upstream(endpointPaths);
        McpServer? server = null;
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpInstructions.ServerName, Title = McpInstructions.ServerTitle, Version = McpContext.AppVersion },
            ServerInstructions = McpInstructions.Text,
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
                Prompts = new PromptsCapability(),
                Resources = new ResourcesCapability(),
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = async (ctx, ct) => new ListToolsResult { Tools = await upstream.ListToolsAsync(ct) },
                CallToolHandler = (ctx, ct) => upstream.CallToolAsync(ctx.Params!, ct),
                ListPromptsHandler = async (ctx, ct) => new ListPromptsResult { Prompts = await upstream.ListPromptsAsync(ct) },
                GetPromptHandler = (ctx, ct) => upstream.GetPromptAsync(ctx.Params!, ct),
                ListResourcesHandler = async (ctx, ct) => new ListResourcesResult { Resources = await upstream.ListResourcesAsync(ct) },
                ReadResourceHandler = (ctx, ct) => upstream.ReadResourceAsync(ctx.Params!, ct),
            },
        };

        await using var transport = new StreamServerTransport(input, output, McpInstructions.ServerName);
        server = McpServer.Create(transport, options);
        upstream.Forward = (notification, ct) => server.SendMessageAsync(notification, ct);
        upstream.ToolsChanged = ct => server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
        await server.RunAsync(cancellationToken);
    }

    /// <summary>A protocol tool from the catalogue (the server and the bridge list exactly the same).</summary>
    public static Tool ToProtocolTool(McpToolInfo info) => new()
    {
        Name = info.Name,
        Title = info.Title,
        Description = info.Description,
        InputSchema = JsonSerializer.SerializeToElement(info.InputSchema),
        Annotations = new ToolAnnotations
        {
            Title = info.Title,
            ReadOnlyHint = info.ReadOnly,
            DestructiveHint = info.Destructive,
            IdempotentHint = info.Idempotent,
            OpenWorldHint = false,
        },
    };

    /// <summary>The connection to the serving window, (re)made when its pid / port / token change.</summary>
    private sealed class Upstream(IReadOnlyList<string> endpointPaths) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private McpClient? _client;
        private McpEndpoint? _connectedTo;
        private McpMode? _listedMode;
        private Timer? _watch;

        public Func<JsonRpcNotification, CancellationToken, Task>? Forward { get; set; }

        public Func<CancellationToken, Task>? ToolsChanged { get; set; }

        private (McpEndpoint? Endpoint, bool Alive) Endpoint()
        {
            foreach (var path in endpointPaths)
                if (McpEndpointFile.Read(path) is { } e)
                    return (e, McpEndpointFile.IsAlive(e));
            return (null, false);
        }

        private async Task<McpClient?> ClientAsync(CancellationToken ct)
        {
            var (endpoint, alive) = Endpoint();
            await _gate.WaitAsync(ct);
            try
            {
                if (endpoint is null || !alive)
                {
                    await ResetAsync();
                    return null;
                }
                if (_client is not null && _connectedTo is { } c && c.Pid == endpoint.Pid && c.Port == endpoint.Port && c.Token == endpoint.Token)
                    return _client;

                await ResetAsync();
                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint.Url),
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + endpoint.Token },
                    Name = "orientpyx-bridge",
                });
                var client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "orientpyx-bridge", Version = McpContext.AppVersion },
                }, cancellationToken: ct);
                client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, async (n, t) =>
                {
                    if (Forward is { } f)
                        await f(n, t);
                });
                client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, async (_, t) =>
                {
                    if (ToolsChanged is { } f)
                        await f(t);
                });
                _client = client;
                _connectedTo = endpoint;
                return client;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await ResetAsync();
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ResetAsync()
        {
            if (_client is { } c)
            {
                _client = null;
                _connectedTo = null;
                try
                {
                    await c.DisposeAsync();
                }
                catch (Exception)
                {
                    // the window is gone; nothing to close cleanly
                }
            }
        }

        public async Task<IList<Tool>> ListToolsAsync(CancellationToken ct)
        {
            StartWatch();
            if (await ClientAsync(ct) is { } client)
            {
                try
                {
                    var tools = await client.ListToolsAsync(cancellationToken: ct);
                    _listedMode = Endpoint().Endpoint?.Mode;
                    return tools.Select(t => t.ProtocolTool).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ResetAsync();
                }
            }
            var mode = Endpoint().Endpoint?.Mode ?? McpMode.ReadOnly;
            _listedMode = mode;
            return McpToolCatalog.All.Where(t => McpToolPolicy.IsListed(t.Class, mode)).Select(ToProtocolTool).ToList();
        }

        public async ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken ct)
        {
            if (await ClientAsync(ct) is { } client)
            {
                try
                {
                    return await client.CallToolAsync(request, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not McpProtocolException)
                {
                    await ResetAsync();
                }
            }
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = Endpoint().Endpoint is null ? SwitchedOff : NotRunning }],
                IsError = true,
            };
        }

        public async Task<IList<Prompt>> ListPromptsAsync(CancellationToken ct)
        {
            if (await ClientAsync(ct) is { } client)
            {
                try
                {
                    return (await client.ListPromptsAsync(cancellationToken: ct)).Select(p => p.ProtocolPrompt).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ResetAsync();
                }
            }
            return McpPrompts.All.Select(p => new Prompt
            {
                Name = p.Name,
                Title = p.Description,
                Description = p.Description,
                Arguments = p.Arguments.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required }).ToList(),
            }).ToList();
        }

        public async ValueTask<GetPromptResult> GetPromptAsync(GetPromptRequestParams request, CancellationToken ct)
        {
            if (await ClientAsync(ct) is { } client)
                return await client.GetPromptAsync(request, ct);
            throw new McpException(NotRunning);
        }

        public async Task<IList<Resource>> ListResourcesAsync(CancellationToken ct)
        {
            if (await ClientAsync(ct) is { } client)
            {
                try
                {
                    return (await client.ListResourcesAsync(cancellationToken: ct)).Select(r => r.ProtocolResource).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await ResetAsync();
                }
            }
            return [];
        }

        public async ValueTask<ReadResourceResult> ReadResourceAsync(ReadResourceRequestParams request, CancellationToken ct)
        {
            if (await ClientAsync(ct) is { } client)
                return await client.ReadResourceAsync(request, ct);
            throw new McpException(NotRunning);
        }

        // The window may start, stop or change its mode after the client listed the tools: tell the client to list again.
        private void StartWatch()
        {
            _watch ??= new Timer(_ =>
            {
                var mode = Endpoint() is { Endpoint: { } e, Alive: true } ? e.Mode : Endpoint().Endpoint?.Mode ?? McpMode.ReadOnly;
                if (_listedMode is { } listed && listed != mode && ToolsChanged is { } f)
                {
                    _listedMode = mode;
                    _ = f(CancellationToken.None);
                }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            _watch?.Dispose();
            await ResetAsync();
        }
    }
}
