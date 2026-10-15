// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using System.Net;

namespace ZuTM.Core.Net;

/// <summary>
/// Host allow-policy for URLs ZuTM itself downloads. ISO files land on disk
/// and are executed by guests, so a hostile download target is a real risk:
/// loopback and non-public address ranges are refused before any connection
/// is made. DNS names cannot be classified without resolving them, so they
/// pass by shape-check only — the same trade-off curl makes for humans.
/// (Drive *streaming* URLs are validated separately by
/// <see cref="RemoteImage"/>: a LAN mirror is a legitimate QEMU source, it is
/// just not a default-allowed download target.)
/// </summary>
public static class DownloadHostPolicy
{
    public static bool IsAllowed(Uri uri)
    {
        var host = uri.Host;
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return IPAddress.TryParse(host, out var address) && IsPublicAddress(address);
        }

        return true;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && IsPublicIpv4(bytes[0], bytes[1]);
        }

        if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return !IsNonPublicIpv6(address);
        }

        return false;
    }

    private static bool IsPublicIpv4(byte first, byte second) => (first, second) switch
    {
        (0, _) or (10, _) or (127, _) => false,           // "this network", private, loopback
        (100, >= 64 and <= 127) => false,                 // CGNAT 100.64/10
        (169, 254) => false,                              // link-local 169.254/16
        (172, >= 16 and <= 31) => false,                  // private 172.16/12
        (192, 0) or (192, 168) => false,                  // 192.0.0/24, private 192.168/16
        (198, 18) or (198, 19) or (198, 51) => false,     // benchmarking, documentation
        (203, 0) => false,                                // documentation 203.0.113/24
        (>= 224, _) => false,                             // multicast + reserved
        _ => true,
    };

    private static bool IsNonPublicIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 16)
        {
            return true;
        }

        bool first(byte b) => bytes[0] == b;
        return first(0)                                          // unspecified "::"
            || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)   // link-local fe80::/10
            || (bytes[0] & 0xFE) == 0xFC                         // unique-local fc00::/7
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            || first(0xFF);                                      // multicast ff00::/8
    }
}
