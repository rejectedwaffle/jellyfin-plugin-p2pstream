using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// Makes "search for any movie" work: looks a search term up on TMDB, writes .strm files for the best matches into the
/// search folders (add a library for each), and scans just those titles so they show up in Jellyfin's results.
/// </summary>
public sealed class SearchImporter
{
    private const int MaxMovies = 5;
    private const int MaxShows = 3;
    private const int MinVotes = 10; // skips obscure or empty TMDB entries that would clutter the library
    private static readonly TimeSpan TermMemory = TimeSpan.FromMinutes(15);

    private readonly TmdbClient _tmdb;
    private readonly CatalogBuilder _catalog;
    private readonly LibrarySetup _libraries;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SearchImporter> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, Task Task)> _recent = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SearchImporter(TmdbClient tmdb, CatalogBuilder catalog, LibrarySetup libraries, ILibraryManager libraryManager, ILogger<SearchImporter> logger)
    {
        _tmdb = tmdb;
        _catalog = catalog;
        _libraries = libraries;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public static string MovieLibrary => Name(Plugin.Instance!.Configuration.SearchMovieLibrary, "P2P Movies");

    public static string ShowLibrary => Name(Plugin.Instance!.Configuration.SearchShowLibrary, "P2P Shows");

    /// <summary>
    /// Called for every Jellyfin search. Waits up to <paramref name="budget"/> for the import, so new titles can appear
    /// in this search's results. The import keeps running in the background after that.
    /// </summary>
    public async Task ImportForSearchAsync(string term, TimeSpan budget, CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.SearchIntegration != true || !_tmdb.IsConfigured)
        {
            return;
        }

        var normalized = term.Trim().ToLowerInvariant();
        if (normalized.Length < 3)
        {
            return;
        }

        foreach (var old in _recent.Where(kv => DateTime.UtcNow - kv.Value.At > TermMemory).ToList())
        {
            _recent.TryRemove(old.Key, out _);
        }

        // The web client sends one request per item type for each search. They all share one import. The key includes
        // the target folders, so after changing the search libraries the same term imports again straight away.
        var key = normalized + "|" + MovieLibrary + "|" + ShowLibrary;
        var entry = _recent.GetOrAdd(key, _ => (DateTime.UtcNow, Task.Run(() => RunSearchImportAsync(normalized))));
        try
        {
            await entry.Task.WaitAsync(budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still running. The titles will show up on the next search.
        }
    }

    /// <summary>Adds one title (used by the "Add any movie or show" box).</summary>
    public async Task<string> AddAsync(int tmdbId, bool isMovie, CancellationToken ct)
    {
        TmdbItem item;
        if (isMovie)
        {
            var m = await _tmdb.GetMovieAsync(tmdbId, TmdbClient.CatalogLanguage, ct).ConfigureAwait(false);
            item = new TmdbItem(m.Id, m.Title, m.Year, true);
        }
        else
        {
            var s = await _tmdb.GetShowAsync(tmdbId, TmdbClient.CatalogLanguage, ct).ConfigureAwait(false);
            item = new TmdbItem(s.Id, s.Name, s.Year, false);
        }

        var dirs = await ImportAsync(isMovie ? [item] : [], isMovie ? [] : [item], ct).ConfigureAwait(false);
        return dirs.FirstOrDefault() ?? CatalogBuilder.FindExistingAnywhere(isMovie, tmdbId) ?? string.Empty;
    }

    private static string Name(string? configured, string fallback) => string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    private bool IsVisible(string? titleDir) =>
        titleDir is not null && Path.GetDirectoryName(titleDir) is string folder && _libraries.Describe(folder).Library is not null;

    /// <summary>The folder chosen for search results on the Search tab. Titles only show up in search once it's in a library.</summary>
    public string TargetFolder(bool movies)
    {
        var folder = CatalogBuilder.FolderFor(movies ? MovieLibrary : ShowLibrary);
        if (_libraries.Describe(folder).Library is null)
        {
            _logger.LogWarning("The search folder for {Kind} isn't in a library, so results can't appear. Pick a library on Settings > Search", movies ? "movies" : "shows");
        }

        return folder;
    }

    private async Task RunSearchImportAsync(string term)
    {
        try
        {
            var results = await _tmdb.SearchAsync(term, CancellationToken.None).ConfigureAwait(false);
            var good = results.Where(r => r.VoteCount >= MinVotes).ToList();
            var movies = good.Where(r => r.IsMovie).Take(MaxMovies).ToList();
            var shows = good.Where(r => !r.IsMovie).Take(MaxShows).ToList();
            var first = await FirstPassAsync(movies, shows, CancellationToken.None).ConfigureAwait(false);
            if (first.Added.Count > 0)
            {
                _logger.LogInformation("Search \"{Term}\" added {Count} titles", term, first.Added.Count);
            }

            if (first.Shows.Count > 0)
            {
                // The rest of each show's episodes, after search has its results.
                _ = Task.Run(() => FinishShowsAsync(first.Shows, first.ShowRoot));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Search import for \"{Term}\" failed: {Message}", term, ex.Message);
        }
    }

    /// <summary>Manual add: both passes, waiting for the full show.</summary>
    private async Task<List<string>> ImportAsync(List<TmdbItem> movies, List<TmdbItem> shows, CancellationToken ct)
    {
        var first = await FirstPassAsync(movies, shows, ct).ConfigureAwait(false);
        await FinishShowsAsync(first.Shows, first.ShowRoot).ConfigureAwait(false);
        return first.Added;
    }

    /// <summary>
    /// The fast pass that search waits for: movies are written and added to the library first, then each show with just its
    /// first episode. Items are created from their names immediately; posters and details are fetched in the background.
    /// </summary>
    private async Task<(List<string> Added, List<TmdbItem> Shows, string ShowRoot)> FirstPassAsync(List<TmdbItem> movies, List<TmdbItem> shows, CancellationToken ct)
    {
        // Titles already in a folder that's in a library are searchable already. Ones sitting in a folder outside any
        // library (e.g. written before the search library existed) don't count, so they're added where Jellyfin can see them.
        movies = movies.Where(m => !IsVisible(CatalogBuilder.FindExistingAnywhere(true, m.Id))).ToList();
        shows = shows.Where(s => !IsVisible(CatalogBuilder.FindExistingAnywhere(false, s.Id))).ToList();
        var movieRoot = TargetFolder(movies: true);
        var showRoot = TargetFolder(movies: false);
        if (movies.Count == 0 && shows.Count == 0)
        {
            return ([], [], showRoot);
        }

        // Each show's first season (season 0 is specials), looked up in parallel before writing anything.
        var firstSeasons = await Task.WhenAll(shows.Select(async s =>
        {
            try
            {
                var info = await _tmdb.GetShowAsync(s.Id, TmdbClient.CatalogLanguage, ct).ConfigureAwait(false);
                return info.Seasons.Count > 0 ? info.Seasons.Min() : 1;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return 1;
            }
        })).ConfigureAwait(false);

        var newMovies = new List<string>();
        var newShows = new List<string>();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (movies.Count > 0)
            {
                Directory.CreateDirectory(movieRoot);
                foreach (var m in movies)
                {
                    var (dir, _) = _catalog.WriteMovie(m, movieRoot);
                    _catalog.MarkSearchImport(dir);
                    newMovies.Add(dir);
                }

                await _libraries.ScanIfInLibraryAsync(movieRoot, newMovies, 0, ct, fast: true).ConfigureAwait(false);
            }

            if (shows.Count > 0)
            {
                Directory.CreateDirectory(showRoot);
                for (var i = 0; i < shows.Count; i++)
                {
                    var dir = _catalog.WriteFirstEpisode(shows[i], showRoot, firstSeasons[i]);
                    _catalog.MarkSearchImport(dir);
                    newShows.Add(dir);
                }

                await _libraries.ScanIfInLibraryAsync(showRoot, newShows, 0, ct, fast: true).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        return (newMovies.Concat(newShows).ToList(), shows, showRoot);
    }

    /// <summary>The slow pass: writes the rest of each show's aired episodes and adds them to the library.</summary>
    private async Task FinishShowsAsync(List<TmdbItem> shows, string showRoot)
    {
        if (shows.Count == 0)
        {
            return;
        }

        var changed = new List<string>();
        foreach (var s in shows)
        {
            try
            {
                var (dir, written) = await _catalog.WriteShowAsync(s, showRoot, CancellationToken.None).ConfigureAwait(false);
                if (written > 0)
                {
                    changed.Add(dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not add the episodes of {Name}: {Message}", s.Title, ex.Message);
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _libraries.ScanIfInLibraryAsync(showRoot, changed, 0, CancellationToken.None, fast: true).ConfigureAwait(false);
            _logger.LogInformation("Added all episodes for {Count} shows from search", changed.Count);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
