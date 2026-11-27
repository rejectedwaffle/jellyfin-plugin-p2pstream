using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.P2PStream.Configuration;
using Jellyfin.Plugin.P2PStream.Torrent;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Queries Torznab endpoints (Prowlarr, Jackett, ...) and turns results into torrent sources.
/// </summary>
public sealed class TorznabClient
{
    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";

    private static readonly string[] FallbackTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://open.demonii.com:1337/announce"
    ];

    private readonly ILogger<TorznabClient> _logger;

    private readonly SourceHealth _health;

    public TorznabClient(SourceHealth health, ILogger<TorznabClient> logger)
    {
        _health = health;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Searches every enabled indexer for a movie or episode. Tries the most precise query first
    /// (IMDb id), then falls back to text, then (for episodes) season packs.
    /// </summary>
    public async Task<List<ReleaseCandidate>> SearchAsync(MediaRequest req, CancellationToken ct)
    {
        var stages = BuildStages(req);
        foreach (var stage in stages)
        {
            var results = await SearchAllAsync(stage, ct).ConfigureAwait(false);
            if (results.Count > 0)
            {
                _logger.LogDebug("Torznab: {Count} results for {Request} using t={Type}", results.Count, req, stage["t"]);
                return results;
            }
        }

        return [];
    }

    /// <summary>Free-text search across every indexer (used by the config page).</summary>
    public Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct)
        => SearchAllAsync(new Dictionary<string, string> { ["t"] = "search", ["q"] = query }, ct);

    /// <summary>
    /// Converts a release into something the engine can add: a magnet, or the bytes of a .torrent file.
    /// <paramref name="trustedLink"/>: the link was typed by an administrator (Add a torrent), so it may point into the
    /// home network; links from search results may not.
    /// </summary>
    public async Task<TorrentSource> ToSourceAsync(ReleaseCandidate release, CancellationToken ct, bool trustedLink = false)
    {
        if (IsMagnet(release.MagnetUrl))
        {
            return TorrentSource.FromMagnet(release.MagnetUrl!);
        }

        if (IsMagnet(release.DownloadUrl))
        {
            return TorrentSource.FromMagnet(release.DownloadUrl!);
        }

        if (!string.IsNullOrEmpty(release.MagnetPageUrl))
        {
            var (redirected, body) = await FetchAsync(release.MagnetPageUrl!, trustedLink, ct).ConfigureAwait(false);
            if (redirected is not null)
            {
                return TorrentSource.FromMagnet(redirected);
            }

            var page = Encoding.UTF8.GetString(body!);
            var m = System.Text.RegularExpressions.Regex.Match(page, @"href=""(magnet:\?xt=urn:btih:[^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return TorrentSource.FromMagnet(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
            }

            throw new InvalidOperationException($"No magnet link found on {release.MagnetPageUrl}");
        }

        if (!string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            try
            {
                return await DownloadAsync(release.DownloadUrl!, trustedLink, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && !string.IsNullOrEmpty(release.InfoHash))
            {
                _logger.LogDebug(ex, "Download of {Title} failed, falling back to info hash", release.Title);
            }
        }

        if (!string.IsNullOrEmpty(release.InfoHash))
        {
            return TorrentSource.FromMagnet(BuildMagnet(release.InfoHash!, release.Title, release.Trackers));
        }

        throw new InvalidOperationException($"Release '{release.Title}' has no usable link");
    }

    public bool HasIndexers => Config.Indexers.Any(i => i.Enabled && !string.IsNullOrWhiteSpace(i.Url));

    internal static bool IsMagnet(string? s) => s is not null && s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    private static string BuildMagnet(string infoHash, string name, IEnumerable<string> extraTrackers)
    {
        var sb = new StringBuilder("magnet:?xt=urn:btih:").Append(infoHash).Append("&dn=").Append(Uri.EscapeDataString(name));
        foreach (var t in extraTrackers.Concat(FallbackTrackers).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("&tr=").Append(Uri.EscapeDataString(t));
        }

        return sb.ToString();
    }

    private static List<Dictionary<string, string>> BuildStages(MediaRequest req)
    {
        var imdb = req.ImdbId?.Trim();
        if (imdb is not null && imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            imdb = imdb[2..]; // Newznab spec: imdbid without the "tt" prefix
        }

        var stages = new List<Dictionary<string, string>>();
        var s = req.Season.ToString(CultureInfo.InvariantCulture);
        var e = req.Episode.ToString(CultureInfo.InvariantCulture);

        if (req.Kind == MediaKind.Movie)
        {
            if (!string.IsNullOrEmpty(imdb))
            {
                stages.Add(new() { ["t"] = "movie", ["imdbid"] = imdb });
            }

            if (!string.IsNullOrEmpty(req.Title))
            {
                stages.Add(new() { ["t"] = "search", ["q"] = req.Year is int y ? $"{req.Title} {y}" : req.Title });
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(imdb))
            {
                stages.Add(new() { ["t"] = "tvsearch", ["imdbid"] = imdb, ["season"] = s, ["ep"] = e });
            }

            if (!string.IsNullOrEmpty(req.Title))
            {
                stages.Add(new() { ["t"] = "tvsearch", ["q"] = req.Title, ["season"] = s, ["ep"] = e });
                stages.Add(new() { ["t"] = "search", ["q"] = $"{req.Title} S{req.Season:00}E{req.Episode:00}" });
                stages.Add(new() { ["t"] = "tvsearch", ["q"] = req.Title, ["season"] = s });
                stages.Add(new() { ["t"] = "search", ["q"] = $"{req.Title} S{req.Season:00}" });
            }
        }

        return stages;
    }

    private async Task<List<ReleaseCandidate>> SearchAllAsync(Dictionary<string, string> query, CancellationToken ct)
    {
        var indexers = Config.Indexers.Where(i => i.Enabled && !string.IsNullOrWhiteSpace(i.Url)).ToList();
        if (indexers.Count == 0)
        {
            throw new StreamNotFoundException("No Torznab indexers are configured");
        }

        var results = await Task.WhenAll(indexers.Select(i => SearchIndexerAsync(i, query, ct))).ConfigureAwait(false);

        // De-duplicate across indexers by info hash (or title), keeping the best-seeded copy.
        return results.SelectMany(r => r)
            .GroupBy(r => string.IsNullOrEmpty(r.InfoHash) ? "t:" + r.Title.ToLowerInvariant() : r.InfoHash!.ToLowerInvariant())
            .Select(g => g.OrderByDescending(r => r.Seeders).First())
            .ToList();
    }

    private async Task<List<ReleaseCandidate>> SearchIndexerAsync(TorznabIndexer indexer, Dictionary<string, string> query, CancellationToken ct)
    {
        var url = new StringBuilder(indexer.Url.Trim());
        url.Append(indexer.Url.Contains('?', StringComparison.Ordinal) ? '&' : '?');
        if (!string.IsNullOrWhiteSpace(indexer.ApiKey))
        {
            url.Append("apikey=").Append(Uri.EscapeDataString(indexer.ApiKey.Trim())).Append('&');
        }

        url.Append(string.Join('&', query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var name = string.IsNullOrWhiteSpace(indexer.Name) ? indexer.Url : indexer.Name;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var xml = await HttpClients.Default.GetStringAsync(new Uri(url.ToString()), timeout.Token).ConfigureAwait(false);
            var results = Parse(xml, name);
            _health.Success(name, results.Count, sw.ElapsedMilliseconds);
            return results;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _health.Failure(name, ex, sw.ElapsedMilliseconds);
            _logger.LogWarning("Indexer {Name} failed: {Message}", indexer.Name, ex.Message);
            return [];
        }
    }

    private List<ReleaseCandidate> Parse(string xml, string indexerName)
    {
        var doc = XDocument.Parse(xml);
        if (doc.Root?.Name.LocalName == "error")
        {
            _logger.LogWarning("Indexer {Name} returned error: {Error}", indexerName, (string?)doc.Root.Attribute("description"));
            return [];
        }

        var list = new List<ReleaseCandidate>();
        foreach (var item in doc.Descendants("item"))
        {
            var attrs = item.Elements(TorznabNs + "attr")
                .Select(a => (Name: (string?)a.Attribute("name"), Value: (string?)a.Attribute("value")))
                .Where(a => a.Name is not null && a.Value is not null)
                .GroupBy(a => a.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value!, StringComparer.OrdinalIgnoreCase);

            var enclosure = item.Element("enclosure");
            var size = ParseLong((string?)item.Element("size"))
                ?? ParseLong(attrs.GetValueOrDefault("size"))
                ?? ParseLong((string?)enclosure?.Attribute("length"))
                ?? 0;
            var seeders = (int)(ParseLong(attrs.GetValueOrDefault("seeders")) ?? 0);

            list.Add(new ReleaseCandidate
            {
                Title = ((string?)item.Element("title") ?? string.Empty).Trim(),
                Origin = indexerName,
                DownloadUrl = (string?)enclosure?.Attribute("url") ?? (string?)item.Element("link"),
                MagnetUrl = attrs.GetValueOrDefault("magneturl"),
                InfoHash = attrs.GetValueOrDefault("infohash"),
                Size = size,
                Seeders = seeders,
                Peers = (int)(ParseLong(attrs.GetValueOrDefault("peers")) ?? seeders)
            });
        }

        return list;
    }

    private static long? ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The largest .torrent file (or magnet page) that is downloaded; real ones are well under this.</summary>
    private const int MaxDownloadBytes = 20 << 20;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Hosts you typed yourself (indexers, RSS feeds, a source's address): links to them may point into your network,
    /// as a Prowlarr or Jackett on your network does. Any other host a link or redirect leads to must be on the internet.
    /// </summary>
    private static HashSet<string> TrustedHosts()
    {
        var c = Plugin.Instance?.Configuration;
        var urls = (c?.Indexers.Select(i => i.Url) ?? [])
            .Concat(c?.RssSources.Select(r => r.Url.Replace("{query}", "q", StringComparison.Ordinal)) ?? [])
            .Concat(c?.Sources.Select(b => b.BaseUrl) ?? []);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in urls)
        {
            if (Uri.TryCreate(u?.Trim(), UriKind.Absolute, out var parsed) && parsed.Host.Length > 0)
            {
                hosts.Add(parsed.Host);
            }
        }

        return hosts;
    }

    /// <summary>
    /// Fetches a link from a search result, following redirects one at a time so each hop is checked against
    /// <see cref="AddressPolicy"/> (unless it's a host you typed, or <paramref name="trusted"/>). Returns a magnet link
    /// a redirect led to, or the body (at most <see cref="MaxDownloadBytes"/>).
    /// </summary>
    private static async Task<(string? Magnet, byte[]? Body)> FetchAsync(string link, bool trusted, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        ct = timeout.Token;
        var trustedHosts = trusted ? null : TrustedHosts();
        var url = new Uri(link);
        for (var hop = 0; hop < 6; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException($"Unsupported link: {url.Scheme}");
            }

            if (trustedHosts is not null && !trustedHosts.Contains(url.Host))
            {
                await AddressPolicy.ResolvePublicAsync(url.IdnHost, "download link", ct).ConfigureAwait(false);
            }

            using var resp = await HttpClients.NoRedirect.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is >= 300 and < 400)
            {
                // Read the raw header: HttpClient can't parse many magnet links into a Uri.
                var location = resp.Headers.TryGetValues("Location", out var values) ? values.FirstOrDefault() : null;
                if (string.IsNullOrEmpty(location))
                {
                    break;
                }

                if (IsMagnet(location))
                {
                    return (location, null);
                }

                url = new Uri(url, location);
                continue;
            }

            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                throw new InvalidOperationException("The download is too large to be a .torrent file");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                {
                    throw new InvalidOperationException("The download is too large to be a .torrent file");
                }

                buffer.Write(chunk, 0, read);
            }

            return (null, buffer.ToArray());
        }

        throw new InvalidOperationException("Too many redirects");
    }

    private static async Task<TorrentSource> DownloadAsync(string link, bool trusted, CancellationToken ct)
    {
        var (magnet, bytes) = await FetchAsync(link, trusted, ct).ConfigureAwait(false);
        if (magnet is not null)
        {
            return TorrentSource.FromMagnet(magnet);
        }

        if (bytes!.Length > 7 && Encoding.ASCII.GetString(bytes, 0, 7).Equals("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return TorrentSource.FromMagnet(Encoding.UTF8.GetString(bytes));
        }

        return TorrentSource.FromTorrentBytes(bytes);
    }
}
