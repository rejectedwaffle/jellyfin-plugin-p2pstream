using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.P2PStream.Catalog;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

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
    private readonly ConcurrentDictionary<string, Task<List<ReleaseCandidate>>> _inflight = new();
    private readonly ConcurrentDictionary<string, ProbeInfo> _probes = new();

    public StreamOptions(SourceHub sources, TmdbClient tmdb, ILogger<StreamOptions> logger)
    {
        _sources = sources;
        _tmdb = tmdb;
        _logger = logger;
    }

    /// <summary>A stable id for a release, also used as the Jellyfin media source id.</summary>
    public static string IdFor(ReleaseCandidate r)
    {
        var identity = !string.IsNullOrEmpty(r.InfoHash)
            ? "h:" + r.InfoHash.ToLowerInvariant()
            : "u:" + (r.MagnetPageUrl ?? r.DownloadUrl ?? r.MagnetUrl ?? r.Title);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    /// <summary>The Version menu label, Elementum-style: "1080p · 8.0 GB · 1234 seeds · BluRay · KickAss — release name".</summary>
    public static string Label(ReleaseCandidate r)
    {
        var parts = new List<string>
        {
            r.Resolution switch { 2160 => "4K", 0 => "?p", var x => x.ToString(CultureInfo.InvariantCulture) + "p" }
        };
        if (r.Size > 0)
        {
            parts.Add(r.Size >= 1L << 30
                ? (r.Size / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                : (r.Size / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB");
        }

        parts.Add(r.Seeders >= 0 ? r.Seeders.ToString(CultureInfo.InvariantCulture) + " seeds" : "seeds ?");
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

    public bool TryGetCached(MediaRequest request, out List<ReleaseCandidate> releases)
    {
        // A list ranked under other settings (e.g. preferred resolution just changed) is searched and ranked again.
        if (_cache.TryGetValue(request.Key, out var hit) && DateTime.UtcNow - hit.At < CacheAge && hit.Rules == ReleaseRanker.RulesStamp())
        {
            releases = hit.Releases;
            return true;
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

        var search = _inflight.GetOrAdd(request.Key, _ => Task.Run(() => SearchAsync(request)));
        return search.WaitAsync(ct);
    }

    /// <summary>Tracks found by checking a release in advance (shown in the Version menu).</summary>
    public bool TryGetProbe(string releaseId, out ProbeInfo info) => _probes.TryGetValue(releaseId, out info!);

    public void SetProbe(string releaseId, ProbeInfo info) => _probes[releaseId] = info;

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

            var c = Plugin.Instance!.Configuration;
            var ranked = ReleaseRanker.Rank(await _sources.SearchAsync(request, CancellationToken.None).ConfigureAwait(false), request);
            var keep = Math.Max(Math.Clamp(c.StreamPickerMax, 1, 50), Math.Max(1, c.CandidatesToTry));
            // The top releases, plus the best few of every resolution, so "Best 1080p" exists even when 4K fills the top.
            var chosen = new HashSet<ReleaseCandidate>(ranked.Take(keep));
            foreach (var group in ranked.GroupBy(r => r.Resolution))
            {
                chosen.UnionWith(group.Take(3));
            }

            var list = ranked.Where(chosen.Contains).ToList(); // keep the ranking order
            if (list.Count > 0)
            {
                _cache[request.Key] = (DateTime.UtcNow, list, ReleaseRanker.RulesStamp()); // an empty result (sources down, VPN off) isn't kept, so the next try searches again
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
