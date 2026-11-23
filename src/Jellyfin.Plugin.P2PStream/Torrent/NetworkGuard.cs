using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>How torrent traffic leaves the server.</summary>
public enum NetworkMode
{
    /// <summary>Normal connections.</summary>
    Direct,

    /// <summary>Only through one network interface, such as a VPN's wg0 or tun0.</summary>
    Interface,

    /// <summary>Only through the proxy set on the Engine tab.</summary>
    Proxy
}

/// <summary>Where torrent traffic may go right now. When <see cref="Ok"/> is false nothing is sent at all.</summary>
public sealed record NetworkState(NetworkMode Mode, bool Ok, string Message, IPAddress? Address = null, IPAddress? Address6 = null, string? Device = null, Uri? Proxy = null);

/// <summary>Thrown instead of connecting when the VPN or proxy that torrent traffic must use isn't available.</summary>
public sealed class NetworkBlockedException(string message) : IOException(message);

/// <summary>
/// The kill switch. Every connection the torrent engine makes (peers, trackers, DHT, web seeds, listeners) is created
/// here. In VPN mode each socket is bound to the VPN interface's address (and, on Linux, to the device itself), so if
/// the VPN drops the connection fails rather than going out another way. In proxy mode TCP goes through the proxy
/// (SOCKS5 or HTTP CONNECT), UDP trackers through SOCKS5 UDP, and DHT and incoming connections are off. Nothing ever
/// falls back to a direct connection.
/// </summary>
public static class NetworkGuard
{
    private const int SolSocket = 1;      // Linux SOL_SOCKET
    private const int SoBindToDevice = 25; // Linux SO_BINDTODEVICE

    private static readonly object Lock = new();
    private static NetworkState _state = new(NetworkMode.Direct, true, "Torrents connect directly.");
    private static volatile bool _bindToDeviceUnavailable;
    private static Action<string>? _log;

    /// <summary>Raised (old, new) when the mode, address or availability changes.</summary>
    public static event Action<NetworkState, NetworkState>? Changed;

    public static NetworkState State => Volatile.Read(ref _state);

    /// <summary>Gets a value indicating whether binding sockets to the device (SO_BINDTODEVICE) works here.</summary>
    public static bool DeviceBindingWorks => OperatingSystem.IsLinux() && !_bindToDeviceUnavailable;

    public static void SetLogger(Action<string> log) => _log = log;

    /// <summary>Writes to the Jellyfin log (set at startup).</summary>
    public static void Log(string message) => _log?.Invoke(message);

    public static NetworkMode ConfiguredMode(Configuration.PluginConfiguration? c) => (c?.TorrentNetwork ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "interface" or "vpn" => NetworkMode.Interface,
        "proxy" => NetworkMode.Proxy,
        _ => NetworkMode.Direct
    };

    /// <summary>Re-reads the settings and the network interfaces (and checks the proxy answers). Raises <see cref="Changed"/> if anything changed.</summary>
    public static async Task<NetworkState> RefreshAsync(CancellationToken ct = default)
    {
        var c = Plugin.Instance?.Configuration;
        var mode = ConfiguredMode(c);
        var next = mode switch
        {
            NetworkMode.Interface => ResolveInterface(c?.BindInterface),
            NetworkMode.Proxy => await ResolveProxyAsync(c, ct).ConfigureAwait(false),
            _ => new NetworkState(NetworkMode.Direct, true, "Torrents connect directly.")
        };
        Apply(next);
        return next;
    }

    /// <summary>Sets the state (used by RefreshAsync, and by tests).</summary>
    public static void Apply(NetworkState next)
    {
        NetworkState old;
        lock (Lock)
        {
            old = _state;
            _state = next;
        }

        if (old.Mode != next.Mode || old.Ok != next.Ok || !Equals(old.Address, next.Address) || !Equals(old.Address6, next.Address6)
            || old.Device != next.Device || old.Proxy != next.Proxy)
        {
            _log?.Invoke($"Torrent network: {next.Message}");
            ResetHttpPools();
            Changed?.Invoke(old, next);
        }
    }

