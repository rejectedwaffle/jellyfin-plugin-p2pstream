using System.Text;
using Xunit;

namespace Jellyfin.Plugin.P2PStream.Tests;

/// <summary>
/// What the torrent library does with hostile file names, which the plugin relies on: a torrent can't make it write
/// outside the torrent's own folder (the plugin also checks every file once the file list is known).
/// </summary>
public class TorrentPathTests
{
    private static byte[] Torrent(string info)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("d4:info" + info));
        bytes.AddRange(new byte[20]); // the piece hash
        bytes.AddRange(Encoding.ASCII.GetBytes("ee"));
        return bytes.ToArray();
    }

    [Theory]
    [InlineData("d5:filesld6:lengthi1e4:pathl2:..2:..8:evil.txteee4:name4:test12:piece lengthi16384e6:pieces20:")]
    [InlineData("d5:filesld6:lengthi1e4:pathl8:/tmp/abs8:evil.txteee4:name4:test12:piece lengthi16384e6:pieces20:")]
    public void Parent_and_absolute_folders_in_a_file_list_are_refused(string info) =>
        Assert.ThrowsAny<ArgumentException>(() => MonoTorrent.Torrent.Load(Torrent(info)));

    [Fact]
    public void The_file_writer_refuses_paths_outside_the_download_folder()
    {
        TestSetup.Settings();
        var downloads = Plugin.Instance!.DownloadPath;
        var inner = new Moq.Mock<MonoTorrent.PieceWriter.IPieceWriter>();
        var writer = new Jellyfin.Plugin.P2PStream.Torrent.ConfinedPieceWriter(inner.Object);
        MonoTorrent.ITorrentManagerFile File(string path)
        {
            var f = new Moq.Mock<MonoTorrent.ITorrentManagerFile>();
            f.SetupGet(x => x.FullPath).Returns(path);
            return f.Object;
        }

        // A single-file torrent named with an absolute path: on Linux the engine would write exactly there.
        var outside = OperatingSystem.IsWindows() ? @"C:\Windows\Temp\evil.sh" : "/tmp/evil.sh";
        Assert.Throws<UnauthorizedAccessException>(() => writer.WriteAsync(File(outside), 0, new byte[1]));
        Assert.Throws<UnauthorizedAccessException>(() => writer.ReadAsync(File(outside), 0, new byte[1]));
        Assert.Throws<UnauthorizedAccessException>(() => writer.ExistsAsync(File(Path.Combine(downloads, "abc", "..", "..", "escaped.txt"))));

        writer.WriteAsync(File(Path.Combine(downloads, "abc", "video.mkv")), 0, new byte[1]);
        inner.Verify(x => x.WriteAsync(Moq.It.IsAny<MonoTorrent.ITorrentManagerFile>(), 0, Moq.It.IsAny<ReadOnlyMemory<byte>>()), Moq.Times.Once);
    }

    [Theory]
    [InlineData("/data/downloads/abc/file.mkv", "/data/downloads", true)]
    [InlineData("/data/downloads2/file.mkv", "/data/downloads", false)]
    [InlineData("/data/downloads/../etc/passwd", "/data/downloads", false)]
    public void Inside_means_inside_by_whole_folder_names(string path, string root, bool expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix paths
        }

        Assert.Equal(expected, Jellyfin.Plugin.P2PStream.Torrent.ConfinedPieceWriter.IsInside(path, root));
    }
}
