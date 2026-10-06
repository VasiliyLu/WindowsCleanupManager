using Wcm.Classification;
using Wcm.Scanning;

namespace Wcm.Model;

public sealed class ScanSession
{
    public required string Label { get; init; }
    public FsNode? Root { get; init; }
    public List<CleanupItem> Items { get; } = [];
    public Dictionary<FsNode, CleanupItem> ByNode { get; } = new(ReferenceEqualityComparer.Instance);

    public TimeSpan ScanTime { get; set; }
    public long ScanErrors { get; set; }
    public JevRunProgress? Jev { get; set; }
    public JevPlan? JevPlan { get; set; }
    public string? JevNote { get; set; }
    public string? DockerError { get; set; }

    public void Add(IEnumerable<CleanupItem> items)
    {
        foreach (var i in items)
        {
            if (i.Node is not null)
            {
                if (ByNode.ContainsKey(i.Node)) continue;
                ByNode[i.Node] = i;
            }
            Items.Add(i);
        }
    }

    public void Remove(CleanupItem item)
    {
        Items.Remove(item);
        if (item.Node is not null) ByNode.Remove(item.Node);
    }

    /// <summary>Checked items, minus those already inside another checked folder.</summary>
    public List<CleanupItem> EffectiveSelection()
    {
        var checkedNodes = new HashSet<FsNode>(Items.Where(i => i.Checked && i.Node is not null).Select(i => i.Node!), ReferenceEqualityComparer.Instance);
        return Items.Where(i => i.Checked && !IsCovered(i, checkedNodes)).ToList();
    }

    public bool IsCovered(CleanupItem item) =>
        IsCovered(item, new HashSet<FsNode>(Items.Where(i => i.Checked && i.Node is not null).Select(i => i.Node!), ReferenceEqualityComparer.Instance));

    private static bool IsCovered(CleanupItem item, HashSet<FsNode> checkedNodes)
    {
        for (var n = item.Node?.Parent; n is not null; n = n.Parent)
            if (checkedNodes.Contains(n)) return true;
        return false;
    }

    public long SelectedSize => EffectiveSelection().Sum(i => i.Size);
}
