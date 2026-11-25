using Jellyfin.Plugin.P2PStream.Catalog;
using Jellyfin.Plugin.P2PStream.Providers;
using Jellyfin.Plugin.P2PStream.Providers.BuiltIn;
using Jellyfin.Plugin.P2PStream.Torrent;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.P2PStream;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StreamUrlBuilder>();
        serviceCollection.AddSingleton<TorrentEngine>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TorrentEngine>());

        // Sources
        serviceCollection.AddSingleton<SearchProvider, ArchiveProvider>();
        serviceCollection.AddSingleton<SearchProvider, YtsProvider>();
        serviceCollection.AddSingleton<SearchProvider, EztvProvider>();
        serviceCollection.AddSingleton<SearchProvider, ApibayProvider>();
        serviceCollection.AddSingleton<SearchProvider, KickassProvider>();
        serviceCollection.AddSingleton<SearchProvider, KnabenProvider>();
        serviceCollection.AddSingleton<SearchProvider, NyaaProvider>();
        serviceCollection.AddSingleton<SourceHealth>();
        serviceCollection.AddSingleton<Catalog.TraktClient>();
        serviceCollection.AddSingleton<TorznabClient>();
        serviceCollection.AddSingleton<SourceHub>();
        serviceCollection.AddSingleton<ResolutionCache>();
        serviceCollection.AddSingleton<StreamOptions>();
        serviceCollection.AddSingleton<StreamProber>();
        serviceCollection.AddSingleton<StreamResolver>();
        serviceCollection.AddSingleton<UserPicks>();
        serviceCollection.AddSingleton<TorrentSubtitles>();
        serviceCollection.AddHostedService<NextEpisodePreloader>();

        // Catalog and search
        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<LibrarySetup>();
        serviceCollection.AddHostedService<Catalog.StrmUrlUpdater>();
        serviceCollection.AddHostedService<Catalog.SearchLibraryHider>();
        serviceCollection.AddSingleton<CatalogBuilder>();
        serviceCollection.AddSingleton<SearchImporter>();
        serviceCollection.Configure<MvcOptions>(o =>
        {
            o.Filters.Add<SearchImportFilter>(-1000); // run before other plugins' search hooks (e.g. Gelato), so matches are added first
            o.Filters.Add<Api.StreamPickerFilter>(); // Version menu on title pages
        });
    }
}
