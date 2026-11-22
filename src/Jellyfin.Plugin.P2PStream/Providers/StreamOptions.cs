using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.P2PStream.Catalog;
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
    private readonly ConcurrentDictionary<string, (DateTime At, List<ReleaseCandidate> Releases)> _cache = new();
    private readonly ConcurrentDictionary<string, Task<List<ReleaseCandidate>>> _inflight = new();

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
        if (_cache.TryGetValue(request.Key, out var hit) && DateTime.UtcNow - hit.At < CacheAge)
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

    /// <summary>Forgets the list for a title (e.g. after its release went stale).</summary>
    public void Invalidate(MediaRequest request) => _cache.TryRemove(request.Key, out _);

    /// <summary>Fills in title, year and IMDb id from TMDB, which the sources need to search.</summary>
    public async Task EnrichAsync(MediaRequest req, CancellationToken ct)
    {
        if (req.TmdbId is not int id || !_tmdb.IsConfigured || (req.Title is not null && req.ImdbId is not null))
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
            }
            else
            {
                var s = await _tmdb.GetShowAsync(id, TmdbClient.SearchLanguage, ct).ConfigureAwait(false);
                req.Title ??= s.Name;
                req.Year ??= s.Year;
                req.ImdbId ??= s.ImdbId;
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
            var c = Plugin.Instance!.Configuration;
            var ranked = ReleaseRanker.Rank(await _sources.SearchAsync(request, CancellationToken.None).ConfigureAwait(false), request);
            var keep = Math.Max(Math.Clamp(c.StreamPickerMax, 1, 50), Math.Max(1, c.CandidatesToTry));
            var list = ranked.Take(keep).ToList();
            _cache[request.Key] = (DateTime.UtcNow, list);
            _logger.LogInformation("Found {Count} streams for {Request}", list.Count, request);
            return list;
        }
        finally
        {
            _inflight.TryRemove(request.Key, out _);
        }
    }
}
