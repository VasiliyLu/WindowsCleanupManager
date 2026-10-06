using Wcm.Classification;
using Wcm.Docker;
using Wcm.Model;
using Wcm.Storage;

namespace Wcm.Deletion;

public sealed record DeleteOutcome(CleanupItem Item, bool Success, long RemainingBytes, List<string> Errors);

public sealed class DeleteReport
{
    public List<DeleteOutcome> Outcomes { get; } = [];
    public long FreedByDrives { get; set; }
    public bool DockerTouched { get; set; }
    public int Succeeded => Outcomes.Count(o => o.Success);
    public int Failed => Outcomes.Count(o => !o.Success);
}

public sealed class Deleter(RuleEngine rules, string? logPath = null)
{
    private const int MaxErrorsPerItem = 20;

    public async Task<DeleteReport> DeleteAsync(IReadOnlyList<CleanupItem> items, bool permanent, IProgress<string>? progress, CancellationToken ct)
    {
        var report = new DeleteReport();
        var drives = items.Where(i => !i.IsDocker).Select(i => Path.GetPathRoot(i.Target)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var freeBefore = drives.ToDictionary(d => d, FreeSpace, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(item.Display);

            DeleteOutcome outcome;
            if (item.IsDocker)
            {
                report.DockerTouched = true;
                var r = await DockerProvider.DeleteAsync(item, ct).ConfigureAwait(false);
                outcome = new DeleteOutcome(item, r.ExitCode == 0, 0, r.ExitCode == 0 ? [] : [r.StdErr.Trim()]);
            }
            else
            {
                outcome = await Task.Run(() => DeleteFs(item, permanent), ct).ConfigureAwait(false);
            }

            report.Outcomes.Add(outcome);
            Log(outcome, permanent);
        }

        report.FreedByDrives = drives.Sum(d => FreeSpace(d) - freeBefore[d]);
        return report;
    }

    /// <summary>Last line of defence: re-checked right before touching the disk.</summary>
    public string? Validate(CleanupItem item)
    {
        if (item.IsDocker) return null;
        if (item.Node is { ContainsProtected: true }) return "contains protected paths";
        var block = rules.BlockReason(item.Target);
        if (block is not null) return block;

        var exists = item.Kind == ItemKind.File ? File.Exists(item.Target) : Directory.Exists(item.Target);
        if (!exists) return "no longer exists";
        if ((File.GetAttributes(item.Target) & FileAttributes.ReparsePoint) != 0 && item.Kind == ItemKind.FolderContents)
            return "it is a link (junction/symlink)";
        return null;
    }

    private DeleteOutcome DeleteFs(CleanupItem item, bool permanent)
    {
        var errors = new List<string>();
        var invalid = Validate(item);
        if (invalid is not null) return new DeleteOutcome(item, false, item.Size, [invalid]);

        // For "contents" items delete every entry inside, keep the folder itself
        var targets = item.Kind == ItemKind.FolderContents
            ? SafeEnumerate(item.Target).ToList()
            : [item.Target];

        if (permanent)
        {
            foreach (var t in targets) DeleteTree(t, errors);
        }
        else
        {
            // Batches keep the pFrom buffer reasonable for huge Temp folders
            foreach (var chunk in targets.Chunk(500))
                if (RecycleBin.Send(chunk) is { } err) errors.Add(err);
        }

        var remaining = Measure(item.Target);
        var success = item.Kind == ItemKind.FolderContents ? errors.Count == 0 || remaining < item.Size / 20 : remaining == 0 && !PathExists(item.Target);
        if (errors.Count > MaxErrorsPerItem)
        {
            var extra = errors.Count - MaxErrorsPerItem;
            errors.RemoveRange(MaxErrorsPerItem, extra);
            errors.Add($"…and {extra} more errors");
        }
        return new DeleteOutcome(item, success, remaining, errors);
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }); }
        catch (Exception) { return []; }
    }

    /// <summary>Recursive delete that never follows junctions/symlinks and clears read-only flags.</summary>
    internal static void DeleteTree(string path, List<string> errors)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }
        catch (Exception e) { errors.Add($"{path}: {e.Message}"); return; }

        try
        {
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

            if ((attrs & FileAttributes.Directory) != 0)
            {
                // A link is removed as a link, its target stays untouched
                if ((attrs & FileAttributes.ReparsePoint) == 0)
                    foreach (var child in SafeEnumerate(path).ToList())
                        DeleteTree(child, errors);
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{path}: {e.Message}");
        }
    }

    private static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);

    internal static long Measure(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts)) total += f.Length;
        }
        catch (Exception) { }
        return total;
    }

    private static long FreeSpace(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace; } catch { return 0; }
    }

    private void Log(DeleteOutcome o, bool permanent)
    {
        if (logPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var mode = o.Item.IsDocker ? "docker" : permanent ? "permanent" : "recycle";
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{(o.Success ? "OK" : "FAIL")}\t{mode}\t{o.Item.Kind}\t{ByteSize.Format(o.Item.Size)}\t{o.Item.Target}"
                       + (o.Errors.Count > 0 ? "\t" + o.Errors[0] : "");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (IOException) { }
    }
}
