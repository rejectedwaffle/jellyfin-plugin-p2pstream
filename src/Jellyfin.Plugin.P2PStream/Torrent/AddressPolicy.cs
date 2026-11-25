using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// Addresses that links from the internet (a search result's download link, a tracker or web seed listed in a torrent)
/// may not make the server connect to: this machine, the home network and other non-public ranges. Without this, a
/// crafted search result or torrent could make the server send requests to devices on your network.
/// Addresses you typed yourself (an indexer, an RSS feed, a source's address) aren't checked.
/// </summary>
public static class AddressPolicy
{
    /// <summary>True for loopback, private, link-local, shared (CGNAT), multicast, reserved and unspecified addresses.</summary>
    public static bool IsInternal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => true,                // "this network", private, loopback
                100 => b[1] is >= 64 and <= 127,       // shared address space (CGNAT)
                169 => b[1] == 254,                    // link-local
                172 => b[1] is >= 16 and <= 31,        // private
                192 => (b[1] == 168) || (b[1] == 0 && b[2] is 0 or 2), // private, IETF, documentation
                198 => b[1] is 18 or 19,               // benchmarking
                _ => b[0] >= 224                        // multicast, reserved, broadcast
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // unique local (fc00::/7)
        }

        return true;
    }

    /// <summary>Throws if the address is internal; <paramref name="what"/> names it in the message ("tracker", "download link").</summary>
    public static void Require(IPAddress address, string host, string what)
    {
        if (IsInternal(address))
        {
            throw new NetworkBlockedException($"The {what} {host} points into your own network ({address}), so it isn't used");
        }
    }

    /// <summary>Resolves a host and returns its addresses, or throws if any of them is internal.</summary>
    public static async Task<IPAddress[]> ResolvePublicAsync(string host, string what, CancellationToken ct, AddressFamily family = AddressFamily.Unspecified)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, family, ct).ConfigureAwait(false);
        foreach (var a in addresses)
        {
            Require(a, host, what);
        }

        return addresses;
    }
}
