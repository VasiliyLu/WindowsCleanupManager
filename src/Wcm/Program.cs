using System.CommandLine;
using System.Text;
using System.Text.Json;
using Wcm;
using Wcm.Classification;
using Wcm.Model;
using Wcm.Storage;
using Wcm.Ui;

Console.OutputEncoding = Encoding.UTF8;

var root = new RootCommand("Windows Cleanup Manager — ncdu-like disk cleanup with rules and Jev. Starts the TUI when run without arguments.");
root.SetAction(_ => new App(AppConfig.LoadOrCreate()).Run());

// Headless scan: prints suggestions, never deletes
var pathArg = new Argument<string>("path") { Description = "Drive or folder, e.g. C:\\" };
var noAi = new Option<bool>("--no-ai") { Description = "Don't call Jev (cached decisions are still used)" };
var maxCalls = new Option<int?>("--max-jev-calls") { Description = "Override the Jev request limit" };
var yes = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation before calling Jev" };
var jsonOut = new Option<string?>("--json") { Description = "Save suggestions to a JSON file" };
var withDocker = new Option<bool>("--docker") { Description = "Include Docker images and volumes" };
var top = new Option<int>("--top") { Description = "How many suggestions to print", DefaultValueFactory = _ => 50 };

var scan = new Command("scan", "Scan without the TUI and print suggestions") { pathArg, noAi, maxCalls, yes, jsonOut, withDocker, top };
scan.SetAction(async (parse, ct) =>
{
    var config = AppConfig.LoadOrCreate();
    if (parse.GetValue(maxCalls) is { } mc) config.Jev.MaxCallsPerScan = mc;
    var pipeline = new Pipeline(config, RuleEngine.Load()) { AiEnabled = !parse.GetValue(noAi) };
    var path = parse.GetValue(pathArg)!;

    Console.Error.WriteLine($"Scanning {path}…");
    var session = await pipeline.ScanAsync(path, ct);
    var pr = pipeline.Scanner.Progress;
    Console.Error.WriteLine($"Done in {session.ScanTime:mm\\:ss\\.f}: {pr.DirCount:N0} folders, {pr.FileCount:N0} files, {ByteSize.Format(session.Root!.Size)}, {pr.ErrorCount:N0} errors");

    if (parse.GetValue(withDocker)) await pipeline.AddDockerAsync(session, ct);

    var plan = pipeline.PlanJev(session);
    if (plan is not null)
    {
        Console.Error.WriteLine($"Jev: {plan.ToAsk.Count} candidates (+{plan.SkippedByLimit} over the limit), {plan.CacheHits} from cache");
        foreach (var c in plan.ToAsk.Take(10)) Console.Error.WriteLine($"   {ByteSize.Format(c.EffectiveSize),9}  {c.Path}");

        if (plan.ToAsk.Count > 0 && pipeline.AiUnavailableReason is null)
        {
            var go = parse.GetValue(yes) || !config.Jev.ConfirmBeforeCalls;
            if (!go)
            {
                Console.Error.Write($"Send {plan.ToAsk.Count} requests (≈${plan.EstimatedCost():0.0000})? [y/N] ");
                go = Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
            }
            if (go)
            {
                var progress = new JevRunProgress { Total = plan.ToAsk.Count };
                await pipeline.RunJevAsync(session, plan, progress, ct);
                Console.Error.WriteLine($"Jev: {progress.Done} done, {progress.Failed} failed, {progress.SkippedByBudget} skipped by budget, cost ${progress.Cost:0.00000}");
                if (progress.LastError is { } e) Console.Error.WriteLine("Last error: " + e);
            }
        }
        else if (pipeline.AiUnavailableReason is { } why && plan.ToAsk.Count > 0)
        {
            Console.Error.WriteLine($"Jev not used: {why}");
        }
    }

    var items = session.Items.OrderByDescending(i => i.Size).ToList();
    var limit = parse.GetValue(top);
    Console.WriteLine();
    Console.WriteLine($"{"",3} {"Size",9}  {"Type",-8} {"Source",-9} {"Category",-14} {"Conf.",5}  Path");
    foreach (var i in items.Take(limit))
    {
        var box = i.Checked ? "[x]" : "[ ]";
        var conf = i.Confidence is { } c ? c.ToString("P0") : "";
        Console.WriteLine($"{box} {ByteSize.Format(i.Size),9}  {i.KindLabel,-8} {i.Source.ToString().ToLowerInvariant(),-9} {i.Category,-14} {conf,5}  {i.Display}");
    }
    if (items.Count > limit) Console.WriteLine($"…and {items.Count - limit} more");
    Console.WriteLine();
    Console.WriteLine($"Total suggestions: {items.Count}, {ByteSize.Format(items.Where(i => !session.IsCovered(i)).Sum(i => i.Size))}; checked by default: {ByteSize.Format(session.SelectedSize)}");
    if (session.DockerError is { } de) Console.Error.WriteLine(de);

    if (parse.GetValue(jsonOut) is { } file)
    {
        var dto = items.Select(i => new { kind = i.Kind.ToString(), i.Target, i.Size, i.Category, i.Reason, source = i.Source.ToString(), i.Confidence, safety = i.Safety.ToString(), i.Checked, i.RuleId });
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(dto, Json.Options), ct);
        Console.Error.WriteLine($"Saved: {file}");
    }
    return 0;
});
root.Subcommands.Add(scan);

var rulesCmd = new Command("rules", "Show active rules");
rulesCmd.SetAction(_ =>
{
    var engine = RuleEngine.Load();
    foreach (var r in engine.AllRules)
        Console.WriteLine($"{(r.IsUser ? "user" : "builtin"),-8} {r.Action.ToString().ToLowerInvariant(),-9} {r.Id,-24} {string.Join("; ", r.Match)}");
    Console.WriteLine($"\nUser rules: {AppPaths.UserRules}");
    return 0;
});
root.Subcommands.Add(rulesCmd);

return await root.Parse(args).InvokeAsync();
