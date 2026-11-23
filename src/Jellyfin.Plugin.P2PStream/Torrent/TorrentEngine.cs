using System.Collections.Concurrent;
using System.Net;
using Jellyfin.Plugin.P2PStream.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// Owns the MonoTorrent engine: adding torrents, opening streams, idle cleanup.
/// </summary>
public sealed class TorrentEngine : IHostedService, IAsyncDisposable
{
    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg", ".flv"];

    private readonly ILogger<TorrentEngine> _logger;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TorrentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _addLocks = new(StringComparer.OrdinalIgnoreCase);
    private ClientEngine? _engine;
    private Timer? _cleanupTimer;
    private Timer? _networkTimer;

    // Bytes per "GB" of MaxDiskGb (a field so tests can shrink it).
    private static long DiskUnitBytes = 1L << 30;
    private readonly HashSet<string> _stoppedByGuard = new(StringComparer.OrdinalIgnoreCase);
    private string _storeDir = string.Empty;
    private int _stopped;

    public TorrentEngine(ILogger<TorrentEngine> logger)
    {
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        && !Path.GetFileName(path).Contains("sample", StringComparison.OrdinalIgnoreCase);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        _storeDir = Path.Combine(plugin.DataFolderPath, "torrents");
        Directory.CreateDirectory(_storeDir);
        Directory.CreateDirectory(plugin.DownloadPath);
        SweepLeftovers();

        NetworkGuard.SetLogger(m => _logger.LogInformation("{Message}", m));
        await NetworkGuard.RefreshAsync(cancellationToken).ConfigureAwait(false);

        // Every connection the engine makes goes through NetworkGuard (VPN binding / proxy / kill switch).
        _engine = new ClientEngine(BuildSettings(), GuardedFactories.Create());
        NetworkGuard.Changed += OnNetworkChanged;
        plugin.ConfigurationChanged += OnConfigurationChanged;
        _cleanupTimer = new Timer(_ => _ = CleanupAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _networkTimer = new Timer(_ => _ = RefreshNetworkAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        _logger.LogInformation("P2P Stream engine started on port {Port}, downloads in {Path}. {Network}", Config.ListenPort, plugin.DownloadPath, NetworkGuard.State.Message);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Called by the host on shutdown and again by DisposeAsync. Only run once.
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        NetworkGuard.Changed -= OnNetworkChanged;
        _networkTimer?.Dispose();
        _cleanupTimer?.Dispose();
        if (_engine is not null)
        {
            try
            {
                await _engine.StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (!Config.KeepFilesAfterPlayback)
                {
                    foreach (var m in _engine.Torrents.ToList())
                    {
                        await _engine.RemoveAsync(m, RemoveMode.DownloadedDataOnly).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping torrent engine");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _engine?.Dispose();
    }

    /// <summary>
    /// Adds (or reuses) a torrent and waits until its metadata is known.
    /// </summary>
    public async Task<TorrentSession> AddAsync(TorrentSource source, CancellationToken cancellationToken)
    {
        var engine = _engine ?? throw new InvalidOperationException("Engine not started");
        NetworkGuard.Require(); // kill switch: nothing starts while the VPN/proxy is unavailable
        var gate = _addLocks.GetOrAdd(source.InfoHash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(source.InfoHash, out var existing))
            {
                existing.Touch();
                await EnsureStartedAsync(existing.Manager).ConfigureAwait(false);
                return existing;
            }

            await EnforceLimitAsync().ConfigureAwait(false);
            Persist(source);

            TorrentManager manager;
            await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                manager = engine.Torrents.FirstOrDefault(t => Hash(t).Equals(source.InfoHash, StringComparison.OrdinalIgnoreCase))
                    ?? (source.TorrentBytes is not null
                        ? await engine.AddStreamingAsync(MonoTorrent.Torrent.Load(source.TorrentBytes), Plugin.Instance!.DownloadPath).ConfigureAwait(false)
                        : await engine.AddStreamingAsync(MagnetLink.Parse(source.Magnet!), Plugin.Instance!.DownloadPath).ConfigureAwait(false));
            }
            finally
            {
                _engineLock.Release();
            }

            await SplitTrackerTiersAsync(manager).ConfigureAwait(false);
            await EnsureStartedAsync(manager).ConfigureAwait(false);

            if (!manager.HasMetadata)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, Config.MetadataTimeoutSeconds)));

                // Stop waiting at once if the kill switch trips (otherwise this holds the title for the full timeout).
                void OnNetwork(NetworkState old, NetworkState now)
                {
                    if (!now.Ok)
                    {
                        try
                        {
                            timeout.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }

                NetworkGuard.Changed += OnNetwork;
                try
                {
                    await manager.WaitForMetadataAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await RemoveManagerAsync(manager, true).ConfigureAwait(false);
                    NetworkGuard.Require();
                    throw new TimeoutException($"No metadata for {source.InfoHash} within timeout (no peers?)");
                }
                finally
                {
                    NetworkGuard.Changed -= OnNetwork;
                }
            }

            // Don't download anything until a stream asks for it.
            foreach (var f in manager.Files)
            {
                await manager.SetFilePriorityAsync(f, Priority.DoNotDownload).ConfigureAwait(false);
            }

            var session = new TorrentSession(source.InfoHash, manager);
            _sessions[source.InfoHash] = session;
            Track(manager);
            _logger.LogInformation("Added torrent {Name} ({Hash})", manager.Torrent?.Name, source.InfoHash);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Finds an active session, or re-adds a previously known torrent from the on-disk store.
    /// </summary>
    public async Task<TorrentSession?> GetOrRestoreAsync(string infoHash, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(infoHash, out var s))
        {
            s.Touch();
            return s;
        }

        var source = LoadPersisted(infoHash);
        return source is null ? null : await AddAsync(source, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<TorrentStatus> GetStatus()
    {
        return _sessions.Values.Select(s =>
        {
            var m = s.Manager;
            return new TorrentStatus
            {
                InfoHash = s.InfoHash,
                Name = m.Torrent?.Name ?? s.InfoHash,
                State = m.State.ToString(),
                Progress = Math.Round(m.PartialProgress, 1),
                DownloadKbps = m.Monitor.DownloadRate / 1024,
                UploadKbps = m.Monitor.UploadRate / 1024,
                Peers = m.Peers.Available + m.OpenConnections,
                Seeds = m.Peers.Seeds,
                Downloaded = m.Monitor.DataBytesReceived,
                ActiveStreams = s.ActiveStreams,
                IdleMinutes = Math.Round((DateTime.UtcNow - s.LastAccess).TotalMinutes, 1),
                Error = TorrentSession.Describe(m.Error),
                Trackers = m.TrackerManager.Tiers.SelectMany(t => t.Trackers)
                    .Select(t => $"{t.Uri.Scheme}://{t.Uri.Host}: {t.Status}{(string.IsNullOrEmpty(t.FailureMessage) ? string.Empty : " (" + t.FailureMessage + ")")}")
                    .ToList(),
                Files = m.Files.Select((f, i) => new TorrentFileStatus { Index = i, Path = f.Path, Length = f.Length, IsVideo = IsVideo(f.Path) }).ToList()
            };
        }).ToList();
    }

    public async Task<bool> RemoveAsync(string infoHash, bool deleteData)
    {
        if (!_sessions.TryRemove(infoHash, out var session))
        {
            return false;
        }

        session.Close();
        await RemoveManagerAsync(session.Manager, deleteData).ConfigureAwait(false);
        return true;
    }

    internal static string Hash(TorrentManager m) => m.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();

    /// <summary>
    /// Gives every tracker its own tier. A magnet's trackers all land in one tier, and MonoTorrent tries a tier's
    /// trackers one after another, moving on only when one fails (up to 45 seconds for a dead UDP tracker). Public
    /// magnets list many dead trackers, so the working ones could wait minutes. Tiers are announced in parallel.
    /// </summary>
    public static async Task SplitTrackerTiersAsync(TorrentManager manager)
    {
        try
        {
            var extra = manager.TrackerManager.Tiers.Where(t => t.Trackers.Count > 1).SelectMany(t => t.Trackers.Skip(1)).ToList();
            foreach (var tracker in extra)
            {
                if (await manager.TrackerManager.RemoveTrackerAsync(tracker).ConfigureAwait(false))
                {
                    await manager.TrackerManager.AddTrackerAsync(tracker).ConfigureAwait(false);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // a private torrent: its trackers can't be changed (and it usually has one)
        }
    }

    /// <summary>Gets the DHT state and node count ("Ready, 312 nodes"), for the status display.</summary>
    public string DhtStatus => _engine?.Dht is { } d ? $"{d.State}, {d.NodeCount} nodes" : "off";

    private static async Task EnsureStartedAsync(TorrentManager manager)
    {
        if (manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error)
        {
            await manager.StartAsync().ConfigureAwait(false);
        }
    }

    private EngineSettings BuildSettings()
    {
        var c = Config;
        var plugin = Plugin.Instance!;
        var port = c.ListenPort is > 0 and < 65536 ? c.ListenPort : 55125;
        var net = NetworkGuard.State;
        var listen = new Dictionary<string, IPEndPoint>();
        IPEndPoint? dht = null;
        switch (net.Mode)
        {
            case NetworkMode.Direct:
                listen["ipv4"] = new IPEndPoint(IPAddress.Any, port);
                listen["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, port);
                dht = new IPEndPoint(IPAddress.Any, port);
                break;
            case NetworkMode.Interface when net.Ok:
                // Listen and run DHT only on the VPN's address. (Incoming connections need the VPN's port forwarding;
                // set the listen port to the port the VPN forwards.)
                if (net.Address is not null)
                {
                    listen["ipv4"] = new IPEndPoint(net.Address, port);
                    dht = new IPEndPoint(net.Address, port);
                }

                if (net.Address6 is not null)
                {
                    listen["ipv6"] = new IPEndPoint(net.Address6, port);
                    dht ??= new IPEndPoint(net.Address6, port);
                }

                break;
            default:
                // Proxy mode (a proxy can't accept incoming connections or carry DHT), or blocked: no listener, no DHT.
                break;
        }

        var direct = net.Mode == NetworkMode.Direct;
        var b = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(plugin.DataFolderPath, "cache"),
            AllowPortForwarding = direct && c.EnablePortForwarding, // UPnP opens the home router's port, which a VPN or proxy doesn't use
            AllowLocalPeerDiscovery = direct,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AutoSaveLoadFastResume = true,
            ListenEndPoints = listen,
            DhtEndPoint = dht,

            // Without a listener the announced port would be 0, which some trackers reject; report the usual port.
            ReportedListenEndPoints = listen.Count == 0 ? new Dictionary<string, IPEndPoint> { ["ipv4"] = new IPEndPoint(IPAddress.Any, port) } : new Dictionary<string, IPEndPoint>(),
            MaximumDownloadRate = Math.Max(0, c.MaxDownloadKbps) * 1024,
            MaximumUploadRate = Math.Max(0, c.MaxUploadKbps) * 1024,
            MaximumConnections = 200,
            DiskCacheBytes = 32 * 1024 * 1024
        };
        return b.ToSettings();
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await NetworkGuard.RefreshAsync().ConfigureAwait(false); // raises Changed (handled below) if the network settings changed
                await engine.UpdateSettingsAsync(BuildSettings()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply new engine settings");
            }
        });
    }

    private async Task RefreshNetworkAsync()
    {
        try
        {
            if (NetworkGuard.ConfiguredMode(Config) != NetworkMode.Direct || NetworkGuard.State.Mode != NetworkMode.Direct)
            {
                await NetworkGuard.RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Network check failed: {Message}", ex.Message);
        }

        await KeepDhtRunningAsync().ConfigureAwait(false);
    }

    private DateTime _lastDhtStart = DateTime.MinValue;
    private string _lastDhtState = string.Empty;

    /// <summary>
    /// MonoTorrent tries to join the DHT once; if that first attempt fails (the VPN was still connecting, the bootstrap
    /// servers didn't answer), it never tries again and torrents only find peers through trackers. While torrents are
    /// running, try again every 2 minutes.
    /// </summary>
    private async Task KeepDhtRunningAsync()
    {
        var engine = _engine;
        if (engine is null || engine.Settings.DhtEndPoint is null || !NetworkGuard.State.Ok)
        {
            return;
        }

        try
        {
            var state = $"{engine.Dht.State}";
            if (state != _lastDhtState)
            {
                _logger.LogInformation("DHT: {State}, {Nodes} nodes", state, engine.Dht.NodeCount);
                _lastDhtState = state;
            }

            if (engine.Dht.State != MonoTorrent.Dht.DhtState.NotReady
                || DateTime.UtcNow - _lastDhtStart < TimeSpan.FromMinutes(2)
                || !engine.Torrents.Any(m => m.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata))
            {
                return;
            }

            _lastDhtStart = DateTime.UtcNow;

            // ClientEngine.DhtEngine is internal; IDhtEngine.StartAsync is public and starts a fresh bootstrap.
            if (typeof(ClientEngine).GetProperty("DhtEngine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(engine)
                is MonoTorrent.Dht.IDhtEngine dht)
            {
                _logger.LogInformation("DHT isn't connected; trying again");
                await dht.StartAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("DHT restart failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The VPN dropped or came back, its address changed, or the mode changed. Rebinds the listeners and DHT, and
    /// restarts running torrents so no connection made the old way survives. While blocked, torrents stay stopped.
    /// </summary>
    private void OnNetworkChanged(NetworkState old, NetworkState now)
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _engineLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var running = engine.Torrents.Where(m => m.State is not (TorrentState.Stopped or TorrentState.Stopping or TorrentState.Error)).ToList();
                foreach (var m in running)
                {
                    await m.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    lock (_stoppedByGuard)
                    {
                        _stoppedByGuard.Add(Hash(m));
                    }
                }

                await engine.UpdateSettingsAsync(BuildSettings()).ConfigureAwait(false);

                if (now.Ok)
                {
                    List<string> resume;
                    lock (_stoppedByGuard)
                    {
                        resume = _stoppedByGuard.ToList();
                        _stoppedByGuard.Clear();
                    }

                    // Only torrents someone is watching. Idle ones stay stopped (playing one starts it again), since
                    // nothing would stop them otherwise: their idle timer already ran while they were blocked.
                    var watched = resume.Where(h => _sessions.TryGetValue(h, out var s) && s.ActiveStreams > 0).ToList();
                    foreach (var m in engine.Torrents.Where(m => watched.Contains(Hash(m))))
                    {
                        await m.StartAsync().ConfigureAwait(false);
                    }

                    resume = watched;

                    _logger.LogInformation("Torrent network changed ({Message}); {Count} torrents reconnected", now.Message, resume.Count);
                }
                else
                {
                    _logger.LogWarning("Kill switch: {Message} {Count} torrents stopped", now.Message, running.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply the network change");
            }
            finally
            {
                _engineLock.Release();
            }
        });
    }

    private async Task EnforceLimitAsync()
    {
        var max = Math.Max(1, Config.MaxActiveTorrents);
        while (_sessions.Count >= max)
        {
            var victim = _sessions.Values.Where(s => s.ActiveStreams == 0).OrderBy(s => s.LastAccess).FirstOrDefault()
                ?? _sessions.Values.OrderBy(s => s.LastAccess).First();
            _logger.LogInformation("Torrent limit reached, evicting {Hash}", victim.InfoHash);
            await RemoveAsync(victim.InfoHash, !Config.KeepFilesAfterPlayback).ConfigureAwait(false);
        }

        await EnforceDiskCapAsync().ConfigureAwait(false);
    }

    private static readonly TimeSpan KeepUnused = TimeSpan.FromDays(30);
    private DateTime _lastPrune = DateTime.MinValue;

    /// <summary>
    /// Deletes saved .torrent/.magnet files and MonoTorrent cache entries (resume data, fetched metadata) that haven't
    /// been used for 30 days, so they don't pile up forever. Torrents in use are never touched. A pruned title just
    /// searches again the next time it's played. Runs at most every 6 hours.
    /// </summary>
    private void PruneOldFiles()
    {
        if (DateTime.UtcNow - _lastPrune < TimeSpan.FromHours(6))
        {
            return;
        }

        _lastPrune = DateTime.UtcNow;
        var cutoff = DateTime.UtcNow - KeepUnused;
        var active = _sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var dir in new[] { _storeDir, Path.Combine(Plugin.Instance!.DataFolderPath, "cache") })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    if (name.Contains("dht", StringComparison.OrdinalIgnoreCase) || active.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue; // the DHT node cache, or a torrent in use
                    }

                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} saved torrent files unused for {Days} days", removed, KeepUnused.Days);
        }
    }

    /// <summary>Downloaded data of a torrent (whole pieces it has, which is what's on disk).</summary>
    public static long DiskBytes(TorrentManager m) =>
        m.HasMetadata && m.Torrent is not null ? (long)m.Bitfield.TrueCount * m.Torrent.PieceLength : 0;

    /// <summary>Gets the total disk space the streaming torrents use.</summary>
    public long TotalDiskBytes => _sessions.Values.Sum(s => DiskBytes(s.Manager));

    /// <summary>
    /// Keeps streaming data under MaxDiskGb: removes (and deletes) the least recently used torrents nobody is watching.
    /// A title being watched is never removed, even if it alone is over the limit.
    /// </summary>
    internal async Task EnforceDiskCapAsync()
    {
        var capGb = Config.MaxDiskGb;
        if (capGb <= 0)
        {
            return;
        }

        var cap = capGb * DiskUnitBytes;
        var used = TotalDiskBytes;
        foreach (var victim in _sessions.Values.Where(s => s.ActiveStreams == 0).OrderBy(s => s.LastAccess).ToList())
        {
            if (used <= cap)
            {
                break;
            }

            var size = DiskBytes(victim.Manager);
            _logger.LogInformation("Disk limit ({Cap} GB) reached, removing {Name} ({Size} MB)", capGb, victim.Manager.Torrent?.Name ?? victim.InfoHash, size >> 20);
            await RemoveAsync(victim.InfoHash, true).ConfigureAwait(false);
            used -= size;
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            var idle = TimeSpan.FromMinutes(Math.Max(1, Config.IdleTimeoutMinutes));
            foreach (var s in _sessions.Values.Where(s => s.ActiveStreams == 0 && DateTime.UtcNow - s.LastAccess > idle).ToList())
            {
                _logger.LogInformation("Torrent {Hash} idle, removing", s.InfoHash);
                await RemoveAsync(s.InfoHash, !Config.KeepFilesAfterPlayback).ConfigureAwait(false);
            }

            await EnforceDiskCapAsync().ConfigureAwait(false);
            PruneOldFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup failed");
        }
    }

    private async Task RemoveManagerAsync(TorrentManager manager, bool deleteData)
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        await _engineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                for (var i = 0; i < 50 && manager.State == TorrentState.Stopping; i++)
                {
                    await Task.Delay(100).ConfigureAwait(false); // a stop already under way (idle stop, kill switch)
                }

                if (manager.State != TorrentState.Stopped)
                {
                    await manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or TaskCanceledException)
            {
                _logger.LogDebug("Stopping {Name} before removal: {Message}", manager.Torrent?.Name, ex.Message);
            }

            await engine.RemoveAsync(manager, deleteData ? RemoveMode.DownloadedDataOnly : RemoveMode.KeepAllData).ConfigureAwait(false);
            Untrack(Hash(manager));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed removing torrent");
        }
        finally
        {
            _engineLock.Release();
        }
    }

    // ---- Leftover cleanup ----
    // Torrents are normally deleted when idle, evicted, or on shutdown. If Jellyfin crashes or is killed, that never
    // happens, and after a restart the engine no longer knows those files. So every added torrent's files are recorded
    // in downloads.json, removed from it when the torrent is removed, and anything still listed at startup is deleted.
    // Only recorded paths are deleted, never anything else in the download folder.

    private static string ManifestPath => Path.Combine(Plugin.Instance!.DataFolderPath, "downloads.json");

    private readonly object _manifestLock = new();

    private Dictionary<string, List<string>> LoadManifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(ManifestPath)) ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read {Path}: {Message}", ManifestPath, ex.Message);
        }

        return [];
    }

    private void SaveManifest(Dictionary<string, List<string>> manifest)
    {
        try
        {
            File.WriteAllText(ManifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not write {Path}: {Message}", ManifestPath, ex.Message);
        }
    }

    private void Track(TorrentManager manager)
    {
        var paths = manager.Files
            .SelectMany(f => new[] { f.FullPath, f.DownloadCompleteFullPath, f.DownloadIncompleteFullPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList();
        if (manager.Files.Count > 1 && !string.IsNullOrEmpty(manager.ContainingDirectory))
        {
            paths.Add(manager.ContainingDirectory); // folder of a multi-file torrent, removed once empty
        }

        lock (_manifestLock)
        {
            var manifest = LoadManifest();
            manifest[Hash(manager)] = paths;
            SaveManifest(manifest);
        }
    }

    private void Untrack(string hash)
    {
        lock (_manifestLock)
        {
            var manifest = LoadManifest();
            if (manifest.Remove(hash))
            {
                SaveManifest(manifest);
            }
        }
    }

    private void SweepLeftovers()
    {
        lock (_manifestLock)
        {
            var manifest = LoadManifest();
            if (manifest.Count == 0)
            {
                return;
            }

            if (Config.KeepFilesAfterPlayback)
            {
                SaveManifest([]); // the user wants downloads kept; just forget them
                return;
            }

            long freed = 0;
            var files = 0;
            foreach (var path in manifest.Values.SelectMany(p => p))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        freed += new FileInfo(path).Length;
                        File.Delete(path);
                        files++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not delete leftover {Path}: {Message}", path, ex.Message);
                }
            }

            // Then empty folders, deepest first.
            foreach (var dir in manifest.Values.SelectMany(p => p).Where(Directory.Exists).OrderByDescending(d => d.Length))
            {
                try
                {
                    foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    {
                        if (!Directory.EnumerateFileSystemEntries(sub).Any())
                        {
                            Directory.Delete(sub);
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not remove leftover folder {Path}: {Message}", dir, ex.Message);
                }
            }

            SaveManifest([]);
            _logger.LogInformation("Deleted {Files} leftover download files ({Megabytes} MB) from an unclean shutdown", files, freed / (1024 * 1024));
        }
    }

    private void Persist(TorrentSource source)
    {
        try
        {
            if (source.TorrentBytes is not null)
            {
                File.WriteAllBytes(Path.Combine(_storeDir, source.InfoHash + ".torrent"), source.TorrentBytes);
            }
            else if (source.Magnet is not null)
            {
                File.WriteAllText(Path.Combine(_storeDir, source.InfoHash + ".magnet"), source.Magnet);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist torrent {Hash}", source.InfoHash);
        }
    }

    private TorrentSource? LoadPersisted(string infoHash)
    {
        infoHash = infoHash.ToLowerInvariant();
        if (infoHash.Any(ch => !Uri.IsHexDigit(ch)))
        {
            return null;
        }

        var t = Path.Combine(_storeDir, infoHash + ".torrent");
        if (File.Exists(t))
        {
            return TorrentSource.FromTorrentBytes(File.ReadAllBytes(t));
        }

        var m = Path.Combine(_storeDir, infoHash + ".magnet");
        return File.Exists(m) ? TorrentSource.FromMagnet(File.ReadAllText(m)) : null;
    }
}
