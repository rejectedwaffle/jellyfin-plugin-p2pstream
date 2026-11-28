using Jellyfin.Plugin.P2PStream.Torrent;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Tells the torrent engine which torrents belong to titles someone has playing or paused in Jellyfin. Reads alone
/// don't show that: Jellyfin can read a whole episode ahead and play from its own prepared copy, and a paused player
/// reads nothing. Such a torrent is kept (not stopped, removed or evicted) until the player stops.
/// </summary>
public sealed class PlayingTitles : IHostedService
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(5);
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly ResolutionCache _cache;
    private readonly object _lock = new();
    private HashSet<string> _last = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _at = DateTime.MinValue;

    public PlayingTitles(ISessionManager sessions, ILibraryManager library, ResolutionCache cache)
    {
        _sessions = sessions;
        _library = library;
        _cache = cache;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        TorrentEngine.PlayingHashes = Hashes;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TorrentEngine.PlayingHashes = null;
        return Task.CompletedTask;
    }

    private IReadOnlySet<string> Hashes()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _at < KeepFor)
            {
                return _last;
            }
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _sessions.Sessions)
        {
            if (session.NowPlayingItem is not { } playing || _library.GetItemById(playing.Id) is not { } item)
            {
                continue;
            }

            if (StreamPicker.TryGetTorrentFile(item, out var hash, out _))
            {
                found.Add(hash); // a torrent added by hand
            }
            else if (StreamPicker.TryGetRequest(item, out _, out var request) && _cache.TryGet(request.Key, out var entry))
            {
                found.Add(entry.InfoHash); // the release this title plays (Auto's or a chosen version)
            }
        }

        lock (_lock)
        {
            _last = found;
            _at = DateTime.UtcNow;
        }

        return found;
    }
}
