using System.Text.RegularExpressions;
using Jellyfin.Plugin.P2PStream.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
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
    private readonly UserPicks _userPicks;
    private readonly MissingSubtitles _missingSubtitles;
    private readonly StartStatus _startStatus;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<StreamPickerFilter> _logger;

    public StreamPickerFilter(StreamOptions options, StreamResolver resolver, StreamProber prober, TorrentSubtitles subtitles, UserPicks userPicks, MissingSubtitles missingSubtitles, StartStatus startStatus, ILibraryManager libraryManager, IServiceProvider services, ILogger<StreamPickerFilter> logger)
    {
        _startStatus = startStatus;
        _missingSubtitles = missingSubtitles;
        _userPicks = userPicks;
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

        // A Play request: only for a title the requesting user may see (Jellyfin checks that itself, but only after this
        // filter; nothing may start a torrent or write files for an item the user can't access).
        BaseItem? playItem = null;
        if (PlaybackInfoPath().IsMatch(path) && context.ActionArguments.TryGetValue("itemId", out var playArg) && playArg is Guid playId)
        {
            playItem = await VisibleItemAsync(http, playId).ConfigureAwait(false);
            if (playItem is null)
            {
                await next().ConfigureAwait(false); // not ours to prepare: let Jellyfin answer (and refuse) as usual
                return;
            }
        }

        // While Jellyfin answers the Play request, its forced re-check of the stream gets only the start of the release
        // (see PlaybackInfoWindow). Held from here until Jellyfin has answered, whatever happens in between.
        MediaRequest? playRequest = playItem is not null && StreamPicker.TryGetRequest(playItem, out _, out var foundRequest) ? foundRequest : null;
        using var checkWindow = playRequest is not null ? PlaybackInfoWindow.Begin(playRequest.Key) : null;

        // Whoever pressed Play sees short messages while the title starts (see StartStatus).
        if (playRequest is not null)
        {
            await WatchStartAsync(http, playRequest).ConfigureAwait(false);
        }

        if (enabled && playItem is not null)
        {
            try
            {
                await UseChosenVersionAsync(context, playItem).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not apply the chosen version: {Message}", ex.Message);
            }
        }

        // The release's own subtitle files, next to the .strm before Jellyfin's per-play check looks for them. After the
        // chosen version is pinned, so they're that release's.
        if (playItem is not null && playRequest is not null)
        {
            await _subtitles.EnsureAsync(playItem, playRequest, TimeSpan.FromSeconds(12), http.RequestAborted).ConfigureAwait(false);

            // No subtitle in the plugin's language: ask Jellyfin's subtitle providers for one (only for what's played).
            await _missingSubtitles.EnsureAsync(playItem, playRequest, TimeSpan.FromSeconds(8), http.RequestAborted).ConfigureAwait(false);
        }
        else if (playItem is not null && StreamPicker.TryGetTorrentFile(playItem, out var addedHash, out var addedIndex))
        {
            await _subtitles.EnsureForTorrentAsync(playItem, addedHash, addedIndex, TimeSpan.FromSeconds(12), http.RequestAborted).ConfigureAwait(false);
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

        var executed = await next().ConfigureAwait(false);

        if (playItem is not null && executed.Result is ObjectResult { Value: PlaybackInfoResponse info })
        {
            UseRequestAddress(http.Request, info);
            UsePickedSubtitleFile(http, info);
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

    /// <summary>
    /// Apps that play a stream directly use the link in the Play answer. With no fixed address set (Stream links), that
    /// link uses the address this app reached Jellyfin by: the home address for a device at home, the reverse proxy's
    /// name for a device away from home. The .strm files keep the home address (Jellyfin itself reads them there).
    /// </summary>
    internal static void UseRequestAddress(HttpRequest request, PlaybackInfoResponse info)
    {
        var baseUrl = RequestBaseUrl(request);
        if (baseUrl is null || info.MediaSources is null)
        {
            return;
        }

        foreach (var source in info.MediaSources)
        {
            if (!string.IsNullOrEmpty(source.Path))
            {
                source.Path = LinkBase().Replace(source.Path, baseUrl + "/P2PStream/", 1);
            }
        }
    }

    /// <summary>The address the request came in on, or null when a fixed address is set or the request has no host.</summary>
    internal static string? RequestBaseUrl(HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.PublicBaseUrl) || !request.Host.HasValue
            || (request.Scheme != "http" && request.Scheme != "https"))
        {
            return null;
        }

        return $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}".TrimEnd('/');
    }

    // ---------------- title page: list the versions ----------------
    private async Task AddVersionsAsync(HttpContext http, ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: BaseItemDto dto } || dto.MediaSources is not { Length: > 0 })
        {
            return;
        }

        var item = await VisibleItemAsync(http, dto.Id).ConfigureAwait(false);
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
                // Still searching; the versions show up next time the page is opened (and this person is told when).
                if (await SessionIdAsync(http).ConfigureAwait(false) is { } waiting)
                {
                    _startStatus.WatchPage(request, waiting, listShown: false);
                }

                return;
            }
        }

        if (releases.Count == 0)
        {
            return;
        }

        // The list shows now, but its live seeder counts are still being checked: say so, and say when it's complete.
        if (_options.IsCheckingLive(request) && await SessionIdAsync(http).ConfigureAwait(false) is { } viewer)
        {
            _startStatus.WatchPage(request, viewer, listShown: true);
        }

        // This person's own choice for the title, if they made one (and it's still listed).
        var userId = await UserIdAsync(http).ConfigureAwait(false);
        var ownId = userId is null ? null : _userPicks.Get(userId, request.Key);
        var own = ownId is null ? null : releases.FirstOrDefault(r => StreamOptions.IdFor(r) == ownId);

        if (Plugin.Instance?.Configuration.PrewarmOnOpen == true)
        {
            _ = _subtitles.EnsureAsync(item, request, TimeSpan.Zero, CancellationToken.None); // ready before Play is pressed
        }

        // Auto plays the remembered pick, or else tries the top-ranked release first. Name it, and if that release has been
        // checked, show its tracks on Auto too (Jellyfin otherwise only knows them after Auto has played once).
        // A device with limits (Quality > Per-device limits) gets the best release within them instead.
        var auto = dto.MediaSources[0];
        var current = own is not null ? ReleaseRanker.CleanTitle(own.Title) : _resolver.CurrentPickTitleFor(request, userId);
        var autoRelease = current is null ? releases[0] : releases.FirstOrDefault(r => r.Title == current);
        var (rule, _) = await DeviceRules.FindAsync(http, _services).ConfigureAwait(false);
        ReleaseCandidate? deviceFits = null;
        if (rule is not null && (autoRelease is null || !DeviceRules.Allows(rule, autoRelease)) && DeviceRules.BestWithin(rule, releases) is { } fits)
        {
            deviceFits = fits;
            autoRelease = fits;
            auto.Name = $"Auto ({DeviceRules.Describe(rule)} on this device) — plays: " + ReleaseRanker.CleanTitle(fits.Title);
        }
        else
        {
            auto.Name = own is not null
                ? "Auto (your pick) — now: " + current
                : current is not null
                ? "Auto (best match) — now: " + current
                : "Auto (best match) — tries first: " + ReleaseRanker.CleanTitle(releases[0].Title);
        }

        // Start loading what this device will play: this person's pick, else the best release within the device's
        // limits, else Auto's pick (not a 4K release a 1080p-limited TV won't use).
        // A device that is playing something asks for the next episode's details for its "Up next" card: that's not
        // someone about to press Play, so it doesn't start two releases alongside what's playing.
        var busy = await IsPlayingAsync(http).ConfigureAwait(false);
        _resolver.Prewarm(request, releaseId: own is not null ? ownId : deviceFits is not null ? StreamOptions.IdFor(deviceFits) : null, race: !busy);

        _prober.ProbeTopInBackground(item, url, releases, autoRelease);

        if (auto.MediaStreams is not { Count: > 0 } && autoRelease is not null && _options.TryGetProbe(StreamOptions.ProbeKey(request, StreamOptions.IdFor(autoRelease)), out var probe))
        {
            auto.MediaStreams = probe.Streams;
            auto.RunTimeTicks ??= probe.RunTimeTicks;
            auto.DefaultAudioStreamIndex ??= (probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                ?? probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio))?.Index;
        }
        dto.MediaSources = new[] { auto }
            .Concat(BestPerResolution(item, url, releases))
            .Concat(StreamPicker.BuildSources(item, url, ByResolution(releases), _options))
            .ToArray();
        dto.MediaSourceCount = dto.MediaSources.Length;

        // Apps only use a version's id (Play is resolved on the server); don't hand them the stream token with it.
        foreach (var source in dto.MediaSources)
        {
            source.Path = StreamUrlBuilder.Redact(source.Path);
        }
    }

    // ---------------- playback: honour the chosen version ----------------
    /// <summary>
    /// The item, only if the user making the request may see it (the same check Jellyfin applies); null for anyone
    /// else, and for requests without a signed-in user.
    /// </summary>
    private async Task<BaseItem?> VisibleItemAsync(HttpContext http, Guid id)
    {
        try
        {
            var auth = _services.GetService<MediaBrowser.Controller.Net.IAuthorizationContext>();
            var user = auth is null ? null : (await auth.GetAuthorizationInfo(http).ConfigureAwait(false)).User;
            // GetItemById with the user checks library access too (as Jellyfin's own endpoints do); BaseItem.IsVisible
            // only checks parental ratings and tags.
            return user is null ? null : _libraryManager.GetItemById<BaseItem>(id, user);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task UseChosenVersionAsync(ActionExecutingContext context, BaseItem item)
    {
        if (!context.ActionArguments.TryGetValue("itemId", out var idArg) || idArg is not Guid itemId)
        {
            return;
        }

        // The chosen version arrives as a query parameter, in the request body, or both.
        var bodies = context.ActionArguments.Values.Where(v => v?.GetType().GetProperty("MediaSourceId") is { PropertyType: var t } && t == typeof(string)).ToList();
        var chosen = context.ActionArguments.TryGetValue("mediaSourceId", out var q) ? q as string : null;
        chosen ??= bodies.Select(b => b!.GetType().GetProperty("MediaSourceId")!.GetValue(b) as string).FirstOrDefault(s => !string.IsNullOrEmpty(s));

        if (item.Id != itemId || !StreamPicker.TryGetRequest(item, out _, out var request))
        {
            return;
        }

        TakePickedSubtitleFile(context, item);
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
            await ApplyOwnPickAsync(context, item, request).ConfigureAwait(false);
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
            if (_options.TryGetProbe(StreamOptions.ProbeKey(request, chosen), out var probe) && probe.Streams is { Count: > 0 })
            {
                _services.GetService<IMediaStreamRepository>()?.SaveMediaStreams(item.Id, probe.Streams, CancellationToken.None);
            }
            else
            {
                ClearSavedTracks(item);
            }
        }

        var chooser = await UserIdAsync(context.HttpContext).ConfigureAwait(false);
        _resolver.Pin(request, chosen, chosenBy: chooser);
        if (chooser is not null)
        {
            _userPicks.Set(chooser, request.Key, chosen); // this person's Auto plays it from now on; other people's doesn't
        }

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
            var probe = await _prober.WaitForProbeAsync(StreamOptions.ProbeKey(request, id), TimeSpan.Zero, context.HttpContext.RequestAborted).ConfigureAwait(false);
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
    /// Auto, for the person asking: the version they chose for this title before, if they did; otherwise not a version
    /// someone else chose (they get the best match). Versions chosen before choices were per person stay shared.
    /// </summary>
    private async Task ApplyOwnPickAsync(ActionExecutingContext context, BaseItem item, MediaRequest request)
    {
        var userId = await UserIdAsync(context.HttpContext).ConfigureAwait(false);
        if (userId is null)
        {
            return;
        }

        var ownId = _userPicks.Get(userId, request.Key);
        if (ownId is null)
        {
            if (_resolver.ForgetOthersPick(request, userId))
            {
                ClearSavedTracks(item);
                _logger.LogInformation("{Item}: best match for this person (the last pick was someone else's choice)", item.Name);
            }

            return;
        }

        if (!_options.TryGetCached(request, out var releases))
        {
            releases = await _options.GetAsync(request, context.HttpContext.RequestAborted).WaitAsync(PlayBudget).ConfigureAwait(false);
        }

        var own = releases.FirstOrDefault(r => StreamOptions.IdFor(r) == ownId);
        if (own is null)
        {
            _userPicks.Remove(userId, request.Key); // no longer listed: Auto picks again
            _resolver.ForgetOthersPick(request, userId);
            return;
        }

        if (!_resolver.IsCurrentPick(request, ownId))
        {
            if (_options.TryGetProbe(StreamOptions.ProbeKey(request, ownId), out var probe) && probe.Streams is { Count: > 0 })
            {
                _services.GetService<IMediaStreamRepository>()?.SaveMediaStreams(item.Id, probe.Streams, CancellationToken.None);
            }
            else
            {
                ClearSavedTracks(item);
            }
        }

        _resolver.Pin(request, ownId, chosenBy: userId);
        _logger.LogInformation("{Item}: playing this person's chosen version {Release}", item.Name, own.Title);
    }

    /// <summary>Registers the session making a Play request for start-up messages about the title.</summary>
    private async Task WatchStartAsync(HttpContext http, MediaRequest request)
    {
        if (await SessionIdAsync(http).ConfigureAwait(false) is { } sessionId)
        {
            _startStatus.Watch(request, sessionId);
        }
    }

    /// <summary>True if the device making the request is playing something right now.</summary>
    private async Task<bool> IsPlayingAsync(HttpContext http)
    {
        var id = await SessionIdAsync(http).ConfigureAwait(false);
        var sessions = _services.GetService<MediaBrowser.Controller.Session.ISessionManager>();
        return id is not null && sessions?.Sessions.FirstOrDefault(s => s.Id == id)?.NowPlayingItem is not null;
    }

    /// <summary>The Jellyfin session (app on a device, signed in) making the request, or null.</summary>
    private async Task<string?> SessionIdAsync(HttpContext http)
    {
        try
        {
            var auth = _services.GetService<MediaBrowser.Controller.Net.IAuthorizationContext>();
            var sessions = _services.GetService<MediaBrowser.Controller.Session.ISessionManager>();
            var info = auth is null ? null : await auth.GetAuthorizationInfo(http).ConfigureAwait(false);
            if (info?.User is null || sessions is null || string.IsNullOrEmpty(info.DeviceId))
            {
                return null;
            }

            return sessions.Sessions.FirstOrDefault(s => s.DeviceId == info.DeviceId && s.UserId == info.User.Id)?.Id;
        }
        catch (Exception)
        {
            return null; // messages are a nicety
        }
    }

    /// <summary>
    /// The Version menu's order: highest resolution first (unknown last), and within a resolution Auto's ranking (best
    /// first). Auto itself still follows the ranking, which also weighs language, size and seeders across resolutions.
    /// The same torrent listed by two sites (known once its torrent id has been looked up) shows once, as the listing
    /// with the live (or more trustworthy) count; the other stays playable, so a pick of it still works.
    /// </summary>
    internal static List<ReleaseCandidate> ByResolution(IEnumerable<ReleaseCandidate> ranked)
    {
        var list = ranked.ToList();
        var hidden = list
            .Where(r => !string.IsNullOrEmpty(r.InfoHash))
            .GroupBy(r => r.InfoHash!.ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.OrderByDescending(r => r.LiveSeeders).ThenByDescending(ReleaseRanker.RankingSeeders).Skip(1))
            .ToHashSet();
        return list
            .Where(r => !hidden.Contains(r))
            .OrderByDescending(r => r.Resolution > 0 ? r.Resolution : -1) // a stable sort keeps the ranking within each
            .ToList();
    }

    /// <summary>The id of the Jellyfin user making the request (no dashes), or null (an API key, or not signed in).</summary>
    private async Task<string?> UserIdAsync(HttpContext http)
    {
        try
        {
            var auth = _services.GetService<MediaBrowser.Controller.Net.IAuthorizationContext>();
            var info = auth is null ? null : await auth.GetAuthorizationInfo(http).ConfigureAwait(false);
            return info?.User?.Id.ToString("N");
        }
        catch (Exception)
        {
            return null;
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

        var auto = _resolver.PinnedRelease(request, releases) ?? _resolver.AutoRelease(request, releases);
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
    private const string PickedSubtitleKey = "p2p.pickedSubtitleFile";

    /// <summary>
    /// A subtitle file picked in a version's Subtitles box carries a number of its own (its real number is only known
    /// after Jellyfin checks the release). Remember which file it is and ask for no subtitle for now; after Jellyfin
    /// answers, <see cref="UsePickedSubtitleFile"/> points the answer at that file's real track.
    /// </summary>
    private static void TakePickedSubtitleFile(ActionExecutingContext context, BaseItem item)
    {
        var bodies = context.ActionArguments.Values
            .Where(v => v?.GetType().GetProperty("SubtitleStreamIndex") is { CanWrite: true } p && (p.PropertyType == typeof(int?) || p.PropertyType == typeof(int)))
            .ToList();
        var hasQuery = context.ActionArguments.TryGetValue("subtitleStreamIndex", out var q);
        int? requested = hasQuery ? q as int? : null;
        requested ??= bodies.Select(b => b!.GetType().GetProperty("SubtitleStreamIndex")!.GetValue(b) as int?).FirstOrDefault(i => i.HasValue);
        if (requested is not int index || index < StreamPicker.VirtualSubtitleBase || !StreamPicker.TryGetVirtualSubtitle(item.Id, index, out var path))
        {
            return;
        }

        context.HttpContext.Items[PickedSubtitleKey] = path;
        if (hasQuery)
        {
            context.ActionArguments["subtitleStreamIndex"] = -1;
        }

        foreach (var body in bodies)
        {
            body!.GetType().GetProperty("SubtitleStreamIndex")!.SetValue(body, -1);
        }
    }

    /// <summary>Points Jellyfin's answer at the real track of the subtitle file picked on the title page.</summary>
    private void UsePickedSubtitleFile(HttpContext http, PlaybackInfoResponse info)
    {
        if (http.Items[PickedSubtitleKey] is not string path || info.MediaSources is null)
        {
            return;
        }

        foreach (var source in info.MediaSources)
        {
            var real = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            if (real is not null)
            {
                source.DefaultSubtitleStreamIndex = real.Index;
                _logger.LogDebug("Picked subtitle {File} is track {Index}", Path.GetFileName(path), real.Index);
            }
        }
    }

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
            var seeds = best.Seeders >= 0 ? $" · {best.Seeders} seeds{(best.LiveSeeders ? " live" : string.Empty)}" : string.Empty;
            source.Name = $"Best {label} · {g.Count()} available{seeds}{size} — {ReleaseRanker.CleanTitle(best.Title)}";
            list.Add(source);
        }

        return list;
    }

    // The start of one of the plugin's stream links, up to "/P2PStream/" (followed by play/ or stream/).
    [GeneratedRegex(@"^https?://[^\s/]+(?:/[^\s]*?)?/P2PStream/(?=(?:play|stream)/)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkBase();

    [GeneratedRegex(@"Client=""([^""]*)""")]
    private static partial Regex ClientName();

    [GeneratedRegex(@"^p2pres(\d{3,4})$")]
    private static partial Regex ResolutionChoice();

    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}$")]
    private static partial Regex ItemPath();

    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}/PlaybackInfo$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaybackInfoPath();
}
