# Plan: debrid support

**Status:** proposal, nothing built yet.

## What it gives you
A debrid service (Real-Debrid, AllDebrid, Premiumize, TorBox, Debrid-Link) has already downloaded popular torrents to its
own servers. If a release is "cached" there, the plugin streams it over plain HTTPS from the service:
- It starts in a second or two, seeks instantly, and doesn't depend on how many seeders there are.
- 4K remuxes stream at full speed.
- Your server does no BitTorrent at all for that title, so no peers see your IP. No VPN is needed for cached titles.

Releases that aren't cached still play over P2P, as now.

## How it fits the current design
Most of the pipeline stays as it is. Debrid becomes one more way to deliver a release.

| Step | Today | With debrid |
|---|---|---|
| Find releases | SourceHub searches the sources | unchanged |
| Rank | ReleaseRanker | cached releases rank first within a resolution (a setting) |
| Version menu | "1080p · 8 GB · 120 seeds · …" | adds "⚡ RD" to cached releases |
| Play | TorrentEngine and TorrentSession | cached: a debrid HTTPS link; not cached: torrent (or wait for the debrid download, a setting) |

`ReleaseCandidate.DirectUrl` already exists for this ("a direct HTTP(S) link (debrid, etc.)").

## Building blocks
1. **`IDebridService`**, one class per service:
   - `CheckCachedAsync(hashes)`: which info hashes are cached (checked in batches).
   - `ResolveAsync(hash, fileHint)`: adds the magnet, picks the file (episode matching reuses `StreamResolver.PickFile`'s logic), and returns a direct link.
   - `ValidateAsync()`: checks the key and shows the account status and days left on the settings page.
2. **Cached check during search:** after ranking, check the top N hashes, and cache the answers for about 1 hour per hash.
   - **Real-Debrid:** it removed its "instant availability" endpoint in late 2024. The only reliable check is to add the magnet, read its status, and delete it if it isn't downloaded. That's slower and rate-limited, so do it only for the top few releases, or when one is chosen.
   - **TorBox:** it has a cached-check endpoint (`/torrents/checkcached`), so it's the easiest service to support well.
   - **Others:** check each API's current docs when building, because these endpoints change.
3. **Playback (`P2PStreamController`):** for a cached release, the play endpoint gets a link from `ResolveAsync`. There are two ways to deliver it:
   - **Proxy (default):** the server fetches byte ranges from the debrid link and passes them on. Pros:
     - It works with ffmpeg probing, transcoding and seeking exactly like now.
     - The service always sees one IP, the server's. Several services ban accounts used from many IPs at once.
     - It costs the server's bandwidth (in and out), the same as P2P does today.
   - **Redirect:** a 302 to the debrid link, so clients download straight from the service. It saves server bandwidth, but remote clients show up from other IPs (an account-sharing risk), and the link is visible to the player.
4. **Settings (a new Debrid card on Sources):**
   - service
   - API key (Real-Debrid: real-debrid.com/apitoken)
   - "Prefer cached releases"
   - "Only use debrid (never P2P)"
   - "Uncached releases: play over P2P / add to debrid and wait"
   - delivery: Proxy or Redirect
   - account status line
5. **Links expire.** Unrestricted links last hours, not days. Keep them per release with an expiry, and get a new one if a range request fails with 403 or 404.

## Phases
1. **TorBox and Real-Debrid:**
   - cached check
   - resolve
   - proxy playback
   - "⚡" in the Version menu
   - cached-first ranking
2. **Add to debrid and wait** for uncached releases, with progress shown in the Torrents tab.
3. **AllDebrid, Premiumize and Debrid-Link.**
4. **Redirect delivery** as an option.

## Testing
- **Needs:** an account and API key for each service (paid; a TorBox free tier exists but is limited).
- **Unit tests** against recorded API replies:
  - cached and not cached
  - file selection for season packs
  - expired link
  - invalid key
- **A live test** like the existing ones: a cached public-domain title from start to finish, with range requests and seeking.

## Risks
- **APIs change.** Real-Debrid removed instant availability with little notice, so keep each service small and isolated.
- **Terms of service:** most services forbid sharing an account. Proxy delivery avoids looking like sharing.
- **Cached content isn't legal by default.** The user is responsible for what they stream, as with P2P.
