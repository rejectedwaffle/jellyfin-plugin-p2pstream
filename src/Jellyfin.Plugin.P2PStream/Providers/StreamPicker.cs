using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// One media source per release, copied from the item's own .strm source so Jellyfin treats it the same way.
    /// Releases checked in advance get their real tracks; the rest have none until they've played.
    /// </summary>
    public static List<MediaSourceInfo> BuildSources(BaseItem item, string strmUrl, IEnumerable<ReleaseCandidate> releases, StreamOptions? options = null)
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
            source.DefaultAudioStreamIndex = null;
            source.DefaultSubtitleStreamIndex = null;
            if (options is not null && options.TryGetProbe(id, out var probe))
            {
                source.MediaStreams = probe.Streams;
                source.RunTimeTicks = probe.RunTimeTicks ?? source.RunTimeTicks;
                source.Bitrate = probe.Bitrate;
                source.DefaultAudioStreamIndex = (probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                    ?? probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio))?.Index;
            }

            list.Add(source);
        }

        return list;
    }

    [GeneratedRegex(@"/P2PStream/play/[^/]+/(?:movie/(?<tmdb>\d+)|imdb/(?<imdb>tt\d+)|tv/(?<tv>\d+)/(?<s>\d+)/(?<e>\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex PlayUrl();
}

/// <summary>
/// Gelato-style "check streams before playback": reads the tracks of a title's top releases in the background
/// (via Jellyfin's own ffprobe), so the Version menu can show them. Checks never change Auto's remembered pick.
/// </summary>
public sealed class StreamProber
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(3);
    private readonly StreamOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<StreamProber> _logger;
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public StreamProber(StreamOptions options, IServiceProvider services, ILogger<StreamProber> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    /// <summary>Starts checking the title's top releases that haven't been checked yet. Returns immediately.</summary>
    public void ProbeTopInBackground(BaseItem item, string strmUrl, IReadOnlyList<ReleaseCandidate> releases)
    {
        var c = Plugin.Instance?.Configuration;
        if (c?.ProbeStreams != true)
        {
            return;
        }

        var todo = releases.Take(Math.Clamp(c.ProbeStreamCount, 1, 5))
            .Where(r => !_options.TryGetProbe(StreamOptions.IdFor(r), out _) && _running.TryAdd(StreamOptions.IdFor(r), 0))
            .ToList();
        if (todo.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var release in todo)
            {
                var id = StreamOptions.IdFor(release);
                try
                {
                    await _oneAtATime.WaitAsync().ConfigureAwait(false); // one check at a time across the server
                    try
                    {
                        await ProbeAsync(item, strmUrl, release, id).ConfigureAwait(false);
                    }
                    finally
                    {
                        _oneAtATime.Release();
                    }
                }
                finally
                {
                    _running.TryRemove(id, out _);
                }
            }
        });
    }

    private async Task ProbeAsync(BaseItem item, string strmUrl, ReleaseCandidate release, string id)
    {
        var encoder = _services.GetService<IMediaEncoder>();
        if (encoder is null)
        {
            return;
        }

        var source = StreamPicker.BuildSources(item, strmUrl, [release])[0];
        source.Path += "&probe=true"; // tells the play endpoint not to remember this release ("1" isn't a valid bool)
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = source, MediaType = DlnaProfileType.Video, ExtractChapters = false }, timeout.Token).ConfigureAwait(false);
            if (info.MediaStreams is { Count: > 0 })
            {
                _options.SetProbe(id, new ProbeInfo(info.MediaStreams, info.RunTimeTicks, info.Bitrate));
                _logger.LogInformation("Checked {Release}: {Count} tracks", release.Title, info.MediaStreams.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Could not check {Release}: {Message}", release.Title, ex.Message);
        }
    }
}
