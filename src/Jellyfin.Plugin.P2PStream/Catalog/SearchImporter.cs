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

        var key = term.Trim().ToLowerInvariant();
        if (key.Length < 3)
        {
            return;
        }

        foreach (var old in _recent.Where(kv => DateTime.UtcNow - kv.Value.At > TermMemory).ToList())
        {
            _recent.TryRemove(old.Key, out _);
        }

        // The web client sends one request per item type for each search. They all share one import.
        var entry = _recent.GetOrAdd(key, k => (DateTime.UtcNow, Task.Run(() => RunSearchImportAsync(k))));
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

    private async Task RunSearchImportAsync(string term)
    {
        try
        {
            var results = await _tmdb.SearchAsync(term, CancellationToken.None).ConfigureAwait(false);
            var good = results.Where(r => r.VoteCount >= MinVotes).ToList();
            var movies = good.Where(r => r.IsMovie).Take(MaxMovies).ToList();
            var shows = good.Where(r => !r.IsMovie).Take(MaxShows).ToList();
            var added = await ImportAsync(movies, shows, CancellationToken.None).ConfigureAwait(false);
            if (added.Count > 0)
            {
                _logger.LogInformation("Search \"{Term}\" added {Count} titles", term, added.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Search import for \"{Term}\" failed: {Message}", term, ex.Message);
        }
    }

    private async Task<List<string>> ImportAsync(List<TmdbItem> movies, List<TmdbItem> shows, CancellationToken ct)
    {
        // Titles already in any P2P library are searchable already.
        movies = movies.Where(m => CatalogBuilder.FindExistingAnywhere(true, m.Id) is null).ToList();
        shows = shows.Where(s => CatalogBuilder.FindExistingAnywhere(false, s.Id) is null).ToList();
        if (movies.Count == 0 && shows.Count == 0)
        {
            return [];
        }

        var movieRoot = CatalogBuilder.FolderFor(MovieLibrary);
        var showRoot = CatalogBuilder.FolderFor(ShowLibrary);
        var newMovies = new List<string>();
        var newShows = new List<string>();
        _libraries.RunLegacyCleanupOnce();

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
            }

            if (shows.Count > 0)
            {
                Directory.CreateDirectory(showRoot);
                foreach (var s in shows)
                {
                    try
                    {
                        var (dir, _) = await _catalog.WriteShowAsync(s, showRoot, ct).ConfigureAwait(false);
                        _catalog.MarkSearchImport(dir);
                        newShows.Add(dir);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("Could not add show {Name}: {Message}", s.Title, ex.Message);
                    }
                }
            }

            await _libraries.ScanIfInLibraryAsync(movieRoot, newMovies, 0, ct).ConfigureAwait(false);
            await _libraries.ScanIfInLibraryAsync(showRoot, newShows, 0, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        return newMovies.Concat(newShows).ToList();
    }
}
