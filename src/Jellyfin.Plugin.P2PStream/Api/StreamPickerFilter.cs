using System.Text.RegularExpressions;
using Jellyfin.Plugin.P2PStream.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Api;

/// <summary>
/// The Elementum-style stream picker, built on Jellyfin's Version menu:
/// <list type="bullet">
/// <item>Title pages: the releases are added as versions ("Auto (best match)" first), and the top ones are checked in the background.</item>
/// <item>Playback: if the playback request names one of those versions, that release is remembered for the title and the
/// request is switched to the title's normal source, which Jellyfin plays reliably. The play endpoint then serves the chosen release.</item>
/// </list>
/// </summary>
public sealed partial class StreamPickerFilter : IAsyncActionFilter
{
    private static readonly TimeSpan PageBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PlayBudget = TimeSpan.FromSeconds(30);

    private readonly StreamOptions _options;
    private readonly StreamResolver _resolver;
    private readonly StreamProber _prober;
    private readonly TorrentSubtitles _subtitles;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<StreamPickerFilter> _logger;

    public StreamPickerFilter(StreamOptions options, StreamResolver resolver, StreamProber prober, TorrentSubtitles subtitles, ILibraryManager libraryManager, IServiceProvider services, ILogger<StreamPickerFilter> logger)
    {
        _subtitles = subtitles;
        _options = options;
        _resolver = resolver;
        _prober = prober;
        _libraryManager = libraryManager;
        _services = services;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var path = http.Request.Path.Value ?? string.Empty;
        var enabled = Plugin.Instance?.Configuration.StreamPicker == true;

        // While Jellyfin answers a Play request for one of our titles, its forced re-check of the stream gets only the
        // start of the release (see PlaybackInfoWindow).
        IDisposable? checkWindow = null;
        BaseItem? playItem = null;
        MediaRequest? playRequest = null;
        if (PlaybackInfoPath().IsMatch(path) && context.ActionArguments.TryGetValue("itemId", out var playArg) && playArg is Guid playId
            && _libraryManager.GetItemById(playId) is { } found && StreamPicker.TryGetRequest(found, out _, out var foundRequest))
        {
            playItem = found;
            playRequest = foundRequest;
            checkWindow = PlaybackInfoWindow.Begin(foundRequest.Key);
        }

        if (enabled && PlaybackInfoPath().IsMatch(path))
        {
            try
            {
                await UseChosenVersionAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not apply the chosen version: {Message}", ex.Message);
            }
        }

        // The release's own subtitle files, next to the .strm before Jellyfin's per-play check looks for them. After the
        // chosen version is pinned, so they're that release's.
        if (checkWindow is not null && playItem is not null && playRequest is not null)
        {
            await _subtitles.EnsureAsync(playItem, playRequest, TimeSpan.FromSeconds(12), http.RequestAborted).ConfigureAwait(false);
        }
        else if (PlaybackInfoPath().IsMatch(path) && context.ActionArguments.TryGetValue("itemId", out var addedArg) && addedArg is Guid addedId
            && _libraryManager.GetItemById(addedId) is { } added && StreamPicker.TryGetTorrentFile(added, out var addedHash, out var addedIndex))
        {
            await _subtitles.EnsureForTorrentAsync(added, addedHash, addedIndex, TimeSpan.FromSeconds(12), http.RequestAborted).ConfigureAwait(false);
        }

        // Apps look a chosen version up as an item before playing it: answer with the title it belongs to.
        Guid? versionLookup = null;
        if (enabled && HttpMethods.IsGet(http.Request.Method) && ItemPath().IsMatch(path)
            && context.ActionArguments.TryGetValue("itemId", out var lookupArg) && lookupArg is Guid lookupId
            && StreamPicker.TryGetVersionOwner(lookupId, out var owner))
        {
            context.ActionArguments["itemId"] = owner;
            versionLookup = lookupId;
        }

        ActionExecutedContext executed;
        try
        {
            executed = await next().ConfigureAwait(false);
        }
        finally
        {
            checkWindow?.Dispose();
        }

        if (enabled && HttpMethods.IsGet(http.Request.Method) && ItemPath().IsMatch(path))
        {
            try
            {
                await AddVersionsAsync(http, executed).ConfigureAwait(false);
                if (versionLookup is { } v && executed.Result is ObjectResult { Value: BaseItemDto dto }
                    && dto.MediaSources?.FirstOrDefault(m => string.Equals(m.Id, v.ToString("N"), StringComparison.OrdinalIgnoreCase)) is { MediaStreams.Count: > 0 } chosenSource)
                {
                    dto.MediaStreams = chosenSource.MediaStreams.ToArray(); // that version's tracks (for remembered audio/subtitle choices)
                }
            }
            catch (Exception)
            {
                // Never break a title's page because of the plugin.
            }
        }
    }

