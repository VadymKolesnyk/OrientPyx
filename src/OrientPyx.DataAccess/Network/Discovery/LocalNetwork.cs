using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OrientPyx.DataAccess.Network.Discovery;

/// <summary>Enumerates this machine's usable IPv4 addresses (and their subnet broadcasts).</summary>
public static class LocalNetwork
{
    /// <summary>One IPv4 interface address with its subnet mask.</summary>
    public sealed record Address(IPAddress Ip, IPAddress Mask, string InterfaceName)
    {
        /// <summary>True for an APIPA (169.254.x.x) address — "no DHCP answered", not a usable LAN.</summary>
        public bool IsLinkLocal => Ip.GetAddressBytes() is [169, 254, ..];

        /// <summary>The directed broadcast of this subnet (<c>ip | ~mask</c>).</summary>
        public IPAddress Broadcast
        {
            get
            {
                var ip = Ip.GetAddressBytes();
                var mask = Mask.GetAddressBytes();
                var result = new byte[4];
                for (var i = 0; i < 4; i++)
                    result[i] = (byte)(ip[i] | ~mask[i]);
                return new IPAddress(result);
            }
        }
    }

    /// <summary>The IPv4 addresses of every interface that is up and not loopback, link-local ones last.</summary>
    public static IReadOnlyList<Address> Addresses()
    {
        var result = new List<Address>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(unicast.Address))
                        continue;
                    var mask = unicast.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
                    result.Add(new Address(unicast.Address, mask, nic.Name));
                }
            }
        }
        catch
        {
            // Enumeration can fail on exotic adapters; a partial list is still useful.
        }
        return result.OrderBy(a => a.IsLinkLocal).ThenBy(a => a.Ip.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <summary>The usable (not link-local) addresses as strings, for display and for the discovery answer.</summary>
    public static IReadOnlyList<string> UsableAddressStrings()
        => Addresses().Where(a => !a.IsLinkLocal).Select(a => a.Ip.ToString()).ToList();
}
