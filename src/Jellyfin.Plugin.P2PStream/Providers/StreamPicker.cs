using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Helpers that turn a P2P Stream .strm item plus its release list into Jellyfin "versions" (media sources).
/// </summary>
public static partial class StreamPicker
{
    /// <summary>Gets the play request behind a .strm item written by this plugin.</summary>
    public static bool TryGetRequest(BaseItem item, out string strmUrl, out MediaRequest request)
    {
        strmUrl = string.Empty;
        request = null!;
        var path = item.Path;
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var url = item.ShortcutPath;
        if (string.IsNullOrWhiteSpace(url) && File.Exists(path))
        {
            url = File.ReadAllText(path).Trim();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var m = PlayUrl().Match(url);
        if (!m.Success)
        {
            return false;
        }

        request = m.Groups["tmdb"].Success
            ? new MediaRequest { Kind = MediaKind.Movie, TmdbId = int.Parse(m.Groups["tmdb"].Value, CultureInfo.InvariantCulture) }
            : m.Groups["imdb"].Success
                ? new MediaRequest { Kind = MediaKind.Movie, ImdbId = m.Groups["imdb"].Value }
                : new MediaRequest
                {
                    Kind = MediaKind.Episode,
                    TmdbId = int.Parse(m.Groups["tv"].Value, CultureInfo.InvariantCulture),
                    Season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
                    Episode = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture)
                };
        strmUrl = url.Split('?')[0];
        return true;
    }

    /// <summary>One media source per release, copied from the item's own .strm source so Jellyfin treats it the same way.</summary>
    public static List<MediaSourceInfo> BuildSources(BaseItem item, string strmUrl, IEnumerable<ReleaseCandidate> releases)
    {
        var template = item.GetMediaSources(false).FirstOrDefault();
        var templateJson = template is null ? null : JsonSerializer.Serialize(template);
        var list = new List<MediaSourceInfo>();
        foreach (var r in releases)
        {
            var id = StreamOptions.IdFor(r);
            var source = (templateJson is null ? null : JsonSerializer.Deserialize<MediaSourceInfo>(templateJson))
                ?? new MediaSourceInfo { IsRemote = true, SupportsDirectStream = true, SupportsTranscoding = true, SupportsProbing = true };
            source.Id = id;
            source.ETag = id;
            source.Name = StreamOptions.Label(r);
            source.Path = strmUrl + "?release=" + id;
            source.Protocol = MediaProtocol.Http;
            source.IsRemote = true;
            source.MediaStreams = [];
            list.Add(source);
        }

        return list;
    }

    [GeneratedRegex(@"/P2PStream/play/[^/]+/(?:movie/(?<tmdb>\d+)|imdb/(?<imdb>tt\d+)|tv/(?<tv>\d+)/(?<s>\d+)/(?<e>\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex PlayUrl();
}

/// <summary>
/// Offers each release as a playable version when playback starts, so the version chosen on the title's page plays that release.
/// Jellyfin discovers this class automatically.
/// </summary>
public sealed class StreamPickerSourceProvider : IMediaSourceProvider
{
    private readonly StreamOptions _options;
    private readonly ILogger<StreamPickerSourceProvider> _logger;

    public StreamPickerSourceProvider(StreamOptions options, ILogger<StreamPickerSourceProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.StreamPicker != true || !StreamPicker.TryGetRequest(item, out var url, out var request))
        {
            return [];
        }

        try
        {
            var releases = await _options.GetAsync(request, cancellationToken).ConfigureAwait(false);
            return StreamPicker.BuildSources(item, url, releases);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not list streams for {Item}: {Message}", item.Name, ex.Message);
            return [];
        }
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => throw new NotSupportedException("P2P Stream versions are plain HTTP streams and need no opening");
}
