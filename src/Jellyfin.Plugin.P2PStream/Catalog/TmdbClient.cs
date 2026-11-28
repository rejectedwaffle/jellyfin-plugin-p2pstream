using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.P2PStream.Providers;

namespace Jellyfin.Plugin.P2PStream.Catalog;

public sealed record MovieInfo(int Id, string Title, int? Year, string? ImdbId)
{
    /// <summary>Gets a value indicating whether it's Japanese animation (searched on anime sites with its romaji titles too).</summary>
    public bool IsAnime { get; init; }

    /// <summary>Gets other names it's released under (romaji, alternative English titles). Only filled for anime.</summary>
    public IReadOnlyList<string> AltTitles { get; init; } = [];
}

public sealed record ShowInfo(int Id, string Name, int? Year, string? ImdbId, List<int> Seasons)
{
    /// <inheritdoc cref="MovieInfo.IsAnime"/>
    public bool IsAnime { get; init; }

    /// <inheritdoc cref="MovieInfo.AltTitles"/>
    public IReadOnlyList<string> AltTitles { get; init; } = [];
}

public sealed record EpisodeInfo(int Number, string Name, DateTime? AirDate);

public sealed record TmdbItem(int Id, string Title, int? Year, bool IsMovie, int VoteCount = 0);

/// <summary>
/// Minimal TMDB v3 client. Accepts either a v3 API key or a v4 read access token.
/// </summary>
public sealed class TmdbClient
{
    /// <summary>Torrent names are almost always English, so searches use English titles.</summary>
    public const string SearchLanguage = "en-US";

    private const string BaseUrl = "https://api.themoviedb.org/3/";
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, (DateTime At, JsonNode Node)> _cache = new();
    private DateTime _lastPrune = DateTime.UtcNow;

