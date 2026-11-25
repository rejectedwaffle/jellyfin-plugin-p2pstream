using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// "Hide the search libraries": adds the libraries that Jellyfin search imports into to every user's "My media"
/// exclusions (the same as each user unticking them under Settings &gt; Home). They then don't show in the library list
/// or on the home screen, but search still finds their titles and Continue Watching still shows them. (The "Latest"
/// exclusion isn't used: Jellyfin also drops those libraries from Continue Watching with it.)
/// Only libraries the plugin hid are un-hidden when the option is turned off, and a user who shows them again in their
/// own settings isn't overruled.
/// </summary>
public sealed class SearchLibraryHider : IHostedService
{
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly ILogger<SearchLibraryHider> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _timer;

    public SearchLibraryHider(IUserManager users, ILibraryManager libraries, ILogger<SearchLibraryHider> logger)
    {
        _users = users;
        _libraries = libraries;
        _logger = logger;
    }

    private static string StatePath => Path.Combine(Plugin.Instance!.DataFolderPath, "hidden-search-libraries.json");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Plugin.Instance!.ConfigurationChanged += OnConfigurationChanged;
        _timer = new Timer(_ => _ = ApplyAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)); // new users and libraries
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        _timer?.Dispose();
        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e) => _ = ApplyAsync();

    /// <summary>The libraries Jellyfin search imports into (those whose folders are the search folders).</summary>
    private List<Guid> SearchLibraryIds()
    {
        var c = Plugin.Instance!.Configuration;
        var folders = new[] { c.SearchMovieLibrary, c.SearchShowLibrary }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Path.TrimEndingDirectorySeparator(CatalogBuilder.FolderFor(n)))
            .ToList();
        return _libraries.GetVirtualFolders()
            .Where(v => v.Locations.Any(l => folders.Contains(Path.TrimEndingDirectorySeparator(l), StringComparer.OrdinalIgnoreCase)))
            .Select(v => Guid.TryParse(v.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
    }

    public async Task ApplyAsync()
    {
        if (!await _running.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var hide = Plugin.Instance?.Configuration.HideSearchLibraries == true;
            var state = Load(); // user id -> library ids the plugin hid for that user
            var libraries = hide ? SearchLibraryIds() : [];
            var changed = 0;
            foreach (var user in _users.GetUsers().ToList())
            {
                var excludes = user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes).ToList();
                var ours = state.TryGetValue(user.Id, out var list) ? list : [];
                var before = excludes.Count;
                var oursBefore = ours.Count;

                if (hide)
                {
                    // Hide each search library once per user: if they show it again themselves, leave it shown.
                    foreach (var id in libraries.Where(id => !ours.Contains(id)))
                    {
                        if (!excludes.Contains(id))
                        {
                            excludes.Add(id);
                        }

                        ours.Add(id);
                    }
                }
                else
                {
                    excludes.RemoveAll(ours.Contains);
                    ours.Clear();
                }

                if (excludes.Count != before)
                {
                    user.SetPreference(PreferenceKind.MyMediaExcludes, excludes.ToArray());
                    await _users.UpdateUserAsync(user).ConfigureAwait(false);
                    changed++;
                }

                if (ours.Count > 0)
                {
                    state[user.Id] = ours;
                }
                else if (oursBefore > 0)
                {
                    state.Remove(user.Id);
                }
            }

            Save(state);
            if (changed > 0)
            {
                _logger.LogInformation(hide ? "Hid the search libraries for {Count} user(s)" : "Showed the search libraries again for {Count} user(s)", changed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not update which libraries users see: {Message}", ex.Message);
        }
        finally
        {
            _running.Release();
        }
    }

    private static Dictionary<Guid, List<Guid>> Load()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, List<Guid>>>(File.ReadAllText(StatePath)) ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Save(Dictionary<Guid, List<Guid>> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }
}
