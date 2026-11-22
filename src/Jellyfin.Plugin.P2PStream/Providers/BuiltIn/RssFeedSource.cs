using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.P2PStream.Configuration;

namespace Jellyfin.Plugin.P2PStream.Providers.BuiltIn;

/// <summary>
/// A user-added source: any site with an RSS or Atom search feed whose items carry magnet or .torrent links.
/// The search URL contains {query} where the search words go, e.g. https://site/search?q={query}&amp;page=rss.
/// Understands plain RSS, Atom, and Torznab-style attributes (seeders, size, infohash, magneturl) when present.
/// </summary>
public static partial class RssFeedSource
{
    public static Task<List<ReleaseCandidate>> SearchAsync(RssSource source, MediaRequest request, CancellationToken ct)
        => SearchProvider.SearchByTitleAsync(request, q => QueryAsync(source, q, ct));

    public static async Task<List<ReleaseCandidate>> QueryAsync(RssSource source, string query, CancellationToken ct)
    {
        var template = source.Url.Trim();
        var url = template.Contains("{query}", StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(template, "\\{query\\}", Uri.EscapeDataString(query).Replace("%20", "+", StringComparison.Ordinal), RegexOptions.IgnoreCase)
            : template + (template.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "q=" + Uri.EscapeDataString(query);
        var xml = await HttpClients.Default.GetStringAsync(new Uri(url), ct).ConfigureAwait(false);
        var name = string.IsNullOrWhiteSpace(source.Name) ? new Uri(url).Host : source.Name.Trim();
        return Parse(xml, name);
    }

    public static List<ReleaseCandidate> Parse(string xml, string origin)
    {
        var list = new List<ReleaseCandidate>();
        foreach (var item in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
        {
            string? Child(string local) => item.Elements().FirstOrDefault(e => e.Name.LocalName == local) is { } e ? (string?)e.Attribute("href") ?? e.Value : null;
            var attrs = item.Elements().Where(e => e.Name.LocalName == "attr")
                .Select(e => (Name: (string?)e.Attribute("name"), Value: (string?)e.Attribute("value")))
                .Where(a => a.Name is not null && a.Value is not null)
                .GroupBy(a => a.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value!, StringComparer.OrdinalIgnoreCase);

            var title = Child("title")?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var link = Child("link")?.Trim();
            var enclosure = item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure");
            var enclosureUrl = (string?)enclosure?.Attribute("url");
            var description = Child("description") ?? Child("summary") ?? Child("content") ?? string.Empty;

            var magnet = new[] { attrs.GetValueOrDefault("magneturl"), link, enclosureUrl }.FirstOrDefault(IsMagnet);
            if (magnet is null && MagnetInText().Match(description) is { Success: true } m)
            {
                magnet = System.Net.WebUtility.HtmlDecode(m.Value);
            }
            var download = new[] { enclosureUrl, link }.FirstOrDefault(u => u is not null && u.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && (u.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || (string?)enclosure?.Attribute("type") == "application/x-bittorrent" || u == enclosureUrl));
            if (magnet is null && download is null)
            {
                continue; // nothing playable
            }

            var hash = attrs.GetValueOrDefault("infohash") ?? Child("infoHash")
                ?? (magnet is not null && HashInMagnet().Match(magnet) is { Success: true } h ? h.Groups[1].Value : null);
            var size = Long(attrs.GetValueOrDefault("size")) ?? Long((string?)enclosure?.Attribute("length")) ?? Long(Child("size"));
            if (size is null && Child("size") is { } sizeText)
            {
                size = SearchProvider.ParseSize(sizeText); // e.g. "1.4 GiB"
            }

            var seeders = Long(attrs.GetValueOrDefault("seeders")) ?? Long(Child("seeders"))
                ?? (SeedersInText().Match(description) is { Success: true } s ? Long(s.Groups[1].Value) : null);

            list.Add(new ReleaseCandidate
            {
                Title = title,
                Origin = origin,
                MagnetUrl = magnet,
                DownloadUrl = download,
                InfoHash = hash?.ToLowerInvariant(),
                Size = size is > 0 ? size.Value : SearchProvider.ParseSize(description),
                Seeders = seeders is null ? -1 : (int)seeders.Value,
                Peers = (int)(Long(attrs.GetValueOrDefault("peers")) ?? ((seeders ?? 0) + (Long(Child("leechers")) ?? 0)))
            });
        }

        return list;
    }

    private static bool IsMagnet(string? s) => s is not null && s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    private static long? Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"magnet:\?xt=urn:btih:[^""'<>\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetInText();

    [GeneratedRegex(@"urn:btih:([0-9a-fA-F]{40})")]
    private static partial Regex HashInMagnet();

    [GeneratedRegex(@"seed(?:er)?s?\W{0,3}(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeedersInText();
}
