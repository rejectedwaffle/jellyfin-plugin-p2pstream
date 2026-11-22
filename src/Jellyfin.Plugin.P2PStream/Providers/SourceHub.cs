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

    public SourceHub(TorznabClient torznab, IEnumerable<SearchProvider> providers, ILogger<SourceHub> logger)
    {
        _torznab = torznab;
        _providers = providers.ToList();
        _logger = logger;
    }

    public IReadOnlyList<SearchProvider> Providers => _providers;

    public bool HasSources => _torznab.HasIndexers || _providers.Any(p => p.Enabled);

    public Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => RunAsync(p => p.SearchAsync(request, ct), () => _torznab.SearchAsync(request, ct), request.ToString(), ct);

    public Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct)
        => RunAsync(p => p.SearchTextAsync(query, ct), () => _torznab.SearchTextAsync(query, ct), query, ct);

    private async Task<List<ReleaseCandidate>> RunAsync(
        Func<SearchProvider, Task<List<ReleaseCandidate>>> providerSearch,
        Func<Task<List<ReleaseCandidate>>> torznabSearch,
        string what,
        CancellationToken ct)
    {
        if (!HasSources)
        {
            throw new StreamNotFoundException("No sources are enabled. Turn on a built-in source (or add a Torznab indexer) on the P2P Stream page.");
        }

        var tasks = _providers.Where(p => p.Enabled)
            .Select(p => GuardAsync(p.Name, () => providerSearch(p), ct))
            .ToList();
        if (_torznab.HasIndexers)
        {
            tasks.Add(GuardAsync("Torznab", torznabSearch, ct));
        }

        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(r => r).ToList();
        _logger.LogDebug("Sources returned {Count} results for {What}", results.Count, what);

        // The same torrent often turns up on several sources. Keep the copy with the most seeders.
        return results
            .GroupBy(r => string.IsNullOrEmpty(r.InfoHash) ? "u:" + (r.DownloadUrl ?? r.MagnetPageUrl ?? r.Title) : r.InfoHash!.ToLowerInvariant())
            .Select(g => g.OrderByDescending(r => r.Seeders).First())
            .ToList();
    }

    private async Task<List<ReleaseCandidate>> GuardAsync(string name, Func<Task<List<ReleaseCandidate>>> search, CancellationToken ct)
    {
        try
        {
            return await search().WaitAsync(SourceTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Source {Name} failed: {Message}", name, ex.Message);
            return [];
        }
    }
}
