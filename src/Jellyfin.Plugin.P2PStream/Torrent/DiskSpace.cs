namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>Free and total space of the disk a folder is on.</summary>
public static class DiskSpace
{
    /// <summary>Free space kept on top of what a stream needs: room for Jellyfin's own transcoding files.</summary>
    public const long Margin = 3L << 30;

    /// <summary>
    /// The mounted drive that holds <paramref name="path"/>: the one with the longest matching root, so a folder on its
    /// own mount (Linux) is right. Null if it can't be told.
    /// </summary>
    public static DriveInfo? DriveFor(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.TotalSize > 0 && full.StartsWith(d.RootDirectory.FullName, comparison))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Free space where torrents download to, or null if it can't be told.</summary>
    public static long? FreeForDownloads()
    {
        try
        {
            return DriveFor(Plugin.Instance?.DownloadPath)?.AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
}
