using System.Globalization;
using System.Text;
using Wcm.Model;
using Wcm.Scanning;
using Wcm.Storage;

namespace Wcm.Classification;

public sealed record JevCandidate(FsNode Node, string Path, long EffectiveSize, string Fingerprint);

public sealed class JevPlan
{
    public List<JevCandidate> ToAsk { get; } = [];
    public List<CleanupItem> FromCache { get; } = [];
    public int CacheHits { get; set; }
    public int SkippedByLimit { get; set; }

    /// <summary>Rough upper bound, input tokens only (Jev output is free).</summary>
    public double EstimatedCost(double pricePerMillionInput = 0.042) => ToAsk.Count * 900 * pricePerMillionInput / 1_000_000;
}

public sealed class JevRunProgress
{
    private int _done, _failed, _skipped;
    private long _costMicros;

    public int Total { get; init; }
    public int Done => Volatile.Read(ref _done);
    public int Failed => Volatile.Read(ref _failed);
    public int SkippedByBudget => Volatile.Read(ref _skipped);
    public double Cost => Interlocked.Read(ref _costMicros) / 1_000_000.0;
    public volatile string? LastError;
    public volatile string Current = "";

    internal void AddDone(double cost)
    {
        Interlocked.Increment(ref _done);
        Interlocked.Add(ref _costMicros, (long)Math.Round(cost * 1_000_000));
    }

    internal void AddFailed(string error) { Interlocked.Increment(ref _failed); LastError = error; }
    internal void AddSkipped() => Interlocked.Increment(ref _skipped);
}

/// <summary>Picks the few nodes worth asking Jev about and keeps the bill in check.</summary>
public sealed class JevPlanner(JevSettings settings)
{
    private static readonly HashSet<string> NeverSafeCategories = ["user-data", "application", "system", "downloads"];
    private static readonly HashSet<string> RiskyCategories = ["user-data", "application", "system"];

    public JevPlan Plan(FsNode root, DecisionCache cache)
    {
        var eff = new Dictionary<FsNode, long>(ReferenceEqualityComparer.Instance);
        ComputeEffective(root, eff);

        var candidates = new List<(FsNode Node, long Eff)>();
        Visit(root, eff, candidates);

        var plan = new JevPlan();
        var toAsk = new List<JevCandidate>();
        foreach (var (node, size) in candidates.OrderByDescending(c => c.Eff))
        {
            var path = node.FullPath;
            var fp = DecisionCache.Fingerprint(node, size);
            var hit = cache.Get(path, fp);
            if (hit is not null)
            {
                plan.CacheHits++;
                var item = ToItem(node, size, hit.Deletable, hit.Category, ItemSource.Cache);
                if (item is not null) plan.FromCache.Add(item);
                continue;
            }
            toAsk.Add(new JevCandidate(node, path, size, fp));
        }

        plan.ToAsk.AddRange(toAsk.Take(settings.MaxCallsPerScan));
        plan.SkippedByLimit = Math.Max(0, toAsk.Count - settings.MaxCallsPerScan);
        return plan;
    }

    /// <summary>Size not already covered by rules/protection.</summary>
    private static long ComputeEffective(FsNode node, Dictionary<FsNode, long> eff)
    {
        long size;
        if (node.Mark is NodeMark.Suggested or NodeMark.Known)
            size = 0;
        else if (!node.IsDir)
            size = node.Mark == NodeMark.Protected ? 0 : node.Size;
        else
        {
            size = node.Mark == NodeMark.Protected ? 0 : node.SmallFilesSize;
            foreach (var c in node.Children) size += ComputeEffective(c, eff);
        }
        eff[node] = size;
        return size;
    }

    private void Visit(FsNode node, Dictionary<FsNode, long> eff, List<(FsNode, long)> candidates)
    {
        var size = eff[node];
        var minDir = settings.MinDirBytes;
        var minFile = settings.MinFileBytes;
        if (size < Math.Min(minDir, minFile)) return;
        if (node.Mark is NodeMark.Suggested or NodeMark.Known) return;

        if (!node.IsDir)
        {
            if (node.Mark != NodeMark.Protected && size >= minFile) candidates.Add((node, size));
            return;
        }

        // Protected dirs may still hold unprotected bits (e.g. a project folder with .git) — only descend
        var mustDescend = node.Mark is NodeMark.Container or NodeMark.Protected || node.Depth == 0 || node.ContainsProtected;
        if (!mustDescend && size >= minDir)
        {
            // If a few big subfolders hold most of the size, asking about them is more precise than about the parent
            var bigKids = node.Children.Where(c => eff[c] >= (c.IsDir ? minDir : minFile)).Sum(c => eff[c]);
            if (bigKids < size / 2)
            {
                candidates.Add((node, size));
                return;
            }
        }

        foreach (var c in node.Children) Visit(c, eff, candidates);
    }

