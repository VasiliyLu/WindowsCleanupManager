using Wcm.Classification;
using Wcm.Docker;
using Wcm.Model;
using Wcm.Scanning;
using Wcm.Storage;

namespace Wcm;

/// <summary>scan → rules → (cache + Jev) → docker. Shared by the TUI and headless mode.</summary>
public sealed class Pipeline
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public Pipeline(AppConfig config, RuleEngine rules)
    {
        Config = config;
        Rules = rules;
        Scanner = new ParallelScanner(config.Scan.Threads, ByteSize.Parse(config.Scan.MinFileNodeSize));
    }

    public AppConfig Config { get; }
    public RuleEngine Rules { get; }
    public ParallelScanner Scanner { get; }
    public bool AiEnabled { get; set; } = true;

    public string? AiUnavailableReason =>
        !AiEnabled || !Config.Jev.Enabled ? "Jev is off"
        : string.IsNullOrWhiteSpace(Config.ApiKey) ? "no API key (OPENROUTER_API_KEY)"
        : null;

    public async Task<ScanSession> ScanAsync(string path, CancellationToken ct)
    {
        var root = await Scanner.ScanAsync(path, ct).ConfigureAwait(false);
        var session = new ScanSession { Label = path, Root = root };
        session.ScanTime = Scanner.Progress.Elapsed.Elapsed;
        session.ScanErrors = Scanner.Progress.ErrorCount;
        session.Add(new Classifier(Rules, ByteSize.Parse(Config.Scan.MinSuggestionSize)).ApplyRules(root));
        return session;
    }

    public JevPlan? PlanJev(ScanSession session)
    {
        if (session.Root is null) return null;
        var cache = new DecisionCache(AppPaths.Cache, Config.Jev.CacheTtlDays);
        var plan = new JevPlanner(Config.Jev).Plan(session.Root, cache);
        session.JevPlan = plan;
        // Cached answers are free, use them even with AI off
        session.Add(plan.FromCache);
        foreach (var i in plan.FromCache) i.Node!.Mark = NodeMark.Suggested;
        return plan;
    }

    public async Task RunJevAsync(ScanSession session, JevPlan plan, JevRunProgress progress, CancellationToken ct)
    {
        if (AiUnavailableReason is { } reason) { session.JevNote = reason; return; }
        session.Jev = progress;
        var client = new JevClient(Http, Config.ApiKey!, Config.Jev.Endpoint, Config.Jev.Model);
        var cache = new DecisionCache(AppPaths.Cache, Config.Jev.CacheTtlDays);
        var items = await new JevPlanner(Config.Jev).RunAsync(plan, client, cache, progress, ct).ConfigureAwait(false);
        session.Add(items);
    }

    public async Task AddDockerAsync(ScanSession session, CancellationToken ct)
    {
        if (!Config.Docker.Enabled) return;
        var (items, error) = await new DockerProvider().ListAsync(ct).ConfigureAwait(false);
        session.DockerError = error;
        session.Add(items);
    }
}
