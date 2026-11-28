using System.Reflection;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PiecePicking;
using MonoTorrent.Streaming;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// One active torrent, readable by any number of readers at once. Jellyfin opens several connections to a stream
/// together (a probe, the playback ffmpeg, a subtitle extractor, seeks), and MonoTorrent's own stream allows only one.
/// So each reader waits for its own pieces, flushes MonoTorrent's write cache, and reads them through MonoTorrent's
/// disk manager. A small steering policy points the streaming piece picker at the data readers need:
/// readers moving forward advance the download window, and a reader waiting for data elsewhere can pull the window
/// to itself, rate-limited, without cancelling requests, so readers in different places can't thrash it.
/// </summary>
public sealed class TorrentSession
{
    /// <summary>How often the download window may jump between distant parts of the file.</summary>
    private static readonly TimeSpan MinJumpInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Pieces after the window start that count as "already being fetched in order". It matches MonoTorrent's
    /// StreamingPieceRequester, which fetches 15 high-priority then 30 low-priority pieces from the window start.
    /// </summary>
    private const int NearPieces = 30;

    // Internal in MonoTorrent 3.0.2 (checked against its source). If a future version renames these, steering or the
    // direct read is skipped/falls back instead of breaking.
    private static readonly PropertyInfo? RequesterProperty =
        typeof(StreamProvider).GetProperty("PieceRequester", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? DiskRead =
        typeof(DiskManager).GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(ITorrentManagerFile), typeof(long), typeof(Memory<byte>)]);

    private readonly SemaphoreSlim _setup = new(1, 1);
    private readonly object _steerLock = new();
    private ITorrentManagerFile? _file;
    private int _windowPiece = -1;
    private DateTime _lastJump = DateTime.MinValue;
    /// <summary>How long a torrent keeps downloading after its last reader leaves.</summary>
    private static readonly TimeSpan PauseAfter = TimeSpan.FromSeconds(60);

    private int _activeStreams;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ITorrentManagerFile, int> _readers = new();

    // Readers that are someone watching (a player), as opposed to pre-loads, track checks and speed checks.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ITorrentManagerFile, int> _players = new();
    private volatile bool _closed;
    private CancellationTokenSource? _parkCts;

    public TorrentSession(string infoHash, TorrentManager manager)
    {
        InfoHash = infoHash;
        Manager = manager;
        LastAccess = DateTime.UtcNow;
    }

    public string InfoHash { get; }

    /// <summary>Gets when this torrent was started (a racer or a just-picked release is young).</summary>
    public DateTime Started { get; } = DateTime.UtcNow;

    public TorrentManager Manager { get; }

    public DateTime LastAccess { get; private set; }

    public int ActiveStreams => Volatile.Read(ref _activeStreams);

    /// <summary>The files of this torrent someone is watching right now (pre-loads and checks don't count).</summary>
    public IReadOnlyList<ITorrentManagerFile> FilesBeingWatched => _players.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();

    /// <summary>True if another file of this torrent is being read right now (a season pack's other episode, say).</summary>
    public bool IsReadingOtherThan(ITorrentManagerFile file) => _readers.Any(kv => kv.Value > 0 && kv.Key != file);

    /// <summary>Stop downloading in a few seconds unless someone reads it (after a pre-load that shouldn't keep going).</summary>
    public void PauseSoon()
    {
        if (ActiveStreams == 0)
        {
            SchedulePause(TimeSpan.FromSeconds(3));
        }
    }

    private IStreamingPieceRequester? Requester => Manager.StreamProvider is { } p ? RequesterProperty?.GetValue(p) as IStreamingPieceRequester : null;

    public void Touch() => LastAccess = DateTime.UtcNow;

    /// <summary>"Disk write failed: No space left on device" and the like.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _connections = new();

    /// <summary>Counts a peer connection attempt's outcome (shown in the Torrents list, to tell a swarm problem from a network one).</summary>
    public void CountConnection(string outcome) => _connections.AddOrUpdate(outcome, 1, (_, n) => n + 1);

    public string? ConnectionSummary() => _connections.IsEmpty
        ? null
        : string.Join(", ", _connections.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));

    public static string? Describe(Error? error) => error is null ? null : error.Reason switch
    {
        Reason.WriteFailure => "Couldn't write to disk",
        Reason.ReadFailure => "Couldn't read from disk",
        _ => error.Reason.ToString()
    } + (error.Exception is { } ex ? ": " + ex.GetBaseException().Message : string.Empty);

    /// <summary>Opens a reader on the file. Any number of readers can be open at once. Dispose the lease when done.</summary>
    /// <param name="playback">True for a player watching it; false for pre-loads and checks.</param>
    public async Task<StreamLease> OpenAsync(ITorrentManagerFile file, CancellationToken ct, bool playback = false)
    {
        Interlocked.Exchange(ref _parkCts, null)?.Cancel(); // someone's reading again: don't pause
        NetworkGuard.Require(); // kill switch
        await _setup.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            // Stopped after nobody watched it for a minute (or by the kill switch, now lifted): start again.
            while (Manager.State == TorrentState.Stopping)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            if (Manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error)
            {
                await Manager.StartAsync().ConfigureAwait(false);
            }

            // Only the files being read are downloaded (never switch off one that someone is reading: a season pack's
            // other episode, or the video while its subtitles are fetched). This also resumes a paused torrent.
            foreach (var other in Manager.Files.Where(f => f != file && f.Priority != Priority.DoNotDownload && _readers.GetValueOrDefault(f) <= 0))
            {
                await Manager.SetFilePriorityAsync(other, Priority.DoNotDownload).ConfigureAwait(false);
            }

            if (file.Priority == Priority.DoNotDownload)
            {
                await Manager.SetFilePriorityAsync(file, Priority.Normal).ConfigureAwait(false);
            }

            if (_file != file)
            {
                _file = file;
                lock (_steerLock)
                {
                    _windowPiece = -1;
                }
            }

            Interlocked.Increment(ref _activeStreams);
            _readers.AddOrUpdate(file, 1, (_, n) => n + 1);
            if (playback)
            {
                _players.AddOrUpdate(file, 1, (_, n) => n + 1);
            }

            Touch();
            return new StreamLease(this, file, playback);
        }
        finally
        {
            _setup.Release();
        }
    }

    /// <summary>The torrent is being removed. Open readers fail with ObjectDisposedException.</summary>
    public void Close() => _closed = true;

    internal async Task<int> ReadAtAsync(ITorrentManagerFile file, long position, Memory<byte> buffer, CancellationToken ct)
    {
        var torrent = Manager.Torrent ?? throw new InvalidOperationException("Torrent metadata missing");
        var fileStart = file.OffsetInTorrent;
        var first = torrent.ByteOffsetToPieceIndex(fileStart + position);

        // Wait for the first piece, steering the download here if it's somewhere new.
        while (!Manager.Bitfield[first])
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (Manager.State == TorrentState.Error)
            {
                // MonoTorrent stopped it (e.g. the disk is full). Fail now instead of waiting forever.
                throw new IOException("The torrent stopped with an error: " + (Describe(Manager.Error) ?? "unknown"));
            }

            NetworkGuard.Require(); // the VPN dropped while waiting: fail now, the player shows an error

            Steer(file, position, waiting: true);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        // Read as far as the downloaded pieces go (up to the buffer size).
        var last = Math.Min(torrent.ByteOffsetToPieceIndex(fileStart + position + buffer.Length - 1), file.EndPieceIndex);
        var lastAvailable = first;
        while (lastAvailable < last && Manager.Bitfield[lastAvailable + 1])
        {
            lastAvailable++;
        }

        var availableEnd = Math.Min((long)(lastAvailable + 1) * torrent.PieceLength - fileStart, file.Length);
        var count = (int)Math.Min(buffer.Length, availableEnd - position);
        var slice = buffer[..count];

        ObjectDisposedException.ThrowIf(_closed, this);
        var disk = Manager.Engine!.DiskManager;
        await disk.FlushAsync(Manager, first, lastAvailable).ConfigureAwait(false); // pieces may still sit in the write cache
        await ReadFromDiskAsync(disk, file, position, slice, ct).ConfigureAwait(false);

        Steer(file, position + count, waiting: false);
        Touch();
        return count;
    }

    internal void Release(ITorrentManagerFile file, bool playback)
    {
        Touch();
        _readers.AddOrUpdate(file, 0, (_, n) => Math.Max(0, n - 1));
        if (playback)
        {
            _players.AddOrUpdate(file, 0, (_, n) => Math.Max(0, n - 1));
        }

        if (Interlocked.Decrement(ref _activeStreams) == 0)
        {
            SchedulePause();
        }
    }

    /// <summary>
    /// When nobody has read the torrent for <see cref="PauseAfter"/> (you left a title's page after a pre-load or probe,
    /// or stopped watching), stop downloading the rest, and (StopSharingWhenIdle) stop sharing too. The torrent stays
    /// ready; the next reader resumes it.
    /// </summary>
    private void SchedulePause() => SchedulePause(PauseAfter);

    private void SchedulePause(TimeSpan after)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _parkCts, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(after, cts.Token).ConfigureAwait(false);
                await _setup.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    if (ActiveStreams > 0 || _closed)
                    {
                        return;
                    }

                    // Paused, or playing from what Jellyfin already read: still being watched. Look again later.
                    if (TorrentEngine.IsPlaying(InfoHash))
                    {
                        SchedulePause();
                        return;
                    }

                    foreach (var f in Manager.Files.Where(f => f.Priority != Priority.DoNotDownload))
                    {
                        await Manager.SetFilePriorityAsync(f, Priority.DoNotDownload).ConfigureAwait(false);
                    }

                    // Share only while watching: stop the torrent completely (no uploading, no peer connections).
                    if (Plugin.Instance?.Configuration.StopSharingWhenIdle != false && Manager.State is not (TorrentState.Stopped or TorrentState.Stopping))
                    {
                        await Manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _setup.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // a reader came back
            }
            catch (Exception)
            {
                // the torrent was removed meanwhile
            }
        });
    }

    private static async Task ReadFromDiskAsync(DiskManager disk, ITorrentManagerFile file, long position, Memory<byte> buffer, CancellationToken ct)
    {
        if (DiskRead?.Invoke(disk, [file, position, buffer]) is Task<bool> read)
        {
            if (!await read.ConfigureAwait(false))
            {
                throw new IOException("Could not read the requested data from the torrent");
            }

            return;
        }

        // Fallback: read the file directly (it was just flushed). MonoTorrent keeps it open, so share everything.
        using var handle = File.OpenHandle(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        var done = 0;
        while (done < buffer.Length)
        {
            var n = await RandomAccess.ReadAsync(handle, buffer[done..], position + done, ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("Unexpected end of torrent file");
            }

            done += n;
        }
    }

    /// <summary>
    /// Points the streaming piece picker at <paramref name="position"/>. Moving forward within the current window is
    /// always allowed (that's normal playback). Jumping elsewhere only happens for a reader that's waiting, at most
    /// once per <see cref="MinJumpInterval"/>. ReadToPosition moves the window without cancelling requests already sent.
    /// </summary>
    private void Steer(ITorrentManagerFile file, long position, bool waiting)
    {
        var requester = Requester;
        if (requester is null || position >= file.Length)
        {
            return;
        }

        var piece = Manager.Torrent!.ByteOffsetToPieceIndex(file.OffsetInTorrent + position);
        lock (_steerLock)
        {
            if (piece == _windowPiece)
            {
                return;
            }

            var forward = _windowPiece >= 0 && piece > _windowPiece && piece <= _windowPiece + NearPieces;
            if (!forward)
            {
                if (!waiting || DateTime.UtcNow - _lastJump < MinJumpInterval)
                {
                    return;
                }

                _lastJump = DateTime.UtcNow;
            }

            requester.ReadToPosition(file, position);
            _windowPiece = piece;
        }
    }
}

/// <summary>One reader of a torrent file, with its own position.</summary>
public sealed class StreamLease : IAsyncDisposable
{
    private readonly TorrentSession _session;
    private readonly ITorrentManagerFile _file;
    private readonly bool _playback;
    private int _disposed;

    internal StreamLease(TorrentSession session, ITorrentManagerFile file, bool playback)
    {
        _session = session;
        _file = file;
        _playback = playback;
    }

    public long Length => _file.Length;

    public long Position { get; set; }

    public void Touch() => _session.Touch();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (Position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var max = (int)Math.Min(buffer.Length, Length - Position);
        var read = await _session.ReadAtAsync(_file, Position, buffer[..max], ct).ConfigureAwait(false);
        Position += read;
        return read;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _session.Release(_file, _playback);
        }

        return ValueTask.CompletedTask;
    }
}
