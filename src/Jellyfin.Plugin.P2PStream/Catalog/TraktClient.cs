using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.P2PStream.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.P2PStream.Catalog;

/// <summary>
/// Trakt lists as catalogs: trending/popular/anticipated, a user's public watchlist and lists (only a Client ID
/// needed), plus a private watchlist and recommendations once an account is connected (device code sign-in, like a
/// TV app). Trakt items carry TMDB ids, so they go through the same .strm writing as TMDB catalogs.
/// </summary>
public sealed class TraktClient
{
    private const string BaseUrl = "https://api.trakt.tv/";
    private readonly ILogger<TraktClient> _logger;
    private readonly object _lock = new();
    private DeviceLogin? _pending;
    private string? _lastError;

    public TraktClient(ILogger<TraktClient> logger)
    {
        _logger = logger;
    }

    /// <summary>A sign-in waiting for the user to enter the code at trakt.tv/activate.</summary>
    public sealed record DeviceLogin(string UserCode, string VerificationUrl, DateTime Expires);

    /// <summary>Tokens of a connected account (kept in trakt.json, not in the settings, so saving the settings page can't wipe them).</summary>
    public sealed class Tokens
    {
        public string AccessToken { get; set; } = string.Empty;

        public string RefreshToken { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public string? User { get; set; }
    }

    public static string ClientId => Plugin.Instance?.Configuration.TraktClientId.Trim() ?? string.Empty;

    public static string ClientSecret => Plugin.Instance?.Configuration.TraktClientSecret.Trim() ?? string.Empty;

    public static bool IsConfigured => ClientId.Length > 0;

    public bool IsConnected => LoadTokens() is not null;

    public DeviceLogin? Pending => _pending is { } p && p.Expires > DateTime.UtcNow ? p : null;

    public string? LastError => _lastError;

    private static string TokenFile => Path.Combine(Plugin.Instance!.DataFolderPath, "trakt.json");

