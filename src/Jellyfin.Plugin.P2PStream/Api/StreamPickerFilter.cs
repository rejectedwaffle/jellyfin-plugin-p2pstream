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
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<StreamPickerFilter> _logger;

    public StreamPickerFilter(StreamOptions options, StreamResolver resolver, StreamProber prober, ILibraryManager libraryManager, IServiceProvider services, ILogger<StreamPickerFilter> logger)
    {
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

        var executed = await next().ConfigureAwait(false);

        if (enabled && HttpMethods.IsGet(http.Request.Method) && ItemPath().IsMatch(path))
        {
            try
            {
                await AddVersionsAsync(http, executed).ConfigureAwait(false);
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

        _prober.ProbeTopInBackground(item, url, releases);

        // Auto plays the remembered pick, or else tries the top-ranked release first. Name it, and if that release has been
        // checked, show its tracks on Auto too (Jellyfin otherwise only knows them after Auto has played once).
        var auto = dto.MediaSources[0];
        var current = _resolver.CurrentPickTitle(request);
        var autoRelease = current is null ? releases[0] : releases.FirstOrDefault(r => r.Title == current);
        auto.Name = current is not null
            ? "Auto (best match) — now: " + Short(current)
            : "Auto (best match) — tries first: " + Short(releases[0].Title);
        if (auto.MediaStreams is not { Count: > 0 } && autoRelease is not null && _options.TryGetProbe(StreamOptions.IdFor(autoRelease), out var probe))
        {
            auto.MediaStreams = probe.Streams;
            auto.RunTimeTicks ??= probe.RunTimeTicks;
            auto.DefaultAudioStreamIndex ??= (probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                ?? probe.Streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio))?.Index;
        }
        dto.MediaSources = new[] { auto }.Concat(StreamPicker.BuildSources(item, url, releases, _options)).ToArray();
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
        if (string.IsNullOrEmpty(chosen))
        {
            return; // Auto
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !StreamPicker.TryGetRequest(item, out _, out var request))
        {
            return;
        }

        var staticId = item.Id.ToString("N");
        if (string.Equals(chosen, staticId, StringComparison.OrdinalIgnoreCase))
        {
            return; // Auto
        }

        if (!_options.TryGetCached(request, out var releases))
        {
            releases = await _options.GetAsync(request, context.HttpContext.RequestAborted).WaitAsync(PlayBudget).ConfigureAwait(false);
        }

        var release = releases.FirstOrDefault(r => StreamOptions.IdFor(r) == chosen);
        if (release is null)
        {
            return; // not one of ours
        }

        // A different release than last time: forget the saved tracks so Jellyfin reads the new release's tracks.
        if (!_resolver.IsCurrentPick(request, chosen))
        {
            ClearSavedTracks(item);
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

    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}$")]
    private static partial Regex ItemPath();

    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}/PlaybackInfo$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaybackInfoPath();
}
