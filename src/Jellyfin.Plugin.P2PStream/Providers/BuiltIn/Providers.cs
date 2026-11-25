using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.P2PStream.Providers.BuiltIn;

/// <summary>Internet Archive: films uploaded to archive.org, web-seeded by archive.org. Their rights vary by upload.</summary>
public sealed class ArchiveProvider : SearchProvider
{
    public override string Id => "archive";

    public override string Name => "Internet Archive";

    public override string Description => "Movies · archive.org";

    public override string DefaultUrl => "https://archive.org";

    public override bool EnabledByDefault => true;

    public override async Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
    {
        if (request.Kind != MediaKind.Movie || string.IsNullOrWhiteSpace(request.Title))
        {
            return [];
        }

        var wanted = Normalize(request.Title!);
        return (await QueryAsync($"title:(\"{request.Title}\")", ct).ConfigureAwait(false))
            .Where(x => Normalize(x.Title) == wanted && (request.Year is null || x.Year is null || Math.Abs(request.Year.Value - x.Year.Value) <= 1))
            .Select(x => x.Release)
            .ToList();
    }

    public override async Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct)
        => (await QueryAsync($"title:({query})", ct).ConfigureAwait(false)).Select(x => x.Release).ToList();

    private async Task<List<(string Title, int? Year, ReleaseCandidate Release)>> QueryAsync(string q, CancellationToken ct)
    {
        var url = $"{BaseUrl}/advancedsearch.php?q={Esc(q + " AND mediatype:(movies)")}&fl[]=identifier&fl[]=title&fl[]=year&rows=20&sort[]=downloads+desc&output=json";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<(string, int?, ReleaseCandidate)>();
        foreach (var d in json?["response"]?["docs"] as JsonArray ?? [])
        {
            var id = (string?)d?["identifier"];
            var title = d?["title"]?.ToString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title))
            {
                continue;
            }

            int? year = int.TryParse(d?["year"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
            list.Add((title, year, new ReleaseCandidate
            {
                Title = year is null ? title : $"{title} ({year})",
                Origin = Name,
                DownloadUrl = $"{BaseUrl}/download/{Esc(id)}/{Esc(id)}_archive.torrent",
                Seeders = -1 // web-seeded by archive.org, so seeder counts don't apply
            }));
        }

        return list;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}

/// <summary>YTS: movies, searched by IMDb id. Small encodes.</summary>
public sealed class YtsProvider : SearchProvider
{
    public override string Id => "yts";

    public override string Name => "YTS";

    public override string Description => "Movies · English";

    public override string DefaultUrl => "https://yts.pw";

    // YTS's API announced its move to movies-api.accel.li; the rest are mirrors serving the same data.
    public override string[] Mirrors => ["https://movies-api.accel.li", "https://yts.lt", "https://yts.am", "https://yts.ag", "https://yts.mx"];

    public override string? TextSearchNote => "movies only";

    public override Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
    {
        if (request.Kind != MediaKind.Movie)
        {
            return Task.FromResult(new List<ReleaseCandidate>());
        }

        if (!string.IsNullOrEmpty(request.ImdbId))
        {
            return QueryAsync(request.ImdbId!, request.ImdbId, ct);
        }

        return string.IsNullOrEmpty(request.Title)
            ? Task.FromResult(new List<ReleaseCandidate>())
            : QueryAsync(request.Title!, null, ct);
    }

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, null, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string term, string? imdb, CancellationToken ct)
    {
        var json = await UseMirrorsAsync((b, t) => GetJsonAsync($"{b}/api/v2/list_movies.json?limit=50&query_term={Esc(term)}", t), ct).ConfigureAwait(false);
        var list = new List<ReleaseCandidate>();
        foreach (var m in json?["data"]?["movies"] as JsonArray ?? [])
        {
            if (imdb is not null && !string.Equals((string?)m?["imdb_code"], imdb, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var t in m?["torrents"] as JsonArray ?? [])
            {
                var hash = (string?)t?["hash"];
                if (string.IsNullOrEmpty(hash))
                {
                    continue;
                }

                list.Add(new ReleaseCandidate
                {
                    Title = $"{m?["title_long"]} {t?["quality"]} {t?["type"]} {t?["video_codec"]}".Trim(),
                    Origin = Name,
                    InfoHash = hash.ToLowerInvariant(),
                    Seeders = (int)ToLong(t?["seeds"]),
                    Peers = (int)ToLong(t?["peers"]),
                    Size = ToLong(t?["size_bytes"])
                });
            }
        }

        return list;
    }
}

