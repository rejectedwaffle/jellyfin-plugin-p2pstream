using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// How much disk space Jellyfin's own files take (artwork and info, image cache, transcodes, database, logs), and how
/// much of the artwork and info belongs to the plugin's titles. Measured in the background (folders with many small
/// files take a few seconds) and kept for 30 minutes.
/// </summary>
public sealed class JellyfinSpace
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);

    private readonly IServerApplicationPaths _paths;
    private readonly IConfigurationManager _config;
    private readonly ILibraryManager _library;
    private readonly ILogger<JellyfinSpace> _logger;
    private readonly object _lock = new();
    private IReadOnlyList<Row> _rows = [];
    private DateTime? _at;
    private Task? _measuring;

    public JellyfinSpace(IServerApplicationPaths paths, IConfigurationManager config, ILibraryManager library, ILogger<JellyfinSpace> logger)
    {
        _paths = paths;
        _config = config;
        _library = library;
        _logger = logger;
    }

    public sealed record Row(string Name, long Mb, string Note);

    /// <summary>The last measurement (empty until the first one is done); starts a new one when it's old.</summary>
    public (IReadOnlyList<Row> Rows, DateTime? MeasuredAt, bool Measuring) Get()
    {
        lock (_lock)
        {
            if ((_at is null || DateTime.UtcNow - _at > KeepFor) && _measuring is null)
            {
                _measuring = Task.Run(Measure);
            }

            return (_rows, _at, _measuring is not null);
        }
    }

    private void Measure()
    {
        try
        {
            var metadata = _paths.InternalMetadataPath;
            var transcodes = _config.GetConfiguration<EncodingOptions>("encoding")?.TranscodingTempPath;
            if (string.IsNullOrWhiteSpace(transcodes))
            {
                transcodes = Path.Combine(_paths.CachePath, "transcodes");
            }

            var rows = new List<Row>
            {
                new("Artwork and info", Mb(Api.P2PStreamController.FolderBytes(metadata)), "posters, backdrops and details Jellyfin keeps for every title, actors and genres"),
                new("…for the plugin's titles", Mb(PluginTitlesBytes()), "the part of that for catalog and search titles (removed with them)"),
                new("Image cache", Mb(Api.P2PStreamController.FolderBytes(_paths.ImageCachePath)), "resized pictures for the apps; Jellyfin rebuilds them when needed"),
                new("Transcodes", Mb(Api.P2PStreamController.FolderBytes(transcodes)), "video converted for devices while they play; Jellyfin clears it"),
                new("Database and data", Mb(Api.P2PStreamController.FolderBytes(_paths.DataPath)), "the library database, seek-bar previews and other data"),
                new("Logs", Mb(Api.P2PStreamController.FolderBytes(_paths.LogDirectoryPath)), "Jellyfin's log files; old ones are deleted"),
            };

            lock (_lock)
            {
                _rows = rows;
                _at = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not measure Jellyfin's folders: {Message}", ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _measuring = null;
            }
        }
    }

    /// <summary>The artwork and info folders of the titles in the plugin's catalog folder (movies, shows, seasons, episodes).</summary>
    private long PluginTitlesBytes()
    {
        var root = Plugin.Instance?.CatalogPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return 0;
        }

        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            AddMetadataFolder(dir, isFolder: true, folders);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories))
        {
            AddMetadataFolder(file, isFolder: false, folders);
        }

        long total = 0;
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                total += new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception)
            {
                // a folder removed while counting
            }
        }

        return total;
    }

    private void AddMetadataFolder(string path, bool isFolder, HashSet<string> folders)
    {
        try
        {
            if (_library.FindByPath(path, isFolder) is { } item && item.GetInternalMetadataPath() is { Length: > 0 } meta)
            {
                folders.Add(meta);
            }
        }
        catch (Exception)
        {
            // not in a library
        }
    }

    private static long Mb(long bytes) => bytes >> 20;
}
