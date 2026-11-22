using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>A catalog folder and the Jellyfin library that uses it, if any.</summary>
public sealed record CatalogFolder(string Name, string Path, string? Library, bool? IsMovie);

/// <summary>
/// Gelato-style folder handling: the plugin only creates folders under its catalog path. You add libraries yourself,
/// and the plugin reports which library (if any) each folder belongs to. It also scans just the folders it changed.
/// </summary>
public sealed class LibrarySetup
{
    private static readonly string[] NotCatalogFolders = ["Added"];

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LibrarySetup> _logger;
    private readonly object _cleanupLock = new();

    public LibrarySetup(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<LibrarySetup> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    private static string Root => Plugin.Instance!.CatalogPath;

    /// <summary>Every folder under the catalog path, with the library using it.</summary>
    public List<CatalogFolder> ListFolders()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return Directory.GetDirectories(Root)
            .Where(d => !NotCatalogFolders.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .Select(Describe)
            .OrderBy(f => f.Library ?? f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Describes one folder (it doesn't have to exist yet).</summary>
    public CatalogFolder Describe(string path)
    {
        var library = _libraryManager.GetVirtualFolders().FirstOrDefault(v => v.Locations.Any(l => SamePath(l, path)));
        bool? isMovie = library?.CollectionType switch
        {
            CollectionTypeOptions.movies => true,
            CollectionTypeOptions.tvshows => false,
            _ => null
        };
        return new CatalogFolder(System.IO.Path.GetFileName(path), path, library?.Name, isMovie);
    }

    /// <summary>Creates a folder for a new library (the "New library..." dialog).</summary>
    public CatalogFolder CreateFolder(string name)
    {
        var path = CatalogBuilder.FolderFor(name);
        Directory.CreateDirectory(path);
        _logger.LogInformation("Created catalog folder {Path}", path);
        return Describe(path);
    }

    /// <summary>
    /// One-time cleanup of what 0.3.0 did before switching to Gelato-style folders: it added its folders to existing
    /// libraries (Gelato's, for example) and wrote search results into "Movies"/"Shows". Only touches the plugin's own folders.
    /// </summary>
    public void RunLegacyCleanupOnce()
    {
        var config = Plugin.Instance!.Configuration;
        if (config.LegacyLibraryCleanupDone)
        {
            return;
        }

        lock (_cleanupLock)
        {
            if (config.LegacyLibraryCleanupDone)
            {
                return;
            }

            try
            {
                foreach (var library in _libraryManager.GetVirtualFolders())
                {
                    var ours = library.Locations.Where(IsInsideCatalog).ToList();
                    if (ours.Count == 0 || ours.Count == library.Locations.Length)
                    {
                        continue; // not ours at all, or entirely ours (fine to keep)
                    }

                    foreach (var location in ours)
                    {
                        _libraryManager.RemoveMediaPath(library.Name, location);
                        _logger.LogInformation("Removed plugin folder {Folder} from library {Library} (the plugin doesn't merge into other libraries)", location, library.Name);
                    }
                }

                MoveIfUnused("Movies", SearchImporter.MovieLibrary);
                MoveIfUnused("Shows", SearchImporter.ShowLibrary);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy library cleanup failed");
            }

            config.LegacyLibraryCleanupDone = true;
            Plugin.Instance.SaveConfiguration();
        }
    }

    /// <summary>Full library scan ("Scan All Libraries").</summary>
    public Task FullScanAsync(CancellationToken ct) => _libraryManager.ValidateMediaLibrary(new Progress<double>(), ct);

    /// <summary>
    /// Scans only the changed title folders inside a library folder. Returns false if Jellyfin doesn't know the folder yet
    /// (the caller should then do a full scan).
    /// </summary>
    public async Task<bool> ScanAsync(string root, IReadOnlyCollection<string> changedDirs, CancellationToken ct)
    {
        if (_libraryManager.FindByPath(root, true) is not Folder libraryFolder)
        {
            return false;
        }

        var refreshPaths = changedDirs
            .Concat(changedDirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories)))
            .ToArray();
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            RefreshPaths = refreshPaths
        };

        // Non-recursive on the library folder: adds new titles and removes deleted ones without touching the rest.
        await libraryFolder.ValidateChildren(new Progress<double>(), options, recursive: false, allowRemoveRoot: false, cancellationToken: ct).ConfigureAwait(false);
        foreach (var dir in changedDirs)
        {
            // Shows are folders (seasons, episodes); movies are single files and need nothing more.
            if (_libraryManager.FindByPath(dir, true) is Folder folder)
            {
                await folder.ValidateChildren(new Progress<double>(), options, recursive: true, allowRemoveRoot: false, cancellationToken: ct).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>
    /// Gets a changed folder's titles into Jellyfin: a targeted scan if the folder is in a library, otherwise nothing
    /// (it's logged, and the titles appear once you add the library).
    /// </summary>
    public async Task ScanIfInLibraryAsync(string root, IReadOnlyCollection<string> changedDirs, int removed, CancellationToken ct)
    {
        if (changedDirs.Count == 0 && removed == 0)
        {
            return;
        }

        var info = Describe(root);
        if (info.Library is null)
        {
            _logger.LogWarning("Folder {Folder} isn't in a library yet. Add a library with it under Dashboard > Libraries to see these titles", root);
            return;
        }

        try
        {
            if (!await ScanAsync(root, changedDirs, ct).ConfigureAwait(false))
            {
                await FullScanAsync(ct).ConfigureAwait(false); // library added but never scanned
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Targeted scan of {Folder} failed ({Message}), queueing a full library scan", root, ex.Message);
            _libraryManager.QueueLibraryScan();
        }
    }

    private static bool IsInsideCatalog(string path)
    {
        var root = Path.GetFullPath(Root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.Ordinal);

    private void MoveIfUnused(string oldName, string newName)
    {
        var from = Path.Combine(Root, oldName);
        var to = CatalogBuilder.FolderFor(newName);
        if (Directory.Exists(from) && !Directory.Exists(to) && Describe(from).Library is null)
        {
            Directory.Move(from, to);
            _logger.LogInformation("Moved old search folder {From} to {To}", from, to);
        }
    }
}
