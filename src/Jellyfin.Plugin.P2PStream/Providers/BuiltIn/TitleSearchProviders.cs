using System.Net;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.P2PStream.Providers.BuiltIn;

/// <summary>
/// KickAss Torrents: HTML search with seeders, leechers and sizes. The magnet comes from the detail page, fetched only when tried.
/// </summary>
public sealed partial class KickassProvider : SearchProvider
{
    public override string Id => "kickass";

    public override string Name => "KickAss Torrents";

    public override string Description => "Movies & TV · English";

    public override string DefaultUrl => "https://kickasstorrents.to";

    public override string[] Mirrors => ["https://katcr.to"];

    public override Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct)
        => SearchByTitleAsync(request, q => QueryAsync(q, ct));

    public override Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => QueryAsync(query, ct);

    private async Task<List<ReleaseCandidate>> QueryAsync(string q, CancellationToken ct)
    {
        string? used = null;
        var html = await UseMirrorsAsync(async (b, t) =>
        {
            var page = await GetPageAsync($"{b}/usearch/{Esc(q)}/", t).ConfigureAwait(false);
            used = b;
            return page;
        }, ct).ConfigureAwait(false);
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
                MagnetPageUrl = (used ?? BaseUrl) + link.Groups["href"].Value,
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
