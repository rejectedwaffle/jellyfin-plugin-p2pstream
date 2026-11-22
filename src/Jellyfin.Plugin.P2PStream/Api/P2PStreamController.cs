using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.P2PStream.Catalog;
using Jellyfin.Plugin.P2PStream.Providers;
using Jellyfin.Plugin.P2PStream.Torrent;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Api;

/// <summary>
/// Streaming endpoints (used by .strm files and protected by the stream token, because ffmpeg and
/// players fetch them without a Jellyfin login) and admin endpoints for the config page.
/// </summary>
[ApiController]
[Route("P2PStream")]
public class P2PStreamController : ControllerBase
{
    private const string AdminPolicy = "RequiresElevation";
    private const int BufferSize = 256 * 1024;

    private readonly TorrentEngine _engine;
    private readonly StreamResolver _resolver;
    private readonly TorznabClient _torznab;
    private readonly SourceHub _sources;
    private readonly SearchImporter _importer;
    private readonly TmdbClient _tmdb;
    private readonly ResolutionCache _cache;
    private readonly CatalogBuilder _catalog;
    private readonly LibrarySetup _libraries;
    private readonly StreamUrlBuilder _urls;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<P2PStreamController> _logger;

    public P2PStreamController(
        TorrentEngine engine,
        StreamResolver resolver,
        TorznabClient torznab,
        SourceHub sources,
        SearchImporter importer,
        TmdbClient tmdb,
        ResolutionCache cache,
        CatalogBuilder catalog,
        LibrarySetup libraries,
        StreamUrlBuilder urls,
        ITaskManager taskManager,
        ILogger<P2PStreamController> logger)
    {
        _engine = engine;
        _resolver = resolver;
        _torznab = torznab;
        _sources = sources;
        _importer = importer;
        _tmdb = tmdb;
        _cache = cache;
        _catalog = catalog;
        _libraries = libraries;
        _urls = urls;
        _taskManager = taskManager;
        _logger = logger;
    }

    // ------------------------------------------------------------------ streaming

    [HttpGet("play/{token}/movie/{tmdbId:int}")]
    [HttpHead("play/{token}/movie/{tmdbId:int}")]
    [AllowAnonymous]
    public Task<IActionResult> PlayMovie(string token, int tmdbId, [FromQuery] string? release = null, [FromQuery] bool probe = false)
        => PlayAsync(token, new MediaRequest { Kind = MediaKind.Movie, TmdbId = tmdbId }, release, probe);

    [HttpGet("play/{token}/imdb/{imdbId}")]
    [HttpHead("play/{token}/imdb/{imdbId}")]
    [AllowAnonymous]
    public Task<IActionResult> PlayImdb(string token, string imdbId, [FromQuery] string? release = null, [FromQuery] bool probe = false)
        => PlayAsync(token, new MediaRequest { Kind = MediaKind.Movie, ImdbId = imdbId }, release, probe);

    [HttpGet("play/{token}/tv/{tmdbId:int}/{season:int}/{episode:int}")]
    [HttpHead("play/{token}/tv/{tmdbId:int}/{season:int}/{episode:int}")]
    [AllowAnonymous]
    public Task<IActionResult> PlayEpisode(string token, int tmdbId, int season, int episode, [FromQuery] string? release = null, [FromQuery] bool probe = false)
        => PlayAsync(token, new MediaRequest { Kind = MediaKind.Episode, TmdbId = tmdbId, Season = season, Episode = episode }, release, probe);

