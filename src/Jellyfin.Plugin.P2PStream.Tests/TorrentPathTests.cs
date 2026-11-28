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
    public void An_absolute_single_file_name_is_saved_inside_the_folder()
    {
        var t = MonoTorrent.Torrent.Load(Torrent("d6:lengthi1e4:name12:/tmp/evil.sh12:piece lengthi16384e6:pieces20:"));
        var save = Path.Combine(Path.GetTempPath(), "p2pstream-tests", "hash");
        Directory.CreateDirectory(save);
        using var engine = new MonoTorrent.Client.ClientEngine(new MonoTorrent.Client.EngineSettingsBuilder { AllowPortForwarding = false, DhtEndPoint = null, ListenEndPoints = new() }.ToSettings());
        var m = engine.AddAsync(t, save).GetAwaiter().GetResult();
        var full = Path.GetFullPath(m.Files[0].FullPath);
        engine.RemoveAsync(m).GetAwaiter().GetResult();
        Assert.StartsWith(Path.GetFullPath(save) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
    }
}
