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
            _noRedirect = Create(false, proxy, viaVpn);
            _default = Create(true, proxy, viaVpn);
            _proxyKey = key;
        }
    }

    private static IWebProxy? BuildProxy(string? url, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var proxy = new WebProxy(uri);
        if (!string.IsNullOrEmpty(user))
        {
            proxy.Credentials = new NetworkCredential(user, password);
        }

        return proxy;
    }

    private static HttpClient Create(bool followRedirects, IWebProxy? proxy, bool viaVpn)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(viaVpn ? 1 : 5),
            UseProxy = proxy is not null,
            Proxy = proxy
        };
        if (viaVpn)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var s = Torrent.NetworkGuard.Require();
                if (s.Mode != Torrent.NetworkMode.Interface)
                {
                    throw new Torrent.NetworkBlockedException("Waiting for the VPN settings to apply");
                }

                var socket = await Torrent.NetworkGuard.ConnectTcpAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct, context.DnsEndPoint.AddressFamily).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            };
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jellyfin-P2PStream/0.2");
        return client;
    }
}