/// <summary>EZTV: TV episodes, searched by IMDb id.</summary>
public sealed class EztvProvider : SearchProvider
{
    private const int MaxPages = 10;

    public override string Id => "eztv";

    public override string Name => "EZTV";

    public override string Description => "TV · English";

    public override string DefaultUrl => "https://eztvx.to";

    public override string[] Mirrors => ["https://eztv.re", "https://eztv.wf", "https://eztv.tf", "https://eztv.yt"];

    public override bool SupportsTextSearch => false;

    public override string? TextSearchNote => "only searches when you open or play an episode";

    public override async Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
    {
        if (request.Kind != MediaKind.Episode || string.IsNullOrEmpty(request.ImdbId))
        {
            return [];
        }

        var list = new List<ReleaseCandidate>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var p = page;
            var json = await UseMirrorsAsync((b, t) => GetJsonAsync($"{b}/api/get-torrents?imdb_id={Esc(ImdbDigits(request.ImdbId!))}&limit=100&page={p}", t), ct).ConfigureAwait(false);
            var torrents = json?["torrents"] as JsonArray;
            if (torrents is null || torrents.Count == 0)
            {
                break;
            }

            var before = list.Count;
            foreach (var t in torrents)
            {
                var season = (int)ToLong(t?["season"]);
                var episode = (int)ToLong(t?["episode"]);
                var hash = (string?)t?["hash"];
                if (season != request.Season || (episode != request.Episode && episode != 0) || string.IsNullOrEmpty(hash))
                {
                    continue;
                }

                list.Add(new ReleaseCandidate
                {
                    Title = (string?)t?["title"] ?? string.Empty,
                    Origin = Name,
                    InfoHash = hash.ToLowerInvariant(),
                    MagnetUrl = (string?)t?["magnet_url"],
                    Seeders = (int)ToLong(t?["seeds"]),
                    Peers = (int)ToLong(t?["peers"]),
                    Size = ToLong(t?["size_bytes"])
                });
            }

            // Results are newest-first, so matches cluster together. Stop once we've moved past them.
            if (before > 0 && list.Count == before)
            {
                break;
            }
        }

        return list;
    }
}

/// <summary>The Pirate Bay's public JSON API (apibay): movies and TV, searched by title.</summary>
public sealed class ApibayProvider : SearchProvider
{
    public override string Id => "apibay";

    public override string Name => "The Pirate Bay";

    public override string Description => "Movies & TV · English";

    public override string DefaultUrl => "https://apibay.org";

    public override async Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return [];
        }

        if (request.Kind == MediaKind.Movie)
        {
            var results = await QueryAsync(request.Year is int y ? $"{request.Title} {y}" : request.Title!, ct).ConfigureAwait(false);

            // Results tagged with a different IMDb id are other films with similar names.
            return results.Where(r => string.IsNullOrEmpty(request.ImdbId) || r.Imdb is null || string.Equals(r.Imdb, request.ImdbId, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Release)
                .ToList();
        }

        var episode = await QueryAsync($"{request.Title} S{request.Season:00}E{request.Episode:00}", ct).ConfigureAwait(false);
        if (episode.Count > 0)
        {
            return episode.Select(r => r.Release).ToList();
        }

        return (await QueryAsync($"{request.Title} S{request.Season:00}", ct).ConfigureAwait(false)).Select(r => r.Release).ToList();
    }

    public override async Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct)
        => (await QueryAsync(query, ct).ConfigureAwait(false)).Select(r => r.Release).ToList();

    private async Task<List<(string? Imdb, ReleaseCandidate Release)>> QueryAsync(string q, CancellationToken ct)
    {
        var json = await UseMirrorsAsync((b, t) => GetJsonAsync($"{b}/q.php?q={Esc(q)}&cat=200", t), ct).ConfigureAwait(false);
        var list = new List<(string?, ReleaseCandidate)>();
        foreach (var r in json as JsonArray ?? [])
        {
            var hash = (string?)r?["info_hash"];
            if (string.IsNullOrEmpty(hash) || hash.All(c => c == '0'))
            {
                continue; // "No results returned" placeholder
            }

            var imdb = (string?)r?["imdb"];
            list.Add((string.IsNullOrWhiteSpace(imdb) ? null : imdb, new ReleaseCandidate
            {
                Title = (string?)r?["name"] ?? string.Empty,
                Origin = Name,
                InfoHash = hash.ToLowerInvariant(),
                Seeders = (int)ToLong(r?["seeders"]),
                Peers = (int)(ToLong(r?["seeders"]) + ToLong(r?["leechers"])),
                Size = ToLong(r?["size"])
            }));
        }

        return list;
    }
}