    /// <summary>Drops expired answers every 10 minutes, and the oldest ones while more than 5000 are kept (a full catalog sync asks for every season).</summary>
    private void PruneCache()
    {
        var now = DateTime.UtcNow;
        if (_cache.Count <= 5000 && now - _lastPrune < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastPrune = now;
        foreach (var old in _cache.Where(e => now - e.Value.At >= CacheAge).ToList())
        {
            _cache.TryRemove(old.Key, out _);
        }

        if (_cache.Count > 5000)
        {
            foreach (var old in _cache.OrderBy(e => e.Value.At).Take(_cache.Count - 4000).ToList())
            {
                _cache.TryRemove(old.Key, out _);
            }
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TmdbApiKey);

    public static string CatalogLanguage => string.IsNullOrWhiteSpace(Plugin.Instance!.Configuration.Language) ? SearchLanguage : Plugin.Instance.Configuration.Language;

    public async Task<MovieInfo> GetMovieAsync(int id, string language, CancellationToken ct)
    {
        var j = await GetAsync($"movie/{id}", language, "append_to_response=alternative_titles", ct).ConfigureAwait(false);
        var title = (string?)j["title"] ?? string.Empty;
        var anime = IsAnimeNode(j);
        return new MovieInfo(id, title, Year((string?)j["release_date"]), NullIfEmpty((string?)j["imdb_id"]))
        {
            IsAnime = anime,
            AltTitles = anime ? PickAltTitles(title, (string?)j["original_title"], j["alternative_titles"]?["titles"] as JsonArray) : []
        };
    }

    public async Task<ShowInfo> GetShowAsync(int id, string language, CancellationToken ct)
    {
        var j = await GetAsync($"tv/{id}", language, "append_to_response=external_ids,alternative_titles", ct).ConfigureAwait(false);
        var seasons = (j["seasons"] as JsonArray ?? [])
            .Select(s => (int?)s?["season_number"] ?? 0)
            .Where(n => n > 0)
            .ToList();
        var name = (string?)j["name"] ?? string.Empty;
        var anime = IsAnimeNode(j);
        return new ShowInfo(id, name, Year((string?)j["first_air_date"]), NullIfEmpty((string?)j["external_ids"]?["imdb_id"]), seasons)
        {
            IsAnime = anime,
            AltTitles = anime ? PickAltTitles(name, (string?)j["original_name"], j["alternative_titles"]?["results"] as JsonArray) : []
        };
    }

    /// <summary>Japanese animation: original language Japanese and the Animation genre (16).</summary>
    public static bool IsAnimeNode(JsonNode j) =>
        (string?)j["original_language"] == "ja" && (j["genres"] as JsonArray ?? []).Any(g => (int?)g?["id"] == 16);

    /// <summary>
    /// The names anime releases use besides the English title: romaji first (fansub groups name releases
    /// "&lt;romaji title&gt; - 12"), then other Latin-script titles. Up to three, without the main title.
    /// </summary>
    public static IReadOnlyList<string> PickAltTitles(string main, string? original, JsonArray? alternatives)
    {
        static bool Latin(string s) => s.Count(char.IsLetter) > 0 && s.Where(char.IsLetter).All(c => c < 0x250);
        var candidates = new List<(string Title, int Rank)>();
        foreach (var a in alternatives ?? [])
        {
            var title = ((string?)a?["title"])?.Trim();
            if (string.IsNullOrEmpty(title) || !Latin(title))
            {
                continue;
            }

            var type = ((string?)a!["type"] ?? string.Empty).ToLowerInvariant();
            var country = (string?)a["iso_3166_1"] ?? string.Empty;
            var rank = type.Contains("romaji", StringComparison.Ordinal) ? 0 : country == "JP" ? 1 : country is "US" or "GB" ? 2 : 3;
            candidates.Add((title, rank));
        }

        if (!string.IsNullOrWhiteSpace(original) && Latin(original))
        {
            candidates.Add((original.Trim(), 1));
        }

        return candidates
            .OrderBy(c => c.Rank)
            .Select(c => c.Title)
            .Where(t => !string.Equals(t, main, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }

    public async Task<List<EpisodeInfo>> GetSeasonAsync(int id, int season, string language, CancellationToken ct)
    {
        var j = await GetAsync($"tv/{id}/season/{season}", language, string.Empty, ct).ConfigureAwait(false);
        return (j["episodes"] as JsonArray ?? [])
            .Where(e => e is not null)
            .Select(e => new EpisodeInfo((int?)e!["episode_number"] ?? 0, (string?)e["name"] ?? string.Empty, Date((string?)e["air_date"])))
            .Where(e => e.Number > 0)
            .ToList();
    }

    /// <summary>Gets one page of a built-in list such as trending, popular, top_rated.</summary>
    public async Task<List<TmdbItem>> GetListPageAsync(bool movies, string list, int page, CancellationToken ct)
    {
        var kind = movies ? "movie" : "tv";
        var path = list.ToLowerInvariant() switch
        {
            "trending" => $"trending/{kind}/week",
            "trending_day" => $"trending/{kind}/day",
            "popular" => $"{kind}/popular",
            "top_rated" => $"{kind}/top_rated",
            "now_playing" when movies => "movie/now_playing",
            "upcoming" when movies => "movie/upcoming",
            "on_the_air" when !movies => "tv/on_the_air",
            "airing_today" when !movies => "tv/airing_today",
            _ => null
        };
        if (path is null)
        {
            return [];
        }

        var j = await GetAsync(path, CatalogLanguage, "page=" + page.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return ParseItems(j["results"] as JsonArray, movies);
    }

    /// <summary>Searches movies and shows by name (people are skipped).</summary>
    public async Task<List<TmdbItem>> SearchAsync(string query, CancellationToken ct)
    {
        var j = await GetAsync("search/multi", CatalogLanguage, "include_adult=false&page=1&query=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        return ParseItems(j["results"] as JsonArray, null);
    }

    /// <summary>Gets the name of a user-created TMDB list, or null if it can't be read.</summary>
    public async Task<string?> TryGetListNameAsync(string listId, CancellationToken ct)
    {
        try
        {
            var j = await GetAsync($"list/{Uri.EscapeDataString(listId)}", CatalogLanguage, "page=1", ct).ConfigureAwait(false);
            var name = (string?)j["name"];
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Gets all items of a user-created TMDB list.</summary>
    public async Task<List<TmdbItem>> GetCustomListAsync(string listId, CancellationToken ct)
    {
        var items = new List<TmdbItem>();
        for (var page = 1; page <= 20; page++)
        {
            var j = await GetAsync($"list/{Uri.EscapeDataString(listId)}", CatalogLanguage, "page=" + page.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            items.AddRange(ParseItems(j["items"] as JsonArray, null));
            if (page >= ((int?)j["total_pages"] ?? 1))
            {
                break;
            }
        }

        return items;
    }

    private static List<TmdbItem> ParseItems(JsonArray? arr, bool? movies)
    {
        var list = new List<TmdbItem>();
        foreach (var r in arr ?? [])
        {
            if (r is null)
            {
                continue;
            }

            var isMovie = movies ?? ((string?)r["media_type"] ?? "movie") == "movie";
            var mediaType = (string?)r["media_type"];
            if (movies is null && mediaType is not null && mediaType != "movie" && mediaType != "tv")
            {
                continue;
            }

            var id = (int?)r["id"] ?? 0;
            var title = isMovie ? (string?)r["title"] : (string?)r["name"];
            var date = isMovie ? (string?)r["release_date"] : (string?)r["first_air_date"];
            if (id > 0 && !string.IsNullOrWhiteSpace(title))
            {
                list.Add(new TmdbItem(id, title!, Year(date), isMovie, (int?)r["vote_count"] ?? 0));
            }
        }

        return list;
    }

    private async Task<JsonNode> GetAsync(string path, string language, string query, CancellationToken ct)
    {
        var key = Plugin.Instance!.Configuration.TmdbApiKey.Trim();
        if (key.Length == 0)
        {
            throw new InvalidOperationException("TMDB API key is not configured");
        }

        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var url = $"{BaseUrl}{path}?language={Uri.EscapeDataString(language)}";
        if (query.Length > 0)
        {
            url += "&" + query;
        }

        if (_cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < CacheAge)
        {
            return hit.Node;
        }

        var requestUrl = bearer ? url : url + "&api_key=" + Uri.EscapeDataString(key);
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            if (bearer)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var resp = await HttpClients.Default.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            resp.EnsureSuccessStatusCode();
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                ?? throw new InvalidOperationException("Empty TMDB response");
            _cache[url] = (DateTime.UtcNow, node);
            PruneCache();
            return node;
        }
    }

    private static int? Year(string? date) => Date(date)?.Year;

    private static DateTime? Date(string? date) =>
        DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
