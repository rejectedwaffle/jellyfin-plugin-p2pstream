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

    /// <summary>
    /// True if a download needing <paramref name="need"/> bytes fits: torrent files only take space as they download,
    /// so the space the titles being watched still need (<paramref name="promised"/>) is taken off what's free, and space
    /// that removing idle torrents would free (<paramref name="reclaimable"/>) is added.
    /// </summary>
    public static bool Fits(long need, long free, long promised, long reclaimable = 0) => need <= free - promised + reclaimable;

    public static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
}
