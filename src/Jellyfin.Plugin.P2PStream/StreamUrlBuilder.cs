using System.Globalization;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.P2PStream;

/// <summary>
/// Builds the URLs written into .strm files and shown on the config page.
/// </summary>
public sealed class StreamUrlBuilder
{
    private readonly IServerApplicationHost _host;

    public StreamUrlBuilder(IServerApplicationHost host)
    {
        _host = host;
    }

    private static string Token => Plugin.Instance!.Configuration.StreamToken;

    public string BaseUrl(HttpRequest? request = null)
    {
        var configured = Plugin.Instance!.Configuration.PublicBaseUrl?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            return configured.TrimEnd('/');
        }

        var url = request is not null
            ? _host.GetSmartApiUrl(request)
            : _host.GetApiUrlForLocalAccess(ipAddress: null, allowHttps: true);
        return url.TrimEnd('/');
    }

    public string Movie(int tmdbId, HttpRequest? request = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{BaseUrl(request)}/P2PStream/play/{Token}/movie/{tmdbId}");

    public string Episode(int tmdbId, int season, int episode, HttpRequest? request = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{BaseUrl(request)}/P2PStream/play/{Token}/tv/{tmdbId}/{season}/{episode}");

    public string File(string infoHash, int fileIndex, HttpRequest? request = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{BaseUrl(request)}/P2PStream/stream/{Token}/{infoHash}/{fileIndex}");
}
