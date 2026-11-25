using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Two minutes into one of the plugin's episodes, pre-loads the next episode (the start and end of Auto's release, then
/// the download pauses) and fetches its subtitle files, so Next or autoplay starts almost at once. It waits two minutes so
/// the episode being watched has started and built up a buffer first, and it skips a next episode that's in the same
/// torrent (a season pack), which is already running.
/// </summary>
public sealed class NextEpisodePreloader : IHostedService
{
    private static readonly long StartAfterTicks = TimeSpan.FromMinutes(2).Ticks;

    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly StreamResolver _resolver;
    private readonly TorrentSubtitles _subtitles;
    private readonly StreamOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<NextEpisodePreloader> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _handled = new();

    public NextEpisodePreloader(ISessionManager sessions, ILibraryManager library, StreamResolver resolver, TorrentSubtitles subtitles, StreamOptions options, IServiceProvider services, ILogger<NextEpisodePreloader> logger)
    {
        _options = options;
        _services = services;
        _sessions = sessions;
        _library = library;
        _resolver = resolver;
        _subtitles = subtitles;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress += OnProgress;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress -= OnProgress;
        return Task.CompletedTask;
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (Plugin.Instance?.Configuration.PreloadNextEpisode != true
                || e.IsPaused
                || e.Item is not Episode episode
                || (e.PlaybackPositionTicks ?? 0) < StartAfterTicks
                || !StreamPicker.TryGetRequest(episode, out _, out _))
            {
                return;
            }

            // Once per episode (per 6 hours: progress reports arrive every few seconds).
            var now = DateTime.UtcNow;
            if (_handled.TryGetValue(episode.Id, out var at) && now - at < TimeSpan.FromHours(6))
            {
                return;
            }

            _handled[episode.Id] = now;
            foreach (var old in _handled.Where(kv => now - kv.Value > TimeSpan.FromHours(6)).Select(kv => kv.Key).ToList())
            {
                _handled.TryRemove(old, out _);
            }

            var device = e.Session is { } s ? Identify(s) : null;
            _ = Task.Run(() => PreloadAsync(episode, device));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Next-episode pre-load skipped: {Message}", ex.Message);
        }
    }

    private async Task PreloadAsync(Episode current, DeviceRules.DeviceIdentity? device)
    {
        try
        {
            var next = FindNext(current);
            if (next is null || !StreamPicker.TryGetRequest(next, out _, out var request))
            {
                return; // the last episode, or the next one isn't one of the plugin's
            }

            // On a device with limits (Per-device limits), pre-load what that device will actually play.
            string? releaseId = null;
            var rule = DeviceRules.Match(device, Plugin.Instance?.Configuration.DeviceRules);
            if (rule is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var releases = await _options.GetAsync(request, timeout.Token).ConfigureAwait(false);
                var auto = _resolver.AutoRelease(request, releases);
                if ((auto is null || !DeviceRules.Allows(rule, auto)) && DeviceRules.BestWithin(rule, releases) is { } fits)
                {
                    releaseId = StreamOptions.IdFor(fits);
                    _logger.LogInformation("{Current} is playing on {Device}: pre-loading the next episode, {Next}, as {Release} ({Limit})", Describe(current), device, Describe(next), fits.Title, DeviceRules.Describe(rule));
                }
            }

            if (releaseId is null)
            {
                _logger.LogInformation("{Current} is playing: pre-loading the next episode, {Next}", Describe(current), Describe(next));
            }

            _resolver.Prewarm(request, nextEpisode: true, releaseId);
            _ = _subtitles.EnsureAsync(next, request, TimeSpan.Zero, CancellationToken.None, releaseId);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Couldn't pre-load the episode after {Current}: {Message}", Describe(current), ex.Message);
        }
    }

    /// <summary>The next episode in the library: later in the season, or the first of a later season (specials skipped).</summary>
    private Episode? FindNext(Episode current)
    {
        if (current.SeriesId == Guid.Empty || current.ParentIndexNumber is not int season || current.IndexNumber is not int number)
        {
            return null;
        }

        var episodes = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [current.SeriesId],
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Episode>();

        return episodes
            .Where(e => e.ParentIndexNumber is int s && s > 0 && e.IndexNumber is int n
                && (s > season || (s == season && n > number)))
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber)
            .FirstOrDefault();
    }

    /// <summary>The playing device, as per-device limits see it: its name, its name under Devices, and the app.</summary>
    private DeviceRules.DeviceIdentity Identify(SessionInfo session)
    {
        string? custom = null;
        try
        {
            if (!string.IsNullOrEmpty(session.DeviceId))
            {
                custom = _services.GetService<IDeviceManager>()?.GetDeviceOptions(session.DeviceId)?.CustomName;
            }
        }
        catch (Exception)
        {
            // unknown device
        }

        return new DeviceRules.DeviceIdentity(session.DeviceName, custom, session.Client);
    }

    private static string Describe(Episode e) => $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00}";
}
