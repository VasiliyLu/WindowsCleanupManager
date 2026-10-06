using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wcm.Classification;

public sealed record JevVerdict(double Deletable, string Category, double CategoryConfidence, double Cost);

public sealed class JevException(string message) : Exception(message);

/// <summary>OpenRouter System One API client for TypeSafe Jev.</summary>
public sealed class JevClient(HttpClient http, string apiKey, string endpoint, string model)
{
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["cache"] = "Application or browser cache that is rebuilt automatically.",
        ["temp"] = "Temporary files, leftovers of installs or crashed programs.",
        ["build"] = "Build output or dependencies of a software project that can be regenerated.",
        ["package-cache"] = "Downloaded packages, SDK or model caches that can be downloaded again.",
        ["logs"] = "Logs, crash dumps, diagnostic reports.",
        ["installer"] = "Installer files, update downloads, setup leftovers.",
        ["downloads"] = "Files the user downloaded manually (archives, ISOs, media).",
        ["user-data"] = "Documents, photos, projects, saves, mail or other data the user created.",
        ["application"] = "Installed application binaries or data required for an app to work.",
        ["system"] = "Operating system or driver files."
    };

    private const string DeletableQuestion =
        "Is this regenerable cache, temporary data, build output, logs or installer leftovers " +
        "that can be deleted without losing user data and without breaking installed applications?";

    private const string CategoryQuestion = "What kind of data is this?";

    public async Task<JevVerdict> AskAsync(string state, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["state"] = state,
            ["questions"] = new JsonObject
            {
                ["deletable"] = new JsonObject { ["type"] = "noul", ["instructions"] = DeletableQuestion },
                ["category"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = CategoryQuestion,
                    ["criteria"] = new JsonObject(Categories.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))
                }
            }
        };

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Add("X-Title", "WindowsCleanupManager");

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
            if (retryable && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new JevException($"Jev HTTP {(int)resp.StatusCode}: {Trim(text)}");

            return Parse(text);
        }
    }

    internal static JevVerdict Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var answers = root.GetProperty("answers");

            var deletable = answers.GetProperty("deletable").GetProperty("noul").GetDouble();

            var cat = answers.GetProperty("category");
            var choice = cat.GetProperty("choice").GetString() ?? "";
            var conf = cat.TryGetProperty("confidence", out var c) ? c.GetDouble()
                : cat.TryGetProperty("probabilities", out var p) && p.TryGetProperty(choice, out var pc) ? pc.GetDouble() : 0;

            var cost = root.TryGetProperty("usage", out var u) && u.TryGetProperty("cost", out var uc) ? uc.GetDouble() : 0;
            return new JevVerdict(deletable, choice, conf, cost);
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException or InvalidOperationException)
        {
            throw new JevException($"Unexpected Jev response: {Trim(json)}");
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
