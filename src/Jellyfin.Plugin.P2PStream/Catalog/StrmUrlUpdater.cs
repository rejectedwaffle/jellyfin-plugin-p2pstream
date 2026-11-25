using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// Keeps every stream link current. The links in .strm files contain the public address and the stream token; when
/// either changes (Engine &gt; Advanced), this rewrites the .strm files and the links Jellyfin stored for those items,
/// so nobody has to re-import anything. Search-added and manually added titles are included.
/// </summary>
public sealed partial class StrmUrlUpdater : IHostedService
{
    private readonly StreamUrlBuilder _urls;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StrmUrlUpdater> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private string _current = string.Empty;
    private string _currentToken = string.Empty;
    private static (string Token, DateTime Until) _previous;

    /// <summary>
    /// True for the token used before the last change, for 10 minutes: whatever is playing (the player holds the old
    /// link) keeps working while all links are rewritten.
    /// </summary>
    /// <summary>The old token stops working at once (instead of 10 minutes after a new one was made).</summary>
    public static void RevokePrevious() => _previous = (string.Empty, DateTime.MinValue);

    public static bool IsRecentPreviousToken(string token)
    {
        var (old, until) = _previous;
        return !string.IsNullOrEmpty(old) && DateTime.UtcNow < until && old == token;
    }

    public StrmUrlUpdater(StreamUrlBuilder urls, ILibraryManager libraryManager, ILogger<StrmUrlUpdater> logger)
    {
        _urls = urls;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private string Prefix => _urls.BaseUrl() + "|" + Plugin.Instance!.Configuration.StreamToken;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _current = Prefix;
        _currentToken = Plugin.Instance!.Configuration.StreamToken;
        Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        var now = Prefix;
        if (now == _current)
        {
            return;
        }

        var token = Plugin.Instance!.Configuration.StreamToken;
        if (token != _currentToken)
        {
            _previous = (_currentToken, DateTime.UtcNow.AddMinutes(10));
            _currentToken = token;
        }

        _current = now;
        _ = Task.Run(RewriteAllAsync);
    }

    /// <summary>Rewrites links to the current address and token. Returns how many files and items changed.</summary>
    public async Task<(int Files, int Items)> RewriteAllAsync()
    {
        await _running.WaitAsync().ConfigureAwait(false);
        try
        {
            var baseUrl = _urls.BaseUrl();
            var token = Plugin.Instance!.Configuration.StreamToken;
            string Fix(string url) => LinkPrefix().Replace(url, m => $"{baseUrl}/P2PStream/{m.Groups["kind"].Value}/{token}/", 1);

            var files = 0;
            var root = Plugin.Instance.CatalogPath;
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories))
                {
                    try
                    {
                        var text = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
                        var updated = Fix(text);
                        if (updated != text)
                        {
                            await File.WriteAllTextAsync(path, updated, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
                            files++;
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning("Could not update {Path}: {Message}", path, ex.Message);
                    }
                }
            }

            // Jellyfin keeps each .strm item's link in its database; update those too, so playback uses the new link at once.
            var items = 0;
            var library = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video], Recursive = true, IsVirtualItem = false });
            foreach (var item in library.Where(i => i.IsShortcut && i.ShortcutPath?.Contains("/P2PStream/", StringComparison.Ordinal) == true))
            {
                var updated = Fix(item.ShortcutPath!);
                if (updated != item.ShortcutPath)
                {
                    item.ShortcutPath = updated;
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                    items++;
                }
            }

            _logger.LogInformation("Stream links updated to the new address/token: {Files} .strm files, {Items} library items", files, items);
            return (files, items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updating stream links failed");
            return (0, 0);
        }
        finally
        {
            _running.Release();
        }
    }

    // http(s)://anything/P2PStream/play|stream/<token>/
    [GeneratedRegex(@"https?://\S+?/P2PStream/(?<kind>play|stream)/[^/\s]+/", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPrefix();
}
