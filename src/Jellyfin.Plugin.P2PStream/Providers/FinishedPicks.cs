using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// A Version someone picks is theirs while they're part-way through the title (so resuming plays the same release).
/// When they watch it to the end, the pick is forgotten and their Auto goes back to the best match.
/// </summary>
public sealed class FinishedPicks : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly UserPicks _picks;
    private readonly StreamResolver _resolver;
    private readonly ILogger<FinishedPicks> _logger;

    public FinishedPicks(ISessionManager sessions, UserPicks picks, StreamResolver resolver, ILogger<FinishedPicks> logger)
    {
        _sessions = sessions;
        _picks = picks;
        _resolver = resolver;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped -= OnStopped;
        return Task.CompletedTask;
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            if (!e.PlayedToCompletion || e.Item is null || !StreamPicker.TryGetRequest(e.Item, out _, out var request))
            {
                return;
            }

            foreach (var user in e.Users ?? [])
            {
                var userId = user.Id.ToString("N");
                if (_picks.Get(userId, request.Key) is null)
                {
                    continue;
                }

                _picks.Remove(userId, request.Key);
                _resolver.ForgetPickBy(request, userId);
                _logger.LogInformation("{Item}: watched to the end, so {User}'s Auto is back to the best match", e.Item.Name, user.Username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not forget a finished pick: {Message}", ex.Message);
        }
    }
}
