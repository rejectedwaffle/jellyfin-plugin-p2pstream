using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.P2PStream.Providers.BuiltIn;

/// <summary>
/// Torrents-CSV: an open torrent database with a JSON search API. Every result carries its torrent id and seeder counts
/// the site refreshes by asking the trackers, so live checks start at once. Smaller than the big sites, mostly
/// well-known releases.
/// </summary>
public sealed class TorrentsCsvProvider : SearchProvider
{
    public override string Id => "torrentscsv";

    public override string Name => "Torrents-CSV";

    public override string Description => "Movies & TV · open database, torrent ids included";

    public override string DefaultUrl => "https://torrents-csv.com";

    public override Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => SearchByTitleAsync(request, q => QueryAsync(q, ct));

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string query, CancellationToken ct)
    {
        var json = await UseMirrorsAsync((b, t) => GetJsonAsync($"{b}/service/search?q={Esc(query)}&size=50", t), ct).ConfigureAwait(false);
        var list = new List<ReleaseCandidate>();
        foreach (var r in json?["torrents"] as JsonArray ?? [])
        {
            var hash = (string?)r?["infohash"];
            if (string.IsNullOrEmpty(hash) || hash.Length != 40)
            {
                continue;
            }

            list.Add(new ReleaseCandidate
            {
                Title = (string?)r?["name"] ?? string.Empty,
                Origin = Name,
                InfoHash = hash.ToLowerInvariant(),
                Size = ToLong(r?["size_bytes"]),
                Seeders = (int)ToLong(r?["seeders"]),
                Peers = (int)(ToLong(r?["seeders"]) + ToLong(r?["leechers"]))
            });
        }

        return list;
    }
}

/// <summary>
/// BitSearch: a large search engine built by crawling the DHT, so it also finds releases that sites have dropped.
/// Results are read from its search page (best seeded first); each carries its magnet link.
/// </summary>
public sealed partial class BitSearchProvider : SearchProvider
{
    public override string Id => "bitsearch";

    public override string Name => "BitSearch";

    public override string Description => "Movies & TV · DHT search engine, finds releases sites have dropped";

    public override string DefaultUrl => "https://bitsearch.eu";

    public override string[] Mirrors => ["https://bitsearch.to"];

    public override Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => SearchByTitleAsync(request, q => QueryAsync(q, ct));

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string query, CancellationToken ct)
    {
        var page = await UseMirrorsAsync((b, t) => GetPageAsync($"{b}/search?q={Esc(query)}&sortBy=seeders&order=desc", t), ct).ConfigureAwait(false);
        return Parse(page, Name);
    }

    /// <summary>Reads the result cards of a search page.</summary>
    internal static List<ReleaseCandidate> Parse(string page, string origin)
    {
        var list = new List<ReleaseCandidate>();
        var cards = page.Split("torrent-result-card", StringSplitOptions.None).Skip(1);
        foreach (var card in cards)
        {
            var magnet = MagnetLink().Match(card) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
            var hash = magnet is not null && HashInMagnet().Match(magnet) is { Success: true } h ? h.Groups[1].Value.ToLowerInvariant() : null;
            var title = TitleLink().Match(card) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value).Trim() : null;
            if (hash is null || string.IsNullOrEmpty(title))
            {
                continue;
            }

            var seeders = Swarm(card, "seeders");
            list.Add(new ReleaseCandidate
            {
                Title = title,
                Origin = origin,
                MagnetUrl = magnet,
                InfoHash = hash,
                Size = SizeText().Match(card) is { Success: true } s ? ParseSize(s.Groups[1].Value) : 0,
                Seeders = seeders,
                Peers = seeders + Math.Max(0, Swarm(card, "leechers"))
            });
        }

        return list;
    }

    private static int Swarm(string card, string field)
    {
        var m = Regex.Match(card, "data-swarm-field=\"" + field + "\">\\s*([\\d,]+)\\s*<");
        return m.Success && int.TryParse(m.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), out var n) ? n : -1;
    }

    [GeneratedRegex("href=\"(magnet:\\?[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetLink();

    [GeneratedRegex("btih:([0-9a-fA-F]{40})")]
    private static partial Regex HashInMagnet();

    [GeneratedRegex("<a href=\"/torrent/[^\"]+\"[^>]*>([^<]+)</a>")]
    private static partial Regex TitleLink();

    [GeneratedRegex("fa-download\"></i>\\s*<span>([\\d.,]+\\s*[KMGT]i?B)</span>", RegexOptions.IgnoreCase)]
    private static partial Regex SizeText();
}
