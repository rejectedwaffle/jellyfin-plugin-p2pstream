# P2P Stream for Jellyfin

Stream torrents on demand inside Jellyfin, similar to Elementum for Kodi.
Press play and the plugin finds a release, starts the torrent and streams it while the rest downloads.
Seeking works, because the engine fetches pieces wherever playback jumps to.

## Features
- **Built-in sources, nothing extra to install:** Internet Archive (films uploaded to archive.org, on by default), plus Knaben (searches many trackers at once), Nyaa (anime), KickAss Torrents, YTS, EZTV and The Pirate Bay, which you switch on yourself. Each has an editable address in case a site moves. Torznab (Prowlarr/Jackett) is supported too.
- **Catalogs as libraries (Gelato-style):** TMDB lists (Trending, Trending Today, Popular, Top Rated, Now Playing, Upcoming, On The Air, Airing Today) and your own TMDB lists are written as `.strm` folders. Each catalog has Import, Collection, Max items and a target library. Artwork and metadata come from Jellyfin's own TMDB provider.
- **Trakt catalogs:** with a free Trakt API Client ID: Trakt Trending, Popular, Anticipated and Box Office, a public watchlist, and any Trakt lists you name. Connect your account (enter a code at trakt.tv/activate) for your private watchlist and recommendations; no Client Secret needed.
- **Available streams picker (Elementum-style):** a title's releases appear in Jellyfin's **Version** menu with resolution, size, seeds, quality and source, plus "Best 4K / Best 1080p" entries. Auto (best match) stays the default, and your pick is remembered.
- **Faster start:** opening a title's page pre-loads the start and end of Auto's release, so playback starts sooner. Peers are kept busy with requests well ahead of the player, so a well-seeded release downloads at several MB/s even through a VPN. Two minutes into an episode, the start of the next one is fetched too, so Next or autoplay starts almost at once (following per-device limits).
- **Subtitles from the release:** subtitle files that come with a release (.srt, .ass, .vtt) appear in Jellyfin's subtitle menu, labelled by language, without downloading the whole release first.
- **Per-device limits:** cap what Auto plays on a device (e.g. the living room TV gets the best release up to 1080p and 8 GB). Choosing a version in the Version menu always wins.
- **Privacy:** send torrent traffic only through a VPN's network interface (with a kill switch: if the VPN drops, torrents stop instead of using another connection) or only through a SOCKS5/HTTP proxy. Works with a VPN's forwarded port ([guide](docs/port-forwarding.md)). Optionally share only while watching, and cap the disk space streaming may use.
- **Anime:** Nyaa is searched with TMDB's romaji (Japanese) titles as well as the English ones, and only for titles that are anime.
- **Source health:** each source on the Sources tab shows its last result count, or why it failed.
- **Search for any movie or show** from Jellyfin's search bar, in any client. Matches from TMDB are added to your library.
- **Burst-style quality filters:** preferred and allowed resolutions, allowed release types (BluRay, WEB-DL, ... TeleSync, Cam), size ranges for movies, episodes and season packs, required and excluded keywords, minimum seeders (and optionally a higher minimum for 4K).
- **Torrent engine** (MonoTorrent): downloads only the file being watched, in playback order, and removes idle torrents.
- **Magnets:** paste a magnet or `.torrent` URL to stream it or add it to your library (its subtitle files come too).
- **Settings page** in the dashboard menu, with tabs: Status, Libraries, Sources, Playback, Filters, Network, Accounts.

## Install (plugin repository)
1. In Jellyfin, go to **Dashboard > Plugins > Repositories** and add:
   - Name: `P2P Stream`
   - URL: `https://raw.githubusercontent.com/rejectedwaffle/jellyfin-plugin-p2pstream/main/manifest.json`
2. Go to **Catalog**, find **P2P Stream**, install it and restart Jellyfin.

Updates then appear in the plugin catalog. Requires Jellyfin 12.4 or newer.

