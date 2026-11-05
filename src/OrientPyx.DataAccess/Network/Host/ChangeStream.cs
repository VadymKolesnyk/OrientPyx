using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>
/// <c>GET /changes</c>: streams the host's <see cref="IDataChangeBus"/> to one station as Server-Sent Events
/// (<c>id:</c> the global event number, <c>event: change</c>, <c>data:</c> the <see cref="ChangeEvent"/>), with a
/// heartbeat comment every 10 s. A station that reconnects sends <c>Last-Event-ID</c>: the missed events are replayed
/// from the bus buffer, or <c>event: full-refresh</c> is sent when it fell behind the buffer.
/// </summary>
internal sealed class ChangeStream
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(10);

    private readonly IDataChangeBus _bus;

    public ChangeStream(IDataChangeBus bus) => _bus = bus;

    public async Task HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = Wire.EventStreamContentType;
        response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var channel = Channel.CreateUnbounded<DataChange>(new UnboundedChannelOptions { SingleReader = true });
        void OnChanged(object? _, DataChange change) => channel.Writer.TryWrite(change);
        _bus.Changed += OnChanged;
        try
        {
            long lastSent = 0;
            if (long.TryParse(context.Request.Headers[Wire.LastEventIdHeader], out var since) && since > 0)
            {
                var missed = _bus.Since(since);
                if (missed is null)
                {
                    await WriteAsync(response, $"event: {Wire.FullRefreshEvent}\ndata: {{}}\n\n", cancellationToken);
                }
                else
                {
                    foreach (var change in missed)
                    {
                        await WriteChangeAsync(response, change, cancellationToken);
                        lastSent = Math.Max(lastSent, change.Sequence);
                    }
                }
            }

            await WriteAsync(response, ": connected\n\n", cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                DataChange change;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Heartbeat);
                try
                {
                    change = await channel.Reader.ReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await WriteAsync(response, ": hb\n\n", cancellationToken);
                    continue;
                }

                if (change.Sequence <= lastSent)
                    continue; // already replayed from the buffer
                await WriteChangeAsync(response, change, cancellationToken);
                lastSent = change.Sequence;
            }
        }
        catch (OperationCanceledException)
        {
            // The station went away.
        }
        catch (IOException)
        {
            // The connection dropped mid-write.
        }
        finally
        {
            _bus.Changed -= OnChanged;
            channel.Writer.TryComplete();
        }
    }

    private static Task WriteChangeAsync(HttpResponse response, DataChange change, CancellationToken cancellationToken)
    {
        var eventPath = change.EventFolderPath == DataChangeBus.AnyEvent
            ? DataChangeBus.AnyEvent
            : EventPathMapper.ToIdentifier(change.EventFolderPath);
        var data = JsonSerializer.Serialize(new ChangeEvent(eventPath, change.Area, change.Version, change.Origin), RpcJson.Options);
        return WriteAsync(response, $"id: {change.Sequence}\nevent: {Wire.ChangeEvent}\ndata: {data}\n\n", cancellationToken);
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken cancellationToken)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
