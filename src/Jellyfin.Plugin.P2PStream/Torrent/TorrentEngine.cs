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

        _engine = new ClientEngine(BuildSettings());
        plugin.ConfigurationChanged += OnConfigurationChanged;
        _cleanupTimer = new Timer(_ => _ = CleanupAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _logger.LogInformation("P2P Stream engine started on port {Port}, downloads in {Path}", Config.ListenPort, plugin.DownloadPath);
        await Task.CompletedTask.ConfigureAwait(false);
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

            await EnsureStartedAsync(manager).ConfigureAwait(false);

            if (!manager.HasMetadata)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, Config.MetadataTimeoutSeconds)));
                try
                {
                    await manager.WaitForMetadataAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await RemoveManagerAsync(manager, true).ConfigureAwait(false);
                    throw new TimeoutException($"No metadata for {source.InfoHash} within timeout (no peers?)");
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
        var b = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(plugin.DataFolderPath, "cache"),
            AllowPortForwarding = c.EnablePortForwarding,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AutoSaveLoadFastResume = true,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, port),
                ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, port)
            },
            DhtEndPoint = new IPEndPoint(IPAddress.Any, port),
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
                await engine.UpdateSettingsAsync(BuildSettings()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply new engine settings");
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
            if (manager.State != TorrentState.Stopped)
            {
                await manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