    public Tokens? LoadTokens()
    {
        try
        {
            return File.Exists(TokenFile) ? JsonSerializer.Deserialize<Tokens>(File.ReadAllText(TokenFile)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _pending = null;
            if (File.Exists(TokenFile))
            {
                File.Delete(TokenFile);
            }
        }
    }

    // ------------------------------------------------------------------ lists

    /// <summary>Gets up to <paramref name="max"/> titles of a Trakt list endpoint such as "movies/trending" or "users/me/watchlist/shows".</summary>
    public async Task<List<TmdbItem>> GetItemsAsync(string path, bool movies, int max, CancellationToken ct)
    {
        var items = new List<TmdbItem>();
        var seen = new HashSet<int>();
        var limit = Math.Clamp(max, 10, 100);
        for (var page = 1; page <= 20 && items.Count < max; page++)
        {
            var sep = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var (json, pageCount) = await GetAsync($"{path}{sep}page={page}&limit={limit}", ct).ConfigureAwait(false);
            var batch = Parse(json, movies);
            foreach (var i in batch.Where(i => seen.Add(i.Id)))
            {
                items.Add(i);
            }

            if (batch.Count == 0 || page >= pageCount)
            {
                break;
            }
        }

        return items.Take(max).ToList();
    }

    /// <summary>Gets a user list's name, or null if it can't be read.</summary>
    public async Task<string?> TryGetListNameAsync(string user, string slug, CancellationToken ct)
    {
        try
        {
            var (json, _) = await GetAsync($"users/{Uri.EscapeDataString(user)}/lists/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
            return (string?)json?["name"];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads Trakt list items. Items are either wrapped ({"movie": {...}} / {"show": {...}}, with "type" in lists and
    /// watchlists) or plain (movies/popular). Items without a TMDB id are skipped.
    /// </summary>
    public static List<TmdbItem> Parse(JsonNode? json, bool movies)
    {
        var list = new List<TmdbItem>();
        foreach (var row in json as JsonArray ?? [])
        {
            if (row is null)
            {
                continue;
            }

            var type = (string?)row["type"];
            if (type is not null && type != (movies ? "movie" : "show"))
            {
                continue;
            }

            var media = row[movies ? "movie" : "show"] ?? (row["ids"] is not null ? row : null);
            if (media is null)
            {
                continue;
            }

            var tmdb = (int?)media["ids"]?["tmdb"];
            var title = (string?)media["title"];
            if (tmdb is > 0 && !string.IsNullOrWhiteSpace(title))
            {
                list.Add(new TmdbItem(tmdb.Value, title, (int?)media["year"], movies));
            }
        }

        return list;
    }

    /// <summary>Turns "user/slug" or "https://trakt.tv/users/user/lists/slug" into (user, slug).</summary>
    public static (string User, string Slug)? ParseListId(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            var u = Array.IndexOf(parts, "users");
            var l = Array.IndexOf(parts, "lists");
            return u >= 0 && l == u + 2 && parts.Length > l + 1 ? (parts[u + 1], parts[l + 1]) : null;
        }

        var bits = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return bits.Length == 2 ? (bits[0], bits[1]) : null;
    }

    // ------------------------------------------------------------------ sign-in (device code)

    /// <summary>Starts connecting an account: returns a code to enter at trakt.tv/activate, then waits for it in the background.</summary>
    public async Task<DeviceLogin> StartLoginAsync(CancellationToken ct)
    {
        if (!IsConfigured || ClientSecret.Length == 0)
        {
            throw new InvalidOperationException("Enter your Trakt app's Client ID and Client Secret, and save, first.");
        }

        var json = await PostAsync("oauth/device/code", new JsonObject { ["client_id"] = ClientId }, ct).ConfigureAwait(false);
        var login = new DeviceLogin(
            (string?)json["user_code"] ?? throw new InvalidOperationException("Trakt sent no code"),
            (string?)json["verification_url"] ?? "https://trakt.tv/activate",
            DateTime.UtcNow.AddSeconds((int?)json["expires_in"] ?? 600));
        var deviceCode = (string?)json["device_code"] ?? string.Empty;
        var interval = Math.Max(1, (int?)json["interval"] ?? 5);
        lock (_lock)
        {
            _pending = login;
            _lastError = null;
        }

        _ = Task.Run(() => PollAsync(login, deviceCode, interval));
        return login;
    }

    private async Task PollAsync(DeviceLogin login, string deviceCode, int interval)
    {
        while (DateTime.UtcNow < login.Expires && ReferenceEquals(_pending, login))
        {
            await Task.Delay(TimeSpan.FromSeconds(interval)).ConfigureAwait(false);
            try
            {
                using var req = Request(HttpMethod.Post, "oauth/device/token", auth: false);
                req.Content = Json(new JsonObject { ["code"] = deviceCode, ["client_id"] = ClientId, ["client_secret"] = ClientSecret });
                using var resp = await HttpClients.Default.SendAsync(req).ConfigureAwait(false);
                switch ((int)resp.StatusCode)
                {
                    case 200:
                        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false))!;
                        var tokens = ToTokens(json);
                        tokens.User = await TryGetUserAsync(tokens.AccessToken).ConfigureAwait(false);
                        SaveTokens(tokens);
                        lock (_lock)
                        {
                            _pending = null;
                        }

                        _logger.LogInformation("Trakt account {User} connected", tokens.User);
                        return;
                    case 400: // waiting for the user
                        continue;
                    case 429: // polling too fast
                        interval++;
                        continue;
                    default: // 404 bad code, 409 used, 410 expired, 418 denied
                        _lastError = (int)resp.StatusCode switch
                        {
                            410 => "The code expired. Try again.",
                            418 => "Access was denied on Trakt.",
                            409 => "That code was already used.",
                            _ => $"Trakt refused the sign-in (HTTP {(int)resp.StatusCode})."
                        };
                        _pending = null;
                        return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Trakt sign-in check failed: {Message}", ex.Message);
            }
        }

        if (ReferenceEquals(_pending, login))
        {
            _pending = null;
            _lastError = "The code expired before it was entered. Try again.";
        }
    }

    private async Task<string?> TryGetUserAsync(string accessToken)
    {
        try
        {
            using var req = Request(HttpMethod.Get, "users/settings", auth: false);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await HttpClients.Default.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? (string?)JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false))?["user"]?["username"] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Tokens ToTokens(JsonNode json) => new()
    {
        AccessToken = (string?)json["access_token"] ?? string.Empty,
        RefreshToken = (string?)json["refresh_token"] ?? string.Empty,
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds((long?)json["created_at"] ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .AddSeconds((long?)json["expires_in"] ?? 7776000).UtcDateTime
    };

    private void SaveTokens(Tokens tokens)
    {
        lock (_lock)
        {
            File.WriteAllText(TokenFile, JsonSerializer.Serialize(tokens));
        }
    }

    /// <summary>The access token, refreshed when it's close to expiring. Null when no account is connected.</summary>
    private async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        var tokens = LoadTokens();
        if (tokens is null || tokens.AccessToken.Length == 0)
        {
            return null;
        }

        if (tokens.ExpiresAt > DateTime.UtcNow.AddDays(1) || ClientSecret.Length == 0 || tokens.RefreshToken.Length == 0)
        {
            return tokens.AccessToken;
        }

        try
        {
            var json = await PostAsync("oauth/token", new JsonObject
            {
                ["refresh_token"] = tokens.RefreshToken,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = "urn:ietf:wg:oauth:2.0:oob",
                ["grant_type"] = "refresh_token"
            }, ct).ConfigureAwait(false);
            var fresh = ToTokens(json);
            fresh.User = tokens.User;
            SaveTokens(fresh);
            return fresh.AccessToken;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Could not refresh the Trakt sign-in: {Message}", ex.Message);
            return tokens.AccessToken;
        }
    }

    // ------------------------------------------------------------------ HTTP

    private static HttpRequestMessage Request(HttpMethod method, string path, bool auth)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Add("trakt-api-version", "2");
        req.Headers.Add("trakt-api-key", ClientId);
        return req;
    }

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private async Task<(JsonNode? Json, int PageCount)> GetAsync(string path, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("No Trakt Client ID is set (Catalogs tab).");
        }

        using var req = Request(HttpMethod.Get, path, auth: true);
        if (await AccessTokenAsync(ct).ConfigureAwait(false) is { } token)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await HttpClients.Default.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException((int)resp.StatusCode switch
            {
                401 => "Trakt: this needs a connected account (or the list is private)",
                403 => "Trakt: the Client ID was refused",
                404 => "Trakt: list or user not found",
                _ => $"Trakt answered HTTP {(int)resp.StatusCode}"
            }, null, resp.StatusCode);
        }

        var pages = resp.Headers.TryGetValues("X-Pagination-Page-Count", out var v) && int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        return (JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), pages);
    }

    private static async Task<JsonNode> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, path, auth: false);
        req.Content = Json(body);
        using var resp = await HttpClients.Default.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Trakt refused the Client ID or Secret. Check them on the Catalogs tab.");
        }

        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) ?? throw new InvalidOperationException("Empty reply from Trakt");
    }
}
