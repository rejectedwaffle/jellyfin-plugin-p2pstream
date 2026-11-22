using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// Scheduled task (Dashboard, then Scheduled Tasks) that refreshes the TMDB catalog.
/// </summary>
public sealed class CatalogSyncTask : IScheduledTask
{
    private readonly CatalogBuilder _builder;

    public CatalogSyncTask(CatalogBuilder builder)
    {
        _builder = builder;
    }

    public string Name => "Sync P2P Stream catalog";

    public string Key => "P2PStreamCatalogSync";

    public string Description => "Imports the catalogs you turned on into their libraries.";

    public string Category => "P2P Stream";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _builder.SyncAsync(null, progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Math.Max(1, Plugin.Instance?.Configuration.CatalogSyncHours ?? 24);
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(hours).Ticks }
        ];
    }
}
