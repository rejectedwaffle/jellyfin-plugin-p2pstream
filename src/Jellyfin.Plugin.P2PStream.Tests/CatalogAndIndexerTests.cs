using Jellyfin.Plugin.P2PStream.Catalog;
using Jellyfin.Plugin.P2PStream.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.P2PStream.Tests;

public class CatalogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p2p-test-" + Guid.NewGuid().ToString("N"));

    public CatalogFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void A_renamed_title_reuses_its_existing_file()
    {
        File.WriteAllText(Path.Combine(_dir, "Old Name (2001).strm"), "x");
        var file = CatalogBuilder.ReuseExisting(_dir, Path.Combine(_dir, "New Name (2001).strm"), "*.strm");
        Assert.Equal("Old Name (2001).strm", Path.GetFileName(file));
    }

    [Fact]
    public void An_episode_reuses_only_its_own_file()
    {
        File.WriteAllText(Path.Combine(_dir, "Old Show S01E01.strm"), "x");
        File.WriteAllText(Path.Combine(_dir, "Old Show S01E02.strm"), "x");
        Assert.Equal("Old Show S01E02.strm", Path.GetFileName(CatalogBuilder.ReuseExisting(_dir, Path.Combine(_dir, "New Show S01E02.strm"), "* S01E02.strm")));
        Assert.Equal("New Show S01E03.strm", Path.GetFileName(CatalogBuilder.ReuseExisting(_dir, Path.Combine(_dir, "New Show S01E03.strm"), "* S01E03.strm")));
    }

    [Fact]
    public void The_wanted_name_wins_when_it_exists_or_the_folder_is_new()
    {
        File.WriteAllText(Path.Combine(_dir, "A (2001).strm"), "x");
        File.WriteAllText(Path.Combine(_dir, "B (2001).strm"), "x");
        Assert.Equal("B (2001).strm", Path.GetFileName(CatalogBuilder.ReuseExisting(_dir, Path.Combine(_dir, "B (2001).strm"), "*.strm")));
        var fresh = Path.Combine(_dir, "missing", "C (2001).strm");
        Assert.Equal(fresh, CatalogBuilder.ReuseExisting(Path.Combine(_dir, "missing"), fresh, "*.strm"));
    }
}

public class TorznabTests
{
    [Fact]
    public void A_missing_seeder_count_is_unknown_not_zero()
    {
        TestSetup.Settings();
        const string xml = """
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
              <item><title>Example.Film.2001.1080p.WEB-GROUP</title><enclosure url="http://indexer.example/dl/1" length="1000" />
                <torznab:attr name="infohash" value="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" /></item>
              <item><title>Example.Film.2001.720p.WEB-GROUP</title><enclosure url="http://indexer.example/dl/2" length="1000" />
                <torznab:attr name="seeders" value="12" /><torznab:attr name="peers" value="20" /></item>
            </channel></rss>
            """;
        var list = new TorznabClient(new SourceHealth(), NullLogger<TorznabClient>.Instance).Parse(xml, "Test");
        Assert.Equal(-1, list[0].Seeders);
        Assert.Equal(12, list[1].Seeders);
        Assert.Equal(20, list[1].Peers);
    }
}

public class PlayingTitlesTests
{
    [Fact]
    public void A_torrent_counts_as_playing_only_while_its_title_plays()
    {
        try
        {
            Jellyfin.Plugin.P2PStream.Torrent.TorrentEngine.PlayingHashes = () => new HashSet<string>(["ABCDEF"], StringComparer.OrdinalIgnoreCase);
            Assert.True(Jellyfin.Plugin.P2PStream.Torrent.TorrentEngine.IsPlaying("abcdef"));
            Assert.False(Jellyfin.Plugin.P2PStream.Torrent.TorrentEngine.IsPlaying("123456"));
            Jellyfin.Plugin.P2PStream.Torrent.TorrentEngine.PlayingHashes = () => throw new InvalidOperationException("sessions unavailable");
            Assert.False(Jellyfin.Plugin.P2PStream.Torrent.TorrentEngine.IsPlaying("abcdef")); // never blocks clean-up on an error
        }
        finally
        {
            Jellyfin.Plugin.P2PStream.Torrent.TorrentEngine.PlayingHashes = null;
        }
    }
}
