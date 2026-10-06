using Wcm.Model;
using Wcm.Scanning;

namespace Wcm.Classification;

/// <summary>Rule pass over the scanned tree: marks nodes and produces rule-based items.</summary>
public sealed class Classifier(RuleEngine rules, long minItemSize = 0)
{
    public List<CleanupItem> ApplyRules(FsNode root, DateTime? nowUtc = null)
    {
        var items = new List<CleanupItem>();
        Walk(root, inProtected: false, items, nowUtc ?? DateTime.UtcNow);
        return items;
    }

    private void Walk(FsNode node, bool inProtected, List<CleanupItem> items, DateTime now)
    {
        var rule = rules.Match(node, inProtected);
        switch (rule?.Action)
        {
            case RuleAction.Protect:
                node.Mark = NodeMark.Protected;
                MarkAncestorsContainProtected(node);
                inProtected = true;
                break;

            case RuleAction.Never:
                node.Mark = NodeMark.Known;
                return;

            case RuleAction.Suggest:
                if (inProtected && node.Mark == NodeMark.None) node.Mark = NodeMark.Protected;
                if (!RuleEngine.ConditionsMet(rule, node, now))
                {
                    if (!inProtected) node.Mark = NodeMark.Known;
                    return;
                }
                // Items inside protected trees keep the node marked as suggested so the UI shows them
                node.Mark = NodeMark.Suggested;
                if (node.Size > 0 && node.Size >= minItemSize) items.Add(FromRule(rule, node));
                else node.Mark = NodeMark.Known;
                return;

            case RuleAction.Container:
                if (!inProtected) node.Mark = NodeMark.Container;
                break;

            default:
                if (inProtected) node.Mark = NodeMark.Protected;
                break;
        }

        if (!node.IsDir) return;
        // Snapshot: Children may be mutated later by deletions, not during this pass
        foreach (var child in node.Children)
            Walk(child, inProtected, items, now);
    }

    private static void MarkAncestorsContainProtected(FsNode node)
    {
        for (var n = node; n is not null && !n.ContainsProtected; n = n.Parent)
            n.ContainsProtected = true;
    }

    public static CleanupItem FromRule(Rule rule, FsNode node) => new()
    {
        Kind = !node.IsDir ? ItemKind.File : rule.Contents ? ItemKind.FolderContents : ItemKind.Folder,
        Target = node.FullPath,
        Display = node.FullPath,
        Size = node.Size,
        Category = rule.Category,
        Reason = rule.Reason,
        Source = ItemSource.Rule,
        Safety = rule.Safety,
        Checked = rule.Safety == Safety.Safe,
        RuleId = rule.Id,
        Node = node
    };
}
