using System.Collections.Concurrent;
using Jellyfin.Plugin.P2PStream.Catalog;
using Jellyfin.Plugin.P2PStream.Torrent;
using Microsoft.Extensions.Logging;
using MonoTorrent.Client;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Turns "play movie X / episode Y" into a running torrent session and file index.
/// </summary>
public sealed class StreamResolver
{
    private readonly TorrentEngine _engine;
    private readonly TorznabClient _torznab;
    private readonly SourceHub _sources;
    private readonly ResolutionCache _cache;
    private readonly TmdbClient _tmdb;
    private readonly ILogger<StreamResolver> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public StreamResolver(TorrentEngine engine, TorznabClient torznab, SourceHub sources, ResolutionCache cache, TmdbClient tmdb, ILogger<StreamResolver> logger)
    {
        _engine = engine;
        _torznab = torznab;
        _sources = sources;
        _cache = cache;
        _tmdb = tmdb;
        _logger = logger;
    }

    public async Task<ResolvedStream> ResolveAsync(MediaRequest req, CancellationToken ct)
    {
        // ffmpeg and clients often fire several requests at once. Only one of them searches.
        var gate = _locks.GetOrAdd(req.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await TryCachedAsync(req, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            await EnrichAsync(req, ct).ConfigureAwait(false);
            var releases = ReleaseRanker.Rank(await _sources.SearchAsync(req, ct).ConfigureAwait(false), req);
            if (releases.Count == 0)
            {
                throw new StreamNotFoundException($"No suitable releases found for {req}");
            }

            var tries = Math.Max(1, Plugin.Instance!.Configuration.CandidatesToTry);
            foreach (var release in releases.Take(tries))
            {
                try
                {
                    _logger.LogInformation("Trying {Release} from {Origin} ({Seeders} seeders) for {Request}", release.Title, release.Origin, release.Seeders, req);
                    var source = await _torznab.ToSourceAsync(release, ct).ConfigureAwait(false);
                    var session = await _engine.AddAsync(source, ct).ConfigureAwait(false);
                    var index = PickFile(session.Manager, req);
                    if (index >= 0)
                    {
                        _cache.Set(req.Key, source.InfoHash, index, release.Title);
                        return new ResolvedStream(session, index);
                    }

                    _logger.LogInformation("No matching video file in {Release}", release.Title);
                    await _engine.RemoveAsync(source.InfoHash, true).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Candidate {Release} failed: {Message}", release.Title, ex.Message);
                }
            }

            throw new StreamNotFoundException($"None of the top {tries} releases for {req} could be started");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Picks the video file to play: the matching episode, or the largest video for movies.
    /// </summary>
    public static int PickFile(TorrentManager manager, MediaRequest? req)
    {
        var videos = manager.Files
            .Select((f, i) => (File: f, Index: i))
            .Where(x => TorrentEngine.IsVideo(x.File.Path))
            .ToList();
        if (videos.Count == 0)
        {
            return -1;
        }

        if (req?.Kind == MediaKind.Episode)
        {
            var match = videos
                .Where(x => ReleaseRanker.ContainsEpisode(x.File.Path, req.Season, req.Episode))
                .OrderByDescending(x => x.File.Length)
                .FirstOrDefault();
            if (match.File is not null)
            {
                return match.Index;
            }

            // Single-episode torrent whose files aren't named by episode.
            return videos.Count == 1 && ReleaseRanker.ContainsEpisode(manager.Torrent?.Name ?? string.Empty, req.Season, req.Episode)
                ? videos[0].Index
                : -1;
        }

        return videos.OrderByDescending(x => x.File.Length).First().Index;
    }

    private async Task<ResolvedStream?> TryCachedAsync(MediaRequest req, CancellationToken ct)
    {
        if (!_cache.TryGet(req.Key, out var entry))
        {
            return null;
        }

        try
        {
            var session = await _engine.GetOrRestoreAsync(entry.InfoHash, ct).ConfigureAwait(false);
            if (session is not null && entry.FileIndex < session.Manager.Files.Count)
            {
                return new ResolvedStream(session, entry.FileIndex);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Cached torrent for {Request} no longer works ({Message}), searching again", req, ex.Message);
        }

        _cache.Remove(req.Key);
        return null;
    }

    private async Task EnrichAsync(MediaRequest req, CancellationToken ct)
    {
        if (req.TmdbId is not int id || !_tmdb.IsConfigured)
        {
            return;
        }

        try
        {
            if (req.Kind == MediaKind.Movie)
            {
                var m = await _tmdb.GetMovieAsync(id, TmdbClient.SearchLanguage, ct).ConfigureAwait(false);
                req.Title ??= m.Title;
                req.Year ??= m.Year;
                req.ImdbId ??= m.ImdbId;
            }
            else
            {
                var s = await _tmdb.GetShowAsync(id, TmdbClient.SearchLanguage, ct).ConfigureAwait(false);
                req.Title ??= s.Name;
                req.Year ??= s.Year;
                req.ImdbId ??= s.ImdbId;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("TMDB lookup failed for {Request}: {Message}", req, ex.Message);
        }
    }
}