    /// <summary>Throws if torrent traffic is blocked right now.</summary>
    public static NetworkState Require()
    {
        var s = State;
        if (!s.Ok)
        {
            throw new NetworkBlockedException(s.Message);
        }

        return s;
    }

    /// <summary>The network interfaces, for the settings page.</summary>
    public static List<object> ListInterfaces()
    {
        var list = new List<object>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var addresses = Addresses(nic).Select(a => a.ToString()).ToList();
            list.Add(new
            {
                nic.Name,
                nic.Description,
                Up = IsUp(nic),
                Addresses = addresses,
                LooksLikeVpn = LooksLikeVpn(nic)
            });
        }

        return list;
    }

    // ------------------------------------------------------------------ interface (VPN)

    public static NetworkState ResolveInterface(string? wanted)
    {
        wanted = wanted?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            return new NetworkState(NetworkMode.Interface, false, "Blocked: choose the VPN's network interface on the Engine tab. Nothing is downloaded or shared until then.");
        }

        NetworkInterface? nic = null;
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            nic = all.FirstOrDefault(n => string.Equals(n.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(n => string.Equals(n.Description, wanted, StringComparison.OrdinalIgnoreCase))
                ?? (IPAddress.TryParse(wanted, out var ip) ? all.FirstOrDefault(n => Addresses(n).Contains(ip)) : null);
        }
        catch (NetworkInformationException)
        {
        }

        if (nic is null)
        {
            return new NetworkState(NetworkMode.Interface, false, $"Blocked: the network interface \"{wanted}\" doesn't exist (is the VPN connected?). Nothing is downloaded or shared until it's back.", Device: wanted);
        }

        if (!IsUp(nic))
        {
            return new NetworkState(NetworkMode.Interface, false, $"Blocked: {nic.Name} is down (is the VPN connected?). Nothing is downloaded or shared until it's back.", Device: nic.Name);
        }

        var v4 = Addresses(nic).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var v6 = Addresses(nic).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal);
        if (v4 is null && v6 is null)
        {
            return new NetworkState(NetworkMode.Interface, false, $"Blocked: {nic.Name} has no IP address yet (is the VPN connected?).", Device: nic.Name);
        }

        var shown = string.Join(", ", new[] { v4, v6 }.Where(a => a is not null));
        return new NetworkState(NetworkMode.Interface, true, $"Only through {nic.Name} ({shown}).", v4, v6, nic.Name);
    }

    private static bool IsUp(NetworkInterface nic) =>
        nic.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown or OperationalStatus.Dormant; // WireGuard reports "unknown"

    private static readonly string[] VpnNameHints =
    [
        "wg", "tun", "tap", "vpn", "wireguard", "pia", "proton", "mullvad", "nord", "surfshark", "expressvpn", "windscribe",
        "ivpn", "airvpn", "openvpn", "ppp", "ipsec", "tailscale", "zt", "nebula"
    ];

    /// <summary>
    /// A VPN tunnel: a VPN-style name, a point-to-point/tunnel type, or (Linux WireGuard and OpenVPN tun devices) no
    /// hardware address at all, unlike real network cards and bridges.
    /// </summary>
    private static bool LooksLikeVpn(NetworkInterface nic)
    {
        var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel || VpnNameHints.Any(text.Contains))
        {
            return true;
        }

        try
        {
            return OperatingSystem.IsLinux() && nic.GetPhysicalAddress().GetAddressBytes().Length == 0
                && !text.StartsWith("docker", StringComparison.Ordinal) && !text.StartsWith("veth", StringComparison.Ordinal);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static IEnumerable<IPAddress> Addresses(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses.Select(u => u.Address).ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ proxy

    private static async Task<NetworkState> ResolveProxyAsync(Configuration.PluginConfiguration? c, CancellationToken ct)
    {
        if (!TryParseProxy(c?.ProxyUrl, out var proxy, out var problem))
        {
            return new NetworkState(NetworkMode.Proxy, false, "Blocked: " + problem + " Nothing is downloaded or shared until then.");
        }

        // Check it answers, so the settings page can say so (and nothing tries peers for minutes for no reason).
        var reason = await ProbeProxyAsync(proxy, ct).ConfigureAwait(false);
        if (reason is not null)
        {
            return new NetworkState(NetworkMode.Proxy, false, $"Blocked: the proxy {proxy.Host}:{proxy.Port} isn't answering ({reason}). Nothing is downloaded or shared until it is.", Proxy: proxy);
        }

        var dhtNote = IsSocks(proxy) ? "UDP trackers via SOCKS5; DHT off" : "HTTP proxy: TCP only, UDP trackers and DHT off";
        return new NetworkState(NetworkMode.Proxy, true, $"Only through the proxy {proxy.Host}:{proxy.Port} ({dhtNote}; no incoming connections).", Proxy: proxy);
    }

    /// <summary>Connects to the proxy (a few of its addresses, if it has several). Returns null if it answers, else why not.</summary>
    private static async Task<string?> ProbeProxyAsync(Uri proxy, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(proxy.Host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(proxy.Host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return "its address can't be looked up: " + ex.SocketErrorCode;
        }

        string? last = "it has no address";
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Take(3))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await s.ConnectAsync(new IPEndPoint(address, proxy.Port), timeout.Token).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = $"no reply from {address} within 4 seconds; a firewall may block port {proxy.Port}";
            }
            catch (SocketException ex)
            {
                last = $"{address}: {ex.SocketErrorCode}";
            }
        }

        return last;
    }

    public static bool TryParseProxy(string? url, out Uri proxy, out string problem)
    {
        proxy = null!;
        if (string.IsNullOrWhiteSpace(url))
        {
            problem = "set a proxy address on the Engine tab.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Port <= 0)
        {
            problem = "the proxy address isn't valid (example: socks5://127.0.0.1:1080).";
            return false;
        }

        if (u.Scheme is not ("socks5" or "socks5h" or "http"))
        {
            problem = $"\"{u.Scheme}\" proxies aren't supported for torrents; use socks5:// (best) or http://.";
            return false;
        }

        proxy = u;
        problem = string.Empty;
        return true;
    }

    private static bool IsSocks(Uri proxy) => proxy.Scheme is "socks5" or "socks5h";

    // ------------------------------------------------------------------ connecting

    /// <summary>Opens a TCP connection the way the current mode allows. <paramref name="host"/> may be a name or an IP address.</summary>
    public static async Task<Socket> ConnectTcpAsync(string host, int port, CancellationToken ct, AddressFamily family = AddressFamily.Unspecified)
    {
        var s = Require();
        switch (s.Mode)
        {
            case NetworkMode.Interface:
            {
                var target = await ResolveAsync(host, family, s, ct).ConfigureAwait(false);
                var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    BindToInterface(socket, s, target.AddressFamily);
                    await socket.ConnectAsync(new IPEndPoint(target, port), ct).ConfigureAwait(false);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            case NetworkMode.Proxy:
                return await ConnectViaProxyAsync(s.Proxy!, host, port, ct).ConfigureAwait(false);

            default:
            {
                Socket socket;
                if (IPAddress.TryParse(host, out var ip))
                {
                    socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }

                    return socket;
                }

                socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; // dual-mode
                try
                {
                    await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }

                return socket;
            }
        }
    }

    /// <summary>A UDP socket for the current mode (not for proxy mode, which uses <see cref="Socks5UdpRelay"/>).</summary>
    public static Socket CreateUdpSocket(AddressFamily family, IPEndPoint? bindTo = null)
    {
        var s = Require();
        if (s.Mode == NetworkMode.Proxy)
        {
            throw new NetworkBlockedException("UDP goes through the SOCKS5 relay in proxy mode");
        }

        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            IgnoreUdpResets(socket);
            if (s.Mode == NetworkMode.Interface)
            {
                BindToInterface(socket, s, family, bindTo?.Port ?? 0);
            }
            else if (bindTo is not null)
            {
                socket.Bind(bindTo);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Shared connection pools (like MonoTorrent's own): MonoTorrent asks for a new HttpClient per tracker and web seed
    // request, so each must reuse pooled connections instead of opening a new connection (and tunnel) every time.
    // The pools are thrown away whenever the network state changes, so no connection made the old way is reused.
    private static readonly object HandlerLock = new();
    private static SocketsHttpHandler? _webHandler;
    private static SocketsHttpHandler?[] _torrentHandlers = new SocketsHttpHandler?[3];

    private static SocketsHttpHandler WebHandler
    {
        get
        {
            lock (HandlerLock)
            {
                return _webHandler ??= CreateHandler(AddressFamily.Unspecified, decompress: true);
            }
        }
    }

    private static SocketsHttpHandler TorrentHandler(int slot, AddressFamily family)
    {
        lock (HandlerLock)
        {
            return _torrentHandlers[slot] ??= CreateHandler(family, decompress: false);
        }
    }

    /// <summary>Closes every pooled HTTP connection (in-flight requests fail), so the next request connects the new way.</summary>
    private static void ResetHttpPools()
    {
        List<SocketsHttpHandler> old;
        lock (HandlerLock)
        {
            old = new[] { _webHandler }.Concat(_torrentHandlers).OfType<SocketsHttpHandler>().ToList();
            _webHandler = null;
            _torrentHandlers = new SocketsHttpHandler?[3];
        }

        foreach (var h in old)
        {
            h.Dispose();
        }
    }

    /// <summary>An HttpClient whose every connection goes the way the current mode allows (checked per connection, so a VPN drop or mode change applies at once).</summary>
    public static HttpClient CreateHttpClient(bool followRedirects = true, TimeSpan? timeout = null)
    {
        var client = followRedirects
            ? new HttpClient(WebHandler, disposeHandler: false)
            : new HttpClient(CreateHandler(AddressFamily.Unspecified, decompress: true, followRedirects: false));
        client.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jellyfin-P2PStream");
        return client;
    }

    /// <summary>For MonoTorrent's trackers and web seeds (per address family, no decompression, like MonoTorrent's own).</summary>
    public static HttpClient CreateTorrentHttpClient(AddressFamily family)
    {
        var handler = family switch
        {
            AddressFamily.InterNetwork => TorrentHandler(1, family),
            AddressFamily.InterNetworkV6 => TorrentHandler(2, family),
            _ => TorrentHandler(0, AddressFamily.Unspecified)
        };
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-P2PStream");
        return client;
    }

    private static SocketsHttpHandler CreateHandler(AddressFamily family, bool decompress, bool followRedirects = true) => new()
    {
        AllowAutoRedirect = followRedirects,
        AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1), // so a mode change soon applies to pooled connections too
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            var socket = await ConnectTcpAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct, family).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
    };

    /// <summary>Binds a socket to the VPN interface: its address (all platforms) and the device itself (Linux, if allowed).</summary>
    public static void BindToInterface(Socket socket, NetworkState s, AddressFamily family, int port = 0)
    {
        var address = family == AddressFamily.InterNetworkV6 ? s.Address6 : s.Address;
        if (address is null)
        {
            throw new NetworkBlockedException($"{s.Device} has no {(family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")} address");
        }

        if (OperatingSystem.IsLinux() && !_bindToDeviceUnavailable && s.Device is { Length: > 0 } device)
        {
            try
            {
                socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(device + "\0"));
            }
            catch (SocketException ex)
            {
                // Older kernels need CAP_NET_RAW. Binding to the address still works with policy routing or a full-tunnel VPN.
                _bindToDeviceUnavailable = true;
                _log?.Invoke($"Can't bind sockets to {device} directly ({ex.Message}); binding to its address {address} instead.");
            }
        }

        socket.Bind(new IPEndPoint(address, port));
    }

    private static async Task<IPAddress> ResolveAsync(string host, AddressFamily family, NetworkState s, CancellationToken ct)
    {
        IPAddress[] candidates = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var usable = candidates.Where(a =>
            (family == AddressFamily.Unspecified || a.AddressFamily == family)
            && ((a.AddressFamily == AddressFamily.InterNetwork && s.Address is not null) || (a.AddressFamily == AddressFamily.InterNetworkV6 && s.Address6 is not null)));
        return usable.FirstOrDefault()
            ?? throw new NetworkBlockedException($"{host} has no address reachable through {s.Device}");
    }

    private static void IgnoreUdpResets(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                const int SioUdpConnReset = -1744830452;
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ proxy handshakes

    /// <summary>What the settings page's proxy test found.</summary>
    /// <param name="Problem">Why it doesn't work at all (can't connect, login rejected), or null.</param>
    /// <param name="Login">True if a login was sent and accepted, null if there's no login.</param>
    /// <param name="Ip">The address the internet sees through the proxy.</param>
    /// <param name="Udp">Whether UDP tracker packets come back (null for an HTTP proxy, which can't carry UDP).</param>
    /// <param name="Trackers">Whether a real torrent tracker answers through the proxy (TCP or UDP).</param>
    public sealed record ProxyTestResult(string? Problem, bool? Login, string? Ip, bool? Udp, bool Trackers);

    /// <summary>
    /// The settings page's test: connects through the proxy (whatever mode is on) and checks each thing torrents need:
    /// it answers, the login is accepted, the address the internet sees, and whether a real tracker answers over TCP
    /// and UDP. VPN providers' SOCKS servers often carry web traffic but block BitTorrent.
    /// </summary>
    public static async Task<ProxyTestResult> TestProxyAsync(Uri proxy, Func<HttpClient, CancellationToken, Task<string?>> whatIsMyIp, CancellationToken ct)
    {
        var reach = await ProbeProxyAsync(proxy, ct).ConfigureAwait(false);
        if (reach is not null)
        {
            return new ProxyTestResult("Can't connect to it (" + reach + ")", null, null, null, false);
        }

        var hasLogin = ProxyCredentials(proxy).User.Length > 0;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, token) => new NetworkStream(await ConnectViaProxyAsync(proxy, context.DnsEndPoint.Host, context.DnsEndPoint.Port, token).ConfigureAwait(false), ownsSocket: true)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        string? ip;
        try
        {
            ip = await whatIsMyIp(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var message = ex.GetBaseException().Message;
            return message.Contains("login", StringComparison.OrdinalIgnoreCase)
                ? new ProxyTestResult("The login was rejected: check the login and password", false, null, null, false)
                : new ProxyTestResult("It answers, but doesn't pass traffic on (" + message + ")", hasLogin ? true : null, null, null, false);
        }

        var tcpTracker = false;
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await client.GetAsync(new Uri("http://tracker.opentrackr.org:1337/announce"), t.Token).ConfigureAwait(false);
            tcpTracker = true; // any HTTP answer (even an error about missing parameters) means the tracker is reachable
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }

        bool? udp = null;
        if (IsSocks(proxy))
        {
            udp = false;
            try
            {
                using var relay = await Socks5UdpRelay.OpenAsync(proxy, ct).ConfigureAwait(false);
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(TimeSpan.FromSeconds(8));
                var request = new MonoTorrent.Messages.UdpTracker.ConnectMessage();
                var receive = relay.ReceiveAsync(t.Token);
                for (var i = 0; i < 3 && !receive.IsCompleted; i++)
                {
                    await relay.SendAsync("tracker.opentrackr.org", 1337, request.Encode(), t.Token).ConfigureAwait(false);
                    await Task.WhenAny(receive, Task.Delay(2500, t.Token)).ConfigureAwait(false);
                }

                udp = receive.IsCompletedSuccessfully && receive.Result.Length >= 16;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
        }

        return new ProxyTestResult(null, hasLogin ? true : null, ip, udp, tcpTracker || udp == true);
    }

    private static async Task<Socket> ConnectViaProxyAsync(Uri proxy, string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            if (IsSocks(proxy))
            {
                await Socks5HandshakeAsync(socket, proxy, ct).ConfigureAwait(false);
                await Socks5CommandAsync(socket, 1, host, port, ct).ConfigureAwait(false); // CONNECT
            }
            else
            {
                await HttpConnectAsync(socket, proxy, host, port, ct).ConfigureAwait(false);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static async Task Socks5HandshakeAsync(Socket socket, Uri proxy, CancellationToken ct)
    {
        var (user, pass) = ProxyCredentials(proxy);
        var withAuth = user.Length > 0;
        byte[] greeting = withAuth ? [5, 2, 0, 2] : [5, 1, 0];
        await SendAllAsync(socket, greeting, ct).ConfigureAwait(false);
        var choice = await ReceiveExactAsync(socket, 2, ct).ConfigureAwait(false);
        if (choice[0] != 5)
        {
            throw new IOException("Not a SOCKS5 proxy");
        }

        if (choice[1] == 2)
        {
            var u = Encoding.UTF8.GetBytes(user);
            var p = Encoding.UTF8.GetBytes(pass);
            var auth = new byte[3 + u.Length + p.Length];
            auth[0] = 1;
            auth[1] = (byte)u.Length;
            u.CopyTo(auth, 2);
            auth[2 + u.Length] = (byte)p.Length;
            p.CopyTo(auth, 3 + u.Length);
            await SendAllAsync(socket, auth, ct).ConfigureAwait(false);
            var ok = await ReceiveExactAsync(socket, 2, ct).ConfigureAwait(false);
            if (ok[1] != 0)
            {
                throw new IOException("The SOCKS5 proxy rejected the login");
            }
        }
        else if (choice[1] != 0)
        {
            throw new IOException(withAuth ? "The SOCKS5 proxy accepts no login method we support" : "The SOCKS5 proxy needs a login");
        }
    }

    /// <summary>Sends a SOCKS5 command (1 CONNECT, 3 UDP ASSOCIATE) and returns the address the proxy reports.</summary>
    internal static async Task<IPEndPoint?> Socks5CommandAsync(Socket socket, byte command, string host, int port, CancellationToken ct)
    {
        var request = new List<byte> { 5, command, 0 };
        if (IPAddress.TryParse(host, out var ip))
        {
            request.Add(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4);
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            request.Add(3);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }

        request.Add((byte)(port >> 8));
        request.Add((byte)port);
        await SendAllAsync(socket, request.ToArray(), ct).ConfigureAwait(false);

        var head = await ReceiveExactAsync(socket, 4, ct).ConfigureAwait(false);
        if (head[1] != 0)
        {
            throw new IOException(head[1] switch
            {
                2 => "The proxy doesn't allow this connection",
                3 => "Network unreachable (from the proxy)",
                4 => "Host unreachable (from the proxy)",
                5 => "Connection refused (from the proxy)",
                7 => "The proxy doesn't support this command",
                _ => $"The proxy refused (SOCKS error {head[1]})"
            });
        }

        IPAddress? bound = null;
        switch (head[3])
        {
            case 1:
                bound = new IPAddress(await ReceiveExactAsync(socket, 4, ct).ConfigureAwait(false));
                break;
            case 4:
                bound = new IPAddress(await ReceiveExactAsync(socket, 16, ct).ConfigureAwait(false));
                break;
            case 3:
                var len = (await ReceiveExactAsync(socket, 1, ct).ConfigureAwait(false))[0];
                await ReceiveExactAsync(socket, len, ct).ConfigureAwait(false);
                break;
        }

        var portBytes = await ReceiveExactAsync(socket, 2, ct).ConfigureAwait(false);
        return bound is null ? null : new IPEndPoint(bound, BinaryPrimitives.ReadUInt16BigEndian(portBytes));
    }

    private static async Task HttpConnectAsync(Socket socket, Uri proxy, string host, int port, CancellationToken ct)
    {
        var target = (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host) + ":" + port;
        var sb = new StringBuilder($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n");
        var (user, pass) = ProxyCredentials(proxy);
        if (user.Length > 0)
        {
            sb.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass))).Append("\r\n");
        }

        sb.Append("\r\n");
        await SendAllAsync(socket, Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);

        // Read the response headers byte by byte so nothing after them is consumed.
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await socket.ReceiveAsync(one, SocketFlags.None, ct).ConfigureAwait(false) == 0 || header.Length > 8192)
            {
                throw new IOException("The HTTP proxy closed the connection");
            }

            header.Append((char)one[0]);
        }

        var status = header.ToString().Split(' ', 3);
        if (status.Length < 2 || status[1] != "200")
        {
            throw new IOException($"The HTTP proxy refused the connection ({header.ToString().Split('\r')[0]})");
        }
    }

    private static (string User, string Pass) ProxyCredentials(Uri proxy)
    {
        var c = Plugin.Instance?.Configuration;
        if (!string.IsNullOrEmpty(c?.ProxyUser))
        {
            return (c.ProxyUser, c.ProxyPassword ?? string.Empty);
        }

        var info = Uri.UnescapeDataString(proxy.UserInfo);
        var colon = info.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (info, string.Empty) : (info[..colon], info[(colon + 1)..]);
    }

    internal static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (data.Length > 0)
        {
            var n = await socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
            data = data[n..];
        }
    }

    internal static async Task<byte[]> ReceiveExactAsync(Socket socket, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var done = 0;
        while (done < count)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(done), SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("The proxy closed the connection");
            }

            done += n;
        }

        return buffer;
    }
}

