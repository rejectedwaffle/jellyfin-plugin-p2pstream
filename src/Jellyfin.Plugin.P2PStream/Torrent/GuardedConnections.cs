using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Messages.UdpTracker;
using MonoTorrent.Trackers;
using ReusableTasks;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// MonoTorrent factories whose every network connection goes through <see cref="NetworkGuard"/>. MonoTorrent's own
/// defaults create peer and tracker connections directly (they ignore an overridden socket connector or HttpClient),
/// so each kind is replaced: peer connections, HTTP and UDP trackers, web seeds, DHT, and the incoming listener.
/// Everything checks the guard when it connects, so a mode change or VPN drop applies straight away.
/// </summary>
public static class GuardedFactories
{
    public static Factories Create()
    {
        var connector = new GuardedSocketConnector();
        Func<AddressFamily, HttpClient> http = NetworkGuard.CreateTorrentHttpClient;
        return Factories.Default
            .WithSocketConnectorCreator(() => connector)
            .WithStreamingPieceRequesterCreator(() => DeepStreamingRequester.Supported ? new DeepStreamingRequester() : new MonoTorrent.PiecePicking.StreamingPieceRequester())
            .WithPeerConnectionCreator("ipv4", uri => new SocketPeerConnection(uri, connector))
            .WithPeerConnectionCreator("ipv6", uri => new SocketPeerConnection(uri, connector))
            .WithHttpClientCreator(family => http(family)) // web seeds
            .WithTrackerCreator("http", uri => new Tracker(new HttpTrackerConnection(uri, http, AddressFamily.InterNetwork), new HttpTrackerConnection(uri, http, AddressFamily.InterNetworkV6)))
            .WithTrackerCreator("https", uri => new Tracker(new HttpTrackerConnection(uri, http, AddressFamily.InterNetwork), new HttpTrackerConnection(uri, http, AddressFamily.InterNetworkV6)))
            .WithTrackerCreator("udp", uri => new Tracker(new GuardedUdpTrackerConnection(uri, AddressFamily.InterNetwork), new GuardedUdpTrackerConnection(uri, AddressFamily.InterNetworkV6)))
            .WithDhtListenerCreator(endpoint => new GuardedDhtListener(endpoint))
            .WithPeerConnectionListenerCreator(endpoint => new GuardedPeerListener(endpoint));
    }
}

/// <summary>Outgoing peer connections (peer URIs look like ipv4://1.2.3.4:6881).</summary>
public sealed class GuardedSocketConnector : ISocketConnector
{
    public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
    {
        var family = uri.Scheme == "ipv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return await NetworkGuard.ConnectTcpAsync(uri.Host.Trim('[', ']'), uri.Port, token, family).ConfigureAwait(false);
    }
}

