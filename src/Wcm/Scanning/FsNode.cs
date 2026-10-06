namespace Wcm.Scanning;

public enum NodeMark
{
    None,
    /// <summary>A cleanup item exists for this node.</summary>
    Suggested,
    /// <summary>Matched a rule (never, or suggest with unmet conditions): no Jev, no descent.</summary>
    Known,
    Protected,
    /// <summary>Never suggested itself, but its children are looked at.</summary>
    Container
}

public sealed class FsNode
{
    private long _size;
    private long _files;
    private long _lastWriteTicks;

    public FsNode(string name, FsNode? parent, bool isDir)
    {
        Name = name;
        Parent = parent;
        IsDir = isDir;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    /// <summary>For the root this is the full path, otherwise just the entry name.</summary>
    public string Name { get; }
    public FsNode? Parent { get; private set; }
    public bool IsDir { get; }
    public int Depth { get; }

    /// <summary>Subdirectories and files above the "big file" threshold. Written only by the worker that enumerated this dir.</summary>
    public List<FsNode> Children { get; } = [];

    /// <summary>Files below the threshold are not kept as nodes, only summed here.</summary>
    public long SmallFilesSize { get; internal set; }
    public int SmallFilesCount { get; internal set; }

    /// <summary>Names of all files directly in this dir that look like project markers (package.json, *.csproj...).</summary>
    public List<string>? Markers { get; internal set; }

    public bool AccessDenied { get; internal set; }
    public bool Scanned { get; internal set; }

    /// <summary>Set by the classifier.</summary>
    public NodeMark Mark { get; set; }

    /// <summary>True if this node or something below it is protected — such a node must never be deleted as a whole.</summary>
    public bool ContainsProtected { get; set; }

    public long Size => Interlocked.Read(ref _size);
    public long FileCount => Interlocked.Read(ref _files);
    public DateTime LastWriteUtc => new(Interlocked.Read(ref _lastWriteTicks), DateTimeKind.Utc);

    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            var parentPath = Parent.FullPath;
            return parentPath.EndsWith('\\') ? parentPath + Name : parentPath + "\\" + Name;
        }
    }

    /// <summary>Adds size/count to this node and all ancestors.</summary>
    internal void AddUp(long bytes, long files)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            if (bytes != 0) Interlocked.Add(ref n._size, bytes);
            if (files != 0) Interlocked.Add(ref n._files, files);
        }
    }

    public void AddSize(long bytes) => AddUp(bytes, 0);

    internal void BumpLastWrite(long ticks)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            long cur;
            do
            {
                cur = Interlocked.Read(ref n._lastWriteTicks);
                if (ticks <= cur) return;
            } while (Interlocked.CompareExchange(ref n._lastWriteTicks, ticks, cur) != cur);
        }
    }

    internal void SetFileInfo(long size, long lastWriteTicks)
    {
        _size = size;
        _files = 1;
        _lastWriteTicks = lastWriteTicks;
    }

    /// <summary>Detaches the node after deletion and fixes ancestor sizes.</summary>
    public void Remove()
    {
        if (Parent is null) return;
        var p = Parent;
        p.AddUp(-Size, -FileCount);
        p.Children.Remove(this);
        Parent = null;
    }

    /// <summary>Clears the contents of a dir (when its contents were deleted, not the dir itself).</summary>
    public void ClearContents()
    {
        AddUp(-Size, -FileCount);
        Children.Clear();
        SmallFilesCount = 0;
        SmallFilesSize = 0;
    }

    public bool IsUnder(FsNode ancestor)
    {
        for (var n = this; n is not null; n = n.Parent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    public override string ToString() => FullPath;
}
