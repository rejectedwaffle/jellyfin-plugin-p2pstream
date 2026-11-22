using System.Net;

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
        var key = $"{c?.ProxyUrl}|{c?.ProxyUser}|{c?.ProxyPassword}";
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
            var proxy = BuildProxy(c?.ProxyUrl, c?.ProxyUser, c?.ProxyPassword);
            _noRedirect = Create(false, proxy);
            _default = Create(true, proxy);
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

    private static HttpClient Create(bool followRedirects, IWebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = proxy is not null,
            Proxy = proxy
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jellyfin-P2PStream/0.2");
        return client;
    }
}
