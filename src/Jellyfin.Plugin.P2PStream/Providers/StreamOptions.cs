using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.P2PStream.Catalog;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

using Jellyfin.Plugin.P2PStream.Torrent;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// The list of releases ("Available streams") for a title: searched once, ranked, cached for 30 minutes and shared by
/// the Version menu, playback of a chosen release, and Auto.
/// </summary>
public sealed class StreamOptions
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(30);

    private readonly SourceHub _sources;
    private readonly TmdbClient _tmdb;
    private readonly ILogger<StreamOptions> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, List<ReleaseCandidate> Releases, string Rules)> _cache = new();
    // Lazy: two callers racing for the same title share one search (GetOrAdd may run its factory twice).
    private readonly ConcurrentDictionary<string, Lazy<Task<List<ReleaseCandidate>>>> _inflight = new();
    private readonly ConcurrentDictionary<string, ProbeInfo> _probes = new();

    // Release lists kept at most (oldest dropped first) and checked tracks kept at most; enough for a big library.
    private const int MaxCachedLists = 500;
    private const int MaxProbes = 5000;

    private readonly TorznabClient _torznab;

    public StreamOptions(SourceHub sources, TmdbClient tmdb, TorznabClient torznab, ILogger<StreamOptions> logger)
    {
        _torznab = torznab;
        _sources = sources;
        _tmdb = tmdb;
        _logger = logger;
    }

    /// <summary>Titles whose release list is having its live seeder counts checked in the background.</summary>
    private readonly ConcurrentDictionary<string, byte> _liveChecking = new();

    /// <summary>Raised when a title's background live seeder check has finished (its Version list is complete).</summary>
    public event Action<MediaRequest>? LiveCheckFinished;

    /// <summary>Tells listeners (StartStatus) that a title's release list is complete.</summary>
    internal void NotifyLiveCheckFinished(MediaRequest request)
    {
        try
        {
            LiveCheckFinished?.Invoke(request);
        }
        catch (Exception)
        {
            // a listener's problem
        }
    }

    /// <summary>True while a title's live seeder counts are still being checked in the background (or its search runs).</summary>
    public bool IsCheckingLive(MediaRequest request) => _liveChecking.ContainsKey(request.Key) || _inflight.ContainsKey(request.Key);

    /// <summary>A stable id for a release, also used as the Jellyfin media source id.</summary>
    public static string IdFor(ReleaseCandidate r)
    {
        // A listing whose torrent id is only on its page keeps its page as its identity: the torrent id gets filled in
        // later (live seeder check, starting it), and the release's id must not change then (picks and menus use it).
        var identity = !string.IsNullOrEmpty(r.MagnetPageUrl)
            ? "u:" + r.MagnetPageUrl
            : !string.IsNullOrEmpty(r.InfoHash)
                ? "h:" + r.InfoHash.ToLowerInvariant()
                : "u:" + (r.DownloadUrl ?? r.MagnetUrl ?? r.Title);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    /// <summary>The Version menu label, Elementum-style: "1080p · 8.0 GB · 1234 seeds · BluRay · KickAss — release name".</summary>
    public static string Label(ReleaseCandidate r)
    {
        var parts = new List<string>
        {
            r.Resolution switch { 2160 => "4K", 0 => "Unknown", var x => x.ToString(CultureInfo.InvariantCulture) + "p" }
        };
        if (r.Size > 0)
        {
            parts.Add(r.Size >= 1L << 30
                ? (r.Size / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                : (r.Size / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB");
        }

        parts.Add(r.Seeders >= 0 ? r.Seeders.ToString(CultureInfo.InvariantCulture) + (r.LiveSeeders ? " seeds live" : " seeds") : "seeds ?");
        var type = ReleaseRanker.DetectReleaseType(r.Title);
        var typeLabel = ReleaseRanker.ReleaseTypes.FirstOrDefault(t => t.Id == type).Label;
        if (typeLabel is not null)
        {
            parts.Add(typeLabel.Split('/')[^1]);
        }

        parts.Add(r.Origin);
        var title = r.Title.Length > 90 ? r.Title[..87] + "..." : r.Title;
        return string.Join(" · ", parts) + " — " + title;
    }

    /// <summary>The ranking rules plus which sources are searched: a list made under other ones is searched again.</summary>
    private static string ListStamp()
    {
        var c = Plugin.Instance!.Configuration;
        var sources = string.Join(',', (c.Sources ?? []).Select(x => $"{x.Id}:{x.Enabled}:{x.BaseUrl}"))
            + ";" + string.Join(',', (c.Indexers ?? []).Select(x => $"{x.Url}:{x.Enabled}"))
            + ";" + string.Join(',', (c.RssSources ?? []).Select(x => $"{x.Url}:{x.Enabled}"));
        return ReleaseRanker.RulesStamp() + "|" + sources;
    }

    public bool TryGetCached(MediaRequest request, out List<ReleaseCandidate> releases)
    {
        // A list ranked under other settings (e.g. preferred resolution just changed) is searched and ranked again.
        if (_cache.TryGetValue(request.Key, out var hit) && DateTime.UtcNow - hit.At < CacheAge && hit.Rules == ListStamp())
        {
            // Ranked again on every read: live seeder counts arrive in the background (and as releases are tried), and
            // a release found to have too few seeders must drop out, and the order follow, at once.
            releases = ReleaseRanker.Rank(hit.Releases, request);
            if (releases.Count > 0)
            {
                return true;
            }
        }

        releases = [];
        return false;
    }

    /// <summary>
    /// Gets the ranked releases for a title. Only one search per title runs at a time. A caller that stops waiting
    /// doesn't cancel it, so the result is cached for the next caller.
    /// </summary>
    public Task<List<ReleaseCandidate>> GetAsync(MediaRequest request, CancellationToken ct)
    {
        if (TryGetCached(request, out var cached))
        {
            return Task.FromResult(cached);
        }

        var search = _inflight.GetOrAdd(request.Key, _ => new Lazy<Task<List<ReleaseCandidate>>>(() => Task.Run(() => SearchAsync(request))));
        return search.Value.WaitAsync(ct);
    }

    /// <summary>Tracks found by checking a release in advance (shown in the Version menu).</summary>
    /// <summary>
    /// The key a release's checked tracks are stored under: the release AND the episode, since a season pack holds a
    /// different file (with different tracks) for each episode.
    /// </summary>
    public static string ProbeKey(MediaRequest? request, string releaseId) => request is null ? releaseId : releaseId + "|" + request.Key;

    public bool TryGetProbe(string releaseId, out ProbeInfo info) => _probes.TryGetValue(releaseId, out info!);

    public void SetProbe(string releaseId, ProbeInfo info)
    {
        if (_probes.Count >= MaxProbes)
        {
            _probes.Clear(); // rare; tracks are checked again when needed
        }

        _probes[releaseId] = info;
    }

    /// <summary>How many of the top releases get their seeders checked with the trackers.</summary>
    private const int LiveCheckCount = 12;

    /// <summary>
    /// Replaces the top releases' listed seeder counts with what the trackers report now. Releases listed without their
    /// torrent id (KickAss) have their page fetched for it first (at most 5). Capped at a few seconds; anything not
    /// answered in time keeps its listed count.
    /// </summary>
    private async Task CheckLiveSeedersAsync(List<ReleaseCandidate> ranked, MediaRequest request, bool followUp = false)
    {
        var top = ranked.Where(r => !r.LiveSeeders && r.Seeders >= 0).Take(followUp ? 2 * LiveCheckCount : LiveCheckCount).ToList();
        if (top.Count == 0)
        {
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Releases whose torrent id is known are scraped straight away, while the others' pages are fetched for theirs.
        var known = top.Where(r => !string.IsNullOrEmpty(r.InfoHash)).ToList();
        // The first scrape runs alongside the page lookups below (2.5 s, then 2 s to scrape what they found), so it can
        // take that whole time without making the page wait longer.
        var scrapeLimit = TimeSpan.FromSeconds(followUp ? 6 : 4.5);
        var firstScrape = TrackerScrape.ScrapeAsync(known.Select(r => r.InfoHash!), scrapeLimit, CancellationToken.None, known.SelectMany(r => TrackerScrape.TrackersOf(r.MagnetUrl ?? r.DownloadUrl)));

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(followUp ? 10 : 2.5));
        var lookedUp = top.Where(r => string.IsNullOrEmpty(r.InfoHash) && !string.IsNullOrEmpty(r.MagnetPageUrl)).Take(followUp ? 12 : 5).ToList();
        await Task.WhenAll(lookedUp.Select(async r =>
        {
            try
            {
                var source = await _torznab.ToSourceAsync(r, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
                r.InfoHash = source.InfoHash;
                if (source.Magnet is { } magnet)
                {
                    r.MagnetUrl = magnet; // so starting it doesn't fetch the page again
                }
            }
            catch (Exception)
            {
                // keeps its listed count
            }
        })).ConfigureAwait(false);

        var found = lookedUp.Where(r => !string.IsNullOrEmpty(r.InfoHash)).ToList();
        var live = new Dictionary<string, TrackerScrape.Result>(await firstScrape.ConfigureAwait(false));
        if (found.Count > 0)
        {
            foreach (var (hash, result) in await TrackerScrape.ScrapeAsync(found.Select(r => r.InfoHash!), followUp ? scrapeLimit : TimeSpan.FromSeconds(2), CancellationToken.None, found.SelectMany(r => TrackerScrape.TrackersOf(r.MagnetUrl))).ConfigureAwait(false))
            {
                live[hash] = result;
            }
        }

        var withHash = known.Concat(found).ToList();
        var changed = 0;
        foreach (var r in withHash)
        {
            if (live.TryGetValue(r.InfoHash!.ToLowerInvariant(), out var counts))
            {
                if (counts.Seeders != r.Seeders)
                {
                    changed++;
                }

                r.Seeders = counts.Seeders;
                r.LiveSeeders = true;
            }
        }

        _logger.LogInformation("Checked live seeders of {Count} releases for {Request} in {Ms} ms ({Changed} differed from the sites' counts){Later:l}", live.Count, request, sw.ElapsedMilliseconds, changed, followUp ? ", after the list was shown" : string.Empty);
    }

    /// <summary>The top releases, plus the best few of every resolution, so "Best 1080p" exists even when 4K fills the top.</summary>
    private static List<ReleaseCandidate> Pick(List<ReleaseCandidate> ranked)
    {
        var c = Plugin.Instance!.Configuration;
        var keep = Math.Max(Math.Clamp(c.StreamPickerMax, 1, 50), Math.Max(1, c.CandidatesToTry));
        var chosen = new HashSet<ReleaseCandidate>(ranked.Take(keep));
        foreach (var group in ranked.GroupBy(r => r.Resolution))
        {
            chosen.UnionWith(group.Take(3));
        }

        return ranked.Where(chosen.Contains).ToList(); // keep the ranking order
    }

    /// <summary>
    /// The same torrent from several sources: keep one copy, preferring a live-checked count, then the most seeders by
    /// the count ranking trusts (a source whose counts run high doesn't win just by its inflated figure).
    /// </summary>
    private static List<ReleaseCandidate> Dedupe(IEnumerable<ReleaseCandidate> releases) =>
        releases
            .GroupBy(r => string.IsNullOrEmpty(r.InfoHash) ? "u:" + (r.DownloadUrl ?? r.MagnetPageUrl ?? r.Title) : r.InfoHash!.ToLowerInvariant())
            .Select(g => ReleaseRanker.KeepOne(g))
            .ToList();

    /// <summary>Drops expired lists, then the oldest ones over <see cref="MaxCachedLists"/>.</summary>
    private void TrimCache()
    {
        var now = DateTime.UtcNow;
        foreach (var old in _cache.Where(kv => now - kv.Value.At >= CacheAge).Select(kv => kv.Key).ToList())
        {
            _cache.TryRemove(old, out _);
        }

        var extra = _cache.Count - MaxCachedLists;
        if (extra > 0)
        {
            foreach (var old in _cache.OrderBy(kv => kv.Value.At).Take(extra).Select(kv => kv.Key).ToList())
            {
                _cache.TryRemove(old, out _);
            }
        }
    }

    /// <summary>Forgets the list for a title (e.g. after its release went stale).</summary>
    public void Invalidate(MediaRequest request) => _cache.TryRemove(request.Key, out _);

    /// <summary>Fills in title, year and IMDb id from TMDB, which the sources need to search.</summary>
    public async Task EnrichAsync(MediaRequest req, CancellationToken ct)
    {
        if (req.TmdbId is not int id || !_tmdb.IsConfigured || (req.Title is not null && req.ImdbId is not null && req.IsAnime is not null))
        {
            return;
        }

        try
        {
            if (req.Kind == MediaKind.Movie)
            {
                var m = await _tmdb.GetMovieAsync(id, TmdbClient.SearchLanguage, ct).ConfigureAwait(false);
                req.Title ??= m.Title;
                req.Year ??= m.Year;
                req.ImdbId ??= m.ImdbId;
                req.IsAnime = m.IsAnime;
                req.AltTitles = m.AltTitles;
            }
            else
            {
                var s = await _tmdb.GetShowAsync(id, TmdbClient.SearchLanguage, ct).ConfigureAwait(false);
                req.Title ??= s.Name;
                req.Year ??= s.Year;
                req.ImdbId ??= s.ImdbId;
                req.IsAnime = s.IsAnime;
                req.AltTitles = s.AltTitles;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("TMDB lookup failed for {Request}: {Message}", req, ex.Message);
        }
    }

    private async Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request)
    {
        try
        {
            await EnrichAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.ImdbId))
            {
                // Without a title or IMDb id no source can search; don't record that as every source finding nothing.
                throw new StreamNotFoundException("Couldn't look this title up on TMDB, so there's nothing to search for. Check the TMDB key, and the proxy if one is set.");
            }

            // Everything the sources returned, so releases from a slow source can be ranked in with them later.
            var found = new List<ReleaseCandidate>();
            var foundLock = new object();
            var stamp = ListStamp();
            void AddLate(string source, List<ReleaseCandidate> late)
            {
                List<ReleaseCandidate> merged;
                lock (foundLock)
                {
                    found = Dedupe(found.Concat(late));
                    merged = Pick(ReleaseRanker.Rank(found, request));
                }

                // Only into a list that's still current (not one ranked under other settings, or already replaced).
                if (merged.Count > 0 && _cache.TryGetValue(request.Key, out var current) && current.Rules == stamp && current.Rules == ListStamp())
                {
                    _cache[request.Key] = (current.At, merged, stamp);
                    _logger.LogInformation("Added {Count} late releases from {Source} to {Request}", late.Count, source, request);
                    FollowUp(); // their live counts too
                }
            }

            // The background live check: one at a time per title; releases that arrive late (a slow source) while it
            // runs get another round. When it's done, whoever is looking at the title is told (StartStatus).
            var followLock = new object();
            var followRunning = false;
            var followAgain = false;
            Task lateSources = Task.CompletedTask;
            var finished = 0;

            // The list is complete once slow sources have all answered and their releases were checked too.
            void FinishIfComplete()
            {
                lock (followLock)
                {
                    if (followRunning || !lateSources.IsCompleted)
                    {
                        return;
                    }
                }

                if (Interlocked.Exchange(ref finished, 1) == 1)
                {
                    return;
                }

                _liveChecking.TryRemove(request.Key, out _);
                NotifyLiveCheckFinished(request);
            }

            void FollowUp()
            {
                if (Plugin.Instance?.Configuration.CheckLiveSeeders != true)
                {
                    return;
                }

                lock (followLock)
                {
                    if (followRunning)
                    {
                        followAgain = true;
                        return;
                    }

                    followRunning = true;
                }

                _liveChecking[request.Key] = 0;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            List<ReleaseCandidate> ranked;
                            lock (foundLock)
                            {
                                ranked = ReleaseRanker.Rank(found, request);
                            }

                            await CheckLiveSeedersAsync(ranked, request, followUp: true).ConfigureAwait(false);
                            List<ReleaseCandidate> merged;
                            lock (foundLock)
                            {
                                merged = Pick(ReleaseRanker.Rank(found, request));
                            }

                            if (merged.Count > 0 && _cache.TryGetValue(request.Key, out var current) && current.Rules == stamp && current.Rules == ListStamp())
                            {
                                _cache[request.Key] = (current.At, merged, stamp);
                            }

                            lock (followLock)
                            {
                                if (!followAgain)
                                {
                                    break;
                                }

                                followAgain = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Background seeder check for {Request} failed", request);
                    }
                    finally
                    {
                        lock (followLock)
                        {
                            followRunning = false;
                            followAgain = false;
                        }

                        FinishIfComplete();
                    }
                });
            }

            var results = await _sources.SearchAsync(request, CancellationToken.None, AddLate, t => lateSources = t).ConfigureAwait(false);
            List<ReleaseCandidate> list;
            lock (foundLock)
            {
                found = Dedupe(found.Concat(results));
            }

            // Sites' seeder counts are often stale (one listed 288 seeders for a torrent with one): ask the trackers for
            // the top releases' real counts, then rank again. A release the new order brings to the top is checked too.
            if (Plugin.Instance?.Configuration.CheckLiveSeeders == true)
            {
                await CheckLiveSeedersAsync(ReleaseRanker.Rank(found, request), request).ConfigureAwait(false);
            }

            lock (foundLock)
            {
                list = Pick(ReleaseRanker.Rank(found, request));
            }

            if (list.Count > 0)
            {
                _cache[request.Key] = (DateTime.UtcNow, list, ListStamp()); // an empty result (sources down, VPN off) isn't kept, so the next try searches again
                TrimCache();

                // The quick check above has to fit in the time a title page waits; the releases it didn't reach are
                // checked in the background, so the next look at the list (and a fallback) has their live counts.
                FollowUp();
                _ = lateSources.ContinueWith(_ => FinishIfComplete(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            _logger.LogInformation("Found {Count} streams for {Request}", list.Count, request);
            return list;
        }
        finally
        {
            _inflight.TryRemove(request.Key, out _);
        }
    }
}

/// <summary>What checking a release found: its tracks, length and bitrate.</summary>
public sealed record ProbeInfo(IReadOnlyList<MediaStream> Streams, long? RunTimeTicks, int? Bitrate);
