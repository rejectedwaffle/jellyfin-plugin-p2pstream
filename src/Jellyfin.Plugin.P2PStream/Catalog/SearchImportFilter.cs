using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// Hooks Jellyfin's own search (the search bar in every client). Before a movie or show search runs,
/// it gives <see cref="SearchImporter"/> a few seconds to add matching TMDB titles to the library.
/// </summary>
public sealed class SearchImportFilter : IAsyncActionFilter
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);
    private readonly SearchImporter _importer;

    public SearchImportFilter(SearchImporter importer)
    {
        _importer = importer;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (TryGetSearchTerm(context.HttpContext.Request, out var term) && await IsSignedInUserAsync(context.HttpContext).ConfigureAwait(false))
        {
            try
            {
                await _importer.ImportForSearchAsync(term, Budget, context.HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // client gave up; carry on
            }
            catch (Exception)
            {
                // Never break Jellyfin's search because of the plugin.
            }
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Only a person's search adds titles. Requests made with an API key (scripts, or request tools that check what the
    /// library has) carry no user and shouldn't fill the library with near-matches.
    /// </summary>
    private static async Task<bool> IsSignedInUserAsync(HttpContext http)
    {
        try
        {
            var auth = http.RequestServices.GetService<IAuthorizationContext>();
            return auth is not null && (await auth.GetAuthorizationInfo(http).ConfigureAwait(false)).User is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetSearchTerm(HttpRequest request, out string term)
    {
        term = string.Empty;
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        if (!path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/Search/Hints", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = request.Query["searchTerm"].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Only movie and show searches. Music, people and so on pass straight through.
        var types = request.Query["includeItemTypes"].ToString();
        if (types.Length > 0
            && !types.Contains("Movie", StringComparison.OrdinalIgnoreCase)
            && !types.Contains("Series", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        term = value;
        return true;
    }
}
