using System.Text.RegularExpressions;
using Jellyfin.Plugin.P2PStream.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.P2PStream.Api;

/// <summary>
/// Adds the available streams to a title's page, as Jellyfin's Version menu: "Auto (best match)" first, then each release.
/// It waits a few seconds for the search the first time. After that the list is cached and appears instantly.
/// </summary>
public sealed partial class StreamPickerFilter : IAsyncActionFilter
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);
    private readonly StreamOptions _options;
    private readonly ILibraryManager _libraryManager;

    public StreamPickerFilter(StreamOptions options, ILibraryManager libraryManager)
    {
        _options = options;
        _libraryManager = libraryManager;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next().ConfigureAwait(false);
        try
        {
            await AddVersionsAsync(context.HttpContext, executed).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never break a title's page because of the plugin.
        }
    }

    private async Task AddVersionsAsync(HttpContext http, ActionExecutedContext executed)
    {
        if (Plugin.Instance?.Configuration.StreamPicker != true
            || !HttpMethods.IsGet(http.Request.Method)
            || !ItemPath().IsMatch(http.Request.Path.Value ?? string.Empty)
            || executed.Result is not ObjectResult { Value: BaseItemDto dto }
            || dto.MediaSources is not { Length: > 0 })
        {
            return;
        }

        var item = _libraryManager.GetItemById(dto.Id);
        if (item is null || !StreamPicker.TryGetRequest(item, out var url, out var request))
        {
            return;
        }

        if (!_options.TryGetCached(request, out var releases))
        {
            try
            {
                releases = await _options.GetAsync(request, http.RequestAborted).WaitAsync(Budget, http.RequestAborted).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return; // still searching; the versions show up next time the page is opened
            }
        }

        if (releases.Count == 0)
        {
            return;
        }

        var auto = dto.MediaSources[0];
        auto.Name = "Auto (best match)";
        dto.MediaSources = new[] { auto }.Concat(StreamPicker.BuildSources(item, url, releases)).ToArray();
        dto.MediaSourceCount = dto.MediaSources.Length;
    }

    [GeneratedRegex(@"/Items/[0-9a-fA-F-]{32,36}$")]
    private static partial Regex ItemPath();
}
