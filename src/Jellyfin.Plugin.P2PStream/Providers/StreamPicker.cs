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
    // Version id -> (title it belongs to, last time it was listed). Saved to disk, so a version picked after a server
    // restart still resolves without reopening the title page first.
    private static readonly TimeSpan VersionKeep = TimeSpan.FromDays(60);
    private static readonly object VersionLock = new();
    private static ConcurrentDictionary<Guid, VersionOwner>? _versionOwners;
    private static int _versionSaveQueued;

    private sealed record VersionOwner(Guid Item, DateTime Seen);

    private static string VersionsFile => Path.Combine(Plugin.Instance!.DataFolderPath, "versions.json");

    private static ConcurrentDictionary<Guid, VersionOwner> VersionOwners
    {
        get
        {
            if (_versionOwners is { } loaded)
            {
                return loaded;
            }

            lock (VersionLock)
            {
                if (_versionOwners is null)
                {
                    var map = new ConcurrentDictionary<Guid, VersionOwner>();
                    try
                    {
                        if (File.Exists(VersionsFile)
                            && JsonSerializer.Deserialize<Dictionary<Guid, VersionOwner>>(File.ReadAllText(VersionsFile)) is { } data)
                        {
                            foreach (var (id, owner) in data.Where(kv => DateTime.UtcNow - kv.Value.Seen < VersionKeep))
                            {
                                map[id] = owner;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // A damaged file only means versions need their title page reopened once.
                    }

                    _versionOwners = map;
                }

                return _versionOwners;
            }
        }
    }

    private static void QueueVersionSave()
    {
        if (Interlocked.Exchange(ref _versionSaveQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false); // a page lists many versions at once: save once
            Interlocked.Exchange(ref _versionSaveQueued, 0);
            lock (VersionLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(VersionsFile)!);
                    var tmp = VersionsFile + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(VersionOwners.Where(kv => DateTime.UtcNow - kv.Value.Seen < VersionKeep).ToDictionary()));
                    File.Move(tmp, VersionsFile, overwrite: true);
                }
                catch (Exception)
                {
                    // Not critical: next listing tries again.
                }
            }
        });
    }

    /// <summary>
    /// The id a version gets in the Version menu. Jellyfin apps look a chosen version up as a library item
    /// (GET Items/{id}) before playing it, so it must be shaped like an item id, and unique per title (the same release
    /// can be offered for several episodes). <paramref name="key"/> is the release id, or "res1080" and the like.
    /// </summary>
    public static string VersionId(BaseItem item, string key)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(item.Id.ToString("N") + "|" + key))).ToLowerInvariant();

    /// <summary>Gives a version its id and remembers which title it belongs to (for the item lookup).</summary>
    public static string RegisterVersion(BaseItem item, string key)
    {
        var id = VersionId(item, key);
        var key2 = Guid.ParseExact(id, "N");
        var now = DateTime.UtcNow;
        if (!VersionOwners.TryGetValue(key2, out var known) || known.Item != item.Id || now - known.Seen > TimeSpan.FromDays(1))
        {
            VersionOwners[key2] = new VersionOwner(item.Id, now);
            QueueVersionSave();
        }

        return id;
    }

    /// <summary>The title a version id (from the Version menu) belongs to.</summary>
    public static bool TryGetVersionOwner(Guid versionId, out Guid itemId)
    {
        itemId = VersionOwners.TryGetValue(versionId, out var owner) ? owner.Item : Guid.Empty;
        return owner is not null;
    }

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
            source.Id = RegisterVersion(item, id);
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
    private readonly ConcurrentDictionary<string, Task> _running = new();

    // The release Auto will play gets its own lane: its tracks are needed the moment Play is pressed, so it never waits
    // behind other titles' slower checks. The other top releases (for the Version menu) are checked one at a time.
    private readonly SemaphoreSlim _autoLane = new(2, 2);
    private readonly SemaphoreSlim _otherLane = new(1, 1);

    public StreamProber(StreamOptions options, IServiceProvider services, ILogger<StreamProber> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Starts checking the release Auto will play (first, in its own lane) and the title's other top releases that
    /// haven't been checked yet. Returns immediately.
    /// </summary>
    public void ProbeTopInBackground(BaseItem item, string strmUrl, IReadOnlyList<ReleaseCandidate> releases, ReleaseCandidate? autoRelease = null)
    {
        var c = Plugin.Instance?.Configuration;
        if (c?.ProbeStreams != true)
        {
            return;
        }

        autoRelease ??= releases.FirstOrDefault();
        if (autoRelease is not null)
        {
            Start(item, strmUrl, autoRelease, _autoLane);
        }

        foreach (var release in releases.Take(Math.Clamp(c.ProbeStreamCount, 1, 5)).Where(r => r != autoRelease))
        {
            Start(item, strmUrl, release, _otherLane);
        }
    }

    /// <summary>
    /// The tracks of a release: at once if it's been checked, after waiting up to <paramref name="wait"/> if a check is
    /// running, otherwise null.
    /// </summary>
    public async Task<ProbeInfo?> WaitForProbeAsync(string releaseId, TimeSpan wait, CancellationToken ct)
    {
        if (_options.TryGetProbe(releaseId, out var done))
        {
            return done;
        }

        if (_running.TryGetValue(releaseId, out var running))
        {
            try
            {
                await running.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        return _options.TryGetProbe(releaseId, out var info) ? info : null;
    }

    private void Start(BaseItem item, string strmUrl, ReleaseCandidate release, SemaphoreSlim lane)
    {
        var id = StreamOptions.IdFor(release);
        if (_options.TryGetProbe(id, out _) || _running.ContainsKey(id))
        {
            return;
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(id, gate.Task))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await lane.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ProbeAsync(item, strmUrl, release, id).ConfigureAwait(false);
                }
                finally
                {
                    lane.Release();
                }
            }
            finally
            {
                _running.TryRemove(id, out _);
                gate.TrySetResult();
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

        // Jellyfin's default analysis reads up to 200 s of video (up to 1 GB) to fill in every track's details: minutes
        // through a torrent. The track list itself comes from the file's header, so 10 s is plenty (live TV does the same).
        source.AnalyzeDurationMs = 10_000;
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        var started = DateTime.UtcNow;
        try
        {
            var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = source, MediaType = DlnaProfileType.Video, ExtractChapters = false }, timeout.Token).ConfigureAwait(false);
            if (info.MediaStreams is { Count: > 0 })
            {
                _options.SetProbe(id, new ProbeInfo(info.MediaStreams, info.RunTimeTicks, info.Bitrate));
                _logger.LogInformation("Checked {Release}: {Count} tracks in {Seconds:0}s", release.Title, info.MediaStreams.Count, (DateTime.UtcNow - started).TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Could not check {Release}: {Message}", release.Title, ex.Message);
        }
    }
}