### Manual install
Download `p2pstream_<version>.zip` from [Releases](https://github.com/rejectedwaffle/jellyfin-plugin-p2pstream/releases), extract it into
`<jellyfin data>/plugins/P2P Stream_<version>/` (for example `/var/lib/jellyfin/plugins/P2P Stream_1.0.0.0/`) and restart Jellyfin.

## Setup
1. **Accounts** tab: paste a free TMDB API key (themoviedb.org, under Settings, then API). Under **Stream links**, the
   server address is found automatically; set it only if devices reach Jellyfin by another address (for example `http://192.168.1.10:8096`).
2. **Sources** tab: turn on the sources you want and click **Save**. Use **Test search** to check them.
3. **Libraries** tab > **Catalogs**: for each catalog you want:
   - Tick **Import**.
   - Pick a library, or choose **New library...** and give it a name. That creates a folder and shows its path.
   - Click **Save**, then **Libraries** and add a library with that folder.
   - Use the row's sync button, or **Import all**.
4. **Libraries** tab > **Jellyfin search**: pick (or create) the folders for movies and shows found by search, and add a
   library for each, so search results have somewhere to appear.
5. **Network** tab > **Connection**: forward the listen port (default 55125, TCP and UDP) on your router for better speeds. UPnP is tried automatically.
6. Optional, **Network** tab > **Privacy method**: choose **Only through a VPN** and pick its interface in the **VPN** box (see below), or **Only through the proxy**.

## Using a VPN
The plugin can only bind to a network interface that exists where Jellyfin runs. Two common setups:
- **VPN inside the Jellyfin container or VM (recommended):** run WireGuard or OpenVPN there (for an LXC container the host
  must allow it: WireGuard is in the kernel, OpenVPN needs `/dev/net/tun` passed through). Pick its interface (`wg0`, `tun0`)
  in the **VPN** box. Every torrent socket is bound to it, so if the VPN goes down nothing is sent until it's back.
  - With a full-tunnel VPN (all traffic through it) this just works.
  - With a split tunnel (`Table = off` in WireGuard), the VPN's address needs its own route, e.g.
    `ip rule add from <wg0 address> table 51820` and `ip route add default dev wg0 table 51820`. On Linux 5.7+ the plugin also
    binds sockets to the device itself, which covers this without the rule.
- **VPN for the whole container from outside** (e.g. a VPN gateway container, or the host routing the container through a VPN):
  leave Privacy on **Direct**; the plugin can't see or check that VPN, so it can't stop torrents if it drops.

Through a VPN or proxy there are no incoming connections unless the VPN forwards a port, so torrents with few seeds can be
slower. If your VPN forwards a port, see **[Port forwarding through a VPN](docs/port-forwarding.md)**: the plugin can read
the current port from a file that the VPN's port-forwarding script keeps up to date (with a complete example for PIA).

In proxy mode DHT is off; a SOCKS5 proxy still carries UDP trackers, an HTTP proxy doesn't.

## How it works
Each `.strm` file holds a URL such as `http://server:8096/P2PStream/play/<token>/movie/603`.
When a player opens it, the plugin looks the title up on TMDB, searches the enabled sources, filters and ranks the
releases, starts the best one and serves the video over HTTP with range support. The pick is remembered, so seeking
and replaying start instantly. The token in the URL lets players stream without a Jellyfin login, so treat it like a password.

## Build and release
```
dotnet build src/Jellyfin.Plugin.P2PStream -c Release          # build only
./scripts/package.ps1 -Changelog "What changed"               # zip + meta.json + manifest.json entry
git commit -am "<version>" && git push                          # commit the version bump and manifest.json first
gh release create v<version> artifacts/p2pstream_<version>.zip --target main  # then publish (tags the pushed commit)
```
Bump `AssemblyVersion` in the `.csproj` first. Requires the .NET 10 SDK.

For quick test builds without a release: `./scripts/package.ps1 -Dev` builds `<version>.<n>` into `artifacts/dev/`, and
`./scripts/dev-serve.ps1` serves that folder on port 8765. Add `http://<your PC>:8765/manifest.json` as a second
repository in Jellyfin and install from there.

## Notes
- Some sites block automated searches or move domains. Use "change address" or the proxy setting.

## Legal notice
- P2P Stream is a BitTorrent client for Jellyfin. It doesn't host, index or distribute any media, and this repository
  contains none.
- Out of the box, the only enabled source is the Internet Archive (films uploaded to archive.org; the uploader states their rights, so check them). Any other
  source is something you choose to turn on or add.
- You are responsible for what you download and share, and for following the copyright law where you live. Downloading or
  sharing copyrighted material without permission may be illegal. While a torrent plays, the plugin also uploads parts
  of it to other peers; that's how BitTorrent works.
- If you believe something in this repository infringes your rights, please [open an issue](https://github.com/rejectedwaffle/jellyfin-plugin-p2pstream/issues).

## License
GPL-3.0. See [LICENSE](LICENSE).