/// <summary>
/// UDP tracker client (BEP 15). Same protocol code as MonoTorrent's UdpTrackerConnection, but the packets go through
/// a socket bound to the VPN, or through the SOCKS5 proxy's UDP relay, instead of an unbound socket.
/// </summary>
public sealed class GuardedUdpTrackerConnection : ITrackerConnection
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private const int MaxRetries = 3;

    private readonly Stopwatch _lastConnected = new();
    private Task<long>? _connectionId;

    public GuardedUdpTrackerConnection(Uri announceUri, AddressFamily addressFamily)
    {
        Uri = announceUri;
        AddressFamily = addressFamily;
    }

    public bool CanScrape => true;

    public Uri Uri { get; }

    public AddressFamily AddressFamily { get; }

    public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest parameters, CancellationToken token)
    {
        try
        {
            var connectionId = await GetConnectionIdAsync(token).ConfigureAwait(false);
            var port = parameters.GetReportedAddress(AddressFamily == AddressFamily.InterNetwork ? "ipv4" : "ipv6").Item2;
            AnnounceResponse? result = null;
            foreach (var infoHash in new[] { parameters.InfoHashes.V1, parameters.InfoHashes.V2 }.Where(h => h is not null))
            {
                var message = new AnnounceMessage(Environment.TickCount ^ Random.Shared.Next(), connectionId, parameters, infoHash!, port);
                var (response, error) = await SendAndReceiveAsync(message, token).ConfigureAwait(false);
                if (error is not null)
                {
                    _connectionId = null;
                    return new AnnounceResponse(TrackerState.InvalidResponse, failureMessage: error);
                }

                var announce = (AnnounceResponseMessage)response!;
                if (result is null)
                {
                    result = new AnnounceResponse(TrackerState.Ok, new Dictionary<InfoHash, IList<PeerInfo>> { [infoHash!] = announce.Peers }, minUpdateInterval: announce.Interval);
                }
                else
                {
                    result.Peers[infoHash!] = announce.Peers;
                }
            }

            return result ?? new AnnounceResponse(TrackerState.InvalidResponse, failureMessage: "No info hash to announce");
        }
        catch (OperationCanceledException)
        {
            _connectionId = null;
            return new AnnounceResponse(TrackerState.Offline, failureMessage: "Announce could not be completed");
        }
        catch (Exception ex)
        {
            _connectionId = null;
            return new AnnounceResponse(TrackerState.InvalidResponse, failureMessage: "Announce could not be completed: " + ex.Message);
        }
    }

    /// <summary>Scrapes many torrents in one request (BEP 15 allows about 70 per packet): info hash to (seeders, leechers).</summary>
    public async Task<Dictionary<InfoHash, (int Seeders, int Leechers)>> ScrapeManyAsync(IReadOnlyList<InfoHash> hashes, CancellationToken token)
    {
        var result = new Dictionary<InfoHash, (int, int)>();
        foreach (var batch in hashes.Chunk(70))
        {
            var connectionId = await GetConnectionIdAsync(token).ConfigureAwait(false);
            var list = batch.ToList();
            var (raw, error) = await SendAndReceiveAsync(new ScrapeMessage(Environment.TickCount ^ Random.Shared.Next(), connectionId, list), token).ConfigureAwait(false);
            if (error is not null || raw is not ScrapeResponseMessage response)
            {
                _connectionId = null;
                throw new TrackerException(error ?? "Unexpected reply");
            }

            for (var i = 0; i < response.Scrapes.Count && i < list.Count; i++)
            {
                result[list[i]] = (response.Scrapes[i].Seeds, response.Scrapes[i].Leeches);
            }
        }

        return result;
    }

    public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest parameters, CancellationToken token)
    {
        try
        {
            var connectionId = await GetConnectionIdAsync(token).ConfigureAwait(false);
            var hashes = new List<InfoHash>(2);
            if (parameters.InfoHashes.V1 is not null)
            {
                hashes.Add(parameters.InfoHashes.V1);
            }

            if (parameters.InfoHashes.V2 is not null)
            {
                hashes.Add(parameters.InfoHashes.V2);
            }

            var (raw, error) = await SendAndReceiveAsync(new ScrapeMessage(Environment.TickCount ^ Random.Shared.Next(), connectionId, hashes), token).ConfigureAwait(false);
            if (error is not null || raw is not ScrapeResponseMessage response)
            {
                _connectionId = null;
                return new ScrapeResponse(TrackerState.InvalidResponse, failureMessage: error ?? "Unexpected reply");
            }

            var info = new Dictionary<InfoHash, ScrapeInfo>();
            for (var i = 0; i < response.Scrapes.Count && i < hashes.Count; i++)
            {
                info[hashes[i]] = new ScrapeInfo(response.Scrapes[i].Seeds, response.Scrapes[i].Complete, response.Scrapes[i].Leeches);
            }

            return new ScrapeResponse(TrackerState.Ok, info);
        }
        catch (OperationCanceledException)
        {
            _connectionId = null;
            return new ScrapeResponse(TrackerState.Offline, failureMessage: "Scrape could not be completed");
        }
        catch (Exception ex)
        {
            _connectionId = null;
            return new ScrapeResponse(TrackerState.InvalidResponse, failureMessage: "Scrape could not be completed: " + ex.Message);
        }
    }

    private Task<long> GetConnectionIdAsync(CancellationToken token)
    {
        // A connection id is valid for 2 minutes; renew after 1 like MonoTorrent does.
        if (_connectionId is null || _connectionId.IsFaulted || _lastConnected.Elapsed > TimeSpan.FromMinutes(1))
        {
            _lastConnected.Restart();
            _connectionId = ConnectAsync(token);
        }

        return _connectionId;
    }

    private async Task<long> ConnectAsync(CancellationToken token)
    {
        var (raw, error) = await SendAndReceiveAsync(new ConnectMessage(), token).ConfigureAwait(false);
        if (error is not null)
        {
            throw new TrackerException(error);
        }

        _lastConnected.Restart();
        return raw is ConnectResponseMessage r ? r.ConnectionId : throw new InvalidOperationException("No connect response");
    }

    private async Task<(UdpTrackerMessage? Response, string? Error)> SendAndReceiveAsync(UdpTrackerMessage message, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(RetryDelay * MaxRetries);
        var ct = cts.Token;
        var state = NetworkGuard.Require();
        var payload = message.Encode();

        if (state.Mode == NetworkMode.Proxy)
        {
            if (state.Proxy!.Scheme is not ("socks5" or "socks5h"))
            {
                throw new NetworkBlockedException("UDP trackers need a SOCKS5 proxy");
            }

            if (AddressFamily == AddressFamily.InterNetworkV6)
            {
                throw new NetworkBlockedException("IPv6 UDP trackers are skipped through a proxy");
            }

            if (IPAddress.TryParse(Uri.Host, out var proxied))
            {
                AddressPolicy.Require(proxied, Uri.Host, "tracker");
            }

            using var relay = await Socks5UdpRelay.OpenAsync(state.Proxy, ct).ConfigureAwait(false);
            _ = RepeatAsync(t => relay.SendAsync(Uri.Host, Uri.Port, payload, t), ct);
            while (true)
            {
                var reply = await relay.ReceiveAsync(ct).ConfigureAwait(false);
                if (Decode(reply, message.TransactionId) is { } decoded)
                {
                    return decoded;
                }
            }
        }

        // Direct or VPN: resolve the tracker, then use a socket bound to the VPN (or a plain one when direct).
        var addresses = IPAddress.TryParse(Uri.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(Uri.Host, AddressFamily, ct).ConfigureAwait(false);
        var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily)
            ?? throw new SocketException((int)SocketError.HostNotFound);
        AddressPolicy.Require(target, Uri.Host, "tracker"); // a tracker listed in a torrent can't point into your network
        using var socket = NetworkGuard.CreateUdpSocket(AddressFamily);
        var endpoint = new IPEndPoint(target, Uri.Port);
        _ = RepeatAsync(t => socket.SendToAsync(payload, SocketFlags.None, endpoint, t).AsTask(), ct);
        var buffer = new byte[65536];
        while (true)
        {
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), ct).ConfigureAwait(false);
            if (Decode(buffer.AsSpan(0, received.ReceivedBytes).ToArray(), message.TransactionId) is { } decoded)
            {
                return decoded;
            }
        }
    }

    private (UdpTrackerMessage?, string?)? Decode(byte[] data, int transactionId)
    {
        UdpTrackerMessage response;
        try
        {
            response = UdpTrackerMessage.DecodeMessage(data, MessageType.Response, AddressFamily);
        }
        catch (Exception)
        {
            return null;
        }

        if (response.TransactionId != transactionId)
        {
            return null;
        }

        return response is ErrorMessage e ? (null, e.Error ?? "The tracker returned an error") : (response, null);
    }

    /// <summary>UDP is lossy: resend every 15 seconds until answered (like MonoTorrent).</summary>
    private static async Task RepeatAsync(Func<CancellationToken, Task> send, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await send(ct).ConfigureAwait(false);
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // cancelled, or the socket closed
        }
    }
}

