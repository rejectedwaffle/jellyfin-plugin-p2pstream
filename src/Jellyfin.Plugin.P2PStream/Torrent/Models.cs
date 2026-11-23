using MonoTorrent;

namespace Jellyfin.Plugin.P2PStream.Torrent;

public sealed class TorrentSource
{
    private TorrentSource(string infoHash, string? magnet, byte[]? torrentBytes, string? name)
    {
        InfoHash = infoHash.ToLowerInvariant();
        Magnet = magnet;
        TorrentBytes = torrentBytes;
        Name = name;
    }

    public string InfoHash { get; }

    public string? Magnet { get; }

    public byte[]? TorrentBytes { get; }

    public string? Name { get; }

    public static TorrentSource FromMagnet(string magnet)
    {
        var link = MagnetLink.Parse(magnet.Trim());
        return new TorrentSource(link.InfoHashes.V1OrV2.ToHex(), magnet.Trim(), null, link.Name);
    }

    public static TorrentSource FromTorrentBytes(byte[] bytes)
    {
        var t = MonoTorrent.Torrent.Load(bytes);
        return new TorrentSource(t.InfoHashes.V1OrV2.ToHex(), null, bytes, t.Name);
    }
}

public sealed class TorrentFileStatus
{
    public int Index { get; set; }

    public string Path { get; set; } = string.Empty;

    public long Length { get; set; }

    public bool IsVideo { get; set; }

    /// <summary>Gets or sets the direct stream URL (filled in by the API for video files).</summary>
    public string? StreamUrl { get; set; }
}

public sealed class TorrentStatus
{
    public string InfoHash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public double Progress { get; set; }

    public long DownloadKbps { get; set; }

    public long UploadKbps { get; set; }

    public int Peers { get; set; }

    public int Seeds { get; set; }

    public long Downloaded { get; set; }

    public int ActiveStreams { get; set; }

    public double IdleMinutes { get; set; }

    /// <summary>Gets or sets why the torrent stopped with an error (e.g. a disk write failed), if it did.</summary>
    public string? Error { get; set; }

    public List<TorrentFileStatus> Files { get; set; } = [];
}
