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

    /// <summary>How long a scrape result is reused before the trackers are asked again.</summary>
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(15);

    private static readonly ConcurrentDictionary<string, (Result Result, DateTime At)> Recent = new();

    /// <summary>
    /// Scrapes all the info hashes (40 hex characters) in one request per tracker, on every tracker at once, and keeps
    /// the highest counts (trackers only know the peers that announce to them). Hashes no tracker answered for are left out.
    /// </summary>
    public static async Task<Dictionary<string, Result>> ScrapeAsync(IEnumerable<string> infoHashes, TimeSpan timeout, CancellationToken ct)
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
        var answers = await Task.WhenAll(Trackers.Select(async tracker =>
        {
            try
            {
                var connection = new GuardedUdpTrackerConnection(new Uri(tracker), AddressFamily.InterNetwork);
                var got = await connection.ScrapeManyAsync(asHashes, limit.Token).ConfigureAwait(false);
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
