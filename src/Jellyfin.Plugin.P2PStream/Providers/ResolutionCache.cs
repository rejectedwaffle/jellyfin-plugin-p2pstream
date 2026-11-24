using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// Remembers which torrent and file worked for a movie or episode, so replays and seeks start
/// instantly instead of searching again. Persisted to disk.
/// </summary>
public sealed class ResolutionCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private readonly ILogger<ResolutionCache> _logger;
    private readonly object _saveLock = new();
    private ConcurrentDictionary<string, Entry>? _entries;

    public ResolutionCache(ILogger<ResolutionCache> logger)
    {
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "resolutions.json");

    private ConcurrentDictionary<string, Entry> Entries => _entries ??= Load();

    public bool TryGet(string key, out Entry entry)
    {
        if (Entries.TryGetValue(key, out entry!) && DateTime.UtcNow - entry.Created < MaxAge)
        {
            // Auto's own pick, made under other ranking rules or quality settings: let Auto pick again.
            if (!entry.Chosen && entry.Rules != ReleaseRanker.RulesStamp())
            {
                _logger.LogInformation("Ranking rules or quality settings changed since Auto picked \"{Title}\"; picking again", entry.Title);
                Remove(key);
                entry = null!;
                return false;
            }

            return true;
        }

        entry = null!;
        return false;
    }

    /// <param name="chosen">True when someone picked this release (Version menu or a device rule), not Auto's ranking.</param>
    public void Set(string key, string infoHash, int fileIndex, string title, bool chosen)
    {
        Entries[key] = new Entry(infoHash, fileIndex, title, DateTime.UtcNow, chosen, chosen ? null : ReleaseRanker.RulesStamp());
        Save();
    }

    public void Remove(string key)
    {
        if (Entries.TryRemove(key, out _))
        {
            Save();
        }
    }

    public int Clear()
    {
        var n = Entries.Count;
        Entries.Clear();
        Save();
        return n;
    }

    private ConcurrentDictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
                if (data is not null)
                {
                    return new ConcurrentDictionary<string, Entry>(data.Where(kv => DateTime.UtcNow - kv.Value.Created < MaxAge));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read resolution cache");
        }

        return new ConcurrentDictionary<string, Entry>();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Entries));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save resolution cache");
            }
        }
    }

    /// <param name="Chosen">Picked in the Version menu or by a device rule (kept when the ranking rules change).</param>
    /// <param name="Rules">For Auto's picks, the <see cref="ReleaseRanker.RulesStamp"/> it was made under.</param>
    public sealed record Entry(string InfoHash, int FileIndex, string Title, DateTime Created, bool Chosen = false, string? Rules = null);
}
