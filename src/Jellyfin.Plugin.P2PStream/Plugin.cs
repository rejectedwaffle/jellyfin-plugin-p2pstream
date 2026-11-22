using Jellyfin.Plugin.P2PStream.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.P2PStream;

/// <summary>
/// P2P Stream: stream torrents straight into Jellyfin, Elementum-style.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Make sure every install gets its own unguessable stream token.
        if (string.IsNullOrWhiteSpace(Configuration.StreamToken))
        {
            Configuration.StreamToken = Guid.NewGuid().ToString("N");
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "P2P Stream";

    public override Guid Id => Guid.Parse("fb38a8c4-7fa6-48e6-9c52-c093e7263aff");

    public override string Description => "Stream torrents on demand from your Torznab indexers, with a TMDB-powered browsable catalog.";

    public string DefaultDownloadPath => Path.Combine(DataFolderPath, "downloads");

    public string DefaultCatalogPath => Path.Combine(DataFolderPath, "catalog");

    public string DownloadPath => string.IsNullOrWhiteSpace(Configuration.DownloadPath) ? DefaultDownloadPath : Configuration.DownloadPath;

    public string CatalogPath => string.IsNullOrWhiteSpace(Configuration.CatalogPath) ? DefaultCatalogPath : Configuration.CatalogPath;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "P2PStream",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                DisplayName = "P2P Stream",
                MenuIcon = "cloud_download"
            }
        ];
    }
}
