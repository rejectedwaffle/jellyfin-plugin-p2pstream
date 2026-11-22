using System.Collections.Concurrent;
using Jellyfin.Plugin.P2PStream.Torrent;
using Microsoft.Extensions.Logging;
using MonoTorrent.Client;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Turns "play movie X / episode Y" (optionally: this exact release) into a running torrent session and file index.
/// </summary>
public sealed class StreamResolver
{
    private readonly TorrentEngine _engine;
    private readonly TorznabClient _torznab;
    private readonly StreamOptions _options;
    private readonly ResolutionCache _cache;
    private readonly ILogger<StreamResolver> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // Release id to the torrent it turned into, so repeated requests for a chosen stream (probe, ffmpeg, seeks) don't refetch it.
    private readonly ConcurrentDictionary<string, (string InfoHash, int FileIndex)> _started = new();

    // A version chosen in the Version menu, played the next time the title's normal source is requested.
    private readonly ConcurrentDictionary<string, string> _pinned = new();

    public StreamResolver(TorrentEngine engine, TorznabClient torznab, StreamOptions options, ResolutionCache cache, ILogger<StreamResolver> logger)
    {
        _engine = engine;
        _torznab = torznab;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    private readonly ConcurrentDictionary<string, byte> _warming = new();

    /// <summary>
    /// Starts Auto's release in the background when a title's page opens: adds the torrent (fetching its file list)
    /// and downloads the start and end of the video, the parts players read first. Pressing play then starts much sooner.
    /// It's the same release Auto would play, so nothing is wasted if you play it.
    /// </summary>
    public void Prewarm(MediaRequest req)
    {
        if (Plugin.Instance?.Configuration.PrewarmOnOpen != true || !_warming.TryAdd(req.Key, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var resolved = await ResolveAsync(req, null, timeout.Token).ConfigureAwait(false);
                var file = resolved.Session.Manager.Files[resolved.FileIndex];
                await using var lease = await resolved.Session.OpenAsync(file, timeout.Token).ConfigureAwait(false);
                var buffer = new byte[256 * 1024];
                async Task ReadAsync(long from, long bytes)
                {
                    lease.Position = Math.Max(0, from);
                    for (long done = 0; done < bytes;)
                    {
                        var n = await lease.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (n == 0)
                        {
                            break;
                        }

                        done += n;
                    }
                }

                await ReadAsync(0, 4L << 20).ConfigureAwait(false);                       // headers and first seconds
                await ReadAsync(file.Length - (2L << 20), 2L << 20).ConfigureAwait(false); // index (MP4 moov / MKV cues)
                _logger.LogInformation("Pre-loaded {Request} so playback starts sooner", req);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Pre-load of {Request} stopped: {Message}", req, ex.Message);
            }
            finally
            {
                _warming.TryRemove(req.Key, out _);
            }
        });
    }

    // Titles whose remembered pick was chosen by a device rule (e.g. 1080p for the TV), not by ranking or by the user.
    private readonly ConcurrentDictionary<string, byte> _rulePicks = new();

    /// <summary>Plays <paramref name="releaseId"/> the next time this title is played through its normal source.</summary>
    /// <param name="byDeviceRule">True when a device rule chose it, so other devices go back to the best match.</param>
    public void Pin(MediaRequest req, string releaseId, bool byDeviceRule = false)
    {
        _pinned[req.Key] = releaseId;
        if (byDeviceRule)
        {
            _rulePicks[req.Key] = 0;
        }
        else
        {
            _rulePicks.TryRemove(req.Key, out _);
        }
    }

    /// <summary>
    /// A device without limits plays Auto: if the remembered pick only exists because a limited device (a TV capped at
    /// 1080p) played it, forget it so Auto picks the best release again. Returns true if it did.
    /// </summary>
    public bool ForgetDeviceRulePick(MediaRequest req)
    {
        if (!_rulePicks.TryRemove(req.Key, out _))
        {
            return false;
        }

        _pinned.TryRemove(req.Key, out _);
        _cache.Remove(req.Key);
        return true;
    }

    /// <summary>The release Auto would play now: the remembered pick if it's in the list, else the top-ranked one.</summary>
    public ReleaseCandidate? AutoRelease(MediaRequest req, IReadOnlyList<ReleaseCandidate> ranked)
    {
        var current = CurrentPickTitle(req);
        return (current is null ? null : ranked.FirstOrDefault(r => r.Title == current)) ?? ranked.FirstOrDefault();
    }

    /// <summary>True if the title currently plays this release (so its saved track info is still right).</summary>
    public bool IsCurrentPick(MediaRequest req, string releaseId) =>
        _started.TryGetValue(releaseId, out var s) && _cache.TryGet(req.Key, out var e) && e.InfoHash == s.InfoHash;

    /// <summary>The release Auto currently plays, if one is remembered.</summary>
    public string? CurrentPickTitle(MediaRequest req) => _cache.TryGet(req.Key, out var e) ? e.Title : null;

