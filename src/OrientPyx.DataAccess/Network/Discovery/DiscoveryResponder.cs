using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Discovery;

/// <summary>
/// The host's half of discovery: listens on UDP <see cref="NetworkProtocol.DiscoveryPort"/> and answers every
/// <c>OPYX?</c> with <c>OPYX {json}</c> (<see cref="DiscoveryResponse"/>) sent back to the asker. Started and stopped
/// with the host. <see cref="Describe"/> is asked for the current answer on every request, so the shown competition
/// follows the session.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly IActivityLog _log;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public DiscoveryResponder(IActivityLog log) => _log = log;

    /// <summary>Builds the answer for a request; null ⇒ do not answer (the host is stopping).</summary>
    public Func<DiscoveryResponse?>? Describe { get; set; }

    public bool IsRunning => _udp is not null;

    /// <summary>Starts listening; false when the port could not be bound (another instance on this machine has it —
    /// stations then find that one, which is acceptable).</summary>
    public bool Start()
    {
        if (_udp is not null)
            return true;
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkProtocol.DiscoveryPort));
            _udp = udp;
            _cts = new CancellationTokenSource();
            _ = ListenAsync(udp, _cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Discovery responder could not bind the UDP port", ex);
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts = null;
    }

    private async Task ListenAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A forcibly closed connection reset (Windows reports ICMP port-unreachable as an exception) — keep going.
                continue;
            }

            try
            {
                var text = Encoding.UTF8.GetString(received.Buffer).Trim();
                if (!string.Equals(text, Wire.DiscoveryRequest, StringComparison.Ordinal))
                    continue;
                var answer = Describe?.Invoke();
                if (answer is null)
                    continue;
                var payload = Encoding.UTF8.GetBytes(Wire.DiscoveryResponsePrefix + JsonSerializer.Serialize(answer, RpcJson.Options));
                await udp.SendAsync(payload, received.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("Discovery answer failed", ex);
            }
        }
    }

    public void Dispose() => Stop();
}
