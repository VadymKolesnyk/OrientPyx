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
/// heartbeat comment every <see cref="Heartbeat"/> (announced in <see cref="Wire.HeartbeatHeader"/>) — frequent, because
/// a mobile router may drop a long-lived connection silently and the station only notices by the missing heartbeat.
/// <para>
/// A station that reconnects sends <c>Last-Event-ID</c> (and the <see cref="Wire.StreamEpochHeader"/> it saw): the
/// missed events are replayed from the bus buffer, or <c>event: full-refresh</c> is sent when it fell behind the buffer
/// or the host restarted since (another epoch). Whenever the station's position is not known exactly, an <c>id:</c>
/// line with the current number follows, so its next reconnect can resume.
/// </para>
/// </summary>
internal sealed class ChangeStream
{
    /// <summary>The heartbeat interval (whole seconds — it goes into a header).</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(3);

    private readonly IDataChangeBus _bus;

    /// <summary>One per host process: the bus numbers events from zero again after a restart.</summary>
    private readonly string _epoch = Guid.NewGuid().ToString("N");

    public ChangeStream(IDataChangeBus bus) => _bus = bus;

    public async Task HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = Wire.EventStreamContentType;
        response.Headers.CacheControl = "no-cache";
        response.Headers[Wire.HeartbeatHeader] = ((int)Heartbeat.TotalSeconds).ToString();
        response.Headers[Wire.StreamEpochHeader] = _epoch;
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var channel = Channel.CreateUnbounded<DataChange>(new UnboundedChannelOptions { SingleReader = true });
        void OnChanged(object? _, DataChange change) => channel.Writer.TryWrite(change);
        _bus.Changed += OnChanged;
        try
        {
            long lastSent = 0;
            var positionKnown = false;
            var request = context.Request;
            if (long.TryParse(request.Headers[Wire.LastEventIdHeader], out var since) && since >= 0)
            {
                // An epoch the station saw on another run of this host: its number means nothing here.
                var stationEpoch = request.Headers[Wire.StreamEpochHeader].ToString();
                var missed = stationEpoch.Length > 0 && stationEpoch != _epoch ? null : _bus.Since(since);
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
                    positionKnown = true;
                }
            }

            // Read after subscribing: an event in between is still in the channel and goes out after this line.
            var baseline = positionKnown ? string.Empty : $"id: {_bus.Sequence}\n";
            await WriteAsync(response, $"{baseline}: connected\n\n", cancellationToken);

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
