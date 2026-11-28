using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace Jellyfin.Plugin.P2PStream.Torrent;

/// <summary>
/// Wraps the torrent engine's file writer so it can only touch files inside the download folder. A hostile torrent can
/// name its file with an absolute path (on Linux the engine then writes exactly there) or a parent folder; every read,
/// write, flush, move and existence check on such a path is refused before it reaches the disk.
/// </summary>
internal sealed class ConfinedPieceWriter : IPieceWriter
{
    private readonly IPieceWriter _inner;

    public ConfinedPieceWriter(IPieceWriter inner) => _inner = inner;

    public int OpenFiles => _inner.OpenFiles;

    public int MaximumOpenFiles => _inner.MaximumOpenFiles;

    /// <summary>True if <paramref name="path"/> is inside <paramref name="root"/> (whole folder names, after resolving ".." and the like).</summary>
    internal static bool IsInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(path);
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(folder, comparison);
    }

    private static string Check(string path)
    {
        var root = Plugin.Instance?.DownloadPath;
        if (string.IsNullOrEmpty(root) || !IsInside(path, root))
        {
            throw new UnauthorizedAccessException($"Refused to touch a file outside the download folder: {path}");
        }

        return path;
    }

    public ReusableTask CloseAsync(ITorrentManagerFile file) => _inner.CloseAsync(file);

    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file)
    {
        Check(file.FullPath);
        return _inner.ExistsAsync(file);
    }

    public ReusableTask FlushAsync(ITorrentManagerFile file)
    {
        Check(file.FullPath);
        return _inner.FlushAsync(file);
    }

    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        Check(file.FullPath);
        Check(fullPath);
        return _inner.MoveAsync(file, fullPath, overwrite);
    }

    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
    {
        Check(file.FullPath);
        return _inner.ReadAsync(file, offset, buffer);
    }

    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
    {
        Check(file.FullPath);
        return _inner.WriteAsync(file, offset, buffer);
    }

    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => _inner.SetMaximumOpenFilesAsync(maximumOpenFiles);

    public void Dispose() => _inner.Dispose();
}