    /// <param name="releaseId">A release picked in the Version menu, or null for Auto (best match).</param>
    /// <param name="remember">False for background checks, which must not change Auto's remembered pick.</param>
    public async Task<ResolvedStream> ResolveAsync(MediaRequest req, string? releaseId, CancellationToken ct, bool remember = true)
    {
        // ffmpeg and clients often fire several requests at once. Only one of them searches or starts a torrent.
        var gate = _locks.GetOrAdd(req.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (releaseId is not null)
            {
                return await ResolveChosenAsync(req, releaseId, remember, ct).ConfigureAwait(false);
            }

            if (_pinned.TryRemove(req.Key, out var pinned))
            {
                try
                {
                    return await ResolveChosenAsync(req, pinned, remember: true, ct).ConfigureAwait(false);
                }
                catch (StreamNotFoundException ex)
                {
                    _logger.LogWarning("Chosen version failed ({Message}); falling back to Auto", ex.Message);
                }
            }

            var cached = await TryCachedAsync(req, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            var releases = await _options.GetAsync(req, ct).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                throw new StreamNotFoundException($"No suitable releases found for {req}");
            }

            var tries = Math.Max(1, Plugin.Instance!.Configuration.CandidatesToTry);
            foreach (var release in releases.Take(tries))
            {
                var started = await TryStartAsync(req, release, remember: true, ct).ConfigureAwait(false);
                if (started is not null)
                {
                    return started;
                }
            }

            throw new StreamNotFoundException($"None of the top {tries} releases for {req} could be started");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Picks the video file to play: the matching episode, or the largest video for movies.
    /// </summary>
    public static int PickFile(TorrentManager manager, MediaRequest? req)
    {
        var videos = manager.Files
            .Select((f, i) => (File: f, Index: i))
            .Where(x => TorrentEngine.IsVideo(x.File.Path))
            .ToList();
        if (videos.Count == 0)
        {
            return -1;
        }

        if (req?.Kind == MediaKind.Episode)
        {
            var match = videos
                .Where(x => ReleaseRanker.ContainsEpisode(x.File.Path, req.Season, req.Episode))
                .OrderByDescending(x => x.File.Length)
                .FirstOrDefault();
            if (match.File is not null)
            {
                return match.Index;
            }

            // Single-episode torrent whose files aren't named by episode.
            return videos.Count == 1 && ReleaseRanker.ContainsEpisode(manager.Torrent?.Name ?? string.Empty, req.Season, req.Episode)
                ? videos[0].Index
                : -1;
        }

        return videos.OrderByDescending(x => x.File.Length).First().Index;
    }

    private async Task<ResolvedStream> ResolveChosenAsync(MediaRequest req, string releaseId, bool remember, CancellationToken ct)
    {
        if (_started.TryGetValue(releaseId, out var known))
        {
            var session = await _engine.GetOrRestoreAsync(known.InfoHash, ct).ConfigureAwait(false);
            if (session is not null && known.FileIndex < session.Manager.Files.Count)
            {
                if (remember)
                {
                    _cache.Set(req.Key, known.InfoHash, known.FileIndex, session.Manager.Torrent?.Name ?? releaseId);
                }

                return new ResolvedStream(session, known.FileIndex);
            }
        }

        var releases = await _options.GetAsync(req, ct).ConfigureAwait(false);
        var chosen = releases.FirstOrDefault(r => StreamOptions.IdFor(r) == releaseId)
            ?? throw new StreamNotFoundException("That stream is no longer listed. Reopen the title to refresh the list, or use Auto.");
        return await TryStartAsync(req, chosen, remember, ct).ConfigureAwait(false)
            ?? throw new StreamNotFoundException($"Couldn't start \"{chosen.Title}\" (no peers, or no matching video file). Try another version.");
    }

    private async Task<ResolvedStream?> TryStartAsync(MediaRequest req, ReleaseCandidate release, bool remember, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Trying {Release} from {Origin} ({Seeders} seeders) for {Request}", release.Title, release.Origin, release.Seeders, req);
            var source = await _torznab.ToSourceAsync(release, ct).ConfigureAwait(false);
            var session = await _engine.AddAsync(source, ct).ConfigureAwait(false);
            var index = PickFile(session.Manager, req);
            if (index >= 0)
            {
                // Remember it: Auto uses this release next time, and repeat requests skip the lookup.
                if (remember)
                {
                    _cache.Set(req.Key, source.InfoHash, index, release.Title);
                }

                _started[StreamOptions.IdFor(release)] = (source.InfoHash, index);
                return new ResolvedStream(session, index);
            }

            _logger.LogInformation("No matching video file in {Release}", release.Title);
            await _engine.RemoveAsync(source.InfoHash, true).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Candidate {Release} failed: {Message}", release.Title, ex.Message);
        }

        return null;
    }

    private async Task<ResolvedStream?> TryCachedAsync(MediaRequest req, CancellationToken ct)
    {
        if (!_cache.TryGet(req.Key, out var entry))
        {
            return null;
        }

        try
        {
            var session = await _engine.GetOrRestoreAsync(entry.InfoHash, ct).ConfigureAwait(false);
            if (session is not null && entry.FileIndex < session.Manager.Files.Count)
            {
                return new ResolvedStream(session, entry.FileIndex);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Cached torrent for {Request} no longer works ({Message}), searching again", req, ex.Message);
        }

        _cache.Remove(req.Key);
        return null;
    }
}
