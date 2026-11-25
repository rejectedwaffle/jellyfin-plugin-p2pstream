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
    private readonly TraktClient _trakt;
    private readonly StreamUrlBuilder _urls;
    private readonly LibrarySetup _libraries;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collections;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILogger<CatalogBuilder> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _importsLock = new();
    private ConcurrentDictionary<string, DateTime>? _searchImports;

    public CatalogBuilder(
        TmdbClient tmdb,
        TraktClient trakt,
        StreamUrlBuilder urls,
        LibrarySetup libraries,
        ILibraryManager libraryManager,
        ICollectionManager collections,
        IUserManager users,
        IUserDataManager userData,
        ILogger<CatalogBuilder> logger)
    {
        _users = users;
        _userData = userData;
        _tmdb = tmdb;
        _trakt = trakt;
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

    /// <summary>
    /// Finds a title's folder inside one library folder. Movie and show ids are separate on TMDB, and a folder can hold
    /// both, so the type is checked: movie folders hold the .strm directly, show folders hold season folders.
    /// </summary>
    public static string? FindExisting(string root, int tmdbId, bool isMovie) =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root, $"*[tmdbid-{tmdbId}]").FirstOrDefault(d => Directory.EnumerateFiles(d, "*.strm").Any() == isMovie)
            : null;

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
        var dir = FindExisting(root, m.Id, true) ?? Path.Combine(root, $"{name} [tmdbid-{m.Id}]");
        var changed = WriteStrm(Path.Combine(dir, name + ".strm"), _urls.Movie(m.Id));
        return (dir, changed);
    }

    /// <summary>Writes a .strm for every aired episode of a show.</summary>
    public async Task<(string Dir, int Changed)> WriteShowAsync(TmdbItem s, string root, CancellationToken ct)
    {
        var dir = FindExisting(root, s.Id, false) ?? Path.Combine(root, $"{DisplayName(s.Title, s.Year)} [tmdbid-{s.Id}]");
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

    /// <summary>
    /// Writes only a show's first episode, so it can appear in search within seconds. The rest follow via WriteShowAsync.
    /// </summary>
    public string WriteFirstEpisode(TmdbItem s, string root, int season)
    {
        var dir = FindExisting(root, s.Id, false) ?? Path.Combine(root, $"{DisplayName(s.Title, s.Year)} [tmdbid-{s.Id}]");
        var file = Path.Combine(dir, $"Season {season:00}", $"{Sanitize(s.Title)} S{season:00}E01.strm");
        WriteStrm(file, _urls.Episode(s.Id, season, 1));
        return dir;
    }

    /// <summary>
    /// Records that a folder was added (or found again) by a search: it's kept for SearchImportDays from now. A title
    /// added by hand stays kept.
    /// </summary>
    public void MarkSearchImport(string dir)
    {
        SearchImports.AddOrUpdate(dir, DateTime.UtcNow, (_, old) => old == DateTime.MaxValue ? old : DateTime.UtcNow);
        SaveSearchImports();
    }

    /// <summary>A title someone added by hand ("Add any movie or show"): never removed by the search clean-up.</summary>
    public void MarkKept(string dir)
    {
        SearchImports[dir] = DateTime.MaxValue;
        SaveSearchImports();
    }

    /// <summary>
    /// Removes titles from the search folders that a search added more than SearchImportDays ago and that nobody has
    /// played, started or favourited (a show counts as touched if any episode was). Titles added by hand, folders that a
    /// catalog also writes into (the catalog sync prunes those), and folders with real video files are left alone.
    /// </summary>
    /// <param name="dryRun">Only list what would be removed (the settings page's "Clean up now" asks first).</param>
    /// <returns>The titles removed (or that would be), as "Title (Year)".</returns>
    private async Task<List<string>> PruneSearchFoldersAsync(ISet<string> catalogFolders, CancellationToken ct, bool dryRun = false)
    {
        var result = new List<string>();
        var days = Plugin.Instance!.Configuration.SearchImportDays;
        if (days <= 0)
        {
            return result;
        }

        var users = _users.GetUsers().ToList();
        foreach (var (root, isMovie) in new[] { (FolderFor(SearchImporter.MovieLibrary), true), (FolderFor(SearchImporter.ShowLibrary), false) }
            .DistinctBy(f => f.Item1, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root) || catalogFolders.Contains(CanonicalFolder(root)))
            {
                continue;
            }

            var removed = 0;
            foreach (var dir in Directory.GetDirectories(root).Where(d => TmdbFolder().IsMatch(Path.GetFileName(d))))
            {
                if (!SearchImports.TryGetValue(dir, out var added))
                {
                    if (!dryRun)
                    {
                        SearchImports[dir] = DateTime.UtcNow; // from before this was tracked: its time starts now
                    }

                    continue;
                }

                if (added == DateTime.MaxValue || DateTime.UtcNow - added < TimeSpan.FromDays(days) || HoldsRealMedia(dir) || WasTouched(dir, users))
                {
                    continue;
                }

                var title = TmdbFolder().Replace(Path.GetFileName(dir), string.Empty).Trim();
                if (dryRun)
                {
                    result.Add(title);
                    continue;
                }

                try
                {
                    Directory.Delete(dir, true);
                    SearchImports.TryRemove(dir, out _);
                    removed++;
                    result.Add(title);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not remove {Dir}: {Message}", dir, ex.Message);
                }
            }

            if (dryRun)
            {
                continue;
            }

            SaveSearchImports();
            if (removed > 0)
            {
                _logger.LogInformation("Search folder {Folder}: removed {Count} titles nobody played in {Days} days", Path.GetFileName(root), removed, days);
                try
                {
                    await _libraries.ScanIfInLibraryAsync(root, [], removed, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // The next library scan picks the removals up; the other search folder is still cleaned.
                    _logger.LogWarning("Could not rescan {Folder} after the clean-up: {Message}", Path.GetFileName(root), ex.Message);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The settings page's "Clean up now": the same rules as the clean-up at each catalog sync. With
    /// <paramref name="dryRun"/> it only lists what would go.
    /// </summary>
    public async Task<List<string>> CleanUpSearchFoldersAsync(bool dryRun, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A catalog sync is running; try again in a minute.");
        }

        try
        {
            var defs = await CatalogDefinitions.GetAllAsync(_tmdb, ct, _trakt).ConfigureAwait(false);
            var catalogFolders = defs.Select(CatalogDefinitions.Effective).Where(e => e.Enabled)
                .Select(e => CanonicalFolder(e.Library)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return await PruneSearchFoldersAsync(catalogFolders, ct, dryRun).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>True if anyone has played, started or favourited the title (for a show: the show or any of its episodes).</summary>
    private bool WasTouched(string dir, List<Jellyfin.Database.Implementations.Entities.User> users)
    {
        var strm = Directory.EnumerateFiles(dir, "*.strm", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var item = strm is not null ? _libraryManager.FindByPath(strm, false) : _libraryManager.FindByPath(dir, true);
        if (item is null)
        {
            return false; // not in the library, so nobody can have watched it
        }

        var items = new List<BaseItem> { item };
        if (item is MediaBrowser.Controller.Entities.TV.Series)
        {
            items.AddRange(_libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = [item.Id], IncludeItemTypes = [BaseItemKind.Episode], Recursive = true }));
        }

        return users.Any(u => items.Any(i => _userData.GetUserData(u, i) is { } d && (d.Played || d.PlaybackPositionTicks > 0 || d.IsFavorite)));
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
            var defs = await CatalogDefinitions.GetAllAsync(_tmdb, ct, _trakt).ConfigureAwait(false);
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
            // Grouped by the folder they really write into: names that differ only in case or in characters that
            // aren't allowed in folder names end up in the same folder, and must be pruned together.
            var groups = jobs.Where(j => titles.ContainsKey(j.Def.Id))
                .GroupBy(j => CanonicalFolder(j.Entry.Library), StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var library = group.First().Entry.Library;
                var root = FolderFor(library);
                Directory.CreateDirectory(root);

                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var changed = new List<string>();
                foreach (var (def, _) in group)
                {
                    var isMovie = def.IsMovie;
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
                            if (FindExisting(root, item.Id, isMovie) is string existing)
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
                if (Plugin.Instance.Configuration.PruneCatalog && SyncedEveryFeeder(defs, group.Select(j => j.Def.Id).ToHashSet(), root))
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

            // 5. On a full sync: titles that searches added long ago and nobody watched.
            if (onlyId is null)
            {
                try
                {
                    var catalogFolders = jobs.Where(j => j.Entry.Enabled).Select(j => CanonicalFolder(j.Entry.Library)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    await PruneSearchFoldersAsync(catalogFolders, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Search folder clean-up failed: {Message}", ex.Message);
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
        else if (def.TraktPath is not null)
        {
            foreach (var i in await _trakt.GetItemsAsync(def.TraktPath, def.IsMovie, max, ct).ConfigureAwait(false))
            {
                items.TryAdd(i.Id, i);
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
    /// <summary>
    /// True if every enabled catalog (movies and shows) that writes into this folder was fetched and written in this
    /// sync. Only then is the folder pruned: a catalog that failed or wasn't part of this sync would lose its titles.
    /// </summary>
    private static bool SyncedEveryFeeder(List<CatalogDef> defs, HashSet<string> synced, string root) =>
        defs.Select(d => (d.Id, Entry: CatalogDefinitions.Effective(d)))
            .Where(x => x.Entry.Enabled && string.Equals(CanonicalFolder(x.Entry.Library), CanonicalFolder(root), StringComparison.OrdinalIgnoreCase))
            .All(x => synced.Contains(x.Id));

    /// <summary>The full path a library name (or a folder path) resolves to, for comparing destinations.</summary>
    private static string CanonicalFolder(string libraryOrPath)
    {
        var path = Path.IsPathRooted(libraryOrPath) ? libraryOrPath : FolderFor(libraryOrPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

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

        // Only collections the plugin made (their ids are recorded) are ever changed. A collection with this name that
        // the plugin didn't make is adopted only if every title in it is one of the plugin's catalog titles (collections
        // from versions that didn't record ids); a collection of your own is left alone.
        var owned = LoadOwnedCollections();
        BoxSet? box = owned.TryGetValue(name, out var ownedId) ? _libraryManager.GetItemById(ownedId) as BoxSet : null;
        if (box is null)
        {
            var sameName = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Name = name, Recursive = true })
                .OfType<BoxSet>()
                .FirstOrDefault();
            if (sameName is not null)
            {
                var catalogRoot = Path.GetFullPath(Plugin.Instance!.CatalogPath);
                var existingMembers = sameName.GetLinkedChildren();
                if (existingMembers.Count > 0 && existingMembers.All(m => !string.IsNullOrEmpty(m.Path) && Path.GetFullPath(m.Path).StartsWith(catalogRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    box = sameName;
                    owned[name] = box.Id;
                    SaveOwnedCollections(owned);
                }
                else
                {
                    _logger.LogInformation("Collection {Name}: a collection with this name already exists and wasn't made by P2P Stream, so it's left alone", name);
                    return;
                }
            }
        }

        if (box is null)
        {
            var created = await _collections.CreateCollectionAsync(new CollectionCreationOptions { Name = name, ItemIdList = ids.Select(i => i.ToString("N")).ToList() }).ConfigureAwait(false);
            owned[name] = created.Id;
            SaveOwnedCollections(owned);
            _logger.LogInformation("Created collection {Name} with {Count} titles", name, ids.Count);
            return;
        }

        var members = box.GetLinkedChildren();
        var current = members.Select(i => i.Id).ToHashSet();
        var add = ids.Where(i => !current.Contains(i)).ToList();

        // Removed only if its folder is no longer one of the catalog's titles: a title that's still listed but that
        // the library scan hasn't caught up with yet stays in.
        var listed = dirs.Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)) + Path.DirectorySeparatorChar).ToList();
        var remove = members
            .Where(m => !ids.Contains(m.Id) && !(m.Path is { Length: > 0 } path && listed.Any(d => Path.GetFullPath(path).StartsWith(d, StringComparison.OrdinalIgnoreCase))))
            .Select(m => m.Id)
            .ToList();
        if (add.Count > 0)
        {
            await _collections.AddToCollectionAsync(box.Id, add).ConfigureAwait(false);
        }

        if (remove.Count > 0)
        {
            await _collections.RemoveFromCollectionAsync(box.Id, remove).ConfigureAwait(false);
        }
    }

    private static string OwnedCollectionsPath => Path.Combine(Plugin.Instance!.DataFolderPath, "collections.json");

    /// <summary>Collections the plugin made or adopted: name to collection id.</summary>
    private static Dictionary<string, Guid> LoadOwnedCollections()
    {
        try
        {
            return File.Exists(OwnedCollectionsPath)
                ? new Dictionary<string, Guid>(JsonSerializer.Deserialize<Dictionary<string, Guid>>(File.ReadAllText(OwnedCollectionsPath)) ?? [], StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveOwnedCollections(Dictionary<string, Guid> owned) =>
        File.WriteAllText(OwnedCollectionsPath, JsonSerializer.Serialize(owned));

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

    /// <summary>True if a title folder holds real video files (someone put them there): never delete those.</summary>
    private static bool HoldsRealMedia(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => Torrent.TorrentEngine.IsVideo(f));
        }
        catch (Exception)
        {
            return true; // can't tell: keep it
        }
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

            if (HoldsRealMedia(dir))
            {
                _logger.LogInformation("Not pruning {Dir}: it holds video files that aren't the plugin's", dir);
                continue;
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
