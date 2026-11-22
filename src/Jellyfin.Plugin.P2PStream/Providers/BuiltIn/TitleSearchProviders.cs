using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfin.Plugin.P2PStream.Providers.BuiltIn;

/// <summary>BT4G: a DHT search engine. Uses its RSS feed (titles, magnets, sizes; no seeder counts).</summary>
public sealed class Bt4gProvider : SearchProvider
{
    private static readonly HashSet<string> NonVideo = new(StringComparer.OrdinalIgnoreCase) { "Audio", "Application", "Doc", "Document", "Archive", "Image", "Software" };

    public override string Id => "bt4g";

    public override string Name => "BT4G";

    public override string Description => "Movies & TV · English";

    public override string DefaultUrl => "https://bt4gprx.com";

    public override Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => SearchByTitleAsync(request, q => QueryAsync(q, ct));

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string q, CancellationToken ct)
    {
        var xml = await GetPageAsync($"{BaseUrl}/search?q={Esc(q)}&page=rss", ct).ConfigureAwait(false);
        var list = new List<ReleaseCandidate>();
        foreach (var item in XDocument.Parse(xml).Descendants("item"))
        {
            var magnet = (string?)item.Element("link");
            if (magnet is null || !magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Description is "title<br>size<br>category<br>infohash"
            var parts = ((string?)item.Element("description") ?? string.Empty).Split("<br>");
            if (parts.Length > 2 && NonVideo.Contains(parts[2].Trim()))
            {
                continue;
            }

            list.Add(new ReleaseCandidate
            {
                Title = (string?)item.Element("title") ?? string.Empty,
                Origin = Name,
                MagnetUrl = magnet,
                InfoHash = parts.Length > 3 ? parts[3].Trim().ToLowerInvariant() : null,
                Size = parts.Length > 1 ? ParseSize(parts[1]) : 0,
                Seeders = -1 // the RSS feed doesn't include seeders
            });
        }

        return list;
    }
}

/// <summary>
/// KickAss Torrents: HTML search with seeders, leechers and sizes. The magnet comes from the detail page, fetched only when tried.
/// </summary>
public sealed partial class KickassProvider : SearchProvider
{
    public override string Id => "kickass";

    public override string Name => "KickAss Torrents";

    public override string Description => "Movies & TV · English";

    public override string DefaultUrl => "https://kickasstorrents.to";

    public override Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => SearchByTitleAsync(request, q => QueryAsync(q, ct));

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string q, CancellationToken ct)
    {
        var html = await GetPageAsync($"{BaseUrl}/usearch/{Esc(q)}/", ct).ConfigureAwait(false);
        var list = new List<ReleaseCandidate>();
        foreach (var row in RowSplit().Split(html).Skip(1))
        {
            var link = MainLink().Match(row);
            if (!link.Success)
            {
                continue;
            }

            list.Add(new ReleaseCandidate
            {
                Title = WebUtility.HtmlDecode(StripTags().Replace(link.Groups["name"].Value, string.Empty)).Trim(),
                Origin = Name,
                MagnetPageUrl = BaseUrl + link.Groups["href"].Value,
                Size = ParseSize(SizeCell().Match(row).Groups[1].Value),
                Seeders = int.TryParse(Seeds().Match(row).Groups[1].Value, out var s) ? s : -1,
                Peers = int.TryParse(Leech().Match(row).Groups[1].Value, out var l) ? l + Math.Max(0, s) : 0
            });
        }

        return list;
    }

    [GeneratedRegex(@"<tr class=""(?:odd|even)""")]
    private static partial Regex RowSplit();

    [GeneratedRegex(@"<a href=""(?<href>/[^""]+\.html)""\s*class=""cellMainLink"">(?<name>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex MainLink();

    [GeneratedRegex(@"<td class=""nobr center"">\s*([^<]+?)\s*</td>")]
    private static partial Regex SizeCell();

    [GeneratedRegex(@"<td class=""green center"">\s*(\d+)")]
    private static partial Regex Seeds();

    [GeneratedRegex(@"<td class=""red lasttd center"">\s*(\d+)")]
    private static partial Regex Leech();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex StripTags();
}
