using System.Collections.Concurrent;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// When a title is played and the release has no subtitle in the plugin's language (none built in, no subtitle file
/// next to it, none Jellyfin already has), asks Jellyfin's subtitle providers (for example the OpenSubtitles plugin)
/// for one. Only for what's actually played, and at most once per title and release every 12 hours, so free download
/// limits go to the titles people watch.
/// </summary>
public sealed class MissingSubtitles
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(12);
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi"];

    private readonly ISubtitleManager _subtitles;
    private readonly ILibraryManager _libraryManager;
    private readonly StreamOptions _options;
    private readonly StreamResolver _resolver;
    private readonly IServiceProvider _services;
    private readonly ILogger<MissingSubtitles> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _tried = new();

    public MissingSubtitles(ISubtitleManager subtitles, ILibraryManager libraryManager, StreamOptions options, StreamResolver resolver, IServiceProvider services, ILogger<MissingSubtitles> logger)
    {
        _subtitles = subtitles;
        _libraryManager = libraryManager;
        _options = options;
        _resolver = resolver;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Called when a title is played (after the release's own subtitle files are in place). Waits up to
    /// <paramref name="wait"/> so a subtitle that arrives quickly is in this play; a slower one is there next time.
    /// </summary>
    public async Task EnsureAsync(BaseItem item, MediaRequest request, TimeSpan wait, CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.FetchMissingSubtitles != true || item is not Video video || string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        var (two, three, english) = Language();
        var releaseId = CurrentReleaseId(request);
        if (HasLanguage(item, request, releaseId, two, three, english))
        {
            return;
        }

        var key = item.Id.ToString("N") + "|" + (releaseId ?? "?");
        if (_tried.TryGetValue(key, out var at) && DateTime.UtcNow - at < RetryAfter)
        {
            return;
        }

        _tried[key] = DateTime.UtcNow;
        var fetch = FetchAsync(video, three, english);
        try
        {
            await fetch.WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("{Item}: still looking for a {Language:l} subtitle; it will be there next time if one is found", item.Name, english);
        }
        catch (OperationCanceledException)
        {
            // the app gave up on this request; the download carries on
        }
    }

    private async Task FetchAsync(Video video, string language, string languageName)
    {
        try
        {
            var found = await _subtitles.SearchSubtitles(video, language, null, true, CancellationToken.None).ConfigureAwait(false);
            var best = found
                .OrderByDescending(s => s.IsHashMatch == true)
                .ThenByDescending(s => s.DownloadCount ?? 0)
                .FirstOrDefault();
            if (best is null)
            {
                _logger.LogInformation(
                    "{Item}: no {Language:l} subtitle found{Why:l}",
                    video.Name,
                    languageName,
                    _subtitles.GetSupportedProviders(video).Any() ? string.Empty : " (no subtitle provider is installed; add one such as OpenSubtitles under Dashboard > Plugins)");
                return;
            }

            await _subtitles.DownloadSubtitles(video, _libraryManager.GetLibraryOptions(video), best.Id, CancellationToken.None).ConfigureAwait(false);

            // Jellyfin caches folder listings: make it look again, so its check of the title finds the new file.
            if (Path.GetDirectoryName(video.Path) is { } dir)
            {
                _services.GetService<IDirectoryService>()?.GetFilePaths(dir, true);
            }

            _logger.LogInformation("{Item}: added a {Language:l} subtitle from {Provider:l}", video.Name, languageName, best.ProviderName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Item}: could not fetch a {Language:l} subtitle: {Message}", video.Name, languageName, ex.Message);
        }
    }

    /// <summary>The release that will play: a chosen version (pinned just before this), or Auto's pick.</summary>
    private string? CurrentReleaseId(MediaRequest request)
    {
        if (!_options.TryGetCached(request, out var releases) || releases.Count == 0)
        {
            return null;
        }

        var release = _resolver.PinnedRelease(request, releases) ?? _resolver.AutoRelease(request, releases);
        return release is null ? null : StreamOptions.IdFor(release);
    }

    /// <summary>
    /// True if a subtitle in the language is already there: built into the release (when it was checked), a subtitle
    /// file next to the title, or an external subtitle Jellyfin knows about (one it downloaded, for example).
    /// </summary>
    private bool HasLanguage(BaseItem item, MediaRequest request, string? releaseId, string two, string three, string name)
    {
        bool Matches(string? language) =>
            !string.IsNullOrEmpty(language)
            && (language.Equals(two, StringComparison.OrdinalIgnoreCase) || language.Equals(three, StringComparison.OrdinalIgnoreCase) || language.Equals(name, StringComparison.OrdinalIgnoreCase));

        // Built into the release, if it was checked.
        if (releaseId is not null && _options.TryGetProbe(StreamOptions.ProbeKey(request, releaseId), out var probe)
            && probe.Streams.Any(s => s.Type == MediaStreamType.Subtitle && Matches(s.Language)))
        {
            return true;
        }

        // Subtitles Jellyfin already lists for the title: external ones always count; built-in ones only if the
        // release playing is the one those tracks came from.
        var known = item.GetMediaStreams() ?? [];
        if (known.Any(s => s.Type == MediaStreamType.Subtitle && Matches(s.Language)
            && (s.IsExternal || (releaseId is not null && _resolver.IsCurrentPick(request, releaseId)))))
        {
            return true;
        }

        // Subtitle files next to the title, named with the language (Title.en.srt, Title.eng.forced.srt, ...).
        var dir = Path.GetDirectoryName(item.Path);
        if (dir is null || !Directory.Exists(dir))
        {
            return false;
        }

        return Directory.EnumerateFiles(dir)
            .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Any(f => Path.GetFileNameWithoutExtension(f).Split('.', '_', '-', ' ', '[', ']', '(', ')').Any(Matches));
    }

    /// <summary>The plugin's language (Accounts > TMDB > Language, e.g. en-US) as two- and three-letter codes and its English name.</summary>
    private static (string Two, string Three, string Name) Language()
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(Plugin.Instance?.Configuration.Language ?? "en-US");
            var neutral = culture.IsNeutralCulture ? culture : culture.Parent;
            return (culture.TwoLetterISOLanguageName, culture.ThreeLetterISOLanguageName, neutral.EnglishName);
        }
        catch (CultureNotFoundException)
        {
            return ("en", "eng", "English");
        }
    }
}
