using System.Text.Json.Nodes;
using Jellyfin.Plugin.P2PStream.Providers;
using Jellyfin.Plugin.P2PStream.Providers.BuiltIn;
using Jellyfin.Plugin.P2PStream.Torrent;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.P2PStream.Tests.TestSetup;

namespace Jellyfin.Plugin.P2PStream.Tests;

/// <summary>Choosing a downloaded subtitle that fits the release being played.</summary>
public class SubtitleMatchTests
{
    private const string Playing = "Sample Film (2020) 1080p BRRip x264 -YTS";

    [Fact]
    public void A_subtitle_made_for_the_same_group_beats_one_for_another_rip() =>
        Assert.True(MissingSubtitles.ReleaseMatch(Playing, "Sample.Film.2020.1080p.BluRay.x264-YIFY")
            > MissingSubtitles.ReleaseMatch(Playing, "Sample.Film.2020.DVDRip.XviD-OTHER"));

    [Fact]
    public void A_different_cut_counts_against() =>
        Assert.True(MissingSubtitles.ReleaseMatch(Playing, "Sample.Film.2020.EXTENDED.1080p.BluRay.x264-OTHER")
            < MissingSubtitles.ReleaseMatch(Playing, "Sample.Film.2020.1080p.BluRay.x264-OTHER"));

    [Fact]
    public void The_group_at_the_end_of_the_name_matches_whatever_it_is() =>
        Assert.True(MissingSubtitles.ReleaseMatch("Example Show S02E05 1080p WEB H264-NHTFS", "Example.Show.S02E05.720p.HDTV-NHTFS")
            > MissingSubtitles.ReleaseMatch("Example Show S02E05 1080p WEB H264-NHTFS", "Example.Show.S02E05.720p.HDTV-OTHERS"));

    [Fact]
    public void Nothing_to_compare_scores_zero()
    {
        Assert.Equal(0, MissingSubtitles.ReleaseMatch(null, "Sample.Film.2020.1080p"));
        Assert.Equal(0, MissingSubtitles.ReleaseMatch(Playing, null));
    }
}

/// <summary>Reading the built-in sources' answers.</summary>
public class SourceParsingTests
{
    private const string BitSearchPage = """
        <style>.torrent-result-card { padding: 1em }</style>
        <div class="torrent-result-card bg-white">
          <h3><a href="/torrent/abc123" data-listed-torrent="abc123" class="x">
              Example.Show.S02E05.1080p.WEB.H264-GROUP
          </a></h3>
          <span class="inline-flex"><i class="fas fa-download"></i>
              <span>1.4 GB</span></span>
          <span data-swarm-id="abc123" data-swarm-field="seeders">1,234</span>
          <span data-swarm-id="abc123" data-swarm-field="leechers">56</span>
          <a href="magnet:?xt&#x3D;urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&amp;dn&#x3D;Example&amp;tr&#x3D;udp%3A%2F%2Ftracker.example.org%3A1337%2Fannounce">Magnet</a>
        </div>
        <div class="torrent-result-card bg-white">
          <h3><a href="/torrent/def456" data-listed-torrent="def456">No magnet here</a></h3>
        </div>
        """;

    [Fact]
    public void BitSearch_cards_give_name_size_seeders_and_magnet()
    {
        var list = BitSearchProvider.Parse(BitSearchPage, "BitSearch");
        var r = Assert.Single(list); // the style rule and the card without a magnet are skipped
        Assert.Equal("Example.Show.S02E05.1080p.WEB.H264-GROUP", r.Title);
        Assert.Equal(new string('a', 40), r.InfoHash);
        Assert.Equal(1234, r.Seeders);
        Assert.Equal(1234 + 56, r.Peers);
        Assert.InRange(r.Size, 1_400_000_000L, 1_600_000_000L);
        Assert.StartsWith("magnet:?xt=urn:btih:", r.MagnetUrl);
        Assert.True(r.StaleCounts);
    }

    [Fact]
    public void Torrents_csv_answer_is_read_and_bad_entries_skipped()
    {
        var json = JsonNode.Parse("""
            {"torrents":[
              {"infohash":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB","name":"Sample Film 2020 1080p","size_bytes":2000000000,"seeders":40,"leechers":3},
              {"infohash":"short","name":"broken"}
            ]}
            """);
        var r = Assert.Single(TorrentsCsvProvider.Parse(json, "Torrents-CSV"));
        Assert.Equal(new string('b', 40), r.InfoHash);
        Assert.Equal(40, r.Seeders);
        Assert.Equal(43, r.Peers);
        Assert.False(r.StaleCounts);
    }

    [Fact]
    public void A_magnet_gives_its_UDP_trackers_only()
    {
        var magnet = "magnet:?xt=urn:btih:" + Hash('c') + "&tr=udp%3A%2F%2Ftracker.one.example%3A1337%2Fannounce&tr=http%3A%2F%2Fweb.example%2Fannounce&tr=udp://tracker.two.example:6969/announce";
        Assert.Equal(["udp://tracker.one.example:1337/announce", "udp://tracker.two.example:6969/announce"], TrackerScrape.TrackersOf(magnet).ToArray());
        Assert.Empty(TrackerScrape.TrackersOf("https://not.a.magnet"));
    }

