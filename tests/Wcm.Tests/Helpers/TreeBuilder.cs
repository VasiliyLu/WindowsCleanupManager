using Wcm.Scanning;

namespace Wcm.Tests.Helpers;

/// <summary>Builds in-memory trees without touching the disk.</summary>
internal static class TreeBuilder
{
    public static FsNode Root(string path = @"C:\") => new(path, null, isDir: true);

    public static FsNode Dir(this FsNode parent, string name, params string[] markers)
    {
        var d = new FsNode(name, parent, isDir: true);
        parent.Children.Add(d);
        if (markers.Length > 0) d.Markers = [.. markers];
        return d;
    }

    public static FsNode File(this FsNode parent, string name, long size, DateTime? lastWriteUtc = null)
    {
        var f = new FsNode(name, parent, isDir: false);
        f.SetFileInfo(0, 0);
        parent.Children.Add(f);
        // Push size into the file node and every ancestor
        f.AddUp(size, 0);
        var ticks = (lastWriteUtc ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks;
        f.BumpLastWrite(ticks);
        parent.AddUp(0, 1);
        return f;
    }

    public static FsNode SmallFiles(this FsNode dir, long size, int count = 10)
    {
        dir.SmallFilesSize += size;
        dir.SmallFilesCount += count;
        dir.AddUp(size, count);
        dir.BumpLastWrite(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);
        return dir;
    }

    public const long MB = 1L << 20;
    public const long GB = 1L << 30;
}
