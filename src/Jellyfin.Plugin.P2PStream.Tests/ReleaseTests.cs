using Jellyfin.Plugin.P2PStream.Api;
using Jellyfin.Plugin.P2PStream.Providers;
using Jellyfin.Plugin.P2PStream.Providers.BuiltIn;
using Xunit;
using static Jellyfin.Plugin.P2PStream.Tests.TestSetup;

namespace Jellyfin.Plugin.P2PStream.Tests;

/// <summary>Reading resolutions from release names, including names cut short by a site.</summary>
public class ResolutionTests
{
    [Theory]
    [InlineData("Example Show S02E05 2160p WEB H265-GROUP", 2160)]
    [InlineData("Sample Film 2020 4K HDR BluRay", 2160)]
    [InlineData("Example.Show.S02E05.1080p.WEB.H264-GROUP", 1080)]
    [InlineData("Example Show S02E05 720p WEB x264-GROUP", 720)]
    [InlineData("Example Show S02E05 480p x264-GROUP", 480)]
    public void Stated_resolution_is_read(string name, int expected) => Assert.Equal(expected, ReleaseRanker.ParseResolution(name));

    [Theory]
    [InlineData("Example.Show.S02E05.WEB.x264-GROUP")]
    [InlineData("Example Show S02E05 XviD-GROUP")]
    [InlineData("Example Show S02E05 HDTV x264")]
    public void Source_or_codec_without_a_resolution_counts_as_SD(string name) => Assert.Equal(480, ReleaseRanker.ParseResolution(name));

    [Fact]
    public void A_name_with_nothing_to_go_on_stays_unknown() =>
        Assert.Equal(0, ReleaseRanker.ParseResolution("Example Show S02E05 Episode Title"));

    [Theory]
    [InlineData("Example Show S02E03 A Rather Long Episode Title That Goes On And Cross the River 1", "1080p")]
    [InlineData("Example Show S02E03 A Rather Long Episode Title That Goes On And Cross the River 7", "720p")]
    [InlineData("Example Show S02E03 A Rather Long Episode Title That Goes On And Cross the River 21", "2160p")]
    public void A_name_cut_mid_resolution_is_completed(string cut, string expected)
    {
        var completed = ApibayProvider.CompleteCutName(cut);
        Assert.EndsWith(expected, completed);
        Assert.True(ReleaseRanker.ParseResolution(completed) > 0);
    }

    [Fact]
    public void Short_or_complete_names_are_left_alone()
    {
        Assert.Equal("Example Show S02E05 1", ApibayProvider.CompleteCutName("Example Show S02E05 1"));
        var full = "Example Show S02E03 A Rather Long Episode Title That Goes On And On 1080p WEB H264-GROUP";
        Assert.Equal(full, ApibayProvider.CompleteCutName(full));
    }

    [Fact]
    public void A_name_cut_before_the_resolution_stays_as_it_is()
    {
        var cut = "Example Show S02E06 Bridge Partners Are Hard to Come By These Days And Other Words";
        Assert.Equal(cut, ApibayProvider.CompleteCutName(cut));
    }
}

/// <summary>Release ids are what picks, menus and dead marks are keyed by: they must not change.</summary>
public class ReleaseIdTests
{
    [Fact]
    public void A_listing_known_by_its_page_keeps_its_id_when_the_torrent_id_is_filled_in()
    {
        var r = Release("Example Show S02E05 1080p", 50, page: "https://site.example/torrent/123");
        var before = StreamOptions.IdFor(r);
        r.InfoHash = Hash('a');
        Assert.Equal(before, StreamOptions.IdFor(r));
    }

    [Fact]
    public void A_listing_with_a_torrent_id_is_known_by_it()
    {
        var a = Release("Name one", 5, hash: Hash('b'));
        var b = Release("Another name", 9, hash: Hash('b'));
        Assert.Equal(StreamOptions.IdFor(a), StreamOptions.IdFor(b));
    }

    [Fact]
    public void Different_pages_give_different_ids() =>
        Assert.NotEqual(
            StreamOptions.IdFor(Release("x", 1, page: "https://site.example/torrent/1")),
            StreamOptions.IdFor(Release("x", 1, page: "https://site.example/torrent/2")));
}

/// <summary>Ranking: trusted counts, stale counts, duplicates and the size and 4K rules.</summary>
public class RankingTests
{
    [Fact]
    public void A_stale_count_is_divided_until_checked_live()
    {
        Assert.Equal(18, ReleaseRanker.RankingSeeders(Release("a", 184, stale: true)));
        Assert.Equal(184, ReleaseRanker.RankingSeeders(Release("a", 184)));
        Assert.Equal(3, ReleaseRanker.RankingSeeders(Release("a", 3, stale: true, live: true)));
    }

    [Fact]
    public void A_live_count_beats_a_larger_stale_one()
    {
        Settings();
        var stale = Release("Example Show S02E05 1080p WEB H264-AAA", 120, 600, hash: Hash('1'), stale: true);
        var live = Release("Example Show S02E05 1080p WEB H264-BBB", 15, 600, hash: Hash('2'), live: true);
        var ranked = ReleaseRanker.Rank([stale, live], Episode());
        Assert.Same(live, ranked[0]);
    }

