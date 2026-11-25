using System.Text.Json.Nodes;
using Jellyfin.Plugin.P2PStream.Configuration;

namespace Jellyfin.Plugin.P2PStream.Providers.BuiltIn;

/// <summary>
/// A built-in torrent search source (Burst-style). It runs inside the plugin, so there's nothing extra to install.
/// </summary>
public abstract class SearchProvider
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string DefaultUrl { get; }

    public virtual bool EnabledByDefault => false;

    public bool Enabled => Setting?.Enabled ?? EnabledByDefault;

    /// <summary>Gets backup addresses tried in order when the default address doesn't answer (sites often move or get blocked).</summary>
    public virtual string[] Mirrors => [];

    /// <summary>Gets a value indicating whether the source can search free text (the Test search). Some only search by IMDb id.</summary>
    public virtual bool SupportsTextSearch => true;

    /// <summary>Gets a short note shown in the Test search when the source doesn't do text searches.</summary>
    public virtual string? TextSearchNote => null;

    /// <summary>Gets the address in use: the one you set, else the last address that worked, else the default.</summary>
    public string BaseUrl => CustomUrl ?? _workingUrl ?? DefaultUrl.TrimEnd('/');

    private string? CustomUrl => Setting?.BaseUrl is { } u && !string.IsNullOrWhiteSpace(u) ? u.Trim().TrimEnd('/') : null;

    private string? _workingUrl;

    /// <summary>
    /// Runs a request against the source's address. If it doesn't answer, it tries the backup addresses and remembers
    /// the first that works. An address set with "change address" is always used on its own.
    /// </summary>
    protected async Task<T> UseMirrorsAsync<T>(Func<string, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        var candidates = CustomUrl is { } custom
            ? new[] { custom }
            : new[] { _workingUrl, DefaultUrl.TrimEnd('/') }.Concat(Mirrors.Select(m => m.TrimEnd('/'))).Where(u => u is not null).Distinct().ToArray();
        Exception? last = null;
        foreach (var url in candidates)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(12)); // a blocked address can hang; move on
            try
            {
                var result = await call(url!, attempt.Token).ConfigureAwait(false);
                _workingUrl = url;
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException
                    or System.Text.Json.JsonException or System.Xml.XmlException)
            {
                // Say what happened: "the operation was canceled" really means the address didn't answer in time.
                last = ex is OperationCanceledException
                    ? new TimeoutException($"{(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url)} didn't answer within 12 s", ex)
                    : ex;
            }
        }

        throw last ?? new InvalidOperationException($"{Name}: no address answered");
    }

    private BuiltInSource? Setting => Plugin.Instance?.Configuration.Sources.FirstOrDefault(s => string.Equals(s.Id, Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds releases for a movie or episode (the request has the title, year and IMDb id when known).</summary>
    public abstract Task<List<ReleaseCandidate>> SearchAsync(MediaRequest request, CancellationToken ct);

    /// <summary>Free-text search (used by the config page's test search). Sources that can't do text return nothing.</summary>
    public virtual Task<List<ReleaseCandidate>> SearchTextAsync(string query, CancellationToken ct) => Task.FromResult(new List<ReleaseCandidate>());

    protected static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        var text = await HttpClients.Default.GetStringAsync(new Uri(url), ct).ConfigureAwait(false);
        return JsonNode.Parse(text);
    }

    protected static string Esc(string s) => Uri.EscapeDataString(s);

    /// <summary>
    /// Standard title search: "Title Year" for movies; "Title S01E02" for episodes, falling back to "Title S01" (season packs).
    /// </summary>
    protected internal static async Task<List<ReleaseCandidate>> SearchByTitleAsync(MediaRequest request, Func<string, Task<List<ReleaseCandidate>>> query)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return [];
        }

        if (request.Kind == MediaKind.Movie)
        {
            return await query(request.Year is int y ? $"{request.Title} {y}" : request.Title!).ConfigureAwait(false);
        }

        var episode = await query($"{request.Title} S{request.Season:00}E{request.Episode:00}").ConfigureAwait(false);
        return episode.Count > 0 ? episode : await query($"{request.Title} S{request.Season:00}").ConfigureAwait(false);
    }

    /// <summary>Parses sizes like "1.4 GB", "57.98GB", "700 MiB" into bytes.</summary>
    protected internal static long ParseSize(string? text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? string.Empty, @"([\d.,]+)\s*([KMGT])i?B", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }

        var power = "KMGT".IndexOf(char.ToUpperInvariant(m.Groups[2].Value[0]), StringComparison.Ordinal) + 1;
        return (long)(n * Math.Pow(1024, power));
    }

    /// <summary>Fetches a page as text and recognises Cloudflare's bot check, which shows up as a 403/503 "Just a moment" page.</summary>
    protected async Task<string> GetPageAsync(string url, CancellationToken ct)
    {
        using var resp = await HttpClients.Default.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{Name} is blocking automated searches (Cloudflare bot check). Try a mirror address or a proxy.");
            }

            resp.EnsureSuccessStatusCode();
        }

        return body;
    }

    protected static string ImdbDigits(string imdb) => imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdb[2..] : imdb;

    protected static long ToLong(JsonNode? n)
    {
        if (n is null)
        {
            return 0;
        }

        return n.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? n.GetValue<long>()
            : long.TryParse(n.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
