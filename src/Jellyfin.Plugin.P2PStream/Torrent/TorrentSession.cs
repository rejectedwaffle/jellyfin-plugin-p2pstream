using MonoTorrent;
using MonoTorrent.Client;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// One active torrent. MonoTorrent allows a single open stream per torrent, so a new
/// request pre-empts the previous one (a seek or a fresh ffmpeg probe always wins).
/// </summary>
public sealed class TorrentSession
{
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private readonly object _ctsLock = new();
    private CancellationTokenSource? _activeCts;
    private int _activeStreams;

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

    public void Touch() => LastAccess = DateTime.UtcNow;

    public void CancelActiveStream()
    {
        lock (_ctsLock)
        {
            _activeCts?.Cancel();
        }
    }

    /// <summary>
    /// Opens the file for streaming, cancelling any reader currently holding it.
    /// Dispose the returned lease to release the stream.
    /// </summary>
    public async Task<StreamLease> OpenAsync(ITorrentManagerFile file, CancellationToken requestToken)
    {
        CancelActiveStream();
        await _streamGate.WaitAsync(requestToken).ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        lock (_ctsLock)
        {
            _activeCts = cts;
        }

        try
        {
            if (Manager.Files.Any(f => f != file && f.Priority != Priority.DoNotDownload))
            {
                foreach (var other in Manager.Files.Where(f => f != file))
                {
                    await Manager.SetFilePriorityAsync(other, Priority.DoNotDownload).ConfigureAwait(false);
                }
            }

            if (file.Priority == Priority.DoNotDownload)
            {
                await Manager.SetFilePriorityAsync(file, Priority.Normal).ConfigureAwait(false);
            }

            var stream = await CreateStreamAsync(file, cts.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _activeStreams);
            Touch();
            return new StreamLease(this, stream, cts);
        }
        catch
        {
            ReleaseCore(cts);
            throw;
        }
    }

    /// <summary>
    /// Disposes a stream MonoTorrent still counts as open although nobody holds it. This happens when
    /// CreateStreamAsync is cancelled mid-prebuffer: MonoTorrent has already registered the stream and never
    /// releases it, so every later CreateStreamAsync throws "You must Dispose the previous stream".
    /// Only called while holding the stream gate, so any open stream at that point is an orphan.
    /// </summary>
    internal static bool ReleaseOrphanedStream(MonoTorrent.Streaming.StreamProvider provider)
    {
        if (ActiveStreamProperty?.GetValue(provider) is Stream orphan)
        {
            orphan.Dispose(); // idempotent if it was already disposed
            return true;
        }

        return false;
    }

    // StreamProvider.ActiveStream is internal in MonoTorrent 3.0.2. Null if a future version renames it, which only skips the cleanup.
    private static readonly System.Reflection.PropertyInfo? ActiveStreamProperty =
        typeof(MonoTorrent.Streaming.StreamProvider).GetProperty("ActiveStream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private async Task<Stream> CreateStreamAsync(ITorrentManagerFile file, CancellationToken ct)
    {
        var provider = Manager.StreamProvider!;
        ReleaseOrphanedStream(provider);
        try
        {
            // No prebuffer: it's the cancellable wait that orphans streams, and reads block until data arrives anyway.
            return await provider.CreateStreamAsync(file, prebuffer: false, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (ReleaseOrphanedStream(provider))
        {
            return await provider.CreateStreamAsync(file, prebuffer: false, ct).ConfigureAwait(false);
        }
    }

    internal void Release(CancellationTokenSource cts)
    {
        Interlocked.Decrement(ref _activeStreams);
        ReleaseCore(cts);
    }

    private void ReleaseCore(CancellationTokenSource cts)
    {
        lock (_ctsLock)
        {
            if (ReferenceEquals(_activeCts, cts))
            {
                _activeCts = null;
            }
        }

        cts.Dispose();
        Touch();
        _streamGate.Release();
    }
}

public sealed class StreamLease : IAsyncDisposable
{
    private readonly TorrentSession _session;
    private readonly CancellationTokenSource _cts;
    private int _disposed;

    internal StreamLease(TorrentSession session, Stream stream, CancellationTokenSource cts)
    {
        _session = session;
        Stream = stream;
        _cts = cts;
    }

    public Stream Stream { get; }

    /// <summary>Gets a token cancelled when the client disconnects or a newer request takes over.</summary>
    public CancellationToken Token => _cts.Token;

    public void Touch() => _session.Touch();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _session.Release(_cts);
        }
    }
}
