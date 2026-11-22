using Jellyfin.Plugin.P2PStream.Providers.BuiltIn;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Searches every enabled source (built-in providers and Torznab indexers) in parallel and merges the results.
/// </summary>
public sealed class SourceHub
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);
    private readonly TorznabClient _torznab;
    private readonly IReadOnlyList<SearchProvider> _providers;
    private readonly ILogger<SourceHub> _logger;
    private readonly SourceHealth _health;

    public SourceHub(TorznabClient torznab, IEnumerable<SearchProvider> providers, SourceHealth health, ILogger<SourceHub> logger)
    {
        _torznab = torznab;
        _providers = providers.ToList();
        _logger = logger;
        _health = health;
    }

    public IReadOnlyList<SearchProvider> Providers => _providers;

    public bool HasSources => _torznab.HasIndexers || _providers.Any(p => p.Enabled) || EnabledFeeds.Count > 0;

    /// <summary>Gets the user-added RSS sources that are switched on.</summary>
    public static List<Configuration.RssSource> EnabledFeeds =>
        (Plugin.Instance?.Configuration.RssSources ?? []).Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Url)).ToList();

    public Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => RunAsync(p => p.SearchAsync(request, ct), () => _torznab.SearchAsync(request, ct), f => RssFeedSource.SearchAsync(f, request, ct), request.ToString(), ct);

    public Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct)
        => RunAsync(p => p.SearchTextAsync(query, ct), () => _torznab.SearchTextAsync(query, ct), f => RssFeedSource.QueryAsync(f, query, ct), query, ct);

    private async Task<List<ReleaseCandidate>> RunAsync(
        Func<SearchProvider, Task<List<ReleaseCandidate>>> providerSearch,
        Func<Task<List<ReleaseCandidate>>> torznabSearch,
        Func<Configuration.RssSource, Task<List<ReleaseCandidate>>> feedSearch,
        string what,
        CancellationToken ct)
    {
        if (!HasSources)
        {
            throw new StreamNotFoundException("No sources are enabled. Turn one on (or add one) on the Sources tab of the P2P Stream page.");
        }

        var tasks = _providers.Where(p => p.Enabled)
            .Select(p => GuardAsync(p.Name, () => providerSearch(p), ct))
            .ToList();
        if (_torznab.HasIndexers)
        {
            tasks.Add(GuardAsync("Torznab", torznabSearch, ct));
        }

        foreach (var feed in EnabledFeeds)
        {
            tasks.Add(GuardAsync(FeedName(feed), () => feedSearch(feed), ct));
        }

        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(r => r).ToList();
        _logger.LogDebug("Sources returned {Count} results for {What}", results.Count, what);

        // The same torrent often turns up on several sources. Keep the copy with the most seeders.
        return results
            .GroupBy(r => string.IsNullOrEmpty(r.InfoHash) ? "u:" + (r.DownloadUrl ?? r.MagnetPageUrl ?? r.Title) : r.InfoHash!.ToLowerInvariant())
            .Select(g => g.OrderByDescending(r => r.Seeders).First())
            .ToList();
    }

    /// <summary>The name a user-added feed is shown (and its health recorded) under.</summary>
    public static string FeedName(Configuration.RssSource feed)
    {
        if (!string.IsNullOrWhiteSpace(feed.Name))
        {
            return feed.Name.Trim();
        }

        return Uri.TryCreate(feed.Url.Replace("{query}", "x", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var u) ? u.Host : feed.Url;
    }

    private async Task<List<ReleaseCandidate>> GuardAsync(string name, Func<Task<List<ReleaseCandidate>>> search, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var recordHere = name != "Torznab"; // Torznab indexers record their own health, one by one
        try
        {
            var results = await search().WaitAsync(SourceTimeout, ct).ConfigureAwait(false);
            if (recordHere)
            {
                _health.Success(name, results.Count, sw.ElapsedMilliseconds);
            }

            return results;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (recordHere)
            {
                _health.Failure(name, ex, sw.ElapsedMilliseconds);
            }

            _logger.LogWarning("Source {Name} failed: {Message}", name, ex.Message);
            return [];
        }
    }
}
