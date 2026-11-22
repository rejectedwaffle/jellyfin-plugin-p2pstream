using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.P2PStream.Providers.BuiltIn;

/// <summary>
/// Knaben: a meta-search engine. One request searches many trackers at once (The Pirate Bay, 1337x, EZTV, YTS and more)
/// and returns live seeder counts and magnets. Limited to movies or TV depending on what's being played.
/// </summary>
public sealed class KnabenProvider : SearchProvider
{
    private const long MoviesCategory = 3000000;
    private const long TvCategory = 2000000;

    public override string Id => "knaben";

    public override string Name => "Knaben";

    public override string Description => "Movies & TV · searches many trackers at once (incl. 1337x, The Pirate Bay)";

    public override string DefaultUrl => "https://api.knaben.org";

    public override Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => SearchByTitleAsync(request, q => QueryAsync(q, request.Kind == MediaKind.Movie ? MoviesCategory : TvCategory, ct));

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, null, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string query, long? category, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["search_type"] = "100%",
            ["search_field"] = "title",
            ["query"] = query,
            ["order_by"] = "seeders",
            ["order_direction"] = "desc",
            ["size"] = 50,
            ["hide_unsafe"] = true,
            ["hide_xxx"] = true
        };
        if (category is long c)
        {
            body["categories"] = new JsonArray(c);
        }

        var json = await UseMirrorsAsync(async (b, t) =>
        {
            using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await HttpClients.Default.PostAsync(new Uri($"{b}/v1"), content, t).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(t).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);

        var list = new List<ReleaseCandidate>();
        foreach (var h in json?["hits"] as JsonArray ?? [])
        {
            var magnet = (string?)h?["magnetUrl"];
            var hash = (string?)h?["hash"];
            var download = (string?)h?["link"];
            if (string.IsNullOrEmpty(magnet) && string.IsNullOrEmpty(hash) && string.IsNullOrEmpty(download))
            {
                continue;
            }

            list.Add(new ReleaseCandidate
            {
                Title = (string?)h?["title"] ?? string.Empty,
                Origin = Name,
                MagnetUrl = magnet,
                InfoHash = hash?.ToLowerInvariant(),
                DownloadUrl = download,
                Size = ToLong(h?["bytes"]),
                Seeders = (int)ToLong(h?["liveSeeders"] ?? h?["seeders"]),
                Peers = (int)ToLong(h?["livePeers"] ?? h?["peers"])
            });
        }

        return list;
    }
}

/// <summary>
/// Nyaa: the main source for anime (English-subtitled category). Anime episodes are usually named "Show - 12",
/// so episodes are searched as "Title 12", falling back to the title alone (batches).
/// </summary>
public sealed class NyaaProvider : SearchProvider
{
    public override string Id => "nyaa";

    public override string Name => "Nyaa";

    public override string Description => "Anime · English subtitles";

    public override string DefaultUrl => "https://nyaa.si";

    public override async Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.IsAnime == false)
        {
            return []; // TMDB says it isn't anime: Nyaa won't have it, and a search would only find unrelated anime
        }

        // Fansub releases are usually named by the romaji title ("Sousou no Frieren - 12"), which TMDB lists as an
        // alternative title, so search the English title and those together.
        var titles = new[] { request.Title! }.Concat(request.AltTitles).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        async Task<List<ReleaseCandidate>> SearchAll(Func<string, string> query) =>
            Merge(await Task.WhenAll(titles.Select(t => QuietAsync(query(t), ct))).ConfigureAwait(false));

        if (request.Kind == MediaKind.Movie)
        {
            return await SearchAll(t => t).ConfigureAwait(false);
        }

        var episode = await SearchAll(t => $"{t} {request.Episode:00}").ConfigureAwait(false);
        return episode.Count > 0 ? episode : await SearchAll(t => t).ConfigureAwait(false);
    }

    /// <summary>One title's search; a failure of an extra title doesn't fail the others.</summary>
    private async Task<List<ReleaseCandidate>> QuietAsync(string query, CancellationToken ct)
    {
        try
        {
            return await QueryAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private static List<ReleaseCandidate> Merge(IEnumerable<List<ReleaseCandidate>> lists) =>
        lists.SelectMany(l => l)
            .GroupBy(r => !string.IsNullOrEmpty(r.InfoHash) ? r.InfoHash.ToLowerInvariant() : r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string query, CancellationToken ct)
    {
        // c=1_2: Anime, English-translated. f=0: no filter.
        var xml = await UseMirrorsAsync((b, t) => GetPageAsync($"{b}/?page=rss&q={Esc(query)}&c=1_2&f=0", t), ct).ConfigureAwait(false);
        return RssFeedSource.Parse(xml, Name);
    }
}
