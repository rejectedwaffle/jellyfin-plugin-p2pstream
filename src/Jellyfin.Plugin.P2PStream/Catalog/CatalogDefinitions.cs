using Jellyfin.Plugin.P2PStream.Configuration;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>A catalog that can be imported (DefaultFolder: the folder name under the catalog path it writes to unless changed): a built-in TMDB list, or one half (movies or shows) of a user's TMDB list.</summary>
public sealed record CatalogDef(string Id, string Name, bool IsMovie, string DefaultFolder, string? TmdbList, string? CustomListId);

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

    /// <summary>All catalogs: the built-ins plus a movie row and a show row for each custom TMDB list id.</summary>
    public static async Task<List<CatalogDef>> GetAllAsync(TmdbClient tmdb, CancellationToken ct)
    {
        var list = BuiltIn.ToList();
        var ids = Plugin.Instance!.Configuration.CustomTmdbListIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids)
        {
            var name = (tmdb.IsConfigured ? await tmdb.TryGetListNameAsync(id, ct).ConfigureAwait(false) : null) ?? $"List {id}";
            list.Add(new CatalogDef($"list:{id}:movie", name, true, $"{name} Movies", null, id));
            list.Add(new CatalogDef($"list:{id}:tv", name, false, $"{name} Shows", null, id));
        }

        return list;
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
