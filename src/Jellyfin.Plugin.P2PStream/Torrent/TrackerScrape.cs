using System.Collections.Concurrent;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Trackers;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// Asks public trackers how many seeders and leechers a torrent has right now (a BEP 15 "scrape"), through the same
/// network path as torrent traffic (VPN or proxy). Torrent sites' own seeder counts are often stale.
/// </summary>
public static class TrackerScrape
{
    /// <summary>Well-known public UDP trackers that answer scrapes.</summary>
    public static readonly string[] Trackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://open.demonii.com:1337/announce",
    ];

    public sealed record Result(int Seeders, int Leechers, int TrackersAnswered);

    /// <summary>The UDP trackers a magnet link lists (its tr= parameters).</summary>
    public static IEnumerable<string> TrackersOf(string? magnet)
    {
        if (string.IsNullOrEmpty(magnet) || !magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var query = magnet.Contains('?', StringComparison.Ordinal) ? magnet[(magnet.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty;
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith("tr=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Uri.UnescapeDataString(p[3..].Replace('+', ' ')).Trim())
            .Where(t => t.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Up to 10 extra trackers, the most often listed first, leaving out the public ones already asked (by host, since
    /// magnets spell the same tracker in several ways).
    /// </summary>
    private static IEnumerable<string> MoreTrackers(IEnumerable<string>? extra)
    {
        if (extra is null)
        {
            return [];
        }

        var known = Trackers.Select(t => new Uri(t).Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return extra
            .Select(t => Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == "udp" && u.Port > 0 ? u : null)
            .OfType<Uri>()
            .Where(u => !known.Contains(u.Host))
            .GroupBy(u => u.Host, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.First().ToString())
            .ToList();
    }

    /// <summary>How long a scrape result is reused before the trackers are asked again.</summary>
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(15);

    private static readonly ConcurrentDictionary<string, (Result Result, DateTime At)> Recent = new();

    /// <summary>One connection per tracker, kept: its connection id is reused (a round trip less per check).</summary>
    private static readonly ConcurrentDictionary<string, GuardedUdpTrackerConnection> Connections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scrapes all the info hashes (40 hex characters) in one request per tracker, on every tracker at once, and keeps
    /// the highest counts (trackers only know the peers that announce to them). Hashes no tracker answered for are left out.
    /// </summary>
    /// <param name="extraTrackers">The torrents' own trackers (from their magnet links), asked as well as the public ones.</param>
    public static async Task<Dictionary<string, Result>> ScrapeAsync(IEnumerable<string> infoHashes, TimeSpan timeout, CancellationToken ct, IEnumerable<string>? extraTrackers = null)
    {
        var all = infoHashes.Where(h => h is { Length: 40 }).Select(h => h.ToLowerInvariant()).Distinct().ToList();
        var now = DateTime.UtcNow;
        var known = new Dictionary<string, Result>();
        foreach (var h in all)
        {
            if (Recent.TryGetValue(h, out var seen) && now - seen.At < Remember)
            {
                known[h] = seen.Result;
            }
        }

        var hashes = all.Where(h => !known.ContainsKey(h)).ToList();
        if (hashes.Count == 0)
        {
            return known;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        var asHashes = hashes.Select(InfoHash.FromHex).ToList();
        var trackers = Trackers.Concat(MoreTrackers(extraTrackers)).ToList();
        if (Connections.Count > 200)
        {
            Connections.Clear(); // magnets name many trackers; don't keep them all forever
        }

        var answers = await Task.WhenAll(trackers.Select(async tracker =>
        {
            try
            {
                // WaitAsync: looking up a tracker's address doesn't always stop when asked, and must not hold up the rest.
                var connection = Connections.GetOrAdd(tracker, t => new GuardedUdpTrackerConnection(new Uri(t), AddressFamily.InterNetwork) { ResendEvery = TimeSpan.FromMilliseconds(750) });
                var got = await connection.ScrapeManyAsync(asHashes, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
                return got.Select(kv => (Hash: kv.Key.ToHex().ToLowerInvariant(), Seeds: kv.Value.Seeders, Leeches: kv.Value.Leechers)).ToList();
            }
            catch (Exception)
            {
                return []; // this tracker didn't answer in time
            }
        })).ConfigureAwait(false);

        foreach (var g in answers.SelectMany(a => a).GroupBy(a => a.Hash))
        {
            var result = new Result(g.Max(x => x.Seeds), g.Max(x => x.Leeches), g.Count());
            known[g.Key] = result;
            Recent[g.Key] = (result, now);
        }

        if (Recent.Count > 5000)
        {
            foreach (var old in Recent.Where(kv => now - kv.Value.At >= Remember).Select(kv => kv.Key).ToList())
            {
                Recent.TryRemove(old, out _);
            }
        }

        return known;
    }
}
