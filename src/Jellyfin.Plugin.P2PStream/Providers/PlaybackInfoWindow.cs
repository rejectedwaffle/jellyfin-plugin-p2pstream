using System.Collections.Concurrent;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Marks the titles Jellyfin is answering a Play request (PlaybackInfo) for. For a .strm item Jellyfin always re-checks
/// the stream at that point (a full metadata refresh with ffprobe), analysing up to 200 s of video with its default
/// settings: through a torrent that took over 6 minutes for a 4K release. While a title is marked, the play endpoint ends
/// a from-the-start read after <see cref="CheckBytes"/>, so that check finishes with what it found (ffprobe treats the end
/// as end of file), much like the plugin's own 10-second checks. Playback itself only starts after the answer.
/// </summary>
public static class PlaybackInfoWindow
{
    /// <summary>How much of the start of a release Jellyfin's check gets.</summary>
    public const long CheckBytes = 40L << 20;

    private static readonly ConcurrentDictionary<string, int> Active = new();

    // Windows whose one capped read was used: only Jellyfin's own check (the first read from the start) is cut short,
    // not another player that happens to start at the same moment.
    private static readonly ConcurrentDictionary<string, byte> Used = new();

    public static IDisposable Begin(string key)
    {
        Active.AddOrUpdate(key, 1, (_, n) => n + 1);
        Used.TryRemove(key, out _);
        return new Mark(key);
    }

    public static bool IsActive(string key) => Active.TryGetValue(key, out var n) && n > 0;

    /// <summary>True once per window: the read that gets capped.</summary>
    public static bool TakeCheck(string key) => IsActive(key) && Used.TryAdd(key, 0);

    private sealed class Mark(string key) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                if (Active.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1)) == 0)
                {
                    Used.TryRemove(key, out _);
                }
            }
        }
    }
}
