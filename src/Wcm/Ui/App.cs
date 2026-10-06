using System.Diagnostics;
using Wcm.Classification;
using Wcm.Model;
using Wcm.Storage;

namespace Wcm.Ui;

internal sealed class App
{
    private AppConfig _config;
    private Pipeline _pipeline;
    private bool _aiEnabled = true;

    public App(AppConfig config)
    {
        _config = config;
        _pipeline = new Pipeline(config, RuleEngine.Load());
    }

    public int Run()
    {
        Term.Enter();
        try
        {
            MainMenu();
            return 0;
        }
        finally
        {
            Term.Leave();
        }
    }

    private void Reload()
    {
        _config = AppConfig.LoadOrCreate();
        _pipeline = new Pipeline(_config, RuleEngine.Load()) { AiEnabled = _aiEnabled };
    }

    private sealed record MenuEntry(string Title, string Note, Action Run, DriveInfo? Drive = null);

    private void MainMenu()
    {
        var selected = 0;
        string? status = null;
        while (true)
        {
            var entries = BuildMenu();
            selected = Math.Clamp(selected, 0, entries.Count - 1);

            Frame Render()
            {
                var f = new Frame();
                f.Add(new Line(f.Width, Style.Header).Text(" Windows Cleanup Manager").Right(AiStatus() + " ").Fill());
                f.Blank();
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    var sel = i == selected;
                    var line = new Line(f.Width, sel ? Style.Reverse : "").Text(sel ? " ▶ " : "   ");
                    if (e.Drive is { } d)
                    {
                        var used = d.TotalSize - d.TotalFreeSpace;
                        var frac = d.TotalSize > 0 ? (double)used / d.TotalSize : 0;
                        line.Text(e.Title.PadRight(26), Style.Bold)
                            .Text(Fmt.Bar(frac, 20) + " ", frac > 0.9 ? Style.Red : frac > 0.75 ? Style.Yellow : Style.Green)
                            .Text($"{ByteSize.Format(used),9} / {ByteSize.Format(d.TotalSize),-9}  free {ByteSize.Format(d.AvailableFreeSpace)}");
                    }
                    else
                    {
                        if (i > 0 && entries[i - 1].Drive is not null) { f.Blank(); }
                        line.Text(e.Title.PadRight(26), Style.Bold).Text(e.Note, Style.Gray);
                    }
                    f.Add(line.Fill());
                }

                f.FillTo(2);
                f.Add(status ?? "", Style.Yellow);
                f.Add(new Line(f.Width, Style.Footer).Text(" ↑↓ select   Enter open   I Jev on/off   R reload config   Q quit").Fill());
                return f;
            }

            Term.Draw(Render());
            var k = Term.ReadKey(Render);
            status = null;
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: selected = (selected - 1 + entries.Count) % entries.Count; break;
                case ConsoleKey.DownArrow: selected = (selected + 1) % entries.Count; break;
                case ConsoleKey.Home: selected = 0; break;
                case ConsoleKey.End: selected = entries.Count - 1; break;
                case ConsoleKey.Enter:
                case ConsoleKey.S:
                    try { entries[selected].Run(); }
                    catch (Exception e) { Dialogs.Message(Render, "Error", e.Message); }
                    break;
                case ConsoleKey.I:
                    _aiEnabled = !_aiEnabled;
                    _pipeline.AiEnabled = _aiEnabled;
                    break;
                case ConsoleKey.R:
                    try { Reload(); status = "Config and rules reloaded."; }
                    catch (Exception e) { status = "Error in config/rules: " + e.Message; }
                    break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return;
                case ConsoleKey.C when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                    return;
            }
        }
    }

    private string AiStatus() =>
        _pipeline.AiUnavailableReason is { } r ? $"Jev: {r}" : $"Jev: {_config.Jev.Model}, limit {_config.Jev.MaxCallsPerScan} req./${_config.Jev.MaxCostPerScan}";

    private List<MenuEntry> BuildMenu()
    {
        var list = new List<MenuEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
            var label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : " " + d.VolumeLabel;
            list.Add(new MenuEntry(d.Name + label, "", () => ScanFlow(d.RootDirectory.FullName, isDrive: true), d));
        }

        list.Add(new MenuEntry("Folder…", "scan any folder", () =>
        {
            var path = Dialogs.Prompt(() => new Frame(), "Folder to scan:", Environment.CurrentDirectory);
            if (string.IsNullOrWhiteSpace(path)) return;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Directory.Exists(path)) { Dialogs.Message(() => new Frame(), "Error", $"Folder not found: {path}"); return; }
            ScanFlow(path, isDrive: false);
        }));
        if (_config.Docker.Enabled)
            list.Add(new MenuEntry("Docker", "images, volumes and build cache only", DockerFlow));
        list.Add(new MenuEntry("Rules", AppPaths.UserRules, () => OpenInEditor(AppPaths.UserRules, "[]")));
        list.Add(new MenuEntry("Settings", AppPaths.Config, () => OpenInEditor(AppPaths.Config, null)));
        list.Add(new MenuEntry("Deletion log", AppPaths.DeletionLog, () => OpenInEditor(AppPaths.DeletionLog, "")));
        return list;
    }

    private void OpenInEditor(string path, string? emptyContent)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (emptyContent is null) Json.Save(path, new AppConfig());
            else File.WriteAllText(path, emptyContent);
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.WaitForExit();
        try { Reload(); }
        catch (Exception e) { Dialogs.Message(() => new Frame(), "Error in file", e.Message); }
    }

    private void ScanFlow(string path, bool isDrive)
    {
        using var cts = new CancellationTokenSource();
        var task = _pipeline.ScanAsync(path, cts.Token);
        var p = _pipeline.Scanner;

        Frame Render()
        {
            var pr = p.Progress;
            var secs = Math.Max(0.1, pr.Elapsed.Elapsed.TotalSeconds);
            var f = new Frame();
            f.Add(new Line(f.Width, Style.Header).Text($" Scanning {path}").Fill());
            f.Blank();
            f.Add($"  Folders:   {pr.DirCount:N0}");
            f.Add($"  Files:     {pr.FileCount:N0}   ({pr.FileCount / secs:N0}/s)");
            f.Add($"  Size:      {ByteSize.Format(pr.ByteCount)}");
            f.Add($"  Errors:    {pr.ErrorCount:N0}", pr.ErrorCount > 0 ? Style.Yellow : "");
            f.Add($"  Time:      {pr.Elapsed.Elapsed:mm\\:ss}");
            f.Blank();
            f.Add("  " + Fmt.MiddleTrim(pr.CurrentPath, f.Width - 4), Style.Gray);
            f.FillTo(1);
            f.Add(new Line(f.Width, Style.Footer).Text(" Esc — cancel").Fill());
            return f;
        }

        while (!task.IsCompleted)
        {
            Term.Draw(Render());
            if (Term.TryReadKey() is { Key: ConsoleKey.Escape }) cts.Cancel();
            Thread.Sleep(150);
        }

        if (task.IsCanceled || cts.IsCancellationRequested) return;
        if (task.IsFaulted)
        {
            Dialogs.Message(Render, "Scan error", task.Exception!.GetBaseException().Message);
            return;
        }

        var session = task.Result;

        if (isDrive && _config.Docker.IncludeInDriveScan && _config.Docker.Enabled)
            RunWithSpinner("Querying Docker…", ct => _pipeline.AddDockerAsync(session, ct));

        RunJev(session);
        new ReviewScreen(session, _pipeline).Run();
    }

    private void DockerFlow()
    {
        var session = new ScanSession { Label = "Docker" };
        RunWithSpinner("Querying Docker…", ct => _pipeline.AddDockerAsync(session, ct));
        if (session.DockerError is not null && session.Items.Count == 0)
        {
            Dialogs.Message(() => new Frame(), "Docker", session.DockerError);
            return;
        }
        new ReviewScreen(session, _pipeline).Run();
    }

    private void RunJev(ScanSession session)
    {
        var plan = _pipeline.PlanJev(session);
        if (plan is null || plan.ToAsk.Count == 0) return;
        if (_pipeline.AiUnavailableReason is { } why)
        {
            session.JevNote = $"{why}: {plan.ToAsk.Count} large folders without rules were not analyzed";
            return;
        }

        if (_config.Jev.ConfirmBeforeCalls)
        {
            Frame Bg()
            {
                var f = new Frame();
                f.Add(new Line(f.Width, Style.Header).Text(" Jev analysis").Fill());
                f.Blank();
                f.Add($"  Large folders/files without rules: {plan.ToAsk.Count}" + (plan.SkippedByLimit > 0 ? $" (+{plan.SkippedByLimit} over the limit)" : ""));
                f.Add($"  From decision cache: {plan.CacheHits}");
                f.Blank();
                foreach (var c in plan.ToAsk.Take(Math.Max(0, f.Remaining - 6)))
                    f.Add(new Line(f.Width).Text("  " + Fmt.Size(c.EffectiveSize) + "  ").Text(Fmt.MiddleTrim(c.Path, f.Width - 16), Style.Gray));
                return f;
            }

            var ok = Dialogs.Confirm(Bg, $"Send {plan.ToAsk.Count} requests to Jev? Estimate ≈ ${plan.EstimatedCost():0.0000}, limit ${_config.Jev.MaxCostPerScan}",
                "Paths (profile replaced with %USERPROFILE%), sizes and names of the largest nested entries will be sent.");
            if (!ok)
            {
                session.JevNote = "Jev analysis skipped";
                return;
            }
        }

        var progress = new JevRunProgress { Total = plan.ToAsk.Count };
        using var cts = new CancellationTokenSource();
        var task = _pipeline.RunJevAsync(session, plan, progress, cts.Token);
        while (!task.IsCompleted)
        {
            var f = new Frame();
            f.Add(new Line(f.Width, Style.Header).Text(" Jev analysis").Fill());
            f.Blank();
            var done = progress.Done + progress.Failed + progress.SkippedByBudget;
            f.Add("  " + Fmt.Bar(progress.Total == 0 ? 1 : (double)done / progress.Total, 40) + $"  {done}/{progress.Total}");
            f.Add($"  Cost: ${progress.Cost:0.00000}   errors: {progress.Failed}   skipped by budget: {progress.SkippedByBudget}");
            if (progress.LastError is { } err) f.Add("  " + err, Style.Red);
            f.Blank();
            f.Add("  " + Fmt.MiddleTrim(progress.Current, f.Width - 4), Style.Gray);
            f.FillTo(1);
            f.Add(new Line(f.Width, Style.Footer).Text(" Esc — stop requests").Fill());
            Term.Draw(f);
            if (Term.TryReadKey() is { Key: ConsoleKey.Escape }) cts.Cancel();
            Thread.Sleep(150);
        }
        if (task.IsFaulted) session.JevNote = "Jev: " + task.Exception!.GetBaseException().Message;
    }

    private static void RunWithSpinner(string title, Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        var task = work(cts.Token);
        var frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        var i = 0;
        while (!task.IsCompleted)
        {
            var f = new Frame();
            f.Add(new Line(f.Width, Style.Header).Text(" " + title).Fill());
            f.Blank();
            f.Add($"  {frames[i++ % frames.Length]} {title}");
            f.FillTo(1);
            f.Add(new Line(f.Width, Style.Footer).Text(" Esc — skip").Fill());
            Term.Draw(f);
            if (Term.TryReadKey() is { Key: ConsoleKey.Escape }) cts.Cancel();
            Thread.Sleep(100);
        }
    }
}