    [Fact]
    public void Extra_trackers_leave_out_the_public_ones_and_are_capped_at_ten()
    {
        var extra = Enumerable.Range(1, 15).Select(i => $"udp://tracker{i}.example:1337/announce")
            .Append("udp://tracker.opentrackr.org:1337/announce")
            .Append("udp://tracker1.example:1337/announce"); // listed twice: comes first
        var picked = TrackerScrape.MoreTrackers(extra).ToList();
        Assert.Equal(10, picked.Count);
        Assert.DoesNotContain(picked, t => t.Contains("opentrackr", StringComparison.Ordinal));
        Assert.Contains("tracker1.example", picked[0], StringComparison.Ordinal);
    }
}

/// <summary>Start-up messages: who gets them, how often, and the "list complete" message.</summary>
public class StartStatusTests
{
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly List<(string Session, string Text)> _sent = [];
    private readonly StreamOptions _options = new(null!, null!, null!, NullLogger<StreamOptions>.Instance);
    private readonly StartStatus _status;

    public StartStatusTests()
    {
        Settings();
        _sessions.Setup(s => s.SendMessageCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, MessageCommand, CancellationToken>((_, id, cmd, _) => { lock (_sent) { _sent.Add((id, cmd.Text)); } })
            .Returns(Task.CompletedTask);
        _sessions.SetupGet(s => s.Sessions).Returns([]);
        _status = new StartStatus(_sessions.Object, _options, NullLogger<StartStatus>.Instance);
    }

    private List<(string Session, string Text)> SentAfter(int ms)
    {
        Thread.Sleep(ms);
        lock (_sent)
        {
            return [.. _sent];
        }
    }

    [Fact]
    public void Only_the_session_that_pressed_play_is_told()
    {
        var title = Episode();
        _status.Watch(title, "tv");
        _status.Report(title, "Trying 2 releases at once");
        _status.Report(Episode(1, 1), "about another title");
        var sent = SentAfter(500);
        Assert.Equal([("tv", "Trying 2 releases at once")], sent);
    }

    [Fact]
    public void Messages_close_together_are_spaced_out_and_the_newest_wins()
    {
        var title = Episode();
        _status.Watch(title, "tv");
        _status.Report(title, "first");
        Thread.Sleep(300);
        _status.Report(title, "second");
        _status.Report(title, "third");
        Assert.Single(SentAfter(500));
        var sent = SentAfter(3500);
        Assert.Equal(["first", "third"], sent.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void Nothing_is_sent_once_the_title_is_done()
    {
        var title = Episode();
        _status.Watch(title, "tv");
        _status.Done(title);
        _status.Report(title, "too late");
        Assert.Empty(SentAfter(500));
    }

    [Fact]
    public void Someone_who_opened_the_page_hears_when_the_list_is_complete()
    {
        // The search is still running for this title, so the list counts as being checked.
        var title = Episode(3, 1);
        var inflight = typeof(StreamOptions).GetField("_liveChecking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((System.Collections.Concurrent.ConcurrentDictionary<string, byte>)inflight.GetValue(_options)!)[title.Key] = 0;

        var session = new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = "phone" };
        _sessions.SetupGet(s => s.Sessions).Returns([session]);

        _status.WatchPage(title, "phone", listShown: true);
        _status.WatchPage(title, "phone", listShown: true); // told once only
        Assert.Single(SentAfter(500));
        Assert.Contains("Checking live seeder counts", SentAfter(0)[0].Text, StringComparison.Ordinal);

        _options.NotifyLiveCheckFinished(title);
        var sent = SentAfter(3500);
        Assert.Equal(2, sent.Count);
        Assert.Contains("are in", sent[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Someone_who_pressed_play_meanwhile_is_not_told_to_reopen_the_page()
    {
        var title = Episode(3, 3);
        var inflight = typeof(StreamOptions).GetField("_liveChecking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((System.Collections.Concurrent.ConcurrentDictionary<string, byte>)inflight.GetValue(_options)!)[title.Key] = 0;
        _sessions.SetupGet(s => s.Sessions).Returns([new SessionInfo(_sessions.Object, NullLogger.Instance) { Id = "tv" }]);

        _status.WatchPage(title, "tv", listShown: true);
        _status.Watch(title, "tv"); // Play pressed
        _options.NotifyLiveCheckFinished(title);
        var sent = SentAfter(3500);
        Assert.Single(sent);
        Assert.Contains("Checking live seeder counts", sent[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_message_when_the_check_finished_before_the_page_registered()
    {
        _status.WatchPage(Episode(3, 2), "phone", listShown: true); // nothing is being checked
        Assert.Empty(SentAfter(500));
    }
}
