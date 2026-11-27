using System.Globalization;
using Jellyfin.Plugin.P2PStream.Torrent;

namespace Jellyfin.Plugin.P2PStream.Providers;

public enum MediaKind
{
    Movie,
    Episode
}

/// <summary>
/// Something a .strm file asked to play.
/// </summary>
public sealed class MediaRequest
{
    public MediaKind Kind { get; init; }

    public int? TmdbId { get; init; }

    public string? ImdbId { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    /// <summary>Gets or sets whether it's anime (true/false once looked up on TMDB, null when unknown).</summary>
    public bool? IsAnime { get; set; }

    /// <summary>Gets or sets other names it's released under (romaji etc.), used by anime sources.</summary>
    public IReadOnlyList<string> AltTitles { get; set; } = [];

    public int Season { get; init; }

    public int Episode { get; init; }

    public string Key
    {
        get
        {
            var id = TmdbId is int t ? "tmdb:" + t.ToString(CultureInfo.InvariantCulture) : "imdb:" + ImdbId;
            return Kind == MediaKind.Movie
                ? $"movie:{id}"
                : string.Create(CultureInfo.InvariantCulture, $"tv:{id}:{Season}:{Episode}");
        }
    }

    public override string ToString()
    {
        var name = Title ?? ImdbId ?? TmdbId?.ToString(CultureInfo.InvariantCulture);
        return Kind == MediaKind.Movie ? $"{name} ({Year})" : $"{name} S{Season:00}E{Episode:00}";
    }
}

/// <summary>
/// A playable option found by a Torznab indexer or a Stremio addon.
/// </summary>
public sealed class ReleaseCandidate
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets where this came from (indexer or addon name).</summary>
    public string Origin { get; set; } = string.Empty;

    public string? DownloadUrl { get; set; }

    public string? MagnetUrl { get; set; }

    /// <summary>Gets or sets a web page holding the magnet link (fetched only if this release is tried).</summary>
    public string? MagnetPageUrl { get; set; }

    public string? InfoHash { get; set; }

    /// <summary>Gets or sets the file index hint from a Stremio addon.</summary>
    public int? FileIndex { get; set; }

    /// <summary>Gets or sets the file name hint from a Stremio addon.</summary>
    public string? FileName { get; set; }

    public List<string> Trackers { get; set; } = [];

    /// <summary>Gets or sets a direct HTTP(S) link (debrid, etc.). It's handed to the player instead of using the torrent engine.</summary>
    public string? DirectUrl { get; set; }

    public long Size { get; set; }

    /// <summary>Gets or sets the seeder count; -1 when unknown.</summary>
    public int Seeders { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="Seeders"/> was checked with the trackers (not the site's figure).</summary>
    public bool LiveSeeders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the source's seeder counts are known to run high (often several times
    /// the real count). Until checked live, such a count is discounted when ranking.
    /// </summary>
    public bool StaleCounts { get; set; }

    /// <summary>Gets or sets a longer name another site gave the same torrent (used when this one is cut short).</summary>
    public string? FullTitle { get; set; }

    public int Peers { get; set; }

    public int Resolution { get; set; }

    public bool IsSeasonPack { get; set; }
}

public sealed record ResolvedStream(TorrentSession Session, int FileIndex);

public sealed class StreamNotFoundException(string message) : Exception(message);

/// <summary>A release that doesn't fit on the disk, even after removing torrents nobody is watching.</summary>
public sealed class NotEnoughSpaceException(string message) : Exception(message);