    [Fact]
    public void A_release_found_to_have_too_few_live_seeders_drops_out()
    {
        Settings().MinSeeders = 3;
        var dead = Release("Example Show S02E05 1080p WEB H264-AAA", 1, 600, live: true);
        var fine = Release("Example Show S02E05 1080p WEB H264-BBB", 8, 600, live: true);
        var ranked = ReleaseRanker.Rank([dead, fine], Episode());
        Assert.DoesNotContain(dead, ranked);
        Assert.Contains(fine, ranked);
    }

    [Fact]
    public void Other_episodes_are_left_out_and_season_packs_go_last()
    {
        Settings();
        var right = Release("Example Show S02E05 1080p WEB H264-AAA", 10, 600);
        var wrong = Release("Example Show S02E06 1080p WEB H264-AAA", 99, 600);
        var pack = Release("Example Show S02 1080p WEB H264-AAA", 99, 6000);
        var ranked = ReleaseRanker.Rank([pack, wrong, right], Episode());
        Assert.DoesNotContain(wrong, ranked);
        Assert.Same(right, ranked[0]);
        Assert.True(pack.IsSeasonPack);
    }

    [Fact]
    public void A_huge_or_thinly_seeded_file_is_not_streaming_friendly()
    {
        var episode = Episode();
        Assert.True(ReleaseRanker.StreamingFriendly(Release("Example Show S02E05 1080p", 30, 2400).WithRes(1080), episode));
        Assert.False(ReleaseRanker.StreamingFriendly(Release("Example Show S02E05 1080p", 5, 2400).WithRes(1080), episode));
        Assert.False(ReleaseRanker.StreamingFriendly(Release("Example Show S02E05 1080p", 50, 9000).WithRes(1080), episode));
    }

    [Fact]
    public void A_weak_4K_release_counts_its_stale_figure_as_divided()
    {
        var listed = Release("Sample Film 2020 2160p", 100, stale: true);
        listed.Resolution = 2160;
        Assert.True(ReleaseRanker.Weak4K(listed, 20)); // 100 listed by a stale source = 10
        listed.LiveSeeders = true;
        Assert.False(ReleaseRanker.Weak4K(listed, 20));
    }

    [Fact]
    public void Keeping_one_copy_prefers_the_live_count_and_takes_the_longest_name()
    {
        var site = Release("Example Show S02E05 1080p WEB H264-GROUP[site].mkv", 300, stale: true, hash: Hash('c'));
        var cut = Release("Example Show S02E05 1", 12, live: true, hash: Hash('c'));
        var kept = ReleaseRanker.KeepOne([site, cut]);
        Assert.Same(cut, kept);
        Assert.Equal("Example Show S02E05 1080p WEB H264-GROUP[site].mkv", kept.Title);
    }
}

/// <summary>The Version menu's order and duplicates.</summary>
public class VersionMenuTests
{
    [Fact]
    public void Highest_resolution_first_keeping_the_ranking_within_each_and_unknown_last()
    {
        ReleaseCandidate R(string t, int res) => new() { Title = t, Resolution = res, Seeders = 5 };
        var ranked = new[] { R("a720", 720), R("b1080", 1080), R("c-unknown", 0), R("d1080", 1080), R("e480", 480) };
        var menu = StreamPickerFilter.ByResolution(ranked).Select(r => r.Title).ToArray();
        Assert.Equal(["b1080", "d1080", "a720", "e480", "c-unknown"], menu);
    }

    [Fact]
    public void The_same_torrent_from_two_sites_shows_once_as_the_live_copy()
    {
        var stale = new ReleaseCandidate { Title = "From site A", Resolution = 1080, Seeders = 154, StaleCounts = true, InfoHash = Hash('d') };
        var live = new ReleaseCandidate { Title = "From site B", Resolution = 1080, Seeders = 15, LiveSeeders = true, InfoHash = Hash('D') };
        var other = new ReleaseCandidate { Title = "Something else", Resolution = 1080, Seeders = 9, InfoHash = Hash('e') };
        var menu = StreamPickerFilter.ByResolution([stale, live, other]);
        Assert.Equal(2, menu.Count);
        Assert.Contains(live, menu);
        Assert.DoesNotContain(stale, menu);
    }
}

/// <summary>Small helper so a release can be given a resolution inline.</summary>
internal static class ReleaseExtensions
{
    public static ReleaseCandidate WithRes(this ReleaseCandidate r, int resolution)
    {
        r.Resolution = resolution;
        return r;
    }
}

/// <summary>Whether a download fits on the disk, counting what the titles being watched still need.</summary>
public class DiskSpaceTests
{
    private const long Gb = 1L << 30;

    [Fact]
    public void A_film_fits_when_nothing_else_is_downloading() =>
        Assert.True(Torrent.DiskSpace.Fits(28 * Gb, free: 42 * Gb, promised: 0));

    [Fact]
    public void A_second_big_film_doesnt_fit_while_the_first_still_needs_its_space() =>
        Assert.False(Torrent.DiskSpace.Fits(28 * Gb, free: 42 * Gb, promised: 25 * Gb)); // each alone would fit

    [Fact]
    public void Removing_idle_torrents_can_make_it_fit() =>
        Assert.True(Torrent.DiskSpace.Fits(28 * Gb, free: 42 * Gb, promised: 25 * Gb, reclaimable: 12 * Gb));

    [Fact]
    public void A_smaller_release_still_fits_beside_a_film_being_watched() =>
        Assert.True(Torrent.DiskSpace.Fits(8 * Gb, free: 42 * Gb, promised: 25 * Gb));
}