    [HttpGet("stream/{token}/{infoHash}/{fileIndex:int}")]
    [HttpHead("stream/{token}/{infoHash}/{fileIndex:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> StreamFile(string token, string infoHash, int fileIndex)
    {
        if (!TokenValid(token))
        {
            return NotFound();
        }

        TorrentSession? session;
        try
        {
            session = await _engine.GetOrRestoreAsync(infoHash, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        if (session is null || fileIndex < 0 || fileIndex >= session.Manager.Files.Count)
        {
            return NotFound();
        }

        await ServeAsync(session, fileIndex).ConfigureAwait(false);
        return new EmptyResult();
    }

    // ------------------------------------------------------------------ admin

    [HttpGet("Status")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<object> GetStatus()
    {
        var torrents = _engine.GetStatus();
        foreach (var t in torrents)
        {
            foreach (var f in t.Files.Where(f => f.IsVideo))
            {
                f.StreamUrl = _urls.File(t.InfoHash, f.Index, Request);
            }
        }

        return new
        {
            Torrents = torrents,
            BaseUrl = _urls.BaseUrl(Request),
            CatalogPath = Plugin.Instance!.CatalogPath,
            DownloadPath = Plugin.Instance.DownloadPath
        };
    }

    [HttpPost("Torrents")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<TorrentStatus>> AddTorrent([FromBody] AddTorrentRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Uri))
        {
            return BadRequest("A magnet link or .torrent URL is required");
        }

        try
        {
            var uri = body.Uri.Trim();
            var source = uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? TorrentSource.FromMagnet(uri)
                : await _torznab.ToSourceAsync(new ReleaseCandidate { Title = uri, DownloadUrl = uri }, HttpContext.RequestAborted).ConfigureAwait(false);
            await _engine.AddAsync(source, HttpContext.RequestAborted).ConfigureAwait(false);
            var status = _engine.GetStatus().First(s => s.InfoHash == source.InfoHash);
            foreach (var f in status.Files.Where(f => f.IsVideo))
            {
                f.StreamUrl = _urls.File(status.InfoHash, f.Index, Request);
            }

            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpDelete("Torrents/{infoHash}")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<IActionResult> RemoveTorrent(string infoHash, [FromQuery] bool deleteData = true)
        => await _engine.RemoveAsync(infoHash, deleteData).ConfigureAwait(false) ? NoContent() : NotFound();

    [HttpPost("Torrents/{infoHash}/Files/{fileIndex:int}/Strm")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<object>> CreateStrm(string infoHash, int fileIndex)
    {
        var session = await _engine.GetOrRestoreAsync(infoHash, HttpContext.RequestAborted).ConfigureAwait(false);
        if (session is null || fileIndex < 0 || fileIndex >= session.Manager.Files.Count)
        {
            return NotFound();
        }

        return new { Path = _catalog.WriteManualStrm(session.Manager, session.InfoHash, fileIndex) };
    }

    [HttpGet("Search")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<object>> Search([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("query is required");
        }

        try
        {
            var searched = _sources.Providers.Where(p => p.Enabled && p.SupportsTextSearch).Select(p => p.TextSearchNote is null ? p.Name : $"{p.Name} ({p.TextSearchNote})").ToList();
            var notForText = _sources.Providers.Where(p => p.Enabled && !p.SupportsTextSearch).Select(p => $"{p.Name} ({p.TextSearchNote})").ToList();
            if (_torznab.HasIndexers)
            {
                searched.AddRange(Plugin.Instance!.Configuration.Indexers.Where(i => i.Enabled && !string.IsNullOrWhiteSpace(i.Url)).Select(i => string.IsNullOrWhiteSpace(i.Name) ? i.Url : i.Name));
            }

            searched.AddRange(SourceHub.EnabledFeeds.Select(f => string.IsNullOrWhiteSpace(f.Name) ? new Uri(f.Url.Replace("{query}", "x", StringComparison.OrdinalIgnoreCase)).Host : f.Name.Trim()));

            var results = await _sources.SearchTextAsync(query, HttpContext.RequestAborted).ConfigureAwait(false);

            // Report what each source found before filtering, so "no results" can be told apart from "all filtered out".
            return new
            {
                Searched = searched,
                NotForText = notForText,
                Found = results.GroupBy(r => r.Origin).ToDictionary(g => g.Key, g => g.Count()),
                Results = ReleaseRanker.Rank(results, null).Take(50).ToList()
            };
        }
        catch (StreamNotFoundException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpGet("Sources")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<object> GetSources()
        => _sources.Providers.Select(p => new { p.Id, p.Name, p.Description, p.DefaultUrl, p.BaseUrl, p.Enabled }).ToList();

    [HttpGet("Tmdb/Search")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<object>> SearchTmdb([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("query is required");
        }

        if (!_tmdb.IsConfigured)
        {
            return Problem("Add a TMDB API key first", statusCode: StatusCodes.Status400BadRequest);
        }

        var results = await _tmdb.SearchAsync(query, HttpContext.RequestAborted).ConfigureAwait(false);
        return results.Select(r => new
        {
            r.Id,
            r.Title,
            r.Year,
            r.IsMovie,
            InLibrary = CatalogBuilder.FindExistingAnywhere(r.IsMovie, r.Id) is not null
        }).ToList();
    }

    [HttpPost("Catalog/Add")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<object>> AddToCatalog([FromBody] AddTitleRequest body)
    {
        try
        {
            var dir = await _importer.AddAsync(body.TmdbId, body.IsMovie, HttpContext.RequestAborted).ConfigureAwait(false);
            return new { Path = dir };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpGet("Catalogs")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult<object>> GetCatalogs()
    {
        _libraries.RunLegacyCleanupOnce();
        var defs = await CatalogDefinitions.GetAllAsync(_tmdb, HttpContext.RequestAborted).ConfigureAwait(false);
        return new
        {
            TmdbConfigured = _tmdb.IsConfigured,
            Syncing = _catalog.IsSyncing,
            CatalogPath = Plugin.Instance!.CatalogPath,
            Folders = _libraries.ListFolders(),
            SearchFolders = new[]
            {
                _libraries.Describe(CatalogBuilder.FolderFor(SearchImporter.MovieLibrary)),
                _libraries.Describe(CatalogBuilder.FolderFor(SearchImporter.ShowLibrary))
            },
            Rows = defs.Select(d =>
            {
                var e = CatalogDefinitions.Effective(d);
                var folder = _libraries.Describe(CatalogBuilder.FolderFor(e.Library));
                return new { d.Id, d.Name, d.IsMovie, d.DefaultFolder, e.Enabled, e.Collection, e.MaxItems, Folder = folder.Name, FolderPath = folder.Path, folder.Library };
            }).ToList()
        };
    }

    [HttpPost("Folders")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<CatalogFolder> CreateFolder([FromBody] CreateFolderRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return BadRequest("A library name is required");
        }

        return _libraries.CreateFolder(body.Name.Trim());
    }

    [HttpPost("Catalogs/{id}/Sync")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult SyncOneCatalog(string id)
    {
        // Runs in the background. Titles appear in the library as the sync and scan finish.
        _ = Task.Run(async () =>
        {
            try
            {
                await _catalog.SyncAsync(id, new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync of catalog {Id} failed", id);
            }
        });
        return Accepted();
    }

    [HttpPost("Catalog/Sync")]
    [Authorize(Policy = AdminPolicy)]
    public IActionResult SyncCatalog()
    {
        _taskManager.QueueScheduledTask<CatalogSyncTask>();
        return NoContent();
    }

    [HttpPost("Cache/Clear")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<object> ClearCache() => new { Removed = _cache.Clear() };

    // ------------------------------------------------------------------ helpers

    private static bool TokenValid(string token)
    {
        var expected = Plugin.Instance?.Configuration.StreamToken;
        return !string.IsNullOrEmpty(expected)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token ?? string.Empty), Encoding.UTF8.GetBytes(expected));
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".wmv" => "video/x-ms-wmv",
        ".ts" or ".m2ts" => "video/mp2t",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".flv" => "video/x-flv",
        _ => "application/octet-stream"
    };

    private static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return false;
        }

        var a = spec[..dash].Trim();
        var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffix);
        }
        else
        {
            if (!long.TryParse(a, out start))
            {
                return false;
            }

            if (b.Length > 0)
            {
                if (!long.TryParse(b, out end))
                {
                    return false;
                }

                end = Math.Min(end, length - 1);
            }
        }

        return start >= 0 && start <= end && start < length;
    }

    private async Task<IActionResult> PlayAsync(string token, MediaRequest req, string? release, bool probe = false)
    {
        if (!TokenValid(token))
        {
            return NotFound();
        }

        ResolvedStream resolved;
        try
        {
            resolved = await _resolver.ResolveAsync(req, release, HttpContext.RequestAborted, remember: !probe).ConfigureAwait(false);
        }
        catch (StreamNotFoundException ex)
        {
            _logger.LogWarning("{Message}", ex.Message);
            return Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve {Request}", req);
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        await ServeAsync(resolved.Session, resolved.FileIndex).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>
    /// Serves a torrent file with HTTP range support. Reads block until the pieces arrive;
    /// MonoTorrent moves piece priority to wherever the stream is reading.
    /// </summary>
    private async Task ServeAsync(TorrentSession session, int fileIndex)
    {
        var file = session.Manager.Files[fileIndex];
        var length = file.Length;
        var rangeHeader = Request.Headers.Range.ToString();

        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = MimeType(file.Path);

        long start = 0, end = length - 1;
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            if (!TryParseRange(rangeHeader, length, out start, out end))
            {
                Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                Response.Headers.ContentRange = $"bytes */{length}";
                return;
            }

            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
        }
        else
        {
            Response.StatusCode = StatusCodes.Status200OK;
        }

        Response.ContentLength = end - start + 1;
        session.Touch();

        // HEAD only needs the headers. Don't open a stream, or it would take over from an active player.
        if (HttpMethods.IsHead(Request.Method))
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var aborted = HttpContext.RequestAborted;
            await using var lease = await session.OpenAsync(file, aborted).ConfigureAwait(false);
            lease.Position = start;

            var remaining = end - start + 1;
            var sinceTouch = Stopwatch.StartNew();
            while (remaining > 0)
            {
                var read = await lease.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), aborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, read), aborted).ConfigureAwait(false);
                remaining -= read;

                if (sinceTouch.Elapsed > TimeSpan.FromSeconds(10))
                {
                    lease.Touch();
                    sinceTouch.Restart();
                }
            }

            if (remaining > 0)
            {
                HttpContext.Abort(); // ended early: close the connection rather than send a short body
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away (ffprobe and ffmpeg routinely close connections early)
            HttpContext.Abort();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug("Stream ended: {Message}", ex.Message); // torrent removed, or the connection dropped
            HttpContext.Abort();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed class AddTorrentRequest
{
    /// <summary>Gets or sets a magnet link or an http(s) URL to a .torrent file.</summary>
    public string Uri { get; set; } = string.Empty;
}

public sealed class AddTitleRequest
{
    public int TmdbId { get; set; }

    public bool IsMovie { get; set; } = true;
}

public sealed class CreateFolderRequest
{
    public string Name { get; set; } = string.Empty;
}
