using System.Diagnostics;
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
    private readonly StartStatus _status;
    private readonly UserPicks _userPicks;
    private readonly ILogger<StreamResolver> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // Release id to the torrent it turned into, so repeated requests for a chosen stream (probe, ffmpeg, seeks) don't refetch it.
    private readonly ConcurrentDictionary<string, (string InfoHash, int FileIndex)> _started = new();

    // A version chosen in the Version menu, played the next time the title's normal source is requested.
    private readonly ConcurrentDictionary<string, string> _pinned = new();

    public StreamResolver(TorrentEngine engine, TorznabClient torznab, StreamOptions options, ResolutionCache cache, StartStatus status, UserPicks userPicks, ILogger<StreamResolver> logger)
    {
        _userPicks = userPicks;
        _status = status;
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

    // How long to wait for the file list of a release the trackers report 0-1 seeders for.
    private static readonly TimeSpan NearlyDeadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeadFor = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTime> _dead = new();

    // Torrents (info hash) that started but didn't download: Auto avoids them for DeadFor.
    private readonly ConcurrentDictionary<string, DateTime> _deadHashes = new(StringComparer.OrdinalIgnoreCase);

    // A release counts as downloading once this much arrives within FlowWindow of starting (about 100 KB/s).
    private static readonly TimeSpan FlowWindow = TimeSpan.FromSeconds(30);
    private const long FlowBytes = 3L << 20;

    /// <param name="nextEpisode">
    /// A pre-load of the episode after the one playing: only if that setting is on, and the torrent is paused again right
    /// after, so it doesn't keep downloading alongside the episode being watched.
    /// </param>
    /// <param name="releaseId">A particular release (what a device with limits will play) instead of Auto's pick; it
    /// doesn't change Auto's remembered pick.</param>
    /// <param name="race">False when the device is busy playing something else: try one release at a time.</param>
    public void Prewarm(MediaRequest req, bool nextEpisode = false, string? releaseId = null, bool race = true)
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
                var resolved = await ResolveAsync(req, releaseId, timeout.Token, remember: releaseId is null, race: race && !nextEpisode).ConfigureAwait(false);
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
    /// <param name="race">False when nobody is waiting (pre-loading the next episode): releases are tried one at a time,
    /// so a pre-load never downloads two torrents alongside the episode being watched.</param>
    public async Task<ResolvedStream> ResolveAsync(MediaRequest req, string? releaseId, CancellationToken ct, bool remember = true, bool race = true)
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

            var skipRemembered = false;
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

                    // Someone's pick lasts until they finish the title; one that can't start would be tried first (and
                    // fail, at a minute a time) on every Play. Drop it: Auto plays, and the Version menu still offers it.
                    if (pinOwner is not null)
                    {
                        _userPicks.Remove(pinOwner, req.Key);
                        if (_cache.TryGet(req.Key, out var remembered) && remembered.Chosen)
                        {
                            if (remembered.ChosenBy == pinOwner)
                            {
                                _cache.Remove(req.Key); // this person's own pick
                            }
                            else
                            {
                                skipRemembered = true; // someone else's pick: this play goes to Auto, as the message says
                            }
                        }

                        _status.Report(req, ex.Message.StartsWith("Not enough disk space", StringComparison.Ordinal)
                            ? "The version you chose won't fit on the disk, so Auto is playing a smaller one instead (your pick was cleared)."
                            : ex.Message.StartsWith("That stream is no longer listed", StringComparison.Ordinal)
                            ? "The version you chose isn't available any more, so Auto is playing instead (your pick was cleared)."
                            : "The version you chose couldn't connect to any peers, so Auto is playing instead (your pick was cleared).");
                    }
                }
            }

            var cached = skipRemembered ? null : await TryCachedAsync(req, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            if (!_options.TryGetCached(req, out _))
            {
                _status.Report(req, $"Finding releases for {req}…");
            }

            var releases = await _options.GetAsync(req, ct).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                _status.Report(req, $"No releases found for {req}.");
                _status.Done(req);
                throw new StreamNotFoundException($"No suitable releases found for {req}");
            }

            return await StartBestAsync(req, releases, ct, race).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>How many releases found to have too few live seeders may be passed over (they don't count as tries).</summary>
    private const int MaxSkips = 6;

    /// <summary>
    /// Auto: tries the best releases two at a time (whichever downloads fast enough first plays; the other is removed),
    /// following the current ranking (live seeder counts arrive in the background). Just before a release is tried its
    /// live seeders are checked, and one with 0-1 is passed over while others remain. The last release tried is kept
    /// even if slow: slow beats nothing.
    /// </summary>
    private async Task<ResolvedStream> StartBestAsync(MediaRequest req, List<ReleaseCandidate> releases, CancellationToken ct, bool race = true)
    {
        var perRound = race ? 2 : 1;
        var tries = Math.Max(1, Plugin.Instance!.Configuration.CandidatesToTry);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attempts = 0;
        var skips = 0;
        var tooBig = 0;

        List<ReleaseCandidate> Remaining()
        {
            var pool = (_options.TryGetCached(req, out var latest) ? latest : releases)
                .Where(r => !seen.Contains(StreamOptions.IdFor(r)))
                .ToList();

            // Releases that started but didn't download a little while ago are left out (unless nothing else is left).
            var fresh = pool.Where(r => !IsDead(r)).ToList();
            return fresh.Count == 0 && seen.Count == 0 ? pool : fresh;
        }

        while (attempts < tries)
        {
            var fresh = Remaining();
            if (fresh.Count == 0)
            {
                break;
            }

            var round = new List<ReleaseCandidate>();
            for (var i = 0; i < fresh.Count && round.Count < perRound && attempts + round.Count < tries; i++)
            {
                var release = fresh[i];
                seen.Add(StreamOptions.IdFor(release));
                NetworkGuard.Require(); // don't try the next release while the VPN/proxy is down

                // A film whose size is listed and clearly can't fit (even after removing idle torrents): don't start it.
                if (req.Kind == MediaKind.Movie && release.Size > 0 && DiskSpace.FreeForDownloads() is long free
                    && !DiskSpace.Fits(release.Size + DiskSpace.Margin, free, _engine.PromisedBytes(), _engine.ReclaimableBytes()))
                {
                    tooBig++;
                    _logger.LogWarning("Skipping {Release}: {Size} won't fit on the disk ({Free} free, {Promised} still needed by what's playing)", release.Title, DiskSpace.Gb(release.Size), DiskSpace.Gb(free), DiskSpace.Gb(_engine.PromisedBytes()));
                    continue;
                }

                // Its real seeders, just before starting it (sites' counts are often stale).
                var live = await LiveSeedersAsync(release, ct).ConfigureAwait(false);
                if (live <= 1 && i < fresh.Count - 1 && skips < MaxSkips)
                {
                    skips++;
                    _logger.LogInformation("Skipping {Release}: the trackers report {Seeders} seeders", release.Title, live);
                    _status.Report(req, $"Skipping {Label(release, seeds: false)}: only {live} live seeder{(live == 1 ? string.Empty : "s")}");
                    continue;
                }

                if (round.Count == 1 && !string.IsNullOrEmpty(release.InfoHash)
                    && string.Equals(round[0].InfoHash, release.InfoHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the same torrent under another site's listing
                }

                round.Add(release);
            }

            if (round.Count == 0)
            {
                continue;
            }

            attempts += round.Count;
            var final = attempts >= tries || Remaining().Count == 0;

            ResolvedStream? started;
            using (var ticker = new CancellationTokenSource())
            {
                var progress = ReportProgressAsync(req, round, ticker.Token);
                try
                {
                    if (round.Count == 1)
                    {
                        _status.Report(req, $"Trying {Label(round[0])}…");
                        var one = await StartCoreAsync(req, round[0], ct, remember: true, checkFlow: !final, keepSlow: false).ConfigureAwait(false);
                        started = one.Ok;
                        if (started is not null)
                        {
                            Commit(req, round[0], started, remember: true);
                        }
                    }
                    else
                    {
                        _status.Report(req, $"Trying 2 releases at once: {Label(round[0])} and {Label(round[1])}…");
                        started = await RaceAsync(req, round[0], round[1], final, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await ticker.CancelAsync().ConfigureAwait(false);
                    await progress.ConfigureAwait(false);
                }
            }

            if (started is not null)
            {
                var peek = _engine.Peek(started.Session.InfoHash);
                _status.Report(req, peek is { BytesPerSecond: > 0 } p ? $"Starting playback ({Speed(p.BytesPerSecond)})…" : "Starting playback…");
                return started;
            }
        }

        _status.Report(req, attempts == 0 && skips > 0
            ? $"Couldn't play {req}: no release has enough seeders right now."
            : $"Couldn't play {req}: the releases tried had no peers or were too slow. Try another version, or later.");
        _status.Done(req);
        throw new StreamNotFoundException(tooBig > 0
            ? $"None of the top {tries} releases for {req} could be started; {tooBig} didn't fit on the disk ({DiskSpace.Gb(DiskSpace.FreeForDownloads() ?? 0)} free)"
            : $"None of the top {tries} releases for {req} could be started");
    }

    /// <summary>
    /// Starts two releases at once. The first that downloads fast enough plays, and the other is stopped and removed.
    /// If neither is fast enough: on the last round the one that got more is kept (slow beats nothing), else both go.
    /// </summary>
    private async Task<ResolvedStream?> RaceAsync(MediaRequest req, ReleaseCandidate a, ReleaseCandidate b, bool final, CancellationToken ct)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entries = new[] { a, b }
            .Select(r => (Release: r, Task: StartCoreAsync(req, r, race.Token, remember: true, checkFlow: true, keepSlow: final)))
            .ToList();
        var pending = entries.Select(e => e.Task).ToList();
        var slow = new List<(ReleaseCandidate Release, ResolvedStream Stream)>();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            StartResult result;
            try
            {
                result = await done.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                continue;
            }

            var release = entries.First(e => e.Task == done).Release;
            if (result.Ok is { } ok)
            {
                // The other one stops at once (and removes its own torrent unless something else reads it).
                await race.CancelAsync().ConfigureAwait(false);
                foreach (var (_, stream) in slow)
                {
                    await DiscardAsync(stream, ok.Session.InfoHash).ConfigureAwait(false);
                }

                _logger.LogInformation("{Release} won the start-up race", release.Title);
                Commit(req, release, ok, remember: true);
                _ = Task.WhenAll(pending).ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return ok;
            }

            if (result.Slow is { } s)
            {
                slow.Add((release, s));
            }
        }

        ct.ThrowIfCancellationRequested();
        if (slow.Count == 0)
        {
            return null;
        }

        // Neither was fast enough. Last round: keep the one that got more; otherwise remove both and try the next ones.
        var keep = final ? slow.OrderByDescending(x => x.Stream.Session.Manager.Monitor.DataBytesReceived).First() : default;
        foreach (var (release, stream) in slow)
        {
            if (keep.Stream is null || !ReferenceEquals(stream, keep.Stream))
            {
                MarkDead(release, stream.Session.InfoHash);
                await DiscardAsync(stream, keep.Stream?.Session.InfoHash).ConfigureAwait(false);
            }
        }

        if (keep.Stream is null)
        {
            return null;
        }

        _logger.LogInformation("Keeping {Release} although it's slow: it's the last one to try", keep.Release.Title);
        Commit(req, keep.Release, keep.Stream, remember: true);
        return keep.Stream;
    }

    /// <summary>Removes a started torrent that isn't used, unless something else reads it or it's <paramref name="keepHash"/>.</summary>
    private async Task DiscardAsync(ResolvedStream stream, string? keepHash)
    {
        if (stream.Session.ActiveStreams == 0 && !string.Equals(stream.Session.InfoHash, keepHash, StringComparison.OrdinalIgnoreCase))
        {
            await _engine.RemoveAsync(stream.Session.InfoHash, true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A release's seeders as the trackers report them now (instant if checked in the last 15 minutes), or null if
    /// unknown (no torrent id, or no tracker answered). A listing without its torrent id has its page fetched for it.
    /// </summary>
    private async Task<int?> LiveSeedersAsync(ReleaseCandidate r, CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.CheckLiveSeeders != true || r.Seeders < 0)
        {
            return null;
        }

        if (r.LiveSeeders)
        {
            return r.Seeders;
        }

        try
        {
            if (string.IsNullOrEmpty(r.InfoHash) && (TorznabClient.IsMagnet(r.MagnetUrl) || !string.IsNullOrEmpty(r.MagnetPageUrl)))
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                var source = await _torznab.ToSourceAsync(r, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
                r.InfoHash = source.InfoHash;
                if (source.Magnet is { } magnet)
                {
                    r.MagnetUrl = magnet; // so starting it doesn't fetch the page again
                }
            }

            if (string.IsNullOrEmpty(r.InfoHash))
            {
                return null;
            }

            var hash = r.InfoHash.ToLowerInvariant();
            var live = await TrackerScrape.ScrapeAsync([hash], TimeSpan.FromSeconds(2.5), ct, TrackerScrape.TrackersOf(r.MagnetUrl ?? r.DownloadUrl)).ConfigureAwait(false);
            if (live.TryGetValue(hash, out var counts))
            {
                r.Seeders = counts.Seeders;
                r.LiveSeeders = true;
                return counts.Seeders;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // unknown: try it as listed
        }

        return null;
    }

    /// <summary>While releases start, tells whoever pressed Play how the best of them is doing (every few seconds).</summary>
    private async Task ReportProgressAsync(MediaRequest req, List<ReleaseCandidate> round, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (_status.IsWatched(req))
                {
                    var states = round
                        .Where(r => !string.IsNullOrEmpty(r.InfoHash))
                        .Select(r => (Release: r, State: _engine.Peek(r.InfoHash!.ToLowerInvariant())))
                        .Where(x => x.State is not null)
                        .Select(x => (x.Release, State: x.State!.Value))
                        .OrderByDescending(x => x.State.HasMetadata)
                        .ThenByDescending(x => x.State.BytesPerSecond)
                        .ThenByDescending(x => x.State.Peers)
                        .ToList();
                    if (states.Count > 0)
                    {
                        var (release, st) = states[0];
                        var who = round.Count > 1 ? $"best so far: {Label(release, seeds: false)}" : Label(release, seeds: false);
                        _status.Report(req, st.HasMetadata
                            ? $"Downloading the start ({who}): {st.Peers} peers, {Speed(st.BytesPerSecond)}, {st.Received >> 20} MB so far"
                            : $"Finding peers ({who}): {st.Peers} connected");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A short description of a release for start-up messages: "1080p, EZTV, 44 live seeders".</summary>
    private static string Label(ReleaseCandidate r, bool seeds = true)
    {
        var parts = new List<string>();
        if (r.Resolution > 0)
        {
            parts.Add(r.Resolution >= 2160 ? "4K" : r.Resolution + "p");
        }

        parts.Add(string.IsNullOrEmpty(r.Origin) ? "release" : r.Origin);
        if (seeds && r.Seeders >= 0)
        {
            parts.Add($"{r.Seeders} {(r.LiveSeeders ? "live " : string.Empty)}seeder{(r.Seeders == 1 ? string.Empty : "s")}");
        }

        return string.Join(", ", parts);
    }

    private static string Speed(long bytesPerSecond) =>
        bytesPerSecond >= 1 << 20 ? $"{bytesPerSecond / 1048576.0:0.0} MB/s" : $"{bytesPerSecond >> 10} KB/s";

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
        // Also a release that dropped out of the list after the Version menu was shown (re-ranked by live counts).
        var chosen = releases.FirstOrDefault(r => StreamOptions.IdFor(r) == releaseId)
            ?? _options.FindRelease(releaseId)
            ?? throw new StreamNotFoundException("That stream is no longer listed. Reopen the title to refresh the list, or use Auto.");
        if (remember)
        {
            await LiveSeedersAsync(chosen, ct).ConfigureAwait(false); // for the message, and a shorter wait if it has (almost) none
            _status.Report(req, $"Starting the version you chose ({Label(chosen)})…");
        }

        // A background check (not something being played) gives up on a release with no peers sooner, and remembers it.
        ResolvedStream? started;
        using (var ticker = new CancellationTokenSource())
        {
            var progress = remember ? ReportProgressAsync(req, [chosen], ticker.Token) : Task.CompletedTask;
            try
            {
                started = await TryStartAsync(req, chosen, remember, ct, chosenByUser: true, remember ? null : BackgroundMetadataTimeout, chosenBy).ConfigureAwait(false);
            }
            finally
            {
                await ticker.CancelAsync().ConfigureAwait(false);
                await progress.ConfigureAwait(false);
            }
        }

        // Remembered as dead either way, so Auto (falling back from a chosen version) doesn't try the same torrent again.
        if (started is null)
        {
            _dead[releaseId] = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(chosen.InfoHash))
            {
                _deadHashes[chosen.InfoHash] = DateTime.UtcNow;
            }
        }

        if (started is null && remember)
        {
            _status.Report(req, $"Couldn't start the version you chose ({Label(chosen)}): no peers answered.");
        }

        return started ?? throw new StreamNotFoundException($"Couldn't start \"{chosen.Title}\" (no peers, or no matching video file). Try another version.");
    }

    /// <summary>What starting a release gave: a stream to play, or (when asked to keep it) one that downloads too slowly.</summary>
    private sealed record StartResult(ResolvedStream? Ok, ResolvedStream? Slow)
    {
        public static readonly StartResult Failed = new(null, null);
    }

    private async Task<ResolvedStream?> TryStartAsync(MediaRequest req, ReleaseCandidate release, bool remember, CancellationToken ct, bool chosenByUser = false, TimeSpan? metadataTimeout = null, string? chosenBy = null, bool checkFlow = false)
    {
        var result = await StartCoreAsync(req, release, ct, remember, checkFlow, keepSlow: false, chosenByUser, metadataTimeout).ConfigureAwait(false);
        if (result.Ok is { } ok)
        {
            Commit(req, release, ok, remember, chosenByUser, chosenBy);
        }

        return result.Ok;
    }

    /// <summary>Remembers a started release: Auto uses it next time, and repeat requests skip the lookup.</summary>
    private void Commit(MediaRequest req, ReleaseCandidate release, ResolvedStream stream, bool remember, bool chosenByUser = false, string? chosenBy = null)
    {
        if (remember)
        {
            _cache.Set(req.Key, stream.Session.InfoHash, stream.FileIndex, release.Title, chosenByUser, byDeviceRule: chosenByUser && _rulePicks.ContainsKey(req.Key), chosenBy);
        }

        _started[StreamOptions.IdFor(release)] = (stream.Session.InfoHash, stream.FileIndex);
    }

    /// <summary>
    /// Starts a release and finds the file to play. With <paramref name="checkFlow"/>, also makes sure it downloads fast
    /// enough (listed seeder counts are often stale; a "31 seeders" release can have one slow peer): a slow one is
    /// removed, or returned as <see cref="StartResult.Slow"/> with <paramref name="keepSlow"/>. If <paramref name="ct"/>
    /// is cancelled (the player left, or another release won a race), a torrent this started is removed again.
    /// </summary>
    private async Task<StartResult> StartCoreAsync(MediaRequest req, ReleaseCandidate release, CancellationToken ct, bool remember, bool checkFlow, bool keepSlow, bool chosenByUser = false, TimeSpan? metadataTimeout = null)
    {
        try
        {
            _logger.LogInformation("Trying {Release} from {Origin} ({Seeders}{Live:l} seeders) for {Request}", release.Title, release.Origin, release.Seeders, release.LiveSeeders ? " live" : string.Empty, req);
            var source = await _torznab.ToSourceAsync(release, ct).ConfigureAwait(false);

            // The trackers know of (almost) no seeders: don't wait the full time for the file list.
            if (release.LiveSeeders && release.Seeders <= 1)
            {
                var full = metadataTimeout ?? TimeSpan.FromSeconds(Math.Max(10, Plugin.Instance!.Configuration.MetadataTimeoutSeconds));
                metadataTimeout = full < NearlyDeadTimeout ? full : NearlyDeadTimeout;
            }

            var session = await _engine.AddAsync(source, ct, metadataTimeout).ConfigureAwait(false);
            var index = PickFile(session.Manager, req);
            if (index < 0)
            {
                _logger.LogInformation("No matching video file in {Release}", release.Title);
                if (session.ActiveStreams == 0)
                {
                    await _engine.RemoveAsync(source.InfoHash, true).ConfigureAwait(false); // never one that's playing another file
                }

                return StartResult.Failed;
            }

            if (remember)
            {
                await EnsureRoomAsync(session, index, release).ConfigureAwait(false);
            }

            if (checkFlow && !await DataFlowsAsync(session, index, req, ct, remember ? Label(release, seeds: false) : null).ConfigureAwait(false))
            {
                if (keepSlow)
                {
                    return new StartResult(null, new ResolvedStream(session, index));
                }

                MarkDead(release, session.InfoHash);
                if (session.ActiveStreams == 0)
                {
                    await _engine.RemoveAsync(source.InfoHash, true).ConfigureAwait(false);
                }

                return StartResult.Failed;
            }

            return new StartResult(new ResolvedStream(session, index), null);
        }
        catch (NotEnoughSpaceException ex) when (chosenByUser)
        {
            throw new StreamNotFoundException(ex.Message); // a version someone picked: say why, don't quietly play another
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Lost a race (or the player left). The torrent isn't removed here: another part of the plugin (a track
            // check) may have started the same one at the same moment and be using it. Unread, it stops downloading
            // within a minute, and the idle clean-up removes it.
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Candidate {Release} failed: {Message}", release.Title, ex.Message);
            if (remember)
            {
                _status.Report(req, ex is TimeoutException
                    ? $"No peers answered for {Label(release, seeds: false)}"
                    : $"{Label(release, seeds: false)} couldn't be started");
            }
        }

        return StartResult.Failed;
    }

    private bool IsDead(ReleaseCandidate r) =>
        (_dead.TryGetValue(StreamOptions.IdFor(r), out var at) && DateTime.UtcNow - at < DeadFor)
        || (!string.IsNullOrEmpty(r.InfoHash) && _deadHashes.TryGetValue(r.InfoHash, out var hashAt) && DateTime.UtcNow - hashAt < DeadFor);

    private void MarkDead(ReleaseCandidate release, string infoHash)
    {
        _dead[StreamOptions.IdFor(release)] = DateTime.UtcNow;
        _deadHashes[infoHash] = DateTime.UtcNow;
    }

    /// <summary>
    /// True if the release downloads fast enough to play: within <see cref="FlowWindow"/>, at least half the rate its
    /// video needs (its size over a typical running time: 2 hours for a film, 50 minutes for an episode), and never less
    /// than <see cref="FlowBytes"/>. Reading the start is what makes the torrent fetch it. A start already on disk passes
    /// at once.
    /// </summary>
    private async Task<bool> DataFlowsAsync(TorrentSession session, int index, MediaRequest req, CancellationToken ct, string? label = null)
    {
        var file = session.Manager.Files[index];
        var seconds = req.Kind == MediaKind.Movie ? 7200.0 : 3000.0;
        var needRate = file.Length / seconds;                            // bytes per second the video plays at, roughly
        var needBytes = Math.Max(FlowBytes, (long)(needRate * 0.5 * FlowWindow.TotalSeconds));
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(FlowWindow);
        var before = session.Manager.Monitor.DataBytesReceived;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var lease = await session.OpenAsync(file, window.Token).ConfigureAwait(false);
            var read = lease.ReadAsync(new byte[16 * 1024], window.Token).AsTask();
            while (true)
            {
                var got = session.Manager.Monitor.DataBytesReceived - before;
                if (got >= needBytes)
                {
                    return true;
                }

                // The start was already on disk (nothing to download yet): nothing to measure, it plays.
                if (read.IsCompletedSuccessfully && got < 64 * 1024)
                {
                    return true;
                }

                // The start arrived quickly: judge by the rate so far rather than waiting out the window.
                if (read.IsCompletedSuccessfully && sw.Elapsed.TotalSeconds >= 5 && got / sw.Elapsed.TotalSeconds >= needRate * 0.5)
                {
                    return true;
                }

                if (read.IsFaulted || read.IsCanceled || window.IsCancellationRequested)
                {
                    break;
                }

                // Once the first read is done it can't wake the loop any more: wait on the clock alone (awaiting a
                // finished task returns at once and spun a CPU core flat out, holding the title's lock).
                try
                {
                    await (read.IsCompleted ? Task.Delay(500, window.Token) : Task.WhenAny(read, Task.Delay(1000, window.Token))).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // the window is over (or the caller stopped)
                }
            }

            // Stopped from outside (another release won the race, or the player left): not a verdict on this release.
            ct.ThrowIfCancellationRequested();
            LogNotDownloading(session, before, needRate, req, label);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogNotDownloading(session, before, needRate, req, label);
            return false;
        }
    }

    private void LogNotDownloading(TorrentSession session, long before, double needRate, MediaRequest req, string? label)
    {
        var got = session.Manager.Monitor.DataBytesReceived - before;
        _logger.LogWarning(
            "{Name} is too slow to play ({Kb} KB in {Seconds} s; it needs about {Need} KB/s); trying the next release",
            session.Manager.Torrent?.Name ?? session.InfoHash,
            got >> 10,
            (int)FlowWindow.TotalSeconds,
            (int)(needRate / 1024));
        _status.Report(req, $"{label ?? "This release"} is too slow ({Speed((long)(got / FlowWindow.TotalSeconds))}; it needs about {Speed((long)needRate)})");
    }

    /// <summary>
    /// Watching a release to the end downloads all of its file. If the disk hasn't room for what's left of it (plus a
    /// margin for Jellyfin's own files), remove torrents nobody is watching; if it still won't fit, give it up with a
    /// clear reason instead of failing with "no space left" near the end.
    /// </summary>
    private async Task EnsureRoomAsync(TorrentSession session, int index, ReleaseCandidate release)
    {
        // What's still to download of this file, plus what the titles already being watched still need: two big
        // films started together must not each count the same free space.
        var need = TorrentEngine.RemainingBytes(session.Manager, index) + DiskSpace.Margin;
        var promised = _engine.PromisedBytes(session.InfoHash);
        if (DiskSpace.FreeForDownloads() is not long free || DiskSpace.Fits(need, free, promised))
        {
            return;
        }

        if (await _engine.MakeRoomAsync(need + promised, session.InfoHash).ConfigureAwait(false))
        {
            return;
        }

        var size = session.Manager.Files[index].Length;
        var left = Math.Max(0, (DiskSpace.FreeForDownloads() ?? 0) - promised);
        if (session.ActiveStreams == 0)
        {
            await _engine.RemoveAsync(session.InfoHash, true).ConfigureAwait(false);
        }

        var playing = promised > 0 ? $" once what's already playing has finished downloading" : string.Empty;
        throw new NotEnoughSpaceException($"Not enough disk space for \"{release.Title}\": it needs {DiskSpace.Gb(size)} and {DiskSpace.Gb(left)} is free{playing}. Pick a smaller version, or free some space.");
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
                // Auto's remembered pick: if it has stopped downloading (its swarm died), search again instead.
                // Only before playback: not on every range request (seeks) of a title that is already playing.
                if (!entry.Chosen && session.FilesBeingWatched.Count == 0 && !await DataFlowsAsync(session, entry.FileIndex, req, ct).ConfigureAwait(false))
                {
                    _deadHashes[entry.InfoHash] = DateTime.UtcNow;
                    _logger.LogWarning("{Title} (remembered for {Request}) isn't downloading any more; picking another release", entry.Title, req);
                    _cache.Remove(req.Key);
                    return null;
                }

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
