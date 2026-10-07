using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using Wcm.Model;

namespace Wcm.Docker;

public sealed record DiskResult(string Path, long Before, long After)
{
    public long Freed => Math.Max(0, Before - After);
}

public sealed class CompactResult
{
    public List<DiskResult> Disks { get; } = [];
    public List<string> Errors { get; } = [];
    public bool Cancelled { get; set; }
    public long Freed => Disks.Sum(d => d.Freed);
}

/// <summary>Shrinks Docker Desktop's WSL disk: stop Docker → wsl --shutdown → diskpart compact → start Docker again.</summary>
public sealed class VhdxCompactor(string? logPath = null)
{
    // Current Docker Desktop layout first, then the legacy docker-desktop-data distro
    private static readonly string[] KnownDisks = [@"Docker\wsl\disk\docker_data.vhdx", @"Docker\wsl\data\ext4.vhdx"];

    public static List<string> FindDisks() =>
        FindDisks(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static List<string> FindDisks(string localAppData) =>
        KnownDisks.Select(d => Path.Combine(localAppData, d)).Where(File.Exists).ToList();

    internal static string BuildCompactScript(IEnumerable<string> disks)
    {
        var sb = new StringBuilder();
        foreach (var d in disks)
        {
            sb.AppendLine($"select vdisk file=\"{d}\"");
            sb.AppendLine("attach vdisk readonly");
            sb.AppendLine("compact vdisk");
            sb.AppendLine("detach vdisk");
        }
        return sb.ToString();
    }

    // Runs after the compact script no matter what, so a failed compact never leaves a disk attached
    internal static string BuildDetachScript(IEnumerable<string> disks)
    {
        var sb = new StringBuilder();
        foreach (var d in disks)
        {
            sb.AppendLine($"select vdisk file=\"{d}\"");
            sb.AppendLine("detach vdisk noerr");
        }
        return sb.ToString();
    }

    internal static string BuildBatch(string compactScript, string detachScript, string log) =>
        $"""
        @echo off
        diskpart /s "{compactScript}" > "{log}" 2>&1
        set rc=%errorlevel%
        diskpart /s "{detachScript}" >> "{log}" 2>&1
        exit /b %rc%
        """.ReplaceLineEndings("\r\n");

    public async Task<CompactResult> CompactAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var result = new CompactResult();
        var disks = FindDisks();
        if (disks.Count == 0)
        {
            result.Errors.Add("Docker disk (vhdx) not found");
            return result;
        }

        var before = disks.ToDictionary(d => d, d => new FileInfo(d).Length, StringComparer.OrdinalIgnoreCase);
        var wasRunning = IsDockerRunning();
        try
        {
            if (wasRunning)
            {
                progress?.Report("Stopping Docker Desktop…");
                await StopDockerAsync(ct).ConfigureAwait(false);
            }

            progress?.Report("Shutting down WSL…");
            var wsl = await DockerProvider.RunAsync("wsl", "--shutdown", TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            if (wsl.ExitCode != 0)
            {
                result.Errors.Add("wsl --shutdown: " + DockerProvider.FirstLine(wsl.StdErr + wsl.StdOut));
                return result;
            }

            progress?.Report("Waiting for the disk to be released…");
            foreach (var d in disks)
            {
                if (!await WaitUnlockedAsync(d, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
                {
                    result.Errors.Add($"{d} is still in use");
                    return result;
                }
            }

            progress?.Report("Compacting (diskpart, needs admin rights)…");
            await RunDiskpartAsync(disks, result).ConfigureAwait(false);

            foreach (var d in disks)
            {
                var r = new DiskResult(d, before[d], new FileInfo(d).Length);
                result.Disks.Add(r);
                Log(r, result.Errors.Count == 0 && !result.Cancelled);
            }
        }
        finally
        {
            if (wasRunning)
            {
                progress?.Report("Starting Docker Desktop…");
                // Docker must come back even if the user pressed Esc
                if (await StartDockerAsync().ConfigureAwait(false) is { } err) result.Errors.Add(err);
            }
        }
        return result;
    }

    private static async Task RunDiskpartAsync(List<string> disks, CompactResult result)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wcm-compact-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var compact = Path.Combine(dir, "compact.txt");
            var detach = Path.Combine(dir, "detach.txt");
            var log = Path.Combine(dir, "diskpart.log");
            var bat = Path.Combine(dir, "compact.cmd");
            await File.WriteAllTextAsync(compact, BuildCompactScript(disks)).ConfigureAwait(false);
            await File.WriteAllTextAsync(detach, BuildDetachScript(disks)).ConfigureAwait(false);
            await File.WriteAllTextAsync(bat, BuildBatch(compact, detach, log)).ConfigureAwait(false);

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{bat}\"\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = IsElevated() ? "" : "runas"
            };

            Process? p;
            try { p = Process.Start(psi); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                result.Cancelled = true;
                result.Errors.Add("administrator rights were declined");
                return;
            }
            if (p is null) { result.Errors.Add("failed to start diskpart"); return; }

            using (p)
            {
                // Never interrupt diskpart halfway through
                await p.WaitForExitAsync().ConfigureAwait(false);
                if (p.ExitCode != 0)
                    result.Errors.Add($"diskpart failed (code {p.ExitCode}): " + LastMeaningfulLine(ReadOem(log)));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string ReadOem(string path)
    {
        if (!File.Exists(path)) return "";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return File.ReadAllText(path, Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage));
    }

    private static string LastMeaningfulLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsDockerRunning() => ProcessesExist("Docker Desktop");

    private static bool ProcessesExist(params string[] names) =>
        names.Any(n => { var ps = Process.GetProcessesByName(n); foreach (var p in ps) p.Dispose(); return ps.Length > 0; });

    private static async Task StopDockerAsync(CancellationToken ct)
    {
        await DockerProvider.RunAsync("desktop stop", TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (await WaitGoneAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)) return;

        // Older Docker Desktop has no `docker desktop stop`
        foreach (var name in new[] { "Docker Desktop", "com.docker.backend" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
                p.Dispose();
            }
        await WaitGoneAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    private static async Task<bool> WaitGoneAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (ProcessesExist("Docker Desktop", "com.docker.backend"))
        {
            if (sw.Elapsed > timeout) return false;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task<bool> WaitUnlockedAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException) when (sw.Elapsed < timeout)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (IOException) { return false; }
        }
    }

    private static async Task<string?> StartDockerAsync()
    {
        var r = await DockerProvider.RunAsync("desktop start --detach", TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        if (r.ExitCode == 0) return null;

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Docker\Docker\Docker Desktop.exe");
        if (!File.Exists(exe)) return "failed to start Docker Desktop, start it manually";
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Win32Exception e) { return "failed to start Docker Desktop: " + e.Message; }
    }

    private void Log(DiskResult r, bool ok)
    {
        if (logPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{(ok ? "OK" : "FAIL")}\tcompact\tVhdx\t{ByteSize.Format(r.Freed)}\t{r.Path}";
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (IOException) { }
    }
}
