using Wcm.Docker;

namespace Wcm.Tests;

public sealed class VhdxCompactorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wcm-test-" + Guid.NewGuid().ToString("N")[..8]);

    public VhdxCompactorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string Touch(string rel)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, [0]);
        return p;
    }

    [Fact]
    public void FindDisks_NoDocker_Empty() => Assert.Empty(VhdxCompactor.FindDisks(_root));

    [Fact]
    public void FindDisks_FindsCurrentAndLegacyLayouts()
    {
        var current = Touch(@"Docker\wsl\disk\docker_data.vhdx");
        var legacy = Touch(@"Docker\wsl\data\ext4.vhdx");
        Touch(@"Docker\wsl\other\random.vhdx");

        Assert.Equal([current, legacy], VhdxCompactor.FindDisks(_root));
    }

    [Fact]
    public void CompactScript_AttachesReadonlyAndDetachesEachDisk()
    {
        var script = VhdxCompactor.BuildCompactScript([@"C:\a b\x.vhdx", @"C:\y.vhdx"]);
        Assert.Equal(
            "select vdisk file=\"C:\\a b\\x.vhdx\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n" +
            "select vdisk file=\"C:\\y.vhdx\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n",
            script);
    }

    [Fact]
    public void DetachScript_IgnoresErrors()
    {
        var script = VhdxCompactor.BuildDetachScript([@"C:\x.vhdx"]);
        Assert.Equal("select vdisk file=\"C:\\x.vhdx\"\r\ndetach vdisk noerr\r\n", script);
    }

    [Fact]
    public void Batch_AlwaysRunsDetachAndKeepsCompactExitCode()
    {
        var bat = VhdxCompactor.BuildBatch(@"C:\t\c.txt", @"C:\t\d.txt", @"C:\t\log.txt");
        var lines = bat.Split('\n', StringSplitOptions.TrimEntries);
        Assert.Equal("diskpart /s \"C:\\t\\c.txt\" > \"C:\\t\\log.txt\" 2>&1", lines[1]);
        Assert.Equal("set rc=%errorlevel%", lines[2]);
        Assert.Equal("diskpart /s \"C:\\t\\d.txt\" >> \"C:\\t\\log.txt\" 2>&1", lines[3]);
        Assert.Equal("exit /b %rc%", lines[4]);
    }
}