    // ---------------- title page: list the versions ----------------
    private async Task AddVersionsAsync(HttpContext http, ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: BaseItemDto dto } || dto.MediaSources is not { Length: > 0 })
        {
            return;
        }

        var item = _libraryManager.GetItemById(dto.Id);
        if (item is null || !StreamPicker.TryGetRequest(item, out var url, out var request))
        {
            return;
        }

        if (!_options.TryGetCached(request, out var releases))
        {
            try
            {
                releases = await _options.GetAsync(request, http.RequestAborted).WaitAsync(PageBudget, http.RequestAborted).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return; // still searching; the versions show up next time the page is opened
            }
        }

        if (releases.Count == 0)
        {
            return;
        }

        _resolver.Prewarm(request);
        if (Plugin.Instance?.Configuration.PrewarmOnOpen == true)
        {
            _ = _subtitles.EnsureAsync(item, request, TimeSpan.Zero, CancellationToken.None); // ready before Play is pressed
        }

        // Auto plays the remembered pick, or else tries the top-ranked release first. Name it, and if that release has been
        // checked, show its tracks on Auto too (Jellyfin otherwise only knows them after Auto has played once).
        // A device with limits (Quality > Per-device limits) gets the best release within them instead.
        var auto = dto.MediaSources[0];
        var current = _resolver.CurrentPickTitle(request);
        var autoRelease = current is null ? releases[0] : releases.FirstOrDefault(r => r.Title == current);
        var (rule, _) = await DeviceRules.FindAsync(http, _services).ConfigureAwait(false);
        if (rule is not null && (autoRelease is null || !DeviceRules.Allows(rule, autoRelease)) && DeviceRules.BestWithin(rule, releases) is { } fits)
        {
            autoRelease = fits;
            auto.Name = $"Auto ({DeviceRules.Describe(rule)} on this device) — plays: " + Short(fits.Title);
        }
        else
        {
            auto.Name = current is not null
                ? "Auto (best match) — now: " + Short(current)
                : "Auto (best match) — tries first: " + Short(releases[0].Title);
        }

        _prober.ProbeTopInBackground(item, url, releases, autoRelease);

        if (auto.MediaStreams is not { Count: > 0 } && autoRelease is not null && _options.TryGetProbe(StreamOptions.IdFor(autoRelease), out var probe))
        {
            auto.MediaStreams = probe.Streams;
            auto.RunTimeTicks ??= probe.RunTimeTicks;
            auto.DefaultAudioStreamIndex ??= (probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                ?? probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio))?.Index;
        }
        dto.MediaSources = new[] { auto }
            .Concat(BestPerResolution(item, url, releases))
            .Concat(StreamPicker.BuildSources(item, url, releases, _options))
            .ToArray();
        dto.MediaSourceCount = dto.MediaSources.Length;
    }

    // ---------------- playback: honour the chosen version ----------------
    private async Task UseChosenVersionAsync(ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue("itemId", out var idArg) || idArg is not Guid itemId)
        {
            return;
        }

        // The chosen version arrives as a query parameter, in the request body, or both.
        var bodies = context.ActionArguments.Values.Where(v => v?.GetType().GetProperty("MediaSourceId") is { PropertyType: var t } && t == typeof(string)).ToList();
        var chosen = context.ActionArguments.TryGetValue("mediaSourceId", out var q) ? q as string : null;
        chosen ??= bodies.Select(b => b!.GetType().GetProperty("MediaSourceId")!.GetValue(b) as string).FirstOrDefault(s => !string.IsNullOrEmpty(s));

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !StreamPicker.TryGetRequest(item, out _, out var request))
        {
            return;
        }

        SkipAutomaticSubtitle(context, item);

        var staticId = item.Id.ToString("N");
        var client = context.HttpContext.Request.Headers.Authorization.ToString() + " " + context.HttpContext.Request.Headers["X-Emby-Authorization"].ToString();
        var clientName = ClientName().Match(client) is { Success: true } cm ? cm.Groups[1].Value : "unknown app";
        _logger.LogInformation(
            "Playback info for {Item} from {Client}: version asked for: {Version}",
            item.Name,
            clientName,
            string.IsNullOrEmpty(chosen) || string.Equals(chosen, staticId, StringComparison.OrdinalIgnoreCase) ? "Auto" : chosen);
        if (string.IsNullOrEmpty(chosen) || string.Equals(chosen, staticId, StringComparison.OrdinalIgnoreCase))
        {
            await ApplyDeviceRuleAsync(context, item, request).ConfigureAwait(false); // Auto
            await UseCheckedTracksAsync(context, item, request).ConfigureAwait(false);
            return;
        }

        if (!_options.TryGetCached(request, out var releases))
        {
            releases = await _options.GetAsync(request, context.HttpContext.RequestAborted).WaitAsync(PlayBudget).ConfigureAwait(false);
        }

        // "Best 1080p" and the like: the best-ranked release at that resolution (the list is ranked, best first).
        var resolution = ResolutionChoice().Match(chosen) is { Success: true } choice
            ? int.Parse(choice.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : releases.Select(r => r.Resolution).Distinct()
                .FirstOrDefault(res => string.Equals(chosen, StreamPicker.VersionId(item, "res" + res.ToString(System.Globalization.CultureInfo.InvariantCulture)), StringComparison.OrdinalIgnoreCase));
        if (resolution > 0)
        {
            chosen = releases.Where(r => r.Resolution == resolution).Select(StreamOptions.IdFor).FirstOrDefault() ?? chosen;
        }

        // A release: by its Version menu id (or its plain release id, as older pages used).
        var release = releases.FirstOrDefault(r => StreamOptions.IdFor(r) == chosen)
            ?? releases.FirstOrDefault(r => string.Equals(StreamPicker.VersionId(item, StreamOptions.IdFor(r)), chosen, StringComparison.OrdinalIgnoreCase));
        if (release is not null)
        {
            chosen = StreamOptions.IdFor(release);
        }

        if (release is null)
        {
            return; // not one of ours
        }

        // A different release than last time: use its tracks if it was checked in advance (otherwise Jellyfin reads the
        // start of the release before answering, which can take minutes on a slow torrent and makes apps give up).
        if (!_resolver.IsCurrentPick(request, chosen))
        {
            if (_options.TryGetProbe(chosen, out var probe) && probe.Streams is { Count: > 0 })
            {
                _services.GetService<IMediaStreamRepository>()?.SaveMediaStreams(item.Id, probe.Streams, CancellationToken.None);
            }
            else
            {
                ClearSavedTracks(item);
            }
        }

        _resolver.Pin(request, chosen);
        _logger.LogInformation("Playing chosen version {Release} for {Item}", release.Title, item.Name);

        // Play through the title's normal source; the play endpoint now serves the chosen release.
        if (context.ActionArguments.ContainsKey("mediaSourceId"))
        {
            context.ActionArguments["mediaSourceId"] = staticId;
        }

        foreach (var body in bodies)
        {
            body!.GetType().GetProperty("MediaSourceId")!.SetValue(body, staticId);
        }
    }

    /// <summary>
    /// Auto is about to play and Jellyfin doesn't know the tracks of the release it will play: if the plugin has already
    /// checked that release, save its tracks (Jellyfin still re-checks the stream, but this keeps the item's tracks right
    /// if that check fails).
    /// </summary>
    private async Task UseCheckedTracksAsync(ActionExecutingContext context, BaseItem item, MediaRequest request)
    {
        try
        {
            if (!_options.TryGetCached(request, out var releases) || releases.Count == 0)
            {
                return; // the page wasn't opened recently, so nothing was checked
            }

            var release = _resolver.PinnedRelease(request, releases) ?? _resolver.AutoRelease(request, releases);
            if (release is null)
            {
                return;
            }

            var id = StreamOptions.IdFor(release);
            var saved = _services.GetService<IMediaSourceManager>()?.GetMediaStreams(item.Id) ?? [];
            if (saved.Count > 0 && _resolver.IsCurrentPick(request, id))
            {
                return; // Jellyfin already knows this release's tracks
            }

            var started = DateTime.UtcNow;
            // Only if it's already done: Jellyfin re-checks a .strm stream on every play anyway (now limited to the start of
            // the release, see PlaybackInfoWindow), so waiting for ours would only add time.
            var probe = await _prober.WaitForProbeAsync(id, TimeSpan.Zero, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (probe?.Streams is not { Count: > 0 })
            {
                return; // Jellyfin checks it itself, as before
            }

            _services.GetService<IMediaStreamRepository>()?.SaveMediaStreams(item.Id, probe.Streams, CancellationToken.None);
            _logger.LogInformation("{Item}: gave Jellyfin the {Count} tracks of {Release} (waited {Seconds:0}s)", item.Name, probe.Streams.Count, release.Title, (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Could not hand over checked tracks: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Auto on a device with limits: play the best release within them (if Auto's usual pick doesn't fit). On a device
    /// without limits, undo a pick that only a limited device made, so it gets the best match again.
    /// </summary>
    private async Task ApplyDeviceRuleAsync(ActionExecutingContext context, BaseItem item, MediaRequest request)
    {
        var (rule, device) = await DeviceRules.FindAsync(context.HttpContext, _services).ConfigureAwait(false);
        if (rule is null)
        {
            if (_resolver.ForgetDeviceRulePick(request))
            {
                ClearSavedTracks(item);
                _logger.LogInformation("{Item}: back to the best match on {Device} (the last pick was limited for another device)", item.Name, device?.ToString() ?? "this device");
            }

            return;
        }

        if (!_options.TryGetCached(request, out var releases))
        {
            releases = await _options.GetAsync(request, context.HttpContext.RequestAborted).WaitAsync(PlayBudget).ConfigureAwait(false);
        }

        var auto = _resolver.AutoRelease(request, releases);
        if (auto is null || DeviceRules.Allows(rule, auto))
        {
            return; // Auto's pick already fits
        }

        var fits = DeviceRules.BestWithin(rule, releases);
        if (fits is null)
        {
            _logger.LogInformation("{Item}: no release is {Limit}, so {Device} plays Auto's usual pick", item.Name, DeviceRules.Describe(rule), device);
            return;
        }

        var id = StreamOptions.IdFor(fits);
        if (!_resolver.IsCurrentPick(request, id))
        {
            ClearSavedTracks(item);
        }

        _resolver.Pin(request, id, byDeviceRule: true);
        _logger.LogInformation("{Item} on {Device}: playing {Release} ({Limit})", item.Name, device, fits.Title, DeviceRules.Describe(rule));
    }

    /// <summary>
    /// For a torrent, Jellyfin can only show an embedded text subtitle by first reading the whole file to extract it,
    /// which means downloading the entire release before playback starts. Releases often mark a subtitle as default or
    /// forced (e.g. Italian forced subtitles), which Jellyfin then picks automatically. So: when no subtitle is asked for,
    /// ask for none; when the asked-for subtitle is the release's own default/forced text track (the automatic pick), drop
    /// it. A subtitle the user picks still works (slowly, as explained in the log).
    /// </summary>
    private void SkipAutomaticSubtitle(ActionExecutingContext context, BaseItem item)
    {
        var bodies = context.ActionArguments.Values
            .Where(v => v?.GetType().GetProperty("SubtitleStreamIndex") is { CanWrite: true } p && (p.PropertyType == typeof(int?) || p.PropertyType == typeof(int)))
            .ToList();
        var hasQuery = context.ActionArguments.TryGetValue("subtitleStreamIndex", out var q);
        int? requested = hasQuery ? q as int? : null;
        requested ??= bodies.Select(b => b!.GetType().GetProperty("SubtitleStreamIndex")!.GetValue(b) as int?).FirstOrDefault(i => i.HasValue);

        if (requested is int index && index >= 0)
        {
            var streams = _services.GetService<IMediaSourceManager>()?.GetMediaStreams(item.Id) ?? [];
            var stream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == index);
            if (stream is null || stream.IsExternal || !stream.IsTextSubtitleStream || !(stream.IsDefault || stream.IsForced))
            {
                if (stream is { IsExternal: false, IsTextSubtitleStream: true })
                {
                    _logger.LogInformation("{Item}: subtitle {Language} chosen; Jellyfin reads the whole torrent to extract it before playback starts", item.Name, stream.Language);
                }

                return; // a subtitle someone picked (or one that needs no extraction): keep it
            }

            _logger.LogInformation("{Item}: skipped the release's automatic {Language} subtitle (showing it would mean downloading the whole torrent first)", item.Name, stream.Language);
        }
        else if (requested is not null)
        {
            return; // already "none"
        }

        if (hasQuery)
        {
            context.ActionArguments["subtitleStreamIndex"] = -1;
        }

        foreach (var body in bodies)
        {
            body!.GetType().GetProperty("SubtitleStreamIndex")!.SetValue(body, -1);
        }
    }

    private void ClearSavedTracks(BaseItem item)
    {
        var repository = _services.GetService<IMediaStreamRepository>();
        if (repository is null)
        {
            return;
        }

        repository.SaveMediaStreams(item.Id, Array.Empty<MediaStream>(), CancellationToken.None);
    }

    private static string Short(string s) => s.Length > 70 ? s[..67] + "..." : s;

    /// <summary>
    /// "Best 4K / Best 1080p / ..." entries for the Version menu: one per resolution found, each standing for that
    /// resolution's best-ranked release. Only shown when there's more than one resolution to choose from.
    /// </summary>
    private List<MediaSourceInfo> BestPerResolution(BaseItem item, string url, List<ReleaseCandidate> releases)
    {
        var groups = releases.Where(r => r.Resolution > 0).GroupBy(r => r.Resolution).OrderByDescending(g => g.Key).ToList();
        if (groups.Count < 2)
        {
            return [];
        }

        var list = new List<MediaSourceInfo>();
        foreach (var g in groups)
        {
            var best = g.First();
            var source = StreamPicker.BuildSources(item, url, [best], _options)[0];
            source.Id = StreamPicker.RegisterVersion(item, "res" + g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            source.ETag = "p2pres" + g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var label = g.Key == 2160 ? "4K" : g.Key + "p";
            var size = best.Size > 0 ? $" · {best.Size / (double)(1L << 30):0.0} GB" : string.Empty;
            var seeds = best.Seeders >= 0 ? $" · {best.Seeders} seeds" : string.Empty;
            source.Name = $"Best {label} · {g.Count()} available{seeds}{size}";
            list.Add(source);
        }

        return list;
    }

    [GeneratedRegex(@"Client=""([^""]*)""")]
    private static partial Regex ClientName();

    [GeneratedRegex(@"^p2pres(\d{3,4})$")]
    private static partial Regex ResolutionChoice();

    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}$")]
    private static partial Regex ItemPath();

    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}/PlaybackInfo$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaybackInfoPath();
}
