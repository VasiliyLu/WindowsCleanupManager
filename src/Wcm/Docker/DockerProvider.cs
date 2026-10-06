using System.Diagnostics;
using System.Text.Json;
using Wcm.Model;

namespace Wcm.Docker;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Docker Desktop via docker.exe: images without containers, unused volumes, build cache.</summary>
public sealed class DockerProvider
{
    public static async Task<ProcessResult> RunAsync(string args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception) { return new ProcessResult(-1, "", "docker.exe not found"); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new ProcessResult(p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new ProcessResult(-1, "", ct.IsCancellationRequested ? "cancelled" : "docker timed out");
        }
    }

    public async Task<(List<CleanupItem> Items, string? Error)> ListAsync(CancellationToken ct = default)
    {
        var info = await RunAsync("info --format \"{{.ServerVersion}}\"", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (info.ExitCode != 0) return ([], "Docker unavailable: " + FirstLine(info.StdErr));

        var df = await RunAsync("system df -v --format \"{{json .}}\"", TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (df.ExitCode != 0) return ([], "docker system df: " + FirstLine(df.StdErr));

        try { return (Parse(df.StdOut), null); }
        catch (JsonException e) { return ([], "Failed to parse docker output: " + e.Message); }
    }

    internal static List<CleanupItem> Parse(string json)
    {
        var items = new List<CleanupItem>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var img in Array(root, "Images"))
        {
            if (Str(img, "Containers") != "0") continue;
            var repo = Str(img, "Repository");
            var tag = Str(img, "Tag");
            var id = Str(img, "ID");
            var dangling = repo == "<none>";
            // UniqueSize is what actually goes away; shared layers stay while other images use them
            var size = ByteSize.Parse(Str(img, "UniqueSize") is { Length: > 0 } u ? u : Str(img, "Size"), decimalUnits: true);
            items.Add(new CleanupItem
            {
                Kind = ItemKind.DockerImage,
                Target = id,
                Display = dangling ? $"<none> {ShortId(id)}" : $"{repo}:{tag}",
                Size = size,
                Category = "docker",
                Reason = (dangling ? "dangling image" : "image not used by any container") + $", created {Str(img, "CreatedSince")}",
                Source = ItemSource.Docker,
                Safety = dangling ? Safety.Safe : Safety.Review,
                Checked = dangling
            });
        }

        foreach (var vol in Array(root, "Volumes"))
        {
            if (Str(vol, "Links") != "0") continue;
            var name = Str(vol, "Name");
            items.Add(new CleanupItem
            {
                Kind = ItemKind.DockerVolume,
                Target = name,
                Display = "volume " + name,
                Size = ByteSize.TryParse(Str(vol, "Size"), out var s, decimalUnits: true) ? s : 0,
                Category = "docker",
                Reason = "volume not attached to any container — it may contain data!",
                Source = ItemSource.Docker,
                Safety = Safety.Review,
                Checked = false
            });
        }

        long cacheSize = 0;
        var cacheCount = 0;
        foreach (var bc in Array(root, "BuildCache"))
        {
            if (Str(bc, "InUse") == "true") continue;
            if (ByteSize.TryParse(Str(bc, "Size"), out var s, decimalUnits: true)) cacheSize += s;
            cacheCount++;
        }
        if (cacheSize > 0)
        {
            items.Add(new CleanupItem
            {
                Kind = ItemKind.DockerBuildCache,
                Target = "builder-cache",
                Display = $"docker build cache ({cacheCount} entries)",
                Size = cacheSize,
                Category = "docker",
                Reason = "unused BuildKit build cache",
                Source = ItemSource.Docker,
                Safety = Safety.Safe,
                Checked = true
            });
        }

        return items;
    }

    public static Task<ProcessResult> DeleteAsync(CleanupItem item, CancellationToken ct) => item.Kind switch
    {
        ItemKind.DockerImage => RunAsync($"rmi {item.Target}", TimeSpan.FromMinutes(5), ct),
        ItemKind.DockerVolume => RunAsync($"volume rm {item.Target}", TimeSpan.FromMinutes(5), ct),
        ItemKind.DockerBuildCache => RunAsync("builder prune -a -f", TimeSpan.FromMinutes(15), ct),
        _ => throw new ArgumentException("not a docker item")
    };

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static string ShortId(string id) => id.StartsWith("sha256:") ? id[7..19] : id.Length > 12 ? id[..12] : id;

    private static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
}
