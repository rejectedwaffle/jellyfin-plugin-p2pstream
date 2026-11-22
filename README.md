# P2P Stream for Jellyfin

Stream torrents on demand inside Jellyfin, similar to Elementum for Kodi.
Press play and the plugin finds a release, starts the torrent and streams it while the rest downloads.
Seeking works, because the engine fetches pieces wherever playback jumps to.

## Features
- **Built-in sources, nothing extra to install:** Internet Archive (public-domain films, on by default), plus Knaben (searches many trackers at once), Nyaa (anime), KickAss Torrents, YTS, EZTV and The Pirate Bay, which you switch on yourself. Each has an editable address in case a site moves. Torznab (Prowlarr/Jackett) is supported too.
- **Catalogs as libraries (Gelato-style):** TMDB lists (Trending, Trending Today, Popular, Top Rated, Now Playing, Upcoming, On The Air, Airing Today) and your own TMDB lists are written as `.strm` folders. Each catalog has Import, Collection, Max items and a target library. Artwork and metadata come from Jellyfin's own TMDB provider.
- **Available streams picker (Elementum-style):** a title's releases appear in Jellyfin's **Version** menu with resolution, size, seeds, quality and source. Auto (best match) stays the default, and your pick is remembered.
- **Search for any movie or show** from Jellyfin's search bar, in any client. Matches from TMDB are added to your library.
- **Burst-style quality filters:** preferred and allowed resolutions, allowed release types (BluRay, WEB-DL, ... TeleSync, Cam), size ranges for movies, episodes and season packs, required and excluded keywords, minimum seeders, and an optional proxy.
- **Torrent engine** (MonoTorrent): downloads only the file being watched, in playback order, and removes idle torrents.
- **Magnets:** paste a magnet or `.torrent` URL to stream it or add it to your library.
- **Settings page** in the dashboard menu, with tabs: Torrents, Sources, Catalogs, Search, Quality, Engine, Access.

## Install (plugin repository)
1. In Jellyfin, go to **Dashboard > Plugins > Repositories** and add:
   - Name: `P2P Stream`
   - URL: `https://raw.githubusercontent.com/rejectedwaffle/jellyfin-plugin-p2pstream/main/manifest.json`
2. Go to **Catalog**, find **P2P Stream**, install it and restart Jellyfin.

Updates then appear in the plugin catalog. Requires Jellyfin 12.4 or newer.

### Manual install
Download `p2pstream_<version>.zip` from [Releases](https://github.com/rejectedwaffle/jellyfin-plugin-p2pstream/releases), extract it into
`<jellyfin data>/plugins/P2P Stream_<version>/` (for example `/var/lib/jellyfin/plugins/P2P Stream_0.3.2.0/`) and restart Jellyfin.

## Setup
1. **Access** tab: set **Public base URL** to the address every device uses to reach Jellyfin (for example `http://192.168.1.10:8096`).
2. **Sources** tab: turn on the sources you want and click **Save**. Use **Test search** to check them.
3. **Catalogs** tab: paste a free TMDB API key (themoviedb.org, under Settings, then API). Then, for each catalog you want:
   - Tick **Import**.
   - Pick a library, or choose **New library...** and give it a name. That creates a folder and shows its path.
   - Click **Save**, then **Libraries** and add a library with that folder.
   - Use the row's sync button, or **Import all**.
4. **Search** tab: add a Movies library and a Shows library for the two search folders, so search results have somewhere to appear.
5. Forward the listen port (default 55125, TCP and UDP) on your router for better speeds. UPnP is tried automatically.

## How it works
Each `.strm` file holds a URL such as `http://server:8096/P2PStream/play/<token>/movie/603`.
When a player opens it, the plugin looks the title up on TMDB, searches the enabled sources, filters and ranks the
releases, starts the best one and serves the video over HTTP with range support. The pick is remembered, so seeking
and replaying start instantly. The token in the URL lets players stream without a Jellyfin login, so treat it like a password.

## Build and release
```
dotnet build src/Jellyfin.Plugin.P2PStream -c Release          # build only
./scripts/package.ps1 -Changelog "What changed"               # zip + meta.json + manifest.json entry
gh release create v<version> artifacts/p2pstream_<version>.zip  # publish, then commit and push manifest.json
```
Bump `AssemblyVersion` in the `.csproj` first. Requires the .NET 10 SDK.

## Notes
- You are responsible for what you download and share; uploading (seeding) is part of how BitTorrent works.
- Some sites block automated searches or move domains. Use "change address" or the proxy setting.

## License
GPL-3.0. See [LICENSE](LICENSE).