    public CleanupItem? ToItem(FsNode node, long size, double deletable, string category, ItemSource source)
    {
        if (deletable < settings.ReviewThreshold) return null;
        var safe = deletable >= settings.SuggestThreshold && !NeverSafeCategories.Contains(category);
        var risky = RiskyCategories.Contains(category);
        return new CleanupItem
        {
            Kind = node.IsDir ? ItemKind.Folder : ItemKind.File,
            Target = node.FullPath,
            Display = node.FullPath,
            Size = size,
            Category = category,
            Confidence = deletable,
            Reason = $"Jev: {category}, deletable with probability {deletable:P0}" + (risky ? " — but the category is risky" : ""),
            Source = source,
            Safety = safe ? Safety.Safe : Safety.Review,
            Checked = safe,
            Node = node
        };
    }

    public async Task<List<CleanupItem>> RunAsync(JevPlan plan, JevClient client, DecisionCache cache, JevRunProgress progress, CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        var gate = new SemaphoreSlim(Math.Max(1, settings.Parallelism));

        var tasks = plan.ToAsk.Select(async c =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (progress.Cost >= settings.MaxCostPerScan) { progress.AddSkipped(); return; }
                progress.Current = c.Path;
                var verdict = await client.AskAsync(StateBuilder.Build(c.Node, c.EffectiveSize), ct).ConfigureAwait(false);
                progress.AddDone(verdict.Cost);
                cache.Put(c.Path, c.Fingerprint, verdict);

                var item = ToItem(c.Node, c.EffectiveSize, verdict.Deletable, verdict.Category, ItemSource.Jev);
                if (item is not null)
                {
                    c.Node.Mark = NodeMark.Suggested;
                    lock (items) items.Add(item);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e) { progress.AddFailed(e.Message); }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { cache.Save(); }

        foreach (var i in plan.FromCache) i.Node!.Mark = NodeMark.Suggested;
        return items;
    }
}

/// <summary>Compact, privacy-aware text description of a node for Jev.</summary>
public static class StateBuilder
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Anonymize(string path) =>
        UserProfile.Length > 0 && path.StartsWith(UserProfile, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[UserProfile.Length..]
            : path;

    public static string Build(FsNode node, long effectiveSize, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"Windows disk cleanup assistant. The user wants to free disk space and asks about this {(node.IsDir ? "folder" : "file")}.");
        sb.AppendLine($"Path: {Anonymize(node.FullPath)}");
        sb.AppendLine($"Size: {ByteSize.Format(effectiveSize)}" + (node.IsDir ? $" in {node.FileCount.ToString("N0", CultureInfo.InvariantCulture)} files" : ""));
        if (node.LastWriteUtc.Ticks > 0)
            sb.AppendLine($"Newest file modified: {node.LastWriteUtc:yyyy-MM-dd} ({(int)(now - node.LastWriteUtc).TotalDays} days ago)");

        if (node.Markers is { Count: > 0 } m) sb.AppendLine($"Project files here: {string.Join(", ", m.Take(10))}");
        if (node.Parent is { } p)
        {
            sb.Append($"Parent folder: {Anonymize(p.FullPath)}");
            if (p.Markers is { Count: > 0 } pm) sb.Append($" (contains {string.Join(", ", pm.Take(5))})");
            sb.AppendLine();
        }

        if (node.IsDir)
        {
            var top = node.Children.OrderByDescending(c => c.Size).Take(15).ToList();
            if (top.Count > 0)
            {
                sb.AppendLine("Largest entries inside:");
                foreach (var c in top)
                {
                    sb.Append(c.IsDir ? "  [dir]  " : "  [file] ").Append(c.Name).Append(" — ").Append(ByteSize.Format(c.Size));
                    if (c.IsDir) sb.Append($", {c.FileCount} files");
                    sb.AppendLine();
                }
                if (node.Children.Count > top.Count) sb.AppendLine($"  …and {node.Children.Count - top.Count} more entries");
            }
            if (node.SmallFilesCount > 0)
                sb.AppendLine($"Small files directly inside: {node.SmallFilesCount} ({ByteSize.Format(node.SmallFilesSize)})");

            var byExt = LargeFilesByExtension(node);
            if (byExt.Count > 0)
                sb.AppendLine("Large files by extension: " + string.Join(", ", byExt.Select(e => $"{e.Ext} {ByteSize.Format(e.Size)} ({e.Count})")));
        }
        else if (node.Parent is { } parent)
        {
            var siblings = parent.Children.Where(c => !ReferenceEquals(c, node)).OrderByDescending(c => c.Size).Take(8).ToList();
            if (siblings.Count > 0)
                sb.AppendLine("Other entries next to it: " + string.Join(", ", siblings.Select(s => $"{s.Name}{(s.IsDir ? "\\" : "")} ({ByteSize.Format(s.Size)})")));
        }

        return sb.ToString();
    }

    private static List<(string Ext, long Size, int Count)> LargeFilesByExtension(FsNode root)
    {
        var acc = new Dictionary<string, (long Size, int Count)>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<FsNode>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 50_000)
        {
            var n = stack.Pop();
            foreach (var c in n.Children)
            {
                if (c.IsDir) { stack.Push(c); continue; }
                var ext = Path.GetExtension(c.Name);
                if (ext.Length == 0) ext = "(none)";
                var cur = acc.GetValueOrDefault(ext);
                acc[ext] = (cur.Size + c.Size, cur.Count + 1);
            }
        }
        return acc.OrderByDescending(kv => kv.Value.Size).Take(10).Select(kv => (kv.Key, kv.Value.Size, kv.Value.Count)).ToList();
    }
}
