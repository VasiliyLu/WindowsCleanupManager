using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace Wcm.Scanning;

public sealed class ScanProgress
{
    internal long Dirs, Files, Bytes, Errors;
    internal volatile string Current = "";

    public long DirCount => Interlocked.Read(ref Dirs);
    public long FileCount => Interlocked.Read(ref Files);
    public long ByteCount => Interlocked.Read(ref Bytes);
    public long ErrorCount => Interlocked.Read(ref Errors);
    public string CurrentPath => Current;
    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
}

public sealed class ParallelScanner(int threads = 0, long fileNodeThreshold = 1 << 20)
{
    // Cloud placeholders (OneDrive etc.) report logical size but take no space on disk
    private const int AttrOffline = 0x1000;
    private const int AttrRecallOnOpen = 0x40000;
    private const int AttrRecallOnDataAccess = 0x400000;

    private static readonly HashSet<string> MarkerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "Cargo.toml", "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle",
        "go.mod", "pyproject.toml", "requirements.txt", "setup.py", "CMakeLists.txt", "composer.json",
        "Gemfile", "pubspec.yaml", "Directory.Build.props"
    };

    private static readonly string[] MarkerExtensions = [".csproj", ".fsproj", ".vbproj", ".vcxproj", ".sln", ".slnx"];

    private readonly int _threads = threads > 0 ? threads : Math.Max(4, Environment.ProcessorCount);

    public ScanProgress Progress { get; private set; } = new();

    public async Task<FsNode> ScanAsync(string rootPath, CancellationToken ct = default)
    {
        Progress = new ScanProgress();
        var root = new FsNode(Path.GetFullPath(rootPath), null, isDir: true);

        var channel = Channel.CreateUnbounded<FsNode>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        long pending = 1;
        channel.Writer.TryWrite(root);

        var workers = Enumerable.Range(0, _threads).Select(_ => Task.Run(async () =>
        {
            await foreach (var dir in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    ScanDirectory(dir, subdir =>
                    {
                        Interlocked.Increment(ref pending);
                        channel.Writer.TryWrite(subdir);
                    });
                }
                catch (Exception)
                {
                    dir.AccessDenied = true;
                    Interlocked.Increment(ref Progress.Errors);
                }

                if (Interlocked.Decrement(ref pending) == 0)
                    channel.Writer.TryComplete();
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        Progress.Elapsed.Stop();
        return root;
    }

    private void ScanDirectory(FsNode dir, Action<FsNode> enqueue)
    {
        var path = dir.FullPath;
        Progress.Current = path;

        using var e = new DirEnumerator(path);
        long smallSize = 0, ownBytes = 0, ownFiles = 0, maxWrite = 0;
        int smallCount = 0;
        List<FsNode>? subdirs = null;

        while (e.MoveNext())
        {
            var entry = e.Current;
            if (entry.IsDir)
            {
                // Junctions/symlinks: skip to avoid loops and double counting
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var child = new FsNode(entry.Name, dir, isDir: true);
                dir.Children.Add(child);
                (subdirs ??= []).Add(child);
                continue;
            }

            var attrs = (int)entry.Attributes;
            if ((attrs & (AttrOffline | AttrRecallOnOpen | AttrRecallOnDataAccess)) != 0) continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            ownBytes += entry.Length;
            ownFiles++;
            if (entry.LastWriteTicks > maxWrite) maxWrite = entry.LastWriteTicks;

            if (IsMarker(entry.Name)) (dir.Markers ??= []).Add(entry.Name);

            if (entry.Length >= fileNodeThreshold)
            {
                var f = new FsNode(entry.Name, dir, isDir: false);
                f.SetFileInfo(entry.Length, entry.LastWriteTicks);
                dir.Children.Add(f);
            }
            else
            {
                smallSize += entry.Length;
                smallCount++;
            }
        }

        dir.SmallFilesSize = smallSize;
        dir.SmallFilesCount = smallCount;
        dir.Scanned = true;
        if (e.Errors > 0)
        {
            dir.AccessDenied = true;
            Interlocked.Add(ref Progress.Errors, e.Errors);
        }

        // Size is accounted before children are queued so parents never look smaller than they are
        if (ownBytes != 0 || ownFiles != 0) dir.AddUp(ownBytes, ownFiles);
        if (maxWrite > 0) dir.BumpLastWrite(maxWrite);

        Interlocked.Increment(ref Progress.Dirs);
        Interlocked.Add(ref Progress.Files, ownFiles);
        Interlocked.Add(ref Progress.Bytes, ownBytes);

        if (subdirs is not null)
            foreach (var s in subdirs) enqueue(s);
    }

    private static bool IsMarker(string name)
    {
        if (MarkerNames.Contains(name)) return true;
        foreach (var ext in MarkerExtensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal readonly record struct Entry(string Name, bool IsDir, long Length, FileAttributes Attributes, long LastWriteTicks);

    private sealed class DirEnumerator(string path) : FileSystemEnumerator<Entry>(path, Options)
    {
        private static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024
        };

        public int Errors { get; private set; }

        protected override Entry TransformEntry(ref FileSystemEntry entry) =>
            new(entry.FileName.ToString(), entry.IsDirectory, entry.Length, entry.Attributes, entry.LastWriteTimeUtc.UtcTicks);

        protected override bool ContinueOnError(int error)
        {
            Errors++;
            return true;
        }
    }
}
