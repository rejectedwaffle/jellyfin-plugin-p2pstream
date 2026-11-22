using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Jellyfin.Plugin.P2PStream.Providers;

/// <summary>How a source did the last time it was searched.</summary>
public sealed record SourceStatus(bool Ok, int Count, string? Message, DateTime At, long Milliseconds);

/// <summary>
/// Remembers each source's last search result (results or the reason it failed), shown on the Sources tab so a
/// blocked or moved site is obvious at a glance.
/// </summary>
public sealed class SourceHealth
{
    private readonly ConcurrentDictionary<string, SourceStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SourceStatus> All => _status;

    public void Success(string source, int count, long ms) => _status[source] = new SourceStatus(true, count, null, DateTime.UtcNow, ms);

    public void Failure(string source, Exception ex, long ms) => _status[source] = new SourceStatus(false, 0, Describe(ex), DateTime.UtcNow, ms);

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
