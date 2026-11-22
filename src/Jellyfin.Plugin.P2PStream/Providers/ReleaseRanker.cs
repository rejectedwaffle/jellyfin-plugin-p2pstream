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

    public static List<ReleaseCandidate> Rank(IEnumerable<ReleaseCandidate> releases, MediaRequest? request)
    {
        var c = Plugin.Instance!.Configuration;
        var excluded = SplitWords(c.ExcludedKeywords);
        var required = SplitWords(c.RequiredKeywords);
        var resolutions = c.AllowedResolutions ?? [];
        var types = new HashSet<string>(c.AllowedReleaseTypes ?? [], StringComparer.OrdinalIgnoreCase);
        var pref = c.PreferredResolution > 0 ? c.PreferredResolution : 1080;

        var list = new List<ReleaseCandidate>();
        foreach (var r in releases)
        {
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
            .ThenBy(r => ResolutionTier(r.Resolution, pref))
            .ThenByDescending(r => r.Seeders)
            .ToList();
    }

    /// <summary>Returns the release type id (see <see cref="ReleaseTypes"/>), or null if it can't be told.</summary>
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

        return false;
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

    [GeneratedRegex(@"\bS(?<a>\d{1,2})\s*-\s*S?(?<b>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRangeRegex();

    [GeneratedRegex(@"(?:\bS(?<s>\d{1,2})\b|\bSeason[ ._]?(?<s>\d{1,2})\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex();

    private enum EpisodeMatch
    {
        Exact,
        Pack,
        Wrong
    }
}
