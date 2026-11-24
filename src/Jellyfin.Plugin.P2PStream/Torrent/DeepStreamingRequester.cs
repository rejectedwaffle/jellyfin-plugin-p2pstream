using System.Reflection;
using MonoTorrent;
using MonoTorrent.PiecePicking;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// MonoTorrent's streaming requester, with deeper request queues. The stock one keeps only 2-4 blocks (16 kB each)
/// requested from each peer, so a peer can send at most ~64 kB per round trip: through a VPN to peers worldwide
/// (0.2-0.3 s round trips) that caps each peer at ~200-300 kB/s however fast it is, and 4K never keeps up.
/// This lets the stock requester pick the urgent pieces first, as before, then tops every unchoked peer up to the
/// engine's own per-peer limit (8 + 1 per 10 kB/s that peer delivers) with pieces from the window ahead of the player.
/// </summary>
public sealed class DeepStreamingRequester : IStreamingPieceRequester
{
    private const long WindowBytes = 64L << 20; // how far ahead of the player to fetch in parallel
    private const int UrgentPieces = 15;         // the stock requester's high-priority set

    private static readonly PropertyInfo? HighPickerProperty = typeof(StreamingPieceRequester).GetProperty("HighPriorityPicker", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly PropertyInfo? LowPickerProperty = typeof(StreamingPieceRequester).GetProperty("LowPriorityPicker", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly StreamingPieceRequester _inner = new();
    private IPieceRequesterData? _data;
    private IMessageEnqueuer? _enqueuer;

    // The stock requester's pickers: requests must go through them, as they track what's been requested from whom
    // (a block that wasn't requested through them is thrown away when it arrives).
    private IPiecePicker? _high;
    private IPiecePicker? _low;
    private ReadOnlyBitField[] _others = [];
    private ReadOnlyBitField[] _haves = [];

    /// <summary>False if this MonoTorrent version doesn't have the expected internals; then it behaves exactly like the stock one.</summary>
    public static bool Supported => HighPickerProperty is not null && LowPickerProperty is not null;

    public bool InEndgameMode => _inner.InEndgameMode;

    public void Initialise(IPieceRequesterData torrentData, IMessageEnqueuer enqueuer, ReadOnlySpan<ReadOnlyBitField> ignorableBitfields)
    {
        _inner.Initialise(torrentData, enqueuer, ignorableBitfields);
        _data = torrentData;
        _enqueuer = enqueuer;
        _haves = ignorableBitfields.ToArray(); // the pieces we have (and others not to request)
        _high = HighPickerProperty?.GetValue(_inner) as IPiecePicker;
        _low = LowPickerProperty?.GetValue(_inner) as IPiecePicker;
    }

    public void AddRequests(ReadOnlySpan<(IRequester Peer, ReadOnlyBitField Available)> peers)
    {
        _inner.AddRequests(peers);
        if (_others.Length < peers.Length)
        {
            _others = new ReadOnlyBitField[peers.Length];
        }

        var others = _others.AsSpan(0, peers.Length);
        for (var i = 0; i < peers.Length; i++)
        {
            others[i] = peers[i].Available;
        }

        foreach (var (peer, available) in peers)
        {
            TopUp(peer, available, others);
        }
    }

    public void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> peers)
    {
        _inner.AddRequests(peer, available, peers);
        TopUp(peer, available, peers);
    }

    private void TopUp(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> others)
    {
        if (_data is null || _enqueuer is null || _high is null || _low is null || peer.IsChoking || _data.PieceCount == 0)
        {
            return;
        }

        var start = Math.Clamp(_inner.HighPriorityPieceIndex, 0, _data.PieceCount - 1);
        var urgentEnd = Math.Min(_data.PieceCount - 1, start + UrgentPieces - 1);
        var windowPieces = (int)Math.Max(UrgentPieces * 2, WindowBytes / Math.Max(1, _data.PieceLength));
        var windowEnd = (int)Math.Min(_data.PieceCount - 1L, (long)start + windowPieces - 1);

        // While the player waits for its current piece, leave requesting to the stock logic: it asks several peers for
        // that piece at once, which it can't do once their queues are full (measured: first byte 7-11 s instead of ~6 s).
        if (!Array.Exists(_haves, b => b.Length > start && b[start]))
        {
            return;
        }

        var target = peer.MaxPendingRequests;

        Span<PieceSegment> buffer = stackalloc PieceSegment[64];
        for (var guard = 0; guard < 64 && peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < target; guard++)
        {
            var room = buffer[..Math.Min(buffer.Length, target - peer.AmRequestingPiecesCount)];

            // Nearest first (in order), then anywhere in the window (rarest first, as the stock requester does).
            var picked = _high.PickPiece(peer, available, others, start, urgentEnd, room);
            if (picked == 0)
            {
                picked = _low.PickPiece(peer, available, others, start, windowEnd, room);
            }

            if (picked == 0)
            {
                if (_low.ContinueAnyExistingRequest(peer, available, start, windowEnd, 1, out var segment))
                {
                    _enqueuer.EnqueueRequest(peer, segment);
                    continue;
                }

                break;
            }

            _enqueuer.EnqueueRequests(peer, room[..picked]);
        }
    }

    public bool ValidatePiece(IRequester peer, PieceSegment pieceSegment, out bool pieceComplete, HashSet<IRequester> peersInvolved)
        => _inner.ValidatePiece(peer, pieceSegment, out pieceComplete, peersInvolved);

    public bool IsInteresting(IRequester peer, ReadOnlyBitField bitField) => _inner.IsInteresting(peer, bitField);

    public void CancelRequests(IRequester peer, int startIndex, int endIndex) => _inner.CancelRequests(peer, startIndex, endIndex);

    public void RequestRejected(IRequester peer, PieceSegment pieceRequest) => _inner.RequestRejected(peer, pieceRequest);

    public int CurrentRequestCount() => _inner.CurrentRequestCount();

    public void SeekToPosition(ITorrentManagerFile file, long position) => _inner.SeekToPosition(file, position);

    public void ReadToPosition(ITorrentManagerFile file, long position) => _inner.ReadToPosition(file, position);
}
