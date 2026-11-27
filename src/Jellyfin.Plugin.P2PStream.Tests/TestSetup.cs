using Jellyfin.Plugin.P2PStream.Configuration;
using Jellyfin.Plugin.P2PStream.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;

// The plugin's settings live in one static instance (as in Jellyfin), so tests run one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.P2PStream.Tests;

/// <summary>Creates the plugin with default settings (Jellyfin's paths and settings file faked).</summary>
internal static class TestSetup
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "p2pstream-tests");

    /// <summary>Fresh default settings for a test.</summary>
    public static PluginConfiguration Settings()
    {
        if (Plugin.Instance is null)
        {
            Directory.CreateDirectory(Folder);
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(p => p.PluginsPath).Returns(Folder);
            paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Folder);
            paths.SetupGet(p => p.DataPath).Returns(Folder);
            var xml = new Mock<IXmlSerializer>();
            xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(() => new PluginConfiguration());
            _ = new Plugin(paths.Object, xml.Object);
        }

        var fresh = new PluginConfiguration();
        Plugin.Instance!.UpdateConfiguration(fresh);
        return Plugin.Instance.Configuration;
    }

    public static MediaRequest Episode(int season = 2, int episode = 5) =>
        new() { Kind = MediaKind.Episode, TmdbId = 4242, Title = "Example Show", Season = season, Episode = episode };

    public static MediaRequest Movie() =>
        new() { Kind = MediaKind.Movie, TmdbId = 4343, Title = "Sample Film", Year = 2020 };

    public static ReleaseCandidate Release(string title, int seeders, long sizeMb = 1500, string origin = "Test", string? hash = null, bool live = false, bool stale = false, string? page = null) =>
        new()
        {
            Title = title,
            Origin = origin,
            Seeders = seeders,
            Size = sizeMb << 20,
            InfoHash = hash,
            LiveSeeders = live,
            StaleCounts = stale,
            MagnetPageUrl = page,
        };

    public static string Hash(char c) => new(c, 40);
}
