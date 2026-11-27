using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.P2PStream.Torrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonoTorrent;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Offers the subtitle files that come with a release (.srt, .ass, .ssa, .vtt) in Jellyfin's subtitle menu. They're
/// saved next to the title's .strm file as "&lt;name&gt;.&lt;language&gt;[.forced][.sdh].srt", which Jellyfin picks up
/// in the stream check it runs on every play. Unlike subtitles inside the video, these don't need the whole release
/// downloaded first. When another release plays, its own subtitles replace the previous ones.
/// </summary>
public sealed partial class TorrentSubtitles
{
    private const long MaxSubtitleBytes = 5L << 20;
    private const int MaxSubtitles = 30;
    private static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".vtt"];
    private static readonly Lazy<Dictionary<string, string>> Languages = new(BuildLanguages);
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "forced", "sdh", "hi", "cc", "full", "default" };

    private readonly StreamResolver _resolver;
    private readonly TorrentEngine _engine;
    private readonly IServiceProvider _services;
    private readonly ILogger<TorrentSubtitles> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _running = new();

    public TorrentSubtitles(StreamResolver resolver, TorrentEngine engine, IServiceProvider services, ILogger<TorrentSubtitles> logger)
    {
        _resolver = resolver;
        _engine = engine;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Makes sure the subtitles of the release this title will play are next to its .strm file. Waits up to
    /// <paramref name="wait"/>; the work carries on in the background if it takes longer.
    /// </summary>
    /// <param name="releaseId">A particular release (what a device with limits will play) instead of Auto's pick.</param>
    public Task EnsureAsync(BaseItem item, MediaRequest request, TimeSpan wait, CancellationToken ct, string? releaseId = null)
        => RunAsync(item, async token => await _resolver.ResolveAsync(request, releaseId, token, remember: releaseId is null).ConfigureAwait(false), wait, ct);

    /// <summary>The same, for a torrent file you added yourself (its .strm links straight to the torrent).</summary>
    public Task EnsureForTorrentAsync(BaseItem item, string infoHash, int fileIndex, TimeSpan wait, CancellationToken ct)
        => RunAsync(
            item,
            async token =>
            {
                var session = await _engine.GetOrRestoreAsync(infoHash, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("that torrent is no longer available");
                return new ResolvedStream(session, fileIndex);
            },
            wait,
            ct);

    private async Task RunAsync(BaseItem item, Func<CancellationToken, Task<ResolvedStream>> resolve, TimeSpan wait, CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.TorrentSubtitles != true || string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        // One at a time per title, but every call gets its own turn: if another version was picked while the previous
        // one's subtitles were being fetched, this turn fetches the new one's (a turn for the same release ends at once).
        var gate = _running.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        var task = Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var resolved = await resolve(timeout.Token).ConfigureAwait(false);
                await WriteAsync(item, resolved, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Subtitles for {Item} not added: {Message}", item.Name, ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await task.WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // still fetching: they'll be there for the next play
        }
    }

    private async Task WriteAsync(BaseItem item, ResolvedStream resolved, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(item.Path)!;
        var baseName = Path.GetFileNameWithoutExtension(item.Path);
        var statePath = Path.Combine(dir, "." + baseName + ".p2psubs.json");
        var manager = resolved.Session.Manager;
        var release = $"{manager.InfoHashes.V1OrV2.ToHex()}:{resolved.FileIndex}";

        var state = ReadState(statePath);
        if (state.Release == release)
        {
            return; // already done for this release
        }

        var subs = PickSubtitles(manager.Files, resolved.FileIndex);
        var ours = new HashSet<string>(state.Files, StringComparer.OrdinalIgnoreCase);

        // Names already taken by files the plugin didn't write (your own subtitles) are never used or touched.
        var used = new HashSet<string>(
            Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(n => n is not null && !ours.Contains(n))!,
            StringComparer.OrdinalIgnoreCase);

        // Write the new set first; the previous release's files are only removed once it's in place.
        var written = new List<string>();
        var failed = 0;
        foreach (var file in subs)
        {
            try
            {
                var bytes = await ReadAllAsync(resolved.Session, file, ct).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    failed++;
                    continue;
                }

                var name = TargetName(baseName, file.Path, manager.Files[resolved.FileIndex].Path, used);
                await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes, ct).ConfigureAwait(false);
                written.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogInformation("Subtitle {File} not added: {Message}", file.Path, ex.Message);
            }
        }

        // Only files this plugin wrote before, and that the new set didn't reuse.
        foreach (var old in state.Files.Where(o => !written.Contains(o, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                File.Delete(Path.Combine(dir, old));
            }
            catch (IOException)
            {
            }
        }

        // Marked as done for this release only if every file was written; otherwise the next play tries again.
        File.WriteAllText(statePath, JsonSerializer.Serialize(new State(failed == 0 ? release : null, written)));

        // Jellyfin caches folder listings: make it look again, so its next check of the title finds them.
        _services.GetService<IDirectoryService>()?.GetFilePaths(dir, true);

        if (written.Count > 0)
        {
            _logger.LogInformation("{Item}: {Count} subtitle file(s) from the release added ({Names})", item.Name, written.Count, string.Join(", ", written.Select(n => n[(baseName.Length + 1)..])));
        }
        else
        {
            if (state.Files.Count > 0)
            {
                _logger.LogInformation("{Item}: the release ({Release}) has no subtitle files; the previous release's were removed", item.Name, manager.Torrent?.Name ?? release);
            }
            else
            {
                _logger.LogInformation("{Item}: the release ({Release}) has no subtitle files", item.Name, manager.Torrent?.Name ?? release);
            }
        }
    }

    /// <summary>The subtitle files that belong to the video being played.</summary>
    public static List<ITorrentManagerFile> PickSubtitles(IList<ITorrentManagerFile> files, int videoIndex)
    {
        var subs = files.Where(f => Extensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase) && f.Length is > 0 and <= MaxSubtitleBytes).ToList();
        if (subs.Count == 0)
        {
            return subs;
        }

        var videos = files.Where(f => TorrentEngine.IsVideo(f.Path)).ToList();
        if (videos.Count > 1)
        {
            // A season pack (or several videos): only the ones named after this video, or in a folder named after it
            // ("Subs/<video name>/2_English.srt"), or for its episode.
            var video = files[videoIndex].Path;
            var stem = Path.GetFileNameWithoutExtension(video);
            var episode = EpisodeTag().Match(stem);
            subs = subs.Where(s => s.Path.Contains(stem, StringComparison.OrdinalIgnoreCase)
                    || (episode.Success && EpisodeTag().Matches(s.Path).Any(m => string.Equals(m.Value, episode.Value, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        return subs.Take(MaxSubtitles).ToList();
    }

    /// <summary>"&lt;base&gt;.&lt;language&gt;[.forced][.sdh][.n].&lt;ext&gt;", the way Jellyfin reads languages and flags from names.</summary>
    public static string TargetName(string baseName, string subtitlePath, string videoPath, HashSet<string> used)
    {
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        var videoStem = Path.GetFileNameWithoutExtension(videoPath);
        if (name.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            name = name[videoStem.Length..]; // "Movie.2019.1080p.eng.forced" -> ".eng.forced"
        }

        // The file name, then its folder ("Subs/English/1.srt").
        var words = Words().Matches(name).Select(m => m.Value).ToList();
        var folderWords = Words().Matches(Path.GetFileName(Path.GetDirectoryName(subtitlePath) ?? string.Empty)).Select(m => m.Value).ToList();
        var language = FindLanguage(words) ?? FindLanguage(folderWords);
        var all = words.Concat(folderWords).Select(w => w.ToLowerInvariant()).ToList();
        var forced = all.Contains("forced");
        var sdh = all.Contains("sdh") || all.Contains("cc") || (all.Contains("hi") && language is not null && language != "hi");

        var parts = new List<string> { baseName };
        if (language is not null)
        {
            parts.Add(language);
        }

        if (forced)
        {
            parts.Add("forced");
        }

        if (sdh)
        {
            parts.Add("sdh");
        }

        var ext = Path.GetExtension(subtitlePath).ToLowerInvariant();
        var candidate = string.Join('.', parts) + ext;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = string.Join('.', parts) + "." + n.ToString(CultureInfo.InvariantCulture) + ext;
        }

        return candidate;
    }

    /// <summary>A language named in the words, as a two-letter code (the last match wins, as it's nearest the extension).</summary>
    private static string? FindLanguage(List<string> words)
    {
        // Flags after the language ("en.forced", "en.hi") don't count as the last word.
        words = words.ToList();
        while (words.Count > 0 && Flags.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        for (var i = words.Count - 1; i >= 0; i--)
        {
            var w = words[i];

            // Two-letter codes only as the last word ("Movie.en"), so titles like "It" don't read as Italian.
            if (w.Length == 2 && i != words.Count - 1)
            {
                continue;
            }

            if (Languages.Value.TryGetValue(w, out var code))
            {
                return code;
            }
        }

        return null;
    }

    private static Dictionary<string, string> BuildLanguages()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var two = c.TwoLetterISOLanguageName;
            if (two.Length != 2 || c.Equals(CultureInfo.InvariantCulture))
            {
                continue;
            }

            map.TryAdd(two, two);
            map.TryAdd(c.ThreeLetterISOLanguageName, two);
            if (Words().Match(c.EnglishName) is { Success: true, Value.Length: > 3 } first)
            {
                map.TryAdd(first.Value, two); // "English", "Portuguese", "Norwegian" (from "Norwegian Bokmål")
            }
        }

        // Common languages by name, in case this system has no culture data, plus the ISO 639-2/B codes and words
        // that release groups use.
        foreach (var (word, code) in new[]
        {
            ("english", "en"), ("spanish", "es"), ("french", "fr"), ("german", "de"), ("italian", "it"), ("portuguese", "pt"),
            ("dutch", "nl"), ("russian", "ru"), ("polish", "pl"), ("swedish", "sv"), ("norwegian", "no"), ("danish", "da"),
            ("finnish", "fi"), ("turkish", "tr"), ("arabic", "ar"), ("hebrew", "he"), ("hindi", "hi"), ("japanese", "ja"),
            ("korean", "ko"), ("chinese", "zh"), ("greek", "el"), ("czech", "cs"), ("hungarian", "hu"), ("romanian", "ro"),
            ("thai", "th"), ("vietnamese", "vi"), ("indonesian", "id"), ("malay", "ms"), ("ukrainian", "uk"), ("bulgarian", "bg"),
            ("croatian", "hr"), ("serbian", "sr"), ("slovenian", "sl"), ("slovak", "sk"), ("estonian", "et"), ("latvian", "lv"),
            ("lithuanian", "lt"), ("persian", "fa"), ("icelandic", "is"), ("catalan", "ca"), ("filipino", "fil"),
            ("eng", "en"), ("spa", "es"), ("ger", "de"), ("fre", "fr"), ("dut", "nl"), ("chi", "zh"), ("cze", "cs"), ("gre", "el"),
            ("rum", "ro"), ("slo", "sk"), ("per", "fa"), ("brazilian", "pt"), ("castilian", "es"), ("latino", "es"),
            ("simplified", "zh"), ("traditional", "zh"), ("mandarin", "zh"), ("cantonese", "zh"),
        })
        {
            map[word] = code;
        }

        // Words that are (parts of) language names but mean something else in release names.
        foreach (var word in new[] { "sub", "subs", "forced", "full", "sdh", "cc", "hi", "web", "rip", "standard", "central", "northern", "southern", "western", "eastern", "upper", "lower", "swiss", "old", "middle", "ancient", "modern", "classical", "plains", "sign", "the", "and" })
        {
            map.Remove(word);
        }

        return map;
    }

    private static async Task<byte[]> ReadAllAsync(TorrentSession session, ITorrentManagerFile file, CancellationToken ct)
    {
        await using var lease = await session.OpenAsync(file, ct).ConfigureAwait(false);
        lease.Position = 0;
        var data = new byte[file.Length];
        var done = 0;
        while (done < data.Length)
        {
            var n = await lease.ReadAsync(data.AsMemory(done), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            done += n;
        }

        return done == data.Length ? data : [];
    }

    private static State ReadState(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State(null, []) : new State(null, []);
        }
        catch (Exception)
        {
            return new State(null, []);
        }
    }

    private sealed record State(string? Release, List<string> Files);

    /// <summary>The subtitle files next to a title that the plugin wrote from its current release (file names).</summary>
    public static HashSet<string> OwnedFiles(string? strmPath)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (strmPath is not null && Path.GetDirectoryName(strmPath) is { } dir)
            {
                var statePath = Path.Combine(dir, "." + Path.GetFileNameWithoutExtension(strmPath) + ".p2psubs.json");
                if (File.Exists(statePath) && JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) is { } state)
                {
                    owned.UnionWith(state.Files);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return owned;
    }

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"S\d{1,2}E\d{1,3}", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeTag();
}
