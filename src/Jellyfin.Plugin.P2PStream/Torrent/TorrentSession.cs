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
    private int _activeStreams;
    private volatile bool _closed;

    public TorrentSession(string infoHash, TorrentManager manager)
    {
        InfoHash = infoHash;
        Manager = manager;
        LastAccess = DateTime.UtcNow;
    }

    public string InfoHash { get; }

    public TorrentManager Manager { get; }

    public DateTime LastAccess { get; private set; }

    public int ActiveStreams => Volatile.Read(ref _activeStreams);

    private IStreamingPieceRequester? Requester => Manager.StreamProvider is { } p ? RequesterProperty?.GetValue(p) as IStreamingPieceRequester : null;

    public void Touch() => LastAccess = DateTime.UtcNow;

    /// <summary>Opens a reader on the file. Any number of readers can be open at once. Dispose the lease when done.</summary>
    public async Task<StreamLease> OpenAsync(ITorrentManagerFile file, CancellationToken ct)
    {
        await _setup.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_file != file)
            {
                // Only the file being watched is downloaded.
                foreach (var other in Manager.Files.Where(f => f != file && f.Priority != Priority.DoNotDownload))
                {
                    await Manager.SetFilePriorityAsync(other, Priority.DoNotDownload).ConfigureAwait(false);
                }

                if (file.Priority == Priority.DoNotDownload)
                {
                    await Manager.SetFilePriorityAsync(file, Priority.Normal).ConfigureAwait(false);
                }

                _file = file;
                lock (_steerLock)
                {
                    _windowPiece = -1;
                }
            }

            Interlocked.Increment(ref _activeStreams);
            Touch();
            return new StreamLease(this, file);
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

    internal void Release()
    {
        Interlocked.Decrement(ref _activeStreams);
        Touch();
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
    private int _disposed;

    internal StreamLease(TorrentSession session, ITorrentManagerFile file)
    {
        _session = session;
        _file = file;
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
            _session.Release();
        }

        return ValueTask.CompletedTask;
    }
}