/// <summary>
/// A SOCKS5 UDP association: UDP packets for any host go through the proxy. Used for UDP trackers in proxy mode.
/// Lives as long as its TCP control connection.
/// </summary>
public sealed class Socks5UdpRelay : IDisposable
{
    private readonly Socket _control;
    private readonly Socket _udp;
    private readonly IPEndPoint _relay;

    private Socks5UdpRelay(Socket control, Socket udp, IPEndPoint relay)
    {
        _control = control;
        _udp = udp;
        _relay = relay;
    }

    public static async Task<Socks5UdpRelay> OpenAsync(Uri proxy, CancellationToken ct)
    {
        var control = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Socket? udp = null;
        try
        {
            await control.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            await NetworkGuard.Socks5HandshakeAsync(control, proxy, ct).ConfigureAwait(false);
            var proxyAddress = ((IPEndPoint)control.RemoteEndPoint!).Address;
            if (proxyAddress.IsIPv4MappedToIPv6)
            {
                proxyAddress = proxyAddress.MapToIPv4(); // the dual-mode socket reports ::ffff:a.b.c.d
            }

            udp = new Socket(proxyAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(proxyAddress.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            var local = (IPEndPoint)udp.LocalEndPoint!;
            var relay = await NetworkGuard.Socks5CommandAsync(control, 3, proxyAddress.AddressFamily == AddressFamily.InterNetwork ? "0.0.0.0" : "::", local.Port, ct).ConfigureAwait(false)
                ?? throw new IOException("The SOCKS5 proxy gave no UDP relay address");
            if (relay.Address.Equals(IPAddress.Any) || relay.Address.Equals(IPAddress.IPv6Any))
            {
                relay = new IPEndPoint(proxyAddress, relay.Port); // "same address as the proxy"
            }

            return new Socks5UdpRelay(control, udp, relay);
        }
        catch
        {
            udp?.Dispose();
            control.Dispose();
            throw;
        }
    }

    public async Task SendAsync(string host, int port, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new List<byte> { 0, 0, 0 };
        if (IPAddress.TryParse(host, out var ip))
        {
            header.Add(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4);
            header.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            header.Add(3);
            header.Add((byte)name.Length);
            header.AddRange(name);
        }

        header.Add((byte)(port >> 8));
        header.Add((byte)port);
        var packet = new byte[header.Count + payload.Length];
        header.CopyTo(packet);
        payload.CopyTo(packet.AsMemory(header.Count));
        await _udp.SendToAsync(packet, SocketFlags.None, _relay, ct).ConfigureAwait(false);
    }

    /// <summary>Receives one packet and strips the SOCKS5 header.</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (true)
        {
            var result = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(_relay.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), ct).ConfigureAwait(false);
            var n = result.ReceivedBytes;
            if (n < 4 || buffer[2] != 0)
            {
                continue; // malformed or fragmented: skip
            }

            var offset = buffer[3] switch
            {
                1 => 4 + 4 + 2,
                4 => 4 + 16 + 2,
                3 => 4 + 1 + buffer[4] + 2,
                _ => -1
            };
            if (offset < 0 || offset > n)
            {
                continue;
            }

            return buffer[offset..n];
        }
    }

    public void Dispose()
    {
        _udp.Dispose();
        _control.Dispose();
    }
}
