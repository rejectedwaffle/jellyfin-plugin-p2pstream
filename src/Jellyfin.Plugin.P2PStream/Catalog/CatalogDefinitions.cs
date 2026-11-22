using Jellyfin.Plugin.P2PStream.Configuration;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// A catalog that can be imported (DefaultFolder: the folder name under the catalog path it writes to unless changed):
/// a built-in TMDB list, one half (movies or shows) of a user's TMDB list, or a Trakt list (TraktPath: the API path).
/// </summary>
public sealed record CatalogDef(string Id, string Name, bool IsMovie, string DefaultFolder, string? TmdbList, string? CustomListId, string? TraktPath = null)
{
    public string Source => TraktPath is not null ? "Trakt" : "TMDB";
}

public static class CatalogDefinitions
{
    public const int DefaultMaxItems = 100;

    public static readonly IReadOnlyList<CatalogDef> BuiltIn =
    [
        new("movie:trending", "Trending", true, "Trending Movies", "trending", null),
        new("movie:trending_day", "Trending Today", true, "Trending Today Movies", "trending_day", null),
        new("movie:popular", "Popular", true, "Popular Movies", "popular", null),
        new("movie:top_rated", "Top Rated", true, "Top Rated Movies", "top_rated", null),
        new("movie:now_playing", "Now Playing", true, "Now Playing Movies", "now_playing", null),
        new("movie:upcoming", "Upcoming", true, "Upcoming Movies", "upcoming", null),
        new("tv:trending", "Trending", false, "Trending Shows", "trending", null),
        new("tv:trending_day", "Trending Today", false, "Trending Today Shows", "trending_day", null),
        new("tv:popular", "Popular", false, "Popular Shows", "popular", null),
        new("tv:top_rated", "Top Rated", false, "Top Rated Shows", "top_rated", null),
        new("tv:on_the_air", "On The Air", false, "On The Air Shows", "on_the_air", null),
        new("tv:airing_today", "Airing Today", false, "Airing Today Shows", "airing_today", null)
    ];

    /// <summary>Trakt's public lists, offered once a Trakt Client ID is set.</summary>
    public static readonly IReadOnlyList<CatalogDef> TraktBuiltIn =
    [
        new("trakt:movies:trending", "Trakt Trending", true, "Trakt Trending Movies", null, null, "movies/trending"),
        new("trakt:movies:popular", "Trakt Popular", true, "Trakt Popular Movies", null, null, "movies/popular"),
        new("trakt:movies:anticipated", "Trakt Anticipated", true, "Trakt Anticipated Movies", null, null, "movies/anticipated"),
        new("trakt:movies:boxoffice", "Trakt Box Office", true, "Trakt Box Office", null, null, "movies/boxoffice"),
        new("trakt:shows:trending", "Trakt Trending", false, "Trakt Trending Shows", null, null, "shows/trending"),
        new("trakt:shows:popular", "Trakt Popular", false, "Trakt Popular Shows", null, null, "shows/popular"),
        new("trakt:shows:anticipated", "Trakt Anticipated", false, "Trakt Anticipated Shows", null, null, "shows/anticipated")
    ];

    /// <summary>All catalogs: the built-ins, a movie row and a show row for each custom TMDB list id, and Trakt's lists if set up.</summary>
    public static async Task<List<CatalogDef>> GetAllAsync(TmdbClient tmdb, CancellationToken ct, TraktClient? trakt = null)
    {
        var list = BuiltIn.ToList();
        var c = Plugin.Instance!.Configuration;
        var ids = c.CustomTmdbListIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids)
        {
            var name = (tmdb.IsConfigured ? await tmdb.TryGetListNameAsync(id, ct).ConfigureAwait(false) : null) ?? $"List {id}";
            list.Add(new CatalogDef($"list:{id}:movie", name, true, $"{name} Movies", null, id));
            list.Add(new CatalogDef($"list:{id}:tv", name, false, $"{name} Shows", null, id));
        }

        if (trakt is not null && TraktClient.IsConfigured)
        {
            list.AddRange(TraktDefs(trakt.IsConnected, c.TraktUser));
            foreach (var entry in c.TraktLists.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TraktClient.ParseListId(entry) is not var (user, slug))
                {
                    continue;
                }

                var name = await trakt.TryGetListNameAsync(user, slug, ct).ConfigureAwait(false) ?? $"{user}/{slug}";
                var path = $"users/{Uri.EscapeDataString(user)}/lists/{Uri.EscapeDataString(slug)}/items";
                list.Add(new CatalogDef($"trakt:list:{user}/{slug}:movie", "Trakt: " + name, true, $"{name} Movies", null, null, path + "/movies"));
                list.Add(new CatalogDef($"trakt:list:{user}/{slug}:tv", "Trakt: " + name, false, $"{name} Shows", null, null, path + "/shows"));
            }
        }

        return list;
    }

    /// <summary>The Trakt rows: public lists, the watchlist (connected account, or a public profile's), and recommendations (connected).</summary>
    public static IEnumerable<CatalogDef> TraktDefs(bool connected, string? user)
    {
        foreach (var d in TraktBuiltIn)
        {
            yield return d;
        }

        var who = connected ? "me" : user?.Trim();
        if (!string.IsNullOrEmpty(who))
        {
            var w = Uri.EscapeDataString(who);
            yield return new("trakt:watchlist:movie", "Trakt Watchlist", true, "Trakt Watchlist Movies", null, null, $"users/{w}/watchlist/movies/rank");
            yield return new("trakt:watchlist:tv", "Trakt Watchlist", false, "Trakt Watchlist Shows", null, null, $"users/{w}/watchlist/shows/rank");
        }

        if (connected)
        {
            yield return new("trakt:recommended:movie", "Trakt Recommended", true, "Trakt Recommended Movies", null, null, "recommendations/movies?ignore_collected=true");
            yield return new("trakt:recommended:tv", "Trakt Recommended", false, "Trakt Recommended Shows", null, null, "recommendations/shows?ignore_collected=true");
        }
    }

    /// <summary>The settings in effect for a catalog: the saved row, or defaults for one that hasn't been configured yet.</summary>
    public static CatalogEntry Effective(CatalogDef def)
    {
        var c = Plugin.Instance!.Configuration;
        var saved = c.Catalogs.FirstOrDefault(x => string.Equals(x.Id, def.Id, StringComparison.OrdinalIgnoreCase));
        if (saved is not null)
        {
            return new CatalogEntry
            {
                Id = def.Id,
                Enabled = saved.Enabled,
                Collection = saved.Collection,
                MaxItems = saved.MaxItems > 0 ? Math.Min(saved.MaxItems, 500) : DefaultMaxItems,
                Library = string.IsNullOrWhiteSpace(saved.Library) ? def.DefaultFolder : saved.Library.Trim()
            };
        }

        // Not in the table yet: carry over the list checkboxes from versions before the catalog table.
        var legacy = c.CatalogEnabled
            && ((def.TmdbList is not null && (def.IsMovie ? c.MovieLists : c.ShowLists).Contains(def.TmdbList)) || def.CustomListId is not null);
        return new CatalogEntry { Id = def.Id, Enabled = legacy, MaxItems = DefaultMaxItems, Library = def.DefaultFolder };
    }
}
