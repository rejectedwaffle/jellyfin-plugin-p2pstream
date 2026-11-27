using Jellyfin.Plugin.P2PStream.Providers.BuiltIn;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Searches every enabled source (built-in providers and Torznab indexers) in parallel and merges the results.
/// </summary>
public sealed class SourceHub
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);

    // Sources normally answer within a second. Once one has returned releases, the others get this much longer; a
    // source still silent after that is left out of this search (it keeps running, so its health is still recorded).
    private static readonly TimeSpan SlowSourceGrace = TimeSpan.FromSeconds(3);
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

    /// <param name="onLate">Called with the releases of a source that answered after the search went on without it.</param>
    /// <param name="lateDone">Given a task that completes once every such slow source has answered (or failed) and its releases were handed over.</param>
    public Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct, Action<string, List<ReleaseCandidate>>? onLate = null, Action<Task>? lateDone = null)
        => RunAsync(p => p.SearchAsync(request, ct), () => _torznab.SearchAsync(request, ct), f => RssFeedSource.SearchAsync(f, request, ct), request.ToString(), ct, waitForAll: false, onLate, lateDone);

    // The settings page's test search waits for every source, so it can report each one.
    public Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct)
        => RunAsync(p => p.SearchTextAsync(query, ct), () => _torznab.SearchTextAsync(query, ct), f => RssFeedSource.QueryAsync(f, query, ct), query, ct, waitForAll: true);

    private async Task<List<ReleaseCandidate>> RunAsync(
        Func<SearchProvider, Task<List<ReleaseCandidate>>> providerSearch,
        Func<Task<List<ReleaseCandidate>>> torznabSearch,
        Func<Configuration.RssSource, Task<List<ReleaseCandidate>>> feedSearch,
        string what,
        CancellationToken ct,
        bool waitForAll,
        Action<string, List<ReleaseCandidate>>? onLate = null,
        Action<Task>? lateDone = null)
    {
        if (!HasSources)
        {
            throw new StreamNotFoundException("No sources are enabled. Turn one on (or add one) on the Sources tab of the P2P Stream page.");
        }

        var named = _providers.Where(p => p.Enabled)
            .Select(p => (Name: p.Name, Task: GuardAsync(p.Name, () => providerSearch(p), ct, waitForAll)))
            .ToList();
        if (_torznab.HasIndexers)
        {
            named.Add(("Torznab", GuardAsync("Torznab", torznabSearch, ct, waitForAll)));
        }

        foreach (var feed in EnabledFeeds)
        {
            var name = FeedName(feed);
            named.Add((name, GuardAsync(name, () => feedSearch(feed), ct, waitForAll)));
        }

        var tasks = named.Select(n => n.Task).ToList();
        var all = Task.WhenAll(tasks);
        if (!waitForAll)
        {
            // As soon as one source has releases, give the rest a short grace, then go on without the stragglers.
            var firstWithResults = WhenAnyHasResults(tasks);
            if (await Task.WhenAny(all, firstWithResults).ConfigureAwait(false) != all)
            {
                await Task.WhenAny(all, Task.Delay(SlowSourceGrace, ct)).ConfigureAwait(false);
            }

            var handedOver = new List<Task>();
            foreach (var straggler in named.Where(n => !n.Task.IsCompleted))
            {
                _logger.LogInformation("Not waiting for {Name} for {What}: no answer yet, the other sources already found releases", straggler.Name, what);
                if (onLate is not null)
                {
                    // A slow source's releases still join the title's list when they arrive (for the Version menu and
                    // later plays); only this search doesn't wait for them.
                    var name = straggler.Name;
                    handedOver.Add(straggler.Task.ContinueWith(
                        t =>
                        {
                            try
                            {
                                if (t.IsCompletedSuccessfully && t.Result.Count > 0)
                                {
                                    onLate(name, t.Result);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug("Could not add late releases from {Name}: {Message}", name, ex.Message);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default));
                }
            }

            lateDone?.Invoke(Task.WhenAll(handedOver));
        }
        else
        {
            await all.ConfigureAwait(false);
        }

        var results = tasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result).ToList();
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

    /// <summary>Completes when any of the searches has returned at least one release, or when all have finished.</summary>
    private static async Task WhenAnyHasResults(List<Task<List<ReleaseCandidate>>> tasks)
    {
        var pending = tasks.ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            if (done.IsCompletedSuccessfully && done.Result.Count > 0)
            {
                return;
            }

            pending.Remove(done);
        }
    }

    private async Task<List<ReleaseCandidate>> GuardAsync(string name, Func<Task<List<ReleaseCandidate>>> search, CancellationToken ct, bool evenIfResting)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var recordHere = name != "Torznab"; // Torznab indexers record their own health, one by one
        if (!evenIfResting && recordHere && _health.IsResting(name, out var left))
        {
            _logger.LogDebug("Skipping {Name}: it didn't answer last time (tried again in {Minutes} min)", name, Math.Ceiling(left.TotalMinutes));
            return [];
        }

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

            _logger.LogWarning(
                "Source {Name} failed: {Message:l}{Rest:l}",
                name,
                ex is TimeoutException { Message.Length: > 0 } ? ex.Message : SourceHealth.Describe(ex),
                recordHere && _health.IsResting(name, out _) ? $"; skipped for {SourceHealth.RestAfterNoAnswer.TotalMinutes:0} minutes" : string.Empty);
            return [];
        }
    }
}
