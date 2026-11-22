using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using MonoTorrent.Client;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// Writes .strm files for catalogs. Each catalog writes into a folder that you add as a library (Gelato-style):
/// <c>&lt;catalog&gt;/&lt;Library&gt;/Title (Year) [tmdbid-N]/Title (Year).strm</c> for movies and
/// <c>&lt;catalog&gt;/&lt;Library&gt;/Name (Year) [tmdbid-N]/Season 01/Name S01E01.strm</c> for shows.
/// </summary>
public sealed partial class CatalogBuilder
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").Distinct().ToArray();

    private readonly TmdbClient _tmdb;
    private readonly StreamUrlBuilder _urls;
    private readonly LibrarySetup _libraries;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collections;
    private readonly ILogger<CatalogBuilder> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _importsLock = new();
    private ConcurrentDictionary<string, DateTime>? _searchImports;

    public CatalogBuilder(
        TmdbClient tmdb,
        StreamUrlBuilder urls,
        LibrarySetup libraries,
        ILibraryManager libraryManager,
        ICollectionManager collections,
        ILogger<CatalogBuilder> logger)
    {
        _tmdb = tmdb;
        _urls = urls;
        _libraries = libraries;
        _libraryManager = libraryManager;
        _collections = collections;
        _logger = logger;
    }

    public static string AddedPath => Path.Combine(Plugin.Instance!.CatalogPath, "Added");

    /// <summary>Gets a value indicating whether a sync is running (shown on the Catalogs tab).</summary>
    public bool IsSyncing => _running.CurrentCount == 0;

    private static string SearchImportsFile => Path.Combine(Plugin.Instance!.DataFolderPath, "search-imports.json");

    private ConcurrentDictionary<string, DateTime> SearchImports => _searchImports ??= LoadSearchImports();

    /// <summary>The folder that backs a library.</summary>
    public static string FolderFor(string library) => Path.Combine(Plugin.Instance!.CatalogPath, Sanitize(library));

    /// <summary>Finds a title's folder inside one library folder.</summary>
    public static string? FindExisting(string root, int tmdbId) =>
        Directory.Exists(root) ? Directory.GetDirectories(root, $"*[tmdbid-{tmdbId}]").FirstOrDefault() : null;

    /// <summary>Finds a title in any of the plugin's library folders (movie and show ids are separate on TMDB, so the type is checked).</summary>
    public static string? FindExistingAnywhere(bool isMovie, int tmdbId)
    {
        var root = Plugin.Instance!.CatalogPath;
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var library in Directory.GetDirectories(root))
        {
            foreach (var dir in Directory.GetDirectories(library, $"*[tmdbid-{tmdbId}]"))
            {
                var hasStrm = Directory.EnumerateFiles(dir, "*.strm").Any(); // movie folders hold the .strm directly
                if (hasStrm == isMovie)
                {
                    return dir;
                }
            }
        }

        return null;
    }

    public (string Dir, bool Changed) WriteMovie(TmdbItem m, string root)
    {
        var name = DisplayName(m.Title, m.Year);
        var dir = FindExisting(root, m.Id) ?? Path.Combine(root, $"{name} [tmdbid-{m.Id}]");
        var changed = WriteStrm(Path.Combine(dir, name + ".strm"), _urls.Movie(m.Id));
        return (dir, changed);
    }

    /// <summary>Writes a .strm for every aired episode of a show.</summary>
    public async Task<(string Dir, int Changed)> WriteShowAsync(TmdbItem s, string root, CancellationToken ct)
    {
        var dir = FindExisting(root, s.Id) ?? Path.Combine(root, $"{DisplayName(s.Title, s.Year)} [tmdbid-{s.Id}]");
        var info = await _tmdb.GetShowAsync(s.Id, TmdbClient.CatalogLanguage, ct).ConfigureAwait(false);
        var fileBase = Sanitize(s.Title);
        var today = DateTime.UtcNow.Date;
        var changed = 0;
        foreach (var season in info.Seasons)
        {
            var episodes = await _tmdb.GetSeasonAsync(s.Id, season, TmdbClient.CatalogLanguage, ct).ConfigureAwait(false);
            foreach (var ep in episodes.Where(e => e.AirDate is not null && e.AirDate.Value.Date <= today))
            {
                var file = Path.Combine(dir, $"Season {season:00}", $"{fileBase} S{season:00}E{ep.Number:00}.strm");
                changed += WriteStrm(file, _urls.Episode(s.Id, season, ep.Number)) ? 1 : 0;
            }
        }

        return (dir, changed);
    }

    /// <summary>Records that a folder was added by a search, so pruning leaves it alone for SearchImportDays.</summary>
    public void MarkSearchImport(string dir)
    {
        SearchImports[dir] = DateTime.UtcNow;
        SaveSearchImports();
    }

    /// <summary>
    /// Syncs every enabled catalog, or only <paramref name="onlyId"/>: fetch from TMDB, make sure the libraries exist,
    /// write .strm files, prune, scan, then update collections.
    /// </summary>
    public async Task SyncAsync(string? onlyId, IProgress<double> progress, CancellationToken ct)
    {
        if (!_tmdb.IsConfigured)
        {
            _logger.LogWarning("Catalog sync skipped: no TMDB API key is set");
            return;
        }

        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Catalog sync already running");
            return;
        }

        try
        {
            _libraries.RunLegacyCleanupOnce();
            var defs = await CatalogDefinitions.GetAllAsync(_tmdb, ct).ConfigureAwait(false);
            var jobs = defs.Select(d => (Def: d, Entry: CatalogDefinitions.Effective(d)))
                .Where(j => onlyId is null ? j.Entry.Enabled : string.Equals(j.Def.Id, onlyId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (jobs.Count == 0)
            {
                _logger.LogInformation("Catalog sync: no catalogs are turned on");
                return;
            }

            if (string.IsNullOrWhiteSpace(Plugin.Instance!.Configuration.PublicBaseUrl))
            {
                _logger.LogWarning("PublicBaseUrl is not set; .strm files will use {Url}, which other devices may not reach", _urls.BaseUrl());
            }

            // 1. Fetch the lists.
            var titles = new Dictionary<string, List<TmdbItem>>();
            foreach (var (def, entry) in jobs)
            {
                try
                {
                    titles[def.Id] = await FetchAsync(def, entry.MaxItems, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Catalog {Name} ({Id}) failed: {Message}", def.Name, def.Id, ex.Message);
                }
            }

            progress.Report(10);

            // 2. Write, one folder at a time. Several catalogs may share a folder.
            var scans = new List<(string Root, List<string> Changed, int Removed)>();
            var titleDirs = new Dictionary<string, List<string>>();
            var total = Math.Max(1, titles.Values.Sum(t => t.Count));
            var done = 0;
            var groups = jobs.Where(j => titles.ContainsKey(j.Def.Id))
                .GroupBy(j => (Library: j.Entry.Library.ToLowerInvariant(), j.Def.IsMovie));

            foreach (var group in groups)
            {
                var library = group.First().Entry.Library;
                var isMovie = group.Key.IsMovie;
                var root = FolderFor(library);
                Directory.CreateDirectory(root);

                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var changed = new List<string>();
                foreach (var (def, _) in group)
                {
                    var dirs = new List<string>();
                    foreach (var item in titles[def.Id])
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (isMovie)
                            {
                                var (dir, written) = WriteMovie(item, root);
                                dirs.Add(dir);
                                if (written)
                                {
                                    changed.Add(dir);
                                }
                            }
                            else
                            {
                                var (dir, written) = await WriteShowAsync(item, root, ct).ConfigureAwait(false);
                                dirs.Add(dir);
                                if (written > 0)
                                {
                                    changed.Add(dir);
                                }
                            }
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            // Keep an existing folder on a passing error rather than pruning it.
                            if (FindExisting(root, item.Id) is string existing)
                            {
                                dirs.Add(existing);
                            }

                            _logger.LogWarning("{Title} failed: {Message}", item.Title, ex.Message);
                        }

                        progress.Report(10 + (70.0 * ++done / total));
                    }

                    keep.UnionWith(dirs);
                    titleDirs[def.Id] = dirs;
                }

                var removed = 0;
                if (Plugin.Instance.Configuration.PruneCatalog && SyncedEveryFeeder(defs, group.Select(j => j.Def.Id).ToHashSet(), library, isMovie))
                {
                    removed = Prune(root, keep);
                }

                scans.Add((root, changed, removed));
                _logger.LogInformation("Catalog folder {Folder}: {Count} titles, {Changed} changed, {Removed} removed", library, keep.Count, changed.Count, removed);
            }

            // 3. Get the changes into Jellyfin (only folders that are in a library).
            progress.Report(80);
            foreach (var (root, changed, removed) in scans)
            {
                await _libraries.ScanIfInLibraryAsync(root, changed, removed, ct).ConfigureAwait(false);
            }

            // 4. Collections.
            progress.Report(95);
            foreach (var (def, entry) in jobs.Where(j => j.Entry.Collection && titleDirs.ContainsKey(j.Def.Id)))
            {
                try
                {
                    await UpdateCollectionAsync(def.DefaultFolder, titleDirs[def.Id], def.IsMovie).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Collection {Name} failed: {Message}", def.DefaultFolder, ex.Message);
                }
            }

            progress.Report(100);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Writes a .strm for a manually added torrent file into the Added folder.
    /// </summary>
    public string WriteManualStrm(TorrentManager manager, string infoHash, int fileIndex)
    {
        var file = manager.Files[fileIndex];
        var folder = Sanitize(manager.Torrent?.Name ?? infoHash);
        var path = Path.Combine(AddedPath, folder, Sanitize(Path.GetFileNameWithoutExtension(file.Path)) + ".strm");
        WriteStrm(path, _urls.File(infoHash, fileIndex));
        return path;
    }

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(InvalidChars.Contains(ch) ? ' ' : ch);
        }

        var s = MultiSpace().Replace(sb.ToString(), " ").Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }

    private async Task<List<TmdbItem>> FetchAsync(CatalogDef def, int max, CancellationToken ct)
    {
        var items = new Dictionary<int, TmdbItem>();
        if (def.TmdbList is not null)
        {
            var pages = Math.Clamp((max + 19) / 20, 1, 25);
            for (var page = 1; page <= pages && items.Count < max; page++)
            {
                var batch = await _tmdb.GetListPageAsync(def.IsMovie, def.TmdbList, page, ct).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var i in batch)
                {
                    items.TryAdd(i.Id, i);
                }
            }
        }
        else if (def.CustomListId is not null)
        {
            foreach (var i in (await _tmdb.GetCustomListAsync(def.CustomListId, ct).ConfigureAwait(false)).Where(i => i.IsMovie == def.IsMovie))
            {
                items.TryAdd(i.Id, i);
            }
        }

        return items.Values.Take(max).ToList();
    }

    /// <summary>Pruning is only safe when every enabled catalog that feeds this library was just synced. Otherwise it would delete the others' titles.</summary>
    private static bool SyncedEveryFeeder(List<CatalogDef> defs, HashSet<string> synced, string library, bool isMovie) =>
        defs.Where(d => d.IsMovie == isMovie)
            .Select(d => (d.Id, Entry: CatalogDefinitions.Effective(d)))
            .Where(x => x.Entry.Enabled && string.Equals(x.Entry.Library, library, StringComparison.OrdinalIgnoreCase))
            .All(x => synced.Contains(x.Id));

    /// <summary>Keeps a Jellyfin collection (box set) in step with a catalog's titles.</summary>
    private async Task UpdateCollectionAsync(string name, List<string> dirs, bool isMovie)
    {
        var ids = new List<Guid>();
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            var item = isMovie
                ? Directory.EnumerateFiles(dir, "*.strm").FirstOrDefault() is string file ? _libraryManager.FindByPath(file, false) : null
                : _libraryManager.FindByPath(dir, true);
            if (item is not null)
            {
                ids.Add(item.Id);
            }
        }

        if (ids.Count == 0)
        {
            _logger.LogInformation("Collection {Name}: titles aren't in the library yet; it will be filled on the next sync", name);
            return;
        }

        var box = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Name = name, Recursive = true })
            .OfType<BoxSet>()
            .FirstOrDefault();
        if (box is null)
        {
            await _collections.CreateCollectionAsync(new CollectionCreationOptions { Name = name, ItemIdList = ids.Select(i => i.ToString("N")).ToList() }).ConfigureAwait(false);
            _logger.LogInformation("Created collection {Name} with {Count} titles", name, ids.Count);
            return;
        }

        var current = box.GetLinkedChildren().Select(i => i.Id).ToHashSet();
        var add = ids.Where(i => !current.Contains(i)).ToList();
        var remove = current.Where(i => !ids.Contains(i)).ToList();
        if (add.Count > 0)
        {
            await _collections.AddToCollectionAsync(box.Id, add).ConfigureAwait(false);
        }

        if (remove.Count > 0)
        {
            await _collections.RemoveFromCollectionAsync(box.Id, remove).ConfigureAwait(false);
        }
    }

    private static bool WriteStrm(string path, string url)
    {
        if (File.Exists(path) && File.ReadAllText(path).Trim() == url)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, url, new UTF8Encoding(false));
        return true;
    }

    private int Prune(string root, HashSet<string> keep)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var days = Plugin.Instance!.Configuration.SearchImportDays;
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(root).Where(d => TmdbFolder().IsMatch(Path.GetFileName(d)) && !keep.Contains(d)))
        {
            if (SearchImports.TryGetValue(dir, out var added) && (days <= 0 || DateTime.UtcNow - added < TimeSpan.FromDays(days)))
            {
                continue; // added by a search recently, so keep it
            }

            try
            {
                Directory.Delete(dir, true);
                SearchImports.TryRemove(dir, out _);
                removed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not prune {Dir}: {Message}", dir, ex.Message);
            }
        }

        SaveSearchImports();
        return removed;
    }

    private ConcurrentDictionary<string, DateTime> LoadSearchImports()
    {
        try
        {
            if (File.Exists(SearchImportsFile))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(SearchImportsFile));
                if (data is not null)
                {
                    return new ConcurrentDictionary<string, DateTime>(data, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read search imports");
        }

        return new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveSearchImports()
    {
        lock (_importsLock)
        {
            try
            {
                File.WriteAllText(SearchImportsFile, JsonSerializer.Serialize(SearchImports));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save search imports");
            }
        }
    }

    private static string DisplayName(string title, int? year) => year is int y ? $"{Sanitize(title)} ({y})" : Sanitize(title);

    [GeneratedRegex(@"\[tmdbid-\d+\]$")]
    private static partial Regex TmdbFolder();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
