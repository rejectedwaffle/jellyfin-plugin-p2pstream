using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Shared HTTP clients. They're rebuilt when the proxy settings change. The no-redirect client is
/// needed because indexer download links often redirect to a magnet: URI, which HttpClient can't follow.
/// </summary>
internal static class HttpClients
{
    private static readonly object Lock = new();
    private static string? _proxyKey;
    private static HttpClient? _default;
    private static HttpClient? _noRedirect;
    private static HttpClient? _publicOnly;

    /// <summary>
    /// Gets a no-redirect client that only connects to public addresses, checked on the address it really connects to
    /// (not a separate lookup, which a hostile site could answer differently the second time: "DNS rebinding"). Used for
    /// download links from sites that aren't one of your own sources or indexers.
    /// </summary>
    public static HttpClient PublicOnly
    {
        get
        {
            Ensure();
            return _publicOnly!;
        }
    }

    public static HttpClient Default
    {
        get
        {
            Ensure();
            return _default!;
        }
    }

    public static HttpClient NoRedirect
    {
        get
        {
            Ensure();
            return _noRedirect!;
        }
    }

    private static void Ensure()
    {
        var c = Plugin.Instance?.Configuration;
        var follow = c?.SearchesFollowTorrentTraffic == true;
        var mode = Torrent.NetworkGuard.ConfiguredMode(c);
        var viaVpn = follow && mode == Torrent.NetworkMode.Interface;
        var useProxy = follow && mode == Torrent.NetworkMode.Proxy;
        var key = $"{c?.ProxyUrl}|{c?.ProxyUser}|{c?.ProxyPassword}|{viaVpn}|{useProxy}";
        if (_default is not null && key == _proxyKey)
        {
            return;
        }

        lock (Lock)
        {
            if (_default is not null && key == _proxyKey)
            {
                return;
            }

            // Old clients aren't disposed: requests may still be in flight on them.
            var proxy = useProxy ? BuildProxy(c?.ProxyUrl, c?.ProxyUser, c?.ProxyPassword) : null; // only when searches follow torrent traffic, and that's the proxy

            // Searches must go through the proxy but its address is missing or invalid: refuse, never go direct.
            var blocked = useProxy && proxy is null
                ? "Searches are set to go through the proxy, but the proxy address isn't valid (Network > Proxy)"
                : null;
            _noRedirect = Create(false, proxy, viaVpn, blocked);
            _default = Create(true, proxy, viaVpn, blocked);
            _publicOnly = Create(false, proxy, viaVpn, blocked, publicOnly: true);
            _proxyKey = key;
        }
    }

    private static IWebProxy? BuildProxy(string? url, string? user, string? password)
    {
        if (!Torrent.NetworkGuard.TryParseProxy(url, out var uri, out _))
        {
            return null; // same validation as torrent traffic (scheme and host)
        }

        var proxy = new WebProxy(uri);
        if (!string.IsNullOrEmpty(user))
        {
            proxy.Credentials = new NetworkCredential(user, password);
        }

        return proxy;
    }

    private static HttpClient Create(bool followRedirects, IWebProxy? proxy, bool viaVpn, string? blocked, bool publicOnly = false)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(viaVpn ? 1 : 5),
            UseProxy = proxy is not null,
            Proxy = proxy
        };
        if (blocked is not null)
        {
            handler.ConnectCallback = (_, _) => throw new Torrent.NetworkBlockedException(blocked);
        }
        else if (viaVpn)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var s = Torrent.NetworkGuard.Require();
                if (s.Mode != Torrent.NetworkMode.Interface)
                {
                    throw new Torrent.NetworkBlockedException("Waiting for the VPN settings to apply");
                }

                var socket = await Torrent.NetworkGuard.ConnectTcpAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct, context.DnsEndPoint.AddressFamily, publicOnly ? "download link" : null).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            };
        }
        else if (publicOnly && proxy is null)
        {
            // Direct: look the name up, refuse private and local addresses, and connect to exactly the address checked.
            handler.ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Torrent.AddressPolicy.ResolvePublicAsync(host, "download link", ct).ConfigureAwait(false);
                var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                    ?? throw new SocketException((int)SocketError.HostNotFound);
                Torrent.AddressPolicy.Require(target, host, "download link");
                var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jellyfin-P2PStream/0.2");
        return client;
    }
}
