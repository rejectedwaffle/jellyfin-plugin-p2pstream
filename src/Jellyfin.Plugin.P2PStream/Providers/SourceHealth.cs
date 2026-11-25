using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>How a source did the last time it was searched.</summary>
public sealed record SourceStatus(bool Ok, int Count, string? Message, DateTime At, long Milliseconds, bool NoAnswer = false);

/// <summary>
/// Remembers each source's last search result (results or the reason it failed), shown on the Sources tab so a
/// blocked or moved site is obvious at a glance.
/// </summary>
public sealed class SourceHealth
{
    private readonly ConcurrentDictionary<string, SourceStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SourceStatus> All => _status;

    public void Success(string source, int count, long ms) => _status[source] = new SourceStatus(true, count, null, DateTime.UtcNow, ms);

    public void Failure(string source, Exception ex, long ms) =>
        _status[source] = new SourceStatus(false, 0, Describe(ex), DateTime.UtcNow, ms, IsNoAnswer(ex));

    /// <summary>How long a source that didn't answer is left out of searches before it's tried again.</summary>
    public static readonly TimeSpan RestAfterNoAnswer = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True while a source that didn't answer last time rests: searches skip it instead of each waiting for it to time
    /// out again. It's tried again after <see cref="RestAfterNoAnswer"/>.
    /// </summary>
    public bool IsResting(string source, out TimeSpan left)
    {
        left = TimeSpan.Zero;
        if (_status.TryGetValue(source, out var s) && !s.Ok && s.NoAnswer)
        {
            left = s.At + RestAfterNoAnswer - DateTime.UtcNow;
            return left > TimeSpan.Zero;
        }

        return false;
    }

    private static bool IsNoAnswer(Exception ex) =>
        (ex is AggregateException { InnerException: { } inner } ? inner : ex) is OperationCanceledException or TimeoutException;

    /// <summary>Turns a failure into a short, plain reason.</summary>
    public static string Describe(Exception ex)
    {
        var root = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        return root switch
        {
            OperationCanceledException or TimeoutException => "no answer (timed out)",
            HttpRequestException { StatusCode: { } code } => $"site returned an error (HTTP {(int)code})",
            HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } } => "address not found (blocked or moved?)",
            HttpRequestException => "couldn't connect",
            InvalidOperationException ioe when ioe.Message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) => "blocked by a bot check",
            System.Text.Json.JsonException or System.Xml.XmlException => "unexpected reply (site changed?)",
            _ => root.Message.Length > 90 ? root.Message[..87] + "..." : root.Message
        };
    }
}