/// <summary>DHT socket bound like every other connection (in proxy mode DHT is off, so this isn't created).</summary>
public sealed class GuardedDhtListener : SocketListener, IDhtListener
{
    private Socket? _socket;
    private DateTime _lastError = DateTime.MinValue;

    public GuardedDhtListener(IPEndPoint endpoint)
        : base(endpoint)
    {
    }

    public event Action<ReadOnlyMemory<byte>, IPEndPoint>? MessageReceived;

    public async Task SendAsync(ReadOnlyMemory<byte> buffer, IPEndPoint endpoint)
    {
        var socket = _socket ?? throw new InvalidOperationException("The DHT listener isn't running");
        if (!NetworkGuard.State.Ok)
        {
            return; // kill switch: drop
        }

        await socket.SendToAsync(buffer, SocketFlags.None, endpoint).ConfigureAwait(false);
    }

    protected override void Start(CancellationToken token)
    {
        base.Start(token);
        var socket = NetworkGuard.CreateUdpSocket(PreferredLocalEndPoint.AddressFamily, PreferredLocalEndPoint);
        _socket = socket;
        LocalEndPoint = (IPEndPoint?)socket.LocalEndPoint;
        token.Register(() =>
        {
            socket.Dispose();
            _socket = null;
        });
        _ = ReceiveLoopAsync(socket, token);
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[65536];
        var any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
                MessageReceived?.Invoke(buffer.AsMemory(0, r.ReceivedBytes).ToArray(), (IPEndPoint)r.RemoteEndPoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port unreachable from an earlier send; keep listening
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (DateTime.UtcNow - _lastError > TimeSpan.FromMinutes(10))
                {
                    _lastError = DateTime.UtcNow;
                    NetworkGuard.Log($"DHT can't receive on {LocalEndPoint}: {ex.Message}");
                }

                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Incoming peer connections, on a socket bound like every other connection.</summary>
public sealed class GuardedPeerListener : SocketListener, IPeerConnectionListener
{
    public GuardedPeerListener(IPEndPoint endpoint)
        : base(endpoint)
    {
    }

    public event EventHandler<PeerConnectionEventArgs>? ConnectionReceived;

    protected override void Start(CancellationToken token)
    {
        base.Start(token);
        var state = NetworkGuard.Require();
        var listener = new Socket(PreferredLocalEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (state.Mode == NetworkMode.Interface)
            {
                NetworkGuard.BindToInterface(listener, state, PreferredLocalEndPoint.AddressFamily, PreferredLocalEndPoint.Port);
            }
            else
            {
                listener.Bind(PreferredLocalEndPoint);
            }

            LocalEndPoint = (IPEndPoint?)listener.LocalEndPoint;
            listener.Listen(16);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        token.Register(listener.Dispose);
        _ = AcceptLoopAsync(listener, token);
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // stopped
            }

            if (!NetworkGuard.State.Ok)
            {
                socket.Dispose(); // kill switch
                continue;
            }

            try
            {
                ConnectionReceived?.Invoke(this, new PeerConnectionEventArgs(new SocketPeerConnection(socket, true), null));
            }
            catch (Exception)
            {
                socket.Dispose();
            }
        }
    }
}
