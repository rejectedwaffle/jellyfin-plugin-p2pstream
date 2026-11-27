using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.P2PStream.Configuration;

public class TorznabIndexer
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Torznab endpoint, e.g. http://prowlarr:9696/1/api or http://jackett:9117/api/v2.0/indexers/all/results/torznab.</summary>
    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A user-added RSS/Atom search feed source (Settings > Sources > Add source).
/// </summary>
public class RssSource
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the search URL with {query} where the search words go.</summary>
    public string Url { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// One row of the Catalogs table (Gelato-style): which TMDB list goes into which library.
/// </summary>
public class CatalogEntry
{
    /// <summary>Gets or sets the catalog id, e.g. "movie:trending", "tv:popular" or "list:8312:movie".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the catalog is imported (synced on schedule).</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether a Jellyfin collection is kept in step with the catalog.</summary>
    public bool Collection { get; set; }

    public int MaxItems { get; set; } = 100;

    /// <summary>Gets or sets the catalog folder (under the catalog path) this catalog writes to. Add a library with that folder to see it; blank uses the default folder.</summary>
    public string Library { get; set; } = string.Empty;
}

/// <summary>
/// Settings for one built-in search source (see Providers/BuiltIn).
/// </summary>
public class BuiltInSource
{
    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>Gets or sets the site address. Editable because these sites change domains.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Caps what Auto plays on matching devices. Match is compared (case-insensitive, "contains") with the device name,
/// its custom name from Dashboard &gt; Devices, and the app name, e.g. "Living Room", "Roku" or "Android TV".
/// </summary>
public class DeviceRule
{
    public string Match { get; set; } = string.Empty;

    /// <summary>Gets or sets the highest resolution Auto plays on the device (0 = any).</summary>
    public int MaxResolution { get; set; } = 1080;

    /// <summary>Gets or sets the largest file Auto plays on the device, in GB (0 = any).</summary>
    public int MaxSizeGb { get; set; }
}

public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Access ----

    /// <summary>Gets or sets the secret embedded in .strm URLs so ffmpeg/clients can stream without a Jellyfin session.</summary>
    public string StreamToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the base URL written into .strm files. Must be reachable by every client (LAN IP or public URL), e.g. http://192.168.1.10:8096.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    // ---- Engine ----
    public int ListenPort { get; set; } = 55125;

    /// <summary>
    /// Gets or sets a file holding the port to listen on instead (optional). A VPN's port-forwarding script writes the
    /// port it was given there (gluetun, PIA or ProtonVPN scripts and the like); it's checked every 15 seconds.
    /// </summary>
    public string ListenPortFile { get; set; } = string.Empty;

    public string DownloadPath { get; set; } = string.Empty;

    public bool KeepFilesAfterPlayback { get; set; }

    public int IdleTimeoutMinutes { get; set; } = 15;

    public int MaxActiveTorrents { get; set; } = 3;

    public int MaxDownloadKbps { get; set; }

    public int MaxUploadKbps { get; set; }

    public bool EnablePortForwarding { get; set; } = true;

    public int MetadataTimeoutSeconds { get; set; } = 60;

    // ---- Privacy ----

    /// <summary>Gets or sets how torrent traffic leaves the server: "direct", "interface" (only through a VPN's network interface) or "proxy".</summary>
    public string TorrentNetwork { get; set; } = "direct";

    /// <summary>Gets or sets the network interface torrent traffic must use in "interface" mode, e.g. wg0 or tun0 (or its IP address).</summary>
    public string BindInterface { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether searches, TMDB lookups and .torrent downloads go the same way as torrent
    /// traffic (through the chosen VPN or proxy). Off by default; in Direct mode it changes nothing.
    /// </summary>
    public bool SearchesFollowTorrentTraffic { get; set; }

    /// <summary>Gets or sets a value indicating whether a torrent stops (no downloading or sharing) a minute after nobody is watching it.</summary>
    public bool StopSharingWhenIdle { get; set; } = true;

    /// <summary>Gets or sets the most disk space streaming torrents may use, in GB (0 = no limit). Titles being watched are never removed.</summary>
    public int MaxDiskGb { get; set; } = 30;

    // ---- Per-device quality ----

    /// <summary>Gets or sets rules that cap what Auto plays on matching devices (e.g. a TV that can't handle 4K).</summary>
    public DeviceRule[] DeviceRules { get; set; } = [];

    // ---- Trakt ----

    /// <summary>Gets or sets the Client ID of the user's Trakt API app (free, trakt.tv/oauth/applications).</summary>
    public string TraktClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Client Secret of the Trakt app (only needed to connect an account for a private watchlist).</summary>
    public string TraktClientSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets the Trakt username whose public watchlist is offered as a catalog.</summary>
    public string TraktUser { get; set; } = string.Empty;

    /// <summary>Gets or sets Trakt lists to offer as catalogs, comma separated: "username/list-slug" or a list URL.</summary>
    public string TraktLists { get; set; } = string.Empty;

    // ---- Providers ----

    /// <summary>Gets or sets the built-in sources. Missing ids fall back to the defaults in BuiltInSourceDefaults.</summary>
    public BuiltInSource[] Sources { get; set; } = [];

    public TorznabIndexer[] Indexers { get; set; } = [];

    /// <summary>Gets or sets user-added RSS feed sources.</summary>
    public RssSource[] RssSources { get; set; } = [];

    public int MinSeeders { get; set; } = 3;

    /// <summary>Gets or sets the preferred resolution: 2160, 1080, 720 or 480.</summary>
    public int PreferredResolution { get; set; } = 1080;

    /// <summary>Gets or sets the allowed resolutions (2160, 1440, 1080, 720, 480, 240). Releases with no resolution in the name are always allowed.</summary>
    public int[] AllowedResolutions { get; set; } = [2160, 1440, 1080, 720, 480];

    /// <summary>Gets or sets the allowed release types (see ReleaseRanker.ReleaseTypes). Unrecognised releases are always allowed.</summary>
    public string[] AllowedReleaseTypes { get; set; } = ["bluray", "webdl", "hdrip", "hdtv", "dvdrip", "dvdscr", "screener"];

    // Size ranges in GB (0 max = no limit).
    public int MinSizeGb { get; set; }

    public int MaxSizeGb { get; set; } = 30;

    public int SeasonMinSizeGb { get; set; }

    public int SeasonMaxSizeGb { get; set; } = 100;

    public int EpisodeMinSizeGb { get; set; }

    public int EpisodeMaxSizeGb { get; set; } = 10;

    /// <summary>Gets or sets comma separated words; releases must contain all of them.</summary>
    public string RequiredKeywords { get; set; } = string.Empty;

    /// <summary>Gets or sets comma separated words; releases containing any are skipped.</summary>
    public string ExcludedKeywords { get; set; } = string.Empty;

    // ---- Proxy (searches and .torrent downloads; torrent traffic too when TorrentNetwork is "proxy") ----

    /// <summary>Gets or sets the proxy address, e.g. socks5://127.0.0.1:1080 or http://proxy:8080. Empty means no proxy.</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    public string ProxyUser { get; set; } = string.Empty;

    public string ProxyPassword { get; set; } = string.Empty;

    public int CandidatesToTry { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether well-seeded, reasonably sized releases rank above huge ones of the same resolution.</summary>
    public bool PreferStreamingSizes { get; set; } = true;

    /// <summary>
    /// Gets or sets the fewest seeders a 4K release needs to be preferred (0 = off). A 4K release with fewer ranks after
    /// the best 1080p one: a thin swarm can't download 4K fast enough to start quickly or play smoothly.
    /// </summary>
    public int Min4KSeeders { get; set; }

    /// <summary>Gets or sets a value indicating whether a title's releases are listed in Jellyfin's Version menu (Elementum-style picker).</summary>
    public bool StreamPicker { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether opening a title's page starts loading Auto's release, so playback starts sooner.</summary>
    public bool PrewarmOnOpen { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether subtitle files that come with a release (.srt and the like) are offered in
    /// Jellyfin's subtitle menu. They're small, so they don't need the whole release downloaded first.
    /// </summary>
    public bool TorrentSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a subtitle in the plugin's language is fetched from Jellyfin's subtitle
    /// providers (e.g. the OpenSubtitles plugin) when a played release has none.
    /// </summary>
    public bool FetchMissingSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the top releases' seeder counts are checked with the trackers (sites'
    /// own counts are often stale) before Auto picks.
    /// </summary>
    public bool CheckLiveSeeders { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether, two minutes into an episode, the start of the next episode is fetched
    /// quietly (then the download pauses), so Next or autoplay starts almost at once.
    /// </summary>
    public bool PreloadNextEpisode { get; set; } = true;

    /// <summary>Gets or sets how many releases the Version menu lists.</summary>
    public int StreamPickerMax { get; set; } = 15;

    /// <summary>Gets or sets a value indicating whether the top releases are checked in advance so their tracks show in the Version menu (Gelato-style).</summary>
    public bool ProbeStreams { get; set; } = true;

    /// <summary>Gets or sets how many of the top releases are checked in advance.</summary>
    public int ProbeStreamCount { get; set; } = 2;

    // ---- Catalog ----
    public bool CatalogEnabled { get; set; }

    public string TmdbApiKey { get; set; } = string.Empty;

    public string CatalogPath { get; set; } = string.Empty;

    public string Language { get; set; } = "en-US";

    public string[] MovieLists { get; set; } = ["trending", "popular"];

    public string[] ShowLists { get; set; } = ["trending", "popular"];

    public int CatalogPages { get; set; } = 2;

    /// <summary>Gets or sets comma separated TMDB list ids (your own curated lists).</summary>
    public string CustomTmdbListIds { get; set; } = string.Empty;

    public bool PruneCatalog { get; set; } = true;

    public int CatalogSyncHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets a value indicating whether the one-time 0.3.0 library cleanup has run (see LibrarySetup). True for new
    /// installs (nothing to clean); a settings file saved by 0.3.0 has it false, and the next catalog sync runs it.
    /// </summary>
    public bool LegacyLibraryCleanupDone { get; set; } = true;

    /// <summary>Gets or sets the Catalogs table rows. Missing rows fall back to defaults (and legacy MovieLists/ShowLists).</summary>
    public CatalogEntry[] Catalogs { get; set; } = [];

    // ---- Jellyfin search ----

    /// <summary>Gets or sets the catalog folder that movies added by search go into (add a library with it).</summary>
    public string SearchMovieLibrary { get; set; } = "P2P Movies";

    /// <summary>Gets or sets the catalog folder that shows added by search go into (add a library with it).</summary>
    public string SearchShowLibrary { get; set; } = "P2P Shows";

    /// <summary>
    /// Gets or sets a value indicating whether the libraries search imports into are hidden from every user's library
    /// list and home screen ("My media"). Search and Continue Watching still include their titles.
    /// </summary>
    public bool HideSearchLibraries { get; set; }

    /// <summary>Gets or sets a value indicating whether Jellyfin searches also look up TMDB and add matches to the library.</summary>
    public bool SearchIntegration { get; set; } = true;

    /// <summary>Gets or sets how long titles added by search are kept if they aren't on a synced list (0 = forever).</summary>
    public int SearchImportDays { get; set; } = 30;
}
