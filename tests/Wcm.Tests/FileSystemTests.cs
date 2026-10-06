using System.Diagnostics;
using Wcm.Classification;
using Wcm.Deletion;
using Wcm.Docker;
using Wcm.Model;
using Wcm.Scanning;

namespace Wcm.Tests;

public sealed class FileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wcm-test-" + Guid.NewGuid().ToString("N")[..8]);

    public FileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Remove junctions first so cleanup never walks into their targets
        foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).ToList())
            if (Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private string Write(string rel, int bytes)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction not created");
    }

    [Fact]
    public async Task Scanner_sums_sizes_and_skips_junctions()
    {
        Write(@"a\one.bin", 1000);
        Write(@"a\deep\two.bin", 2000);
        Write(@"b\big.bin", 3 << 20); // above node threshold
        Write(@"proj\package.json", 10);
        Junction(Path.Combine(_root, "link"), Path.Combine(_root, "a"));

        var root = await new ParallelScanner(threads: 4, fileNodeThreshold: 1 << 20).ScanAsync(_root);

        Assert.Equal(1000 + 2000 + (3 << 20) + 10, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.DoesNotContain(root.Children, c => c.Name == "link");

        var a = root.Children.Single(c => c.Name == "a");
        Assert.Equal(3000, a.Size);
        Assert.Equal(1000, a.SmallFilesSize);
        var b = root.Children.Single(c => c.Name == "b");
        Assert.Single(b.Children, c => !c.IsDir && c.Name == "big.bin");
        Assert.Contains("package.json", root.Children.Single(c => c.Name == "proj").Markers!);
        Assert.Equal(Path.Combine(_root, "a", "deep"), a.Children.Single().FullPath);
    }

    [Fact]
    public void Permanent_delete_removes_link_but_not_its_target()
    {
        var keep = Write(@"keep\precious.txt", 100);
        Write(@"victim\sub\x.bin", 100);
        var ro = Write(@"victim\readonly.txt", 10);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        Junction(Path.Combine(_root, "victim", "sub", "link"), Path.Combine(_root, "keep"));

        var errors = new List<string>();
        Deleter.DeleteTree(Path.Combine(_root, "victim"), errors);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(Path.Combine(_root, "victim")));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Deleter_contents_mode_keeps_folder_and_validates()
    {
        Write(@"tmp\a.txt", 100);
        Write(@"tmp\sub\b.txt", 100);
        var dir = Path.Combine(_root, "tmp");
        var engine = new RuleEngine(RuleEngine.LoadBuiltin(), []);
        var item = new CleanupItem { Kind = ItemKind.FolderContents, Target = dir, Display = dir, Size = 200 };

        var report = await new Deleter(engine).DeleteAsync([item], permanent: true, null, CancellationToken.None);

        Assert.True(report.Outcomes.Single().Success);
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public async Task Deleter_refuses_protected_paths()
    {
        var engine = new RuleEngine(RuleEngine.LoadBuiltin(), []);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var item = new CleanupItem { Kind = ItemKind.Folder, Target = win, Display = win };

        var report = await new Deleter(engine).DeleteAsync([item], permanent: true, null, CancellationToken.None);

        Assert.False(report.Outcomes.Single().Success);
        Assert.True(Directory.Exists(win));
    }

    [Fact]
    public void Session_selection_skips_items_inside_checked_folders()
    {
        var root = new FsNode(@"C:\", null, true);
        var parent = new FsNode("p", root, true); root.Children.Add(parent);
        var child = new FsNode("c", parent, true); parent.Children.Add(child);

        var s = new ScanSession { Label = "t", Root = root };
        var pi = new CleanupItem { Kind = ItemKind.Folder, Target = @"C:\p", Size = 10, Checked = true, Node = parent };
        var ci = new CleanupItem { Kind = ItemKind.Folder, Target = @"C:\p\c", Size = 5, Checked = true, Node = child };
        s.Add([pi, ci]);

        Assert.Equal([pi], s.EffectiveSelection());
        pi.Checked = false;
        Assert.Equal([ci], s.EffectiveSelection());
    }

    [Fact]
    public void Docker_df_parsing()
    {
        const string json = """
            {"Images":[
              {"Containers":"0","CreatedSince":"2 days ago","ID":"sha256:aaaaaaaaaaaaaaaaaaaa","Repository":"<none>","Tag":"<none>","Size":"2.4MB","UniqueSize":"2.397MB"},
              {"Containers":"0","CreatedSince":"1 day ago","ID":"sha256:bbbbbbbbbbbbbbbbbbbb","Repository":"node","Tag":"22","Size":"227MB","UniqueSize":"227MB"},
              {"Containers":"1","ID":"sha256:cccc","Repository":"used","Tag":"latest","Size":"1GB","UniqueSize":"1GB"}],
             "Volumes":[{"Name":"orphan","Links":"0","Size":"1.5GB"},{"Name":"inuse","Links":"1","Size":"3GB"}],
             "BuildCache":[{"InUse":"false","Size":"1GB"},{"InUse":"true","Size":"5GB"},{"InUse":"false","Size":"500MB"}]}
            """;
        var items = DockerProvider.Parse(json);

        Assert.Equal(4, items.Count);
        var dangling = items.Single(i => i.Display.StartsWith("<none>"));
        Assert.True(dangling.Checked);
        Assert.Equal(2_397_000, dangling.Size);
        Assert.False(items.Single(i => i.Display == "node:22").Checked);
        var vol = items.Single(i => i.Kind == ItemKind.DockerVolume);
        Assert.Equal("orphan", vol.Target);
        Assert.False(vol.Checked);
        Assert.Equal(1_500_000_000, items.Single(i => i.Kind == ItemKind.DockerBuildCache).Size);
    }

    [Theory]
    [InlineData("50MB", 50L << 20)]
    [InlineData("1.5 GB", 3L << 29)]
    [InlineData("2048", 2048)]
    [InlineData("0B", 0)]
    public void ByteSize_parse(string text, long expected) => Assert.Equal(expected, ByteSize.Parse(text));
}
