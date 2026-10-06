using System.Net;
using System.Text;
using Wcm.Classification;
using Wcm.Model;
using Wcm.Scanning;
using Wcm.Storage;
using Wcm.Tests.Helpers;
using static Wcm.Tests.Helpers.TreeBuilder;

namespace Wcm.Tests;

public class JevTests
{
    private const string Sample = """
        {
          "id": "gen-dec-1",
          "model": "typesafe/jev-1.13-20260917",
          "provider": "TypeSafe",
          "answers": {
            "deletable": { "type": "noul", "noul": 0.93 },
            "category": { "type": "choice", "choice": "cache", "confidence": 0.8, "probabilities": { "cache": 0.8, "temp": 0.2 } }
          },
          "usage": { "input_tokens": 600, "output_tokens": 20, "cost": 0.00003 }
        }
        """;

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return respond(request, Interlocked.Increment(ref Calls));
        }
    }

    private static HttpResponseMessage Ok(string json = Sample) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void Parses_documented_response_shape()
    {
        var v = JevClient.Parse(Sample);
        Assert.Equal(0.93, v.Deletable);
        Assert.Equal("cache", v.Category);
        Assert.Equal(0.8, v.CategoryConfidence);
        Assert.Equal(0.00003, v.Cost);
    }

    [Fact]
    public async Task Sends_both_questions_in_one_request_with_auth()
    {
        string? auth = null;
        var handler = new FakeHandler((req, _) => { auth = req.Headers.Authorization?.ToString(); return Ok(); });
        var client = new JevClient(new HttpClient(handler), "sk-test", "https://example.test/api/v1/systemone", "jev-1.13");

        await client.AskAsync("Path: X");

        Assert.Equal(1, handler.Calls);
        Assert.Equal("Bearer sk-test", auth);
        var body = handler.Bodies[0];
        Assert.Contains("\"model\":\"jev-1.13\"", body);
        Assert.Contains("\"deletable\":{\"type\":\"noul\"", body);
        Assert.Contains("\"category\":{\"type\":\"choice\"", body);
    }

    [Fact]
    public async Task Retries_on_429()
    {
        var handler = new FakeHandler((_, n) => n == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") } : Ok());
        var client = new JevClient(new HttpClient(handler), "k", "https://example.test/x", "jev-1.13");
        var v = await client.AskAsync("s");
        Assert.Equal(2, handler.Calls);
        Assert.Equal("cache", v.Category);
    }

    [Fact]
    public async Task Fails_fast_on_401()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad key") });
        var client = new JevClient(new HttpClient(handler), "k", "https://example.test/x", "jev-1.13");
        await Assert.ThrowsAsync<JevException>(() => client.AskAsync("s"));
        Assert.Equal(1, handler.Calls);
    }

    private static JevSettings Settings(int maxCalls = 100, double maxCost = 1) => new()
    {
        MinDirSize = "1GB",
        MinFileSize = "2GB",
        MaxCallsPerScan = maxCalls,
        MaxCostPerScan = maxCost,
        Parallelism = 1
    };

    [Fact]
    public void Planner_descends_into_dominant_children_and_skips_covered_size()
    {
        var root = Root(@"C:\");
        var data = root.Dir("Data");
        var a = data.Dir("A"); a.SmallFiles(3 * GB);
        var b = data.Dir("B"); b.SmallFiles(2 * GB);
        data.SmallFiles(100 * MB);

        // Spread out: no dominant child → asked as a whole
        var mixed = root.Dir("Mixed");
        for (var i = 0; i < 6; i++) mixed.Dir("p" + i).SmallFiles(300 * MB);

        // Mostly covered by a rule → too small to ask
        var proj = root.Dir("Proj");
        var target = proj.Dir("target"); target.SmallFiles(5 * GB); target.Mark = NodeMark.Suggested;
        proj.Dir("src").SmallFiles(100 * MB);

        var big = root.Dir("Iso").File("win.iso", 3 * GB);

        var plan = new JevPlanner(Settings()).Plan(root, new DecisionCache(null, 90));

        var asked = plan.ToAsk.Select(c => c.Node).ToList();
        Assert.Contains(a, asked);
        Assert.Contains(b, asked);
        Assert.Contains(mixed, asked);
        Assert.Contains(big, asked);
        Assert.DoesNotContain(data, asked);
        Assert.DoesNotContain(proj, asked);
        Assert.DoesNotContain(target, asked);
        // Biggest first
        Assert.Same(a, plan.ToAsk[0].Node);
    }

    [Fact]
    public void Planner_respects_call_limit()
    {
        var root = Root(@"C:\");
        for (var i = 0; i < 10; i++) root.Dir("d" + i).Dir("x").SmallFiles(2 * GB);

        var plan = new JevPlanner(Settings(maxCalls: 3)).Plan(root, new DecisionCache(null, 90));
        Assert.Equal(3, plan.ToAsk.Count);
        Assert.Equal(7, plan.SkippedByLimit);
    }

    [Fact]
    public async Task Run_stops_at_cost_budget_and_caches_answers()
    {
        var root = Root(@"C:\");
        for (var i = 0; i < 5; i++) root.Dir("d" + i).SmallFiles(2 * GB);

        var settings = Settings(maxCost: 0.00005); // each call costs 0.00003 → two calls
        var cachePath = Path.Combine(Path.GetTempPath(), $"wcm-cache-{Guid.NewGuid():N}.json");
        try
        {
            var cache = new DecisionCache(cachePath, 90);
            var planner = new JevPlanner(settings);
            var plan = planner.Plan(root, cache);
            var handler = new FakeHandler((_, _) => Ok());
            var client = new JevClient(new HttpClient(handler), "k", "https://example.test/x", "jev");
            var progress = new JevRunProgress { Total = plan.ToAsk.Count };

            var items = await planner.RunAsync(plan, client, cache, progress, CancellationToken.None);

            Assert.Equal(2, handler.Calls);
            Assert.Equal(3, progress.SkippedByBudget);
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal(ItemSource.Jev, i.Source));
            Assert.All(items, i => Assert.True(i.Checked)); // 0.93 & cache → safe

            // Second scan: answered ones come from the cache for free
            foreach (var n in root.Children) n.Mark = NodeMark.None;
            var again = planner.Plan(root, new DecisionCache(cachePath, 90));
            Assert.Equal(2, again.CacheHits);
            Assert.Equal(2, again.FromCache.Count);
            Assert.Equal(3, again.ToAsk.Count);
        }
        finally { File.Delete(cachePath); }
    }

    [Theory]
    [InlineData(0.95, "cache", true, Safety.Safe)]
    [InlineData(0.95, "user-data", true, Safety.Review)]
    [InlineData(0.95, "downloads", true, Safety.Review)]
    [InlineData(0.7, "cache", true, Safety.Review)]
    [InlineData(0.4, "cache", false, Safety.Review)]
    public void Verdict_thresholds(double p, string category, bool listed, Safety safety)
    {
        var node = Root(@"C:\").Dir("x");
        var item = new JevPlanner(Settings()).ToItem(node, GB, p, category, ItemSource.Jev);
        Assert.Equal(listed, item is not null);
        if (item is null) return;
        Assert.Equal(safety, item.Safety);
        Assert.Equal(safety == Safety.Safe, item.Checked);
    }

    [Fact]
    public void Fingerprint_is_stable_and_changes_with_content()
    {
        var root = Root(@"C:\");
        var d = root.Dir("cache");
        d.Dir("a").SmallFiles(GB);
        d.Dir("b").SmallFiles(GB);
        var fp1 = DecisionCache.Fingerprint(d, d.Size);
        Assert.Equal(fp1, DecisionCache.Fingerprint(d, d.Size));
        Assert.Equal(fp1, DecisionCache.Fingerprint(d, d.Size + 10 * MB)); // same size bucket

        d.Dir("new-thing").SmallFiles(GB);
        Assert.NotEqual(fp1, DecisionCache.Fingerprint(d, d.Size));
    }

    [Fact]
    public void State_hides_user_profile_and_lists_children()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Root(profile);
        var d = root.Dir("stuff", "package.json");
        d.Dir("blobs").SmallFiles(GB);
        d.File("model.bin", 3 * GB);

        var state = StateBuilder.Build(d, d.Size);
        Assert.DoesNotContain(profile, state, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%\\stuff", state);
        Assert.Contains("[dir]  blobs", state);
        Assert.Contains("model.bin", state);
        Assert.Contains("package.json", state);
    }
}
