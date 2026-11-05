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
/// The station's half of discovery: every two seconds sends <c>OPYX?</c> to the subnet broadcast of <b>every</b> IPv4
/// interface (Windows sends <c>255.255.255.255</c> through one interface only, and a laptop on a phone hot-spot plus
/// Wi-Fi is the usual case) and collects the hosts that answer. A host unseen for 6 s drops off the list.
/// </summary>
public sealed class DiscoveryClient : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(6);

    private readonly IActivityLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredHost> _hosts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;

    public DiscoveryClient(IActivityLog log) => _log = log;

    /// <summary>The hosts seen within the TTL, newest first.</summary>
    public IReadOnlyList<DiscoveredHost> Hosts
    {
        get
        {
            lock (_gate)
            {
                var cutoff = DateTimeOffset.UtcNow - Ttl;
                return _hosts.Values.Where(h => h.SeenAt >= cutoff).OrderByDescending(h => h.SeenAt).ToList();
            }
        }
    }

    /// <summary>Raised (on a pool thread) when the host list changed.</summary>
    public event EventHandler? HostsChanged;

    public bool IsRunning => _cts is not null;

    /// <summary>True once at least one answer arrived since <see cref="Start"/> (the broadcast gets through).</summary>
    public bool AnyAnswer { get; private set; }

    public void Start()
    {
        if (_cts is not null)
            return;
        AnyAnswer = false;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>One broadcast round, waiting up to <paramref name="wait"/> for answers (used by the diagnostics).</summary>
    public async Task<IReadOnlyList<DiscoveredHost>> ProbeOnceAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        using var udp = OpenSocket();
        if (udp is null)
            return [];
        Broadcast(udp);
        var deadline = DateTime.UtcNow + wait;
        var found = new List<DiscoveredHost>();
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(remaining);
            try
            {
                var received = await udp.ReceiveAsync(timeout.Token);
                if (Parse(received) is { } host && found.All(h => h.HostId != host.HostId))
                    found.Add(host);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        return found;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var udp = OpenSocket();
        if (udp is null)
            return;

        var receiving = ReceiveLoopAsync(udp, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            Broadcast(udp);
            Prune();
            try
            {
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        udp.Dispose();
        try { await receiving; } catch { /* closed */ }
    }

    private UdpClient? OpenSocket()
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            return udp;
        }
        catch (Exception ex)
        {
            _log.Error("Discovery client could not open a UDP socket", ex);
            return null;
        }
    }

    private void Broadcast(UdpClient udp)
    {
        var payload = Encoding.UTF8.GetBytes(Wire.DiscoveryRequest);
        var targets = LocalNetwork.Addresses().Where(a => !a.IsLinkLocal).Select(a => a.Broadcast).ToList();
        targets.Add(IPAddress.Broadcast);
        foreach (var target in targets.Distinct())
        {
            try
            {
                udp.Send(payload, payload.Length, new IPEndPoint(target, NetworkProtocol.DiscoveryPort));
            }
            catch (SocketException)
            {
                // An interface without a route (a dormant VPN adapter) — ignore and try the others.
            }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken cancellationToken)
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
                continue;
            }

            if (Parse(received) is not { } host)
                continue;
            AnyAnswer = true;
            lock (_gate)
                _hosts[host.HostId] = host;
            HostsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private DiscoveredHost? Parse(UdpReceiveResult received)
    {
        try
        {
            var text = Encoding.UTF8.GetString(received.Buffer);
            if (!text.StartsWith(Wire.DiscoveryResponsePrefix, StringComparison.Ordinal))
                return null;
            var answer = JsonSerializer.Deserialize<DiscoveryResponse>(text[Wire.DiscoveryResponsePrefix.Length..], RpcJson.Options);
            if (answer is null || string.IsNullOrEmpty(answer.HostId))
                return null;
            // Prefer the address the answer actually came from: it is the one that is reachable from here.
            var addresses = new List<string> { received.RemoteEndPoint.Address.ToString() };
            addresses.AddRange(answer.Ip.Where(ip => !addresses.Contains(ip)));
            return new DiscoveredHost(answer.HostId, answer.Name, answer.Version, addresses, answer.Port, answer.Competition, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Prune()
    {
        var changed = false;
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow - Ttl;
            foreach (var stale in _hosts.Where(kv => kv.Value.SeenAt < cutoff).Select(kv => kv.Key).ToList())
            {
                _hosts.Remove(stale);
                changed = true;
            }
        }
        if (changed)
            HostsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
