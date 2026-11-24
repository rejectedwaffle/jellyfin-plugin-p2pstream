using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Filters and orders search results according to the user's preferences.
/// </summary>
public static partial class ReleaseRanker
{
    /// <summary>Release types, as shown on the config page. Order matters: the first match wins.</summary>
    public static readonly (string Id, string Label, Regex Pattern)[] ReleaseTypes =
    [
        ("cam", "Cam/HDCam", new Regex(@"\b(cam|camrip|hdcam|cam-?rip)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("telesync", "TeleSync/TS/TC", new Regex(@"\b(ts|telesync|hd-?ts|tc|telecine|hd-?tc|pdvd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("dvdscr", "DVDScr", new Regex(@"\b(dvdscr|dvd-?screener)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("screener", "Screener/SCR", new Regex(@"\b(scr|screener|bdscr|webscr)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("3d", "3D", new Regex(@"\b(3d|sbs|h-?sbs|half-?ou)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("bluray", "BRRip/BDRip/BluRay", new Regex(@"\b(blu-?ray|bdrip|brrip|bdremux|remux|bd25|bd50|bdmv)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("webdl", "WEB-DL/WEBRip", new Regex(@"\b(web-?dl|webrip|web-?rip|web)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("hdrip", "HDRip", new Regex(@"\bhd-?rip\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("hdtv", "HDTV", new Regex(@"\b(hdtv|pdtv|sdtv|dsr|tvrip)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("dvdrip", "DVDRip", new Regex(@"\b(dvdrip|dvd-?rip|dvd5|dvd9|dvd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    /// <summary>Bump when the ranking rules change, so picks Auto made under the old rules are made again.</summary>
    private const int RulesVersion = 2; // 2: foreign-language releases rank lower (0.9.4)

    /// <summary>
    /// Identifies the rules and settings that decide Auto's pick. A pick Auto remembered under a different stamp is
    /// dropped and made again (picks someone chose in the Version menu are kept).
    /// </summary>
    public static string RulesStamp()
    {
        var c = Plugin.Instance!.Configuration;
        return string.Join('|', RulesVersion, c.PreferredResolution, c.Language ?? string.Empty, c.PreferStreamingSizes, c.Min4KSeeders);
    }

    public static List<ReleaseCandidate> Rank(IEnumerable<ReleaseCandidate> releases, MediaRequest? request)
    {
        var c = Plugin.Instance!.Configuration;
        var excluded = SplitWords(c.ExcludedKeywords);
        var required = SplitWords(c.RequiredKeywords);
        var resolutions = c.AllowedResolutions ?? [];
        var types = new HashSet<string>(c.AllowedReleaseTypes ?? [], StringComparer.OrdinalIgnoreCase);
        var pref = c.PreferredResolution > 0 ? c.PreferredResolution : 1080;
        var english = string.IsNullOrWhiteSpace(c.Language) || c.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        var list = new List<ReleaseCandidate>();
        foreach (var r in releases)
        {
            r.Title = CleanTitle(r.Title);
            r.Resolution = ParseResolution(r.Title);
            var type = DetectReleaseType(r.Title);
            if ((r.Seeders >= 0 && r.Seeders < c.MinSeeders) // -1 = unknown (e.g. web-seeded), don't filter
                || (r.Resolution > 0 && !resolutions.Contains(r.Resolution))
                || (type is not null && !types.Contains(type))
                || excluded.Any(k => r.Title.Contains(k, StringComparison.OrdinalIgnoreCase))
                || !required.All(k => r.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (request?.Kind == MediaKind.Episode)
            {
                var match = MatchEpisodeTitle(r.Title, request.Season, request.Episode);
                if (match == EpisodeMatch.Wrong)
                {
                    continue;
                }

                r.IsSeasonPack = match == EpisodeMatch.Pack;
            }

            if (request is not null && !SizeAllowed(r, request, c))
            {
                continue;
            }

            list.Add(r);
        }

        return list
            .OrderBy(r => r.IsSeasonPack)
            .ThenBy(r => english && IsForeignLanguage(r.Title) ? 1 : 0)
            .ThenBy(r => Weak4K(r, c.Min4KSeeders) ? Math.Max(ResolutionTier(r.Resolution, pref), ResolutionTier(1080, pref) + 0.00001) : ResolutionTier(r.Resolution, pref))
            .ThenBy(r => c.PreferStreamingSizes && !StreamingFriendly(r, request) ? 1 : 0)
            .ThenByDescending(r => r.Seeders)
            .ToList();
    }

    /// <summary>
    /// Whether a release is a sensible size to stream: within a soft size cap for its resolution and well seeded.
    /// Huge remuxes (20 GB+) are slow to start over a torrent, so a well-seeded, reasonably sized encode ranks
    /// above them at the same resolution. They stay in the list; they're just not tried first.
    /// </summary>
    public static bool StreamingFriendly(ReleaseCandidate r, MediaRequest? request)
    {
        if (r.Size <= 0 || r.IsSeasonPack)
        {
            return true; // unknown size, or a pack whose size isn't the episode's
        }

        var seeded = r.Seeders < 0 || r.Seeders >= 10;
        return seeded && r.Size <= SoftCapGb(r.Resolution, request?.Kind == MediaKind.Episode) * (1L << 30);
    }

    /// <summary>Soft size caps (GB) used by <see cref="StreamingFriendly"/>.</summary>
    public static double SoftCapGb(int resolution, bool episode) => (resolution, episode) switch
    {
        (2160, false) => 15,
        (1440, false) => 12,
        (1080, false) => 8,
        (720, false) => 4,
        (0, false) => 8,
        (_, false) => 2,
        (2160, true) => 6,
        (1440, true) => 4,
        (1080, true) => 3,
        (720, true) => 1.5,
        (0, true) => 3,
        _ => 1
    };

    /// <summary>Returns the release type id (see <see cref="ReleaseTypes"/>), or null if it can't be told.</summary>
    /// <summary>
    /// A release tagged with another language (an Italian or French dub, multi-language releases that default to it).
    /// For an English library these rank after untagged releases: their default audio or forced subtitles are often
    /// not English.
    /// </summary>
    public static bool IsForeignLanguage(string title) => ForeignLanguage().IsMatch(title);

    [GeneratedRegex(@"(?<![A-Za-z])(ITA|ITALIAN|FRENCH|TRUEFRENCH|VFF|VFQ|VFI|VOSTFR|GER|GERMAN|DEUTSCH|SPA|SPANISH|ESP|CASTELLANO|LATINO|RUS|RUSSIAN|HINDI|TAMIL|TELUGU|DUBLADO|TURKISH|ARABIC|POLISH|NORDIC)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignLanguage();

    /// <summary>Removes junk some sites put into names (e.g. "The###@ Lord###@ of###@ ...") and extra spaces.</summary>
    public static string CleanTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return title;
        }

        var cleaned = JunkMarks().Replace(title, string.Empty);
        return MultiSpace().Replace(cleaned, " ").Trim();
    }

    [GeneratedRegex(@"#{2,}@*|#+@+|@{2,}")]
    private static partial Regex JunkMarks();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    public static string? DetectReleaseType(string title)
    {
        foreach (var (id, _, pattern) in ReleaseTypes)
        {
            if (pattern.IsMatch(title))
            {
                return id;
            }
        }

        return null;
    }

    public static int ParseResolution(string title)
    {
        if (Res2160().IsMatch(title))
        {
            return 2160;
        }

        if (Res1440().IsMatch(title))
        {
            return 1440;
        }

        if (title.Contains("1080", StringComparison.Ordinal))
        {
            return 1080;
        }

        if (title.Contains("720p", StringComparison.OrdinalIgnoreCase))
        {
            return 720;
        }

        if (ResSd().IsMatch(title))
        {
            return 480;
        }

        return ResLow().IsMatch(title) ? 240 : 0;
    }

    private static bool SizeAllowed(ReleaseCandidate r, MediaRequest request, Configuration.PluginConfiguration c)
    {
        if (r.Size <= 0)
        {
            return true; // unknown size
        }

        var (min, max) = request.Kind == MediaKind.Movie
            ? (c.MinSizeGb, c.MaxSizeGb)
            : r.IsSeasonPack ? (c.SeasonMinSizeGb, c.SeasonMaxSizeGb) : (c.EpisodeMinSizeGb, c.EpisodeMaxSizeGb);
        const long Gb = 1024L * 1024 * 1024;
        return r.Size >= min * Gb && (max <= 0 || r.Size <= max * Gb);
    }

    private static string[] SplitWords(string? s) =>
        (s ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Returns true when a file name or release title contains the given episode (handles S01E02, S01E01-E03 and 1x02).
    /// </summary>
    public static bool ContainsEpisode(string text, int season, int episode)
    {
        foreach (Match m in EpisodeRegex().Matches(text))
        {
            var s = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            var e1 = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
            var e2 = m.Groups["e2"].Success ? int.Parse(m.Groups["e2"].Value, CultureInfo.InvariantCulture) : e1;
            if (s == season && episode >= e1 && episode <= e2)
            {
                return true;
            }
        }

        foreach (Match m in CrossRegex().Matches(text))
        {
            if (int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) == season
                && int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) == episode)
            {
                return true;
            }
        }

        return AnimeEpisode(text) == episode && AnimeSeasonMatches(text, season);
    }

    /// <summary>
    /// Anime names give the season as "S2 - 05", "Season 2 - 05" or "2nd Season - 05", or not at all (then it's the
    /// first season, or the show numbers episodes straight through).
    /// </summary>
    private static bool AnimeSeasonMatches(string text, int season)
    {
        var marked = SeasonRegex().Matches(text).Select(m => int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture))
            .Concat(OrdinalSeasonRegex().Matches(text).Select(m => int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)))
            .ToList();
        return marked.Count == 0 ? season == 1 : marked.Contains(season);
    }

    /// <summary>Anime-style episode number ("[Group] Show - 12 (1080p)"), only when there's no SxxEyy/1x02 in the name.</summary>
    private static int? AnimeEpisode(string text)
    {
        if (EpisodeRegex().IsMatch(text) || CrossRegex().IsMatch(text))
        {
            return null;
        }

        var m = AnimeEpisodeRegex().Match(text);
        return m.Success ? int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) : null;
    }

    private static EpisodeMatch MatchEpisodeTitle(string title, int season, int episode)
    {
        if (ContainsEpisode(title, season, episode))
        {
            return EpisodeMatch.Exact;
        }

        if (EpisodeRegex().IsMatch(title) || CrossRegex().IsMatch(title))
        {
            return EpisodeMatch.Wrong; // names some other episode
        }

        if (AnimeEpisode(title) is int anime)
        {
            return anime == episode && AnimeSeasonMatches(title, season) ? EpisodeMatch.Exact : EpisodeMatch.Wrong;
        }

        var range = SeasonRangeRegex().Match(title);
        if (range.Success)
        {
            var a = int.Parse(range.Groups["a"].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(range.Groups["b"].Value, CultureInfo.InvariantCulture);
            return season >= a && season <= b ? EpisodeMatch.Pack : EpisodeMatch.Wrong;
        }

        var seasons = SeasonRegex().Matches(title).Select(m => int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)).ToList();
        if (seasons.Count > 0)
        {
            return seasons.Contains(season) ? EpisodeMatch.Pack : EpisodeMatch.Wrong;
        }

        // No season info at all: probably a "complete series" pack.
        return EpisodeMatch.Pack;
    }

    /// <summary>A 4K release with fewer seeders than the "minimum seeders for 4K" setting (unknown counts don't count).</summary>
    public static bool Weak4K(ReleaseCandidate r, int min4KSeeders) => min4KSeeders > 0 && r.Resolution >= 2160 && r.Seeders >= 0 && r.Seeders < min4KSeeders;

    private static double ResolutionTier(int res, int pref)
    {
        if (res == pref)
        {
            return 0;
        }

        if (res > 0 && res < pref)
        {
            return 1 + ((pref - res) / 10000.0);
        }

        return res == 0 ? 2 : 3 + ((res - pref) / 10000.0);
    }

    [GeneratedRegex(@"\b(2160p|4k|uhd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Res2160();

    [GeneratedRegex(@"\b(1440p|2k)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Res1440();

    [GeneratedRegex(@"\b(480p|576p|dvdrip|sdtv|xvid)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResSd();

    [GeneratedRegex(@"\b(240p|360p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResLow();

    [GeneratedRegex(@"S(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,3})(?:[-E]+(?<e2>\d{1,3}))?", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"(?<![\dx])(?<s>\d{1,2})x(?<e>\d{2,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex CrossRegex();

    [GeneratedRegex(@"\s-\s(?<e>\d{1,3})(?:v\d)?(?=\s|\[|\(|\.|$)")]
    private static partial Regex AnimeEpisodeRegex();

    [GeneratedRegex(@"\bS(?<a>\d{1,2})\s*-\s*S?(?<b>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRangeRegex();

    [GeneratedRegex(@"(?:\bS(?<s>\d{1,2})\b|\bSeason[ ._]?(?<s>\d{1,2})\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex();

    [GeneratedRegex(@"\b(?<s>\d{1,2})(?:st|nd|rd|th)[ ._]Season\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSeasonRegex();

    private enum EpisodeMatch
    {
        Exact,
        Pack,
        Wrong
    }
}
