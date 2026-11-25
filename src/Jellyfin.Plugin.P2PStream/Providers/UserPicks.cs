using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>
/// The Version each person chose for a title, so one person's choice isn't what Auto plays for everyone else.
/// Kept in user-picks.json (user id and title key to release id); a pick not used for 60 days is forgotten.
/// </summary>
public sealed class UserPicks
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(60);
    private readonly ILogger<UserPicks> _logger;
    private readonly object _saveLock = new();
    private ConcurrentDictionary<string, Pick>? _picks;

    public UserPicks(ILogger<UserPicks> logger)
    {
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "user-picks.json");

    private ConcurrentDictionary<string, Pick> Picks => _picks ??= Load();

    /// <summary>The release this person chose for this title, if they chose one.</summary>
    public string? Get(string userId, string titleKey) =>
        Picks.TryGetValue(Key(userId, titleKey), out var p) && DateTime.UtcNow - p.At < KeepFor ? p.ReleaseId : null;

    public void Set(string userId, string titleKey, string releaseId)
    {
        Picks[Key(userId, titleKey)] = new Pick(releaseId, DateTime.UtcNow);
        Save();
    }

    public void Remove(string userId, string titleKey)
    {
        if (Picks.TryRemove(Key(userId, titleKey), out _))
        {
            Save();
        }
    }

    private static string Key(string userId, string titleKey) => userId + "|" + titleKey;

    private ConcurrentDictionary<string, Pick> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, Pick>>(File.ReadAllText(FilePath)) ?? [];
                return new ConcurrentDictionary<string, Pick>(saved.Where(kv => DateTime.UtcNow - kv.Value.At < KeepFor));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning("Could not read the saved Version choices: {Message}", ex.Message);
        }

        return new ConcurrentDictionary<string, Pick>();
    }

    private void Save()
    {
        try
        {
            lock (_saveLock)
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Picks.ToDictionary(kv => kv.Key, kv => kv.Value)));
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Could not save the Version choices: {Message}", ex.Message);
        }
    }

    public sealed record Pick(string ReleaseId, DateTime At);
}
