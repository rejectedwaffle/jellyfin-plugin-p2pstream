using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Short pop-up messages on the screen of whoever pressed Play, while a title starts ("Trying 2 releases…",
/// "Connecting: 6 peers, 1.2 MB/s", "Too slow, trying the next release"), so a slow start isn't a silent spinner.
/// A session is registered at its Play request and unregistered when playback begins (or after a few minutes).
/// Messages for one session are spaced out; when several arrive close together only the latest is shown.
/// </summary>
public sealed class StartStatus
{
    private static readonly TimeSpan WatchFor = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(3);
    private const int ShowMs = 4500;

    private readonly ISessionManager _sessions;
    private readonly ILogger<StartStatus> _logger;

    /// <summary>Title key to the sessions waiting for it (session id to when they asked).</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _watching = new();

    private readonly ConcurrentDictionary<string, Outbox> _outboxes = new();

    public StartStatus(ISessionManager sessions, ILogger<StartStatus> logger)
    {
        _sessions = sessions;
        _logger = logger;
        _sessions.PlaybackStart += OnPlaybackStart;
    }

    private static bool Enabled => Plugin.Instance?.Configuration.ShowStartStatus != false;

    /// <summary>Starts showing messages about <paramref name="request"/> to the session that asked for it.</summary>
    public void Watch(MediaRequest request, string sessionId)
    {
        if (!Enabled || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        // A session waits for one title at a time.
        foreach (var other in _watching.Values)
        {
            other.TryRemove(sessionId, out _);
        }

        _watching.GetOrAdd(request.Key, _ => new()).AddOrUpdate(sessionId, DateTime.UtcNow, (_, _) => DateTime.UtcNow);
    }

    /// <summary>True if someone is waiting for this title to start (so progress is worth reporting).</summary>
    public bool IsWatched(MediaRequest request) => Watchers(request).Count > 0;

    /// <summary>Shows <paramref name="text"/> to everyone waiting for this title.</summary>
    public void Report(MediaRequest request, string text)
    {
        foreach (var sessionId in Watchers(request))
        {
            _outboxes.GetOrAdd(sessionId, id => new Outbox(this, id)).Post(text);
        }
    }

    /// <summary>Stops reporting about this title (it has started, or failed with a message already shown).</summary>
    public void Done(MediaRequest request) => _watching.TryRemove(request.Key, out _);

    private List<string> Watchers(MediaRequest request)
    {
        if (!_watching.TryGetValue(request.Key, out var map))
        {
            return [];
        }

        var now = DateTime.UtcNow;
        foreach (var stale in map.Where(kv => now - kv.Value > WatchFor).Select(kv => kv.Key).ToList())
        {
            map.TryRemove(stale, out _);
        }

        return map.Keys.ToList();
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Session?.Id is not { } id)
        {
            return;
        }

        foreach (var map in _watching.Values)
        {
            map.TryRemove(id, out _);
        }

        if (_outboxes.TryRemove(id, out var box))
        {
            box.Drop();
        }
    }

    private async Task SendAsync(string sessionId, string text)
    {
        try
        {
            var command = new MessageCommand { Header = "P2P Stream", Text = text, TimeoutMs = ShowMs };
            await _sessions.SendMessageCommand(null!, sessionId, command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not show a start-up message: {Message}", ex.Message);
        }
    }

    /// <summary>One session's messages: at most one every <see cref="Spacing"/>; a newer message replaces a waiting one.</summary>
    private sealed class Outbox(StartStatus owner, string sessionId)
    {
        private readonly object _lock = new();
        private DateTime _lastSent = DateTime.MinValue;
        private string? _waiting;
        private bool _scheduled;

        public void Post(string text)
        {
            TimeSpan delay;
            lock (_lock)
            {
                _waiting = text;
                if (_scheduled)
                {
                    return;
                }

                delay = Spacing - (DateTime.UtcNow - _lastSent);
                _scheduled = true;
            }

            _ = Task.Run(async () =>
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                string? send;
                lock (_lock)
                {
                    send = _waiting;
                    _waiting = null;
                    _scheduled = false;
                    _lastSent = DateTime.UtcNow;
                }

                if (send is not null)
                {
                    await owner.SendAsync(sessionId, send).ConfigureAwait(false);
                }
            });
        }

        public void Drop()
        {
            lock (_lock)
            {
                _waiting = null;
            }
        }
    }
}
