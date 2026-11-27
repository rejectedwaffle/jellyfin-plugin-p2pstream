using System.Collections.Concurrent;
using Jellyfin.Plugin.P2PStream.Torrent;
using NetworkGuard = Jellyfin.Plugin.P2PStream.Torrent.NetworkGuard;
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
    private readonly ConcurrentDictionary<string, DateTime> _warmed = new();

    // Releases a background check couldn't start (no peers sent the file list): not tried again in the background for a
    // while, so a dead release doesn't keep costing a torrent slot and a minute of waiting every time a page opens.
    private static readonly TimeSpan BackgroundMetadataTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeadFor = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTime> _dead = new();

    /// <param name="nextEpisode">
    /// A pre-load of the episode after the one playing: only if that setting is on, and the torrent is paused again right
    /// after, so it doesn't keep downloading alongside the episode being watched.
    /// </param>
    /// <param name="releaseId">A particular release (what a device with limits will play) instead of Auto's pick; it
    /// doesn't change Auto's remembered pick.</param>
    public void Prewarm(MediaRequest req, bool nextEpisode = false, string? releaseId = null)
    {
        var c = Plugin.Instance?.Configuration;
        var key = releaseId is null ? req.Key : req.Key + "|" + releaseId;

        // Once per title per 15 minutes: apps fetch a title's page several times (and again when playback starts).
        if ((nextEpisode ? c?.PreloadNextEpisode : c?.PrewarmOnOpen) != true
            || (_warmed.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(15))
            || !_warming.TryAdd(key, 0))
        {
            return;
        }

        _warmed[key] = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                // The next episode has most of the current one to arrive in, so it gets longer and fetches as much of the
                // start as Jellyfin's stream check reads when Play is pressed (see PlaybackInfoWindow).
                using var timeout = new CancellationTokenSource(nextEpisode ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3));
                var resolved = await ResolveAsync(req, releaseId, timeout.Token, remember: releaseId is null).ConfigureAwait(false);
                var file = resolved.Session.Manager.Files[resolved.FileIndex];

                // The same torrent is playing another of its files (a season pack): reading this one would pull the
                // download away from what's playing. It's already running, so it will start quickly anyway.
                if (resolved.Session.IsReadingOtherThan(file))
                {
                    _logger.LogInformation("Not pre-loading {Request}: its torrent is playing another file", req);
                    return;
                }

                await PreloadFileAsync(resolved.Session, file, nextEpisode ? PlaybackInfoWindow.CheckBytes : 4L << 20, timeout.Token).ConfigureAwait(false);
                if (nextEpisode)
                {
                    resolved.Session.PauseSoon();
                }

                _logger.LogInformation(nextEpisode ? "Pre-loaded the next episode, {Request}" : "Pre-loaded {Request} so playback starts sooner", req);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Pre-load of {Request} stopped: {Message}", req, ex.Message);
            }
            finally
            {
                _warming.TryRemove(key, out _);
            }
        });
    }

    /// <summary>Reads the start and end of the video, the parts players read first.</summary>
    private static async Task PreloadFileAsync(TorrentSession session, MonoTorrent.ITorrentManagerFile file, long startBytes, CancellationToken ct)
    {
        await using var lease = await session.OpenAsync(file, ct).ConfigureAwait(false);
        var buffer = new byte[256 * 1024];
        async Task ReadAsync(long from, long bytes)
        {
            lease.Position = Math.Max(0, from);
            for (long done = 0; done < bytes;)
            {
                var n = await lease.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                done += n;
            }
        }

        await ReadAsync(0, startBytes).ConfigureAwait(false);                     // headers and first seconds
        await ReadAsync(file.Length - (2L << 20), 2L << 20).ConfigureAwait(false); // index (MP4 moov / MKV cues)
    }

    // Titles whose remembered pick was chosen by a device rule (e.g. 1080p for the TV), not by ranking or by the user.
    // The remembered pick itself carries the same flag (ResolutionCache), so it survives a restart.
    // Picks are per title, not per person or device: the last Version chosen (or device rule applied) is what Auto plays
    // next for everyone, until a device without limits plays it again.
    private readonly ConcurrentDictionary<string, byte> _rulePicks = new();

    // Who chose each pinned release in the Version menu (user id), carried into the remembered pick.
    private readonly ConcurrentDictionary<string, string> _pinOwners = new();

    /// <summary>Plays <paramref name="releaseId"/> the next time this title is played through its normal source.</summary>
    /// <param name="byDeviceRule">True when a device rule chose it, so other devices go back to the best match.</param>
    /// <param name="chosenBy">The user who chose it (null for a device rule's pick or an unknown user).</param>
    public void Pin(MediaRequest req, string releaseId, bool byDeviceRule = false, string? chosenBy = null)
    {
        _pinned[req.Key] = releaseId;
        if (chosenBy is not null && !byDeviceRule)
        {
            _pinOwners[req.Key] = chosenBy;
        }
        else
        {
            _pinOwners.TryRemove(req.Key, out _);
        }

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
        var flagged = _rulePicks.TryRemove(req.Key, out _);
        var remembered = _cache.TryGet(req.Key, out var e) && e.ByDeviceRule;
        if (!flagged && !remembered)
        {
            return false;
        }

        if (flagged)
        {
            _pinned.TryRemove(req.Key, out _); // only a device rule's pin; a person's own pick made just now stays
        }

        if (remembered)
        {
            _cache.Remove(req.Key);
        }

        return true;
    }

    /// <summary>
    /// For a person playing Auto who hasn't chosen a version of this title: if the current pick is someone else's choice,
    /// forget it, so this person gets the best match. Picks from before choices were per person have no owner and stay.
    /// Returns true if it did.
    /// </summary>
    public bool ForgetOthersPick(MediaRequest req, string userId)
    {
        var changed = false;
        if (_pinOwners.TryGetValue(req.Key, out var owner) && owner != userId)
        {
            _pinned.TryRemove(req.Key, out _);
            _pinOwners.TryRemove(req.Key, out _);
            changed = true;
        }

        if (_cache.TryGet(req.Key, out var e) && e.Chosen && e.ChosenBy is { } by && by != userId)
        {
            _cache.Remove(req.Key);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Forgets this person's Version pick for the title (they finished it): their pin, and the remembered pick if it's
    /// theirs, so the next Auto play picks the best match again.
    /// </summary>
    public void ForgetPickBy(MediaRequest req, string userId)
    {
        if (_pinOwners.TryGetValue(req.Key, out var owner) && owner == userId)
        {
            _pinned.TryRemove(req.Key, out _);
            _pinOwners.TryRemove(req.Key, out _);
        }

        if (_cache.TryGet(req.Key, out var e) && e.Chosen && e.ChosenBy == userId)
        {
            _cache.Remove(req.Key);
        }
    }

    /// <summary>The current pick's title for this person: null when it's someone else's choice (Auto plays the best match for them).</summary>
    public string? CurrentPickTitleFor(MediaRequest req, string? userId) =>
        _cache.TryGet(req.Key, out var e) && !(userId is not null && e.Chosen && e.ChosenBy is { } by && by != userId)
            ? ReleaseRanker.CleanTitle(e.Title)
            : null;

    /// <summary>The release pinned to play next (a chosen version or a device rule's pick), if it's in the list.</summary>
    public ReleaseCandidate? PinnedRelease(MediaRequest req, IReadOnlyList<ReleaseCandidate> ranked) =>
        _pinned.TryGetValue(req.Key, out var id) ? ranked.FirstOrDefault(r => StreamOptions.IdFor(r) == id) : null;

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
    public string? CurrentPickTitle(MediaRequest req) => _cache.TryGet(req.Key, out var e) ? ReleaseRanker.CleanTitle(e.Title) : null; // picks remembered before names were cleaned

    /// <param name="releaseId">A release picked in the Version menu, or null for Auto (best match).</param>
    /// <param name="remember">False for background checks, which must not change Auto's remembered pick.</param>
    public async Task<ResolvedStream> ResolveAsync(MediaRequest req, string? releaseId, CancellationToken ct, bool remember = true)
    {
        NetworkGuard.Require(); // VPN/proxy down: say so, rather than searching (which may be blocked too) and reporting "nothing found"

        // ffmpeg and clients often fire several requests at once. Only one of them searches or starts a torrent.
        // A background check of one particular release (the Version menu's track checks) only locks that release:
        // starting a release that has no peers waits up to a minute for its file list, and holding the title's lock
        // meanwhile made playback of the title wait for it too (measured: 65 s).
        var gateKey = releaseId is not null && !remember ? req.Key + "|" + releaseId : req.Key;
        var gate = _locks.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (releaseId is not null)
            {
                if (!remember && _dead.TryGetValue(releaseId, out var failedAt) && DateTime.UtcNow - failedAt < DeadFor)
                {
                    throw new StreamNotFoundException("Skipped: this release couldn't be started a little while ago (no peers)");
                }

                return await ResolveChosenAsync(req, releaseId, remember, ct).ConfigureAwait(false);
            }

            if (_pinned.TryRemove(req.Key, out var pinned))
            {
                _pinOwners.TryRemove(req.Key, out var pinOwner);
                try
                {
                    return await ResolveChosenAsync(req, pinned, remember: true, ct, pinOwner).ConfigureAwait(false);
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
                NetworkGuard.Require(); // don't try the next release while the VPN/proxy is down
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

    private async Task<ResolvedStream> ResolveChosenAsync(MediaRequest req, string releaseId, bool remember, CancellationToken ct, string? chosenBy = null)
    {
        if (_started.TryGetValue(releaseId, out var known))
        {
            var session = await _engine.GetOrRestoreAsync(known.InfoHash, ct).ConfigureAwait(false);

            // The torrent is already running, but pick the file for THIS request: a season pack serves a different
            // file for each episode (the remembered index is only the one it was first started for).
            var index = session is null ? -1 : PickFile(session.Manager, req);
            if (session is not null && index >= 0)
            {
                if (remember)
                {
                    _cache.Set(req.Key, known.InfoHash, index, session.Manager.Torrent?.Name ?? releaseId, chosen: true, byDeviceRule: _rulePicks.ContainsKey(req.Key), chosenBy);
                }

                return new ResolvedStream(session, index);
            }
        }

        var releases = await _options.GetAsync(req, ct).ConfigureAwait(false);
        var chosen = releases.FirstOrDefault(r => StreamOptions.IdFor(r) == releaseId)
            ?? throw new StreamNotFoundException("That stream is no longer listed. Reopen the title to refresh the list, or use Auto.");
        // A background check (not something being played) gives up on a release with no peers sooner, and remembers it.
        var started = await TryStartAsync(req, chosen, remember, ct, chosenByUser: true, remember ? null : BackgroundMetadataTimeout, chosenBy).ConfigureAwait(false);
        if (started is null && !remember)
        {
            _dead[releaseId] = DateTime.UtcNow;
        }

        return started ?? throw new StreamNotFoundException($"Couldn't start \"{chosen.Title}\" (no peers, or no matching video file). Try another version.");
    }

    private async Task<ResolvedStream?> TryStartAsync(MediaRequest req, ReleaseCandidate release, bool remember, CancellationToken ct, bool chosenByUser = false, TimeSpan? metadataTimeout = null, string? chosenBy = null)
    {
        try
        {
            _logger.LogInformation("Trying {Release} from {Origin} ({Seeders} seeders) for {Request}", release.Title, release.Origin, release.Seeders, req);
            var source = await _torznab.ToSourceAsync(release, ct).ConfigureAwait(false);
            var session = await _engine.AddAsync(source, ct, metadataTimeout).ConfigureAwait(false);
            var index = PickFile(session.Manager, req);
            if (index >= 0)
            {
                // Remember it: Auto uses this release next time, and repeat requests skip the lookup.
                if (remember)
                {
                    _cache.Set(req.Key, source.InfoHash, index, release.Title, chosenByUser, byDeviceRule: chosenByUser && _rulePicks.ContainsKey(req.Key), chosenBy);
                }

                _started[StreamOptions.IdFor(release)] = (source.InfoHash, index);
                return new ResolvedStream(session, index);
            }

            _logger.LogInformation("No matching video file in {Release}", release.Title);
            if (session.ActiveStreams == 0)
            {
                await _engine.RemoveAsync(source.InfoHash, true).ConfigureAwait(false); // never one that's playing another file
            }
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
