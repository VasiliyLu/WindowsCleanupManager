using System.CommandLine;
using System.Text;
using System.Text.Json;
using Wcm;
using Wcm.Classification;
using Wcm.Model;
using Wcm.Storage;
using Wcm.Ui;

Console.OutputEncoding = Encoding.UTF8;

var root = new RootCommand("Windows Cleanup Manager — ncdu-подобная очистка диска с правилами и Jev. Без аргументов запускает TUI.");
root.SetAction(_ => new App(AppConfig.LoadOrCreate()).Run());

// Headless scan: prints suggestions, never deletes
var pathArg = new Argument<string>("path") { Description = "Диск или папка, например C:\\" };
var noAi = new Option<bool>("--no-ai") { Description = "Не обращаться к Jev (кэш решений используется)" };
var maxCalls = new Option<int?>("--max-jev-calls") { Description = "Переопределить лимит запросов к Jev" };
var yes = new Option<bool>("--yes", "-y") { Description = "Не спрашивать подтверждение перед запросами к Jev" };
var jsonOut = new Option<string?>("--json") { Description = "Сохранить предложения в JSON-файл" };
var withDocker = new Option<bool>("--docker") { Description = "Добавить Docker-образы и тома" };
var top = new Option<int>("--top") { Description = "Сколько предложений печатать", DefaultValueFactory = _ => 50 };

var scan = new Command("scan", "Сканировать без TUI и вывести предложения") { pathArg, noAi, maxCalls, yes, jsonOut, withDocker, top };
scan.SetAction(async (parse, ct) =>
{
    var config = AppConfig.LoadOrCreate();
    if (parse.GetValue(maxCalls) is { } mc) config.Jev.MaxCallsPerScan = mc;
    var pipeline = new Pipeline(config, RuleEngine.Load()) { AiEnabled = !parse.GetValue(noAi) };
    var path = parse.GetValue(pathArg)!;

    Console.Error.WriteLine($"Сканирование {path}…");
    var session = await pipeline.ScanAsync(path, ct);
    var pr = pipeline.Scanner.Progress;
    Console.Error.WriteLine($"Готово за {session.ScanTime:mm\\:ss\\.f}: {pr.DirCount:N0} папок, {pr.FileCount:N0} файлов, {ByteSize.Format(session.Root!.Size)}, ошибок {pr.ErrorCount:N0}");

    if (parse.GetValue(withDocker)) await pipeline.AddDockerAsync(session, ct);

    var plan = pipeline.PlanJev(session);
    if (plan is not null)
    {
        Console.Error.WriteLine($"Jev: кандидатов {plan.ToAsk.Count} (+{plan.SkippedByLimit} сверх лимита), из кэша {plan.CacheHits}");
        foreach (var c in plan.ToAsk.Take(10)) Console.Error.WriteLine($"   {ByteSize.Format(c.EffectiveSize),9}  {c.Path}");

        if (plan.ToAsk.Count > 0 && pipeline.AiUnavailableReason is null)
        {
            var go = parse.GetValue(yes) || !config.Jev.ConfirmBeforeCalls;
            if (!go)
            {
                Console.Error.Write($"Отправить {plan.ToAsk.Count} запросов (≈${plan.EstimatedCost():0.0000})? [y/N] ");
                go = Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
            }
            if (go)
            {
                var progress = new JevRunProgress { Total = plan.ToAsk.Count };
                await pipeline.RunJevAsync(session, plan, progress, ct);
                Console.Error.WriteLine($"Jev: выполнено {progress.Done}, ошибок {progress.Failed}, по бюджету пропущено {progress.SkippedByBudget}, стоимость ${progress.Cost:0.00000}");
                if (progress.LastError is { } e) Console.Error.WriteLine("Последняя ошибка: " + e);
            }
        }
        else if (pipeline.AiUnavailableReason is { } why && plan.ToAsk.Count > 0)
        {
            Console.Error.WriteLine($"Jev не используется: {why}");
        }
    }

    var items = session.Items.OrderByDescending(i => i.Size).ToList();
    var limit = parse.GetValue(top);
    Console.WriteLine();
    Console.WriteLine($"{"",3} {"Размер",9}  {"Тип",-8} {"Источник",-8} {"Категория",-14} {"Увер.",5}  Путь");
    foreach (var i in items.Take(limit))
    {
        var box = i.Checked ? "[x]" : "[ ]";
        var conf = i.Confidence is { } c ? c.ToString("P0") : "";
        Console.WriteLine($"{box} {ByteSize.Format(i.Size),9}  {i.KindLabel,-8} {i.Source.ToString().ToLowerInvariant(),-8} {i.Category,-14} {conf,5}  {i.Display}");
    }
    if (items.Count > limit) Console.WriteLine($"…и ещё {items.Count - limit}");
    Console.WriteLine();
    Console.WriteLine($"Всего предложений: {items.Count}, {ByteSize.Format(items.Where(i => !session.IsCovered(i)).Sum(i => i.Size))}; отмечено по умолчанию: {ByteSize.Format(session.SelectedSize)}");
    if (session.DockerError is { } de) Console.Error.WriteLine(de);

    if (parse.GetValue(jsonOut) is { } file)
    {
        var dto = items.Select(i => new { kind = i.Kind.ToString(), i.Target, i.Size, i.Category, i.Reason, source = i.Source.ToString(), i.Confidence, safety = i.Safety.ToString(), i.Checked, i.RuleId });
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(dto, Json.Options), ct);
        Console.Error.WriteLine($"Сохранено: {file}");
    }
    return 0;
});
root.Subcommands.Add(scan);

var rulesCmd = new Command("rules", "Показать активные правила");
rulesCmd.SetAction(_ =>
{
    var engine = RuleEngine.Load();
    foreach (var r in engine.AllRules)
        Console.WriteLine($"{(r.IsUser ? "user" : "builtin"),-8} {r.Action.ToString().ToLowerInvariant(),-9} {r.Id,-24} {string.Join("; ", r.Match)}");
    Console.WriteLine($"\nПользовательские правила: {AppPaths.UserRules}");
    return 0;
});
root.Subcommands.Add(rulesCmd);

return await root.Parse(args).InvokeAsync();
