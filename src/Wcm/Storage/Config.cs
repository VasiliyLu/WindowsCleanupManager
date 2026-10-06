using System.Text.Json;
using System.Text.Json.Serialization;
using Wcm.Model;

namespace Wcm.Storage;

public static class AppPaths
{
    public static string Dir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wcm");

    public static string Config => Path.Combine(Dir, "config.json");
    public static string UserRules => Path.Combine(Dir, "rules.json");
    public static string Cache => Path.Combine(Dir, "jev-cache.json");
    public static string DeletionLog => Path.Combine(Dir, "deletions.log");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Keep Cyrillic readable in rules.json
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(s, Options);
    }

    /// <summary>Write to temp + rename so a crash never leaves a half-written file.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class JevSettings
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1/systemone";
    public string Model { get; set; } = "jev-1.13";
    public string MinDirSize { get; set; } = "1GB";
    public string MinFileSize { get; set; } = "2GB";
    public int MaxCallsPerScan { get; set; } = 100;
    public double MaxCostPerScan { get; set; } = 0.05;
    public int Parallelism { get; set; } = 4;
    public double SuggestThreshold { get; set; } = 0.85;
    public double ReviewThreshold { get; set; } = 0.6;
    public int CacheTtlDays { get; set; } = 90;
    public bool ConfirmBeforeCalls { get; set; } = true;

    [JsonIgnore] public long MinDirBytes => ByteSize.Parse(MinDirSize);
    [JsonIgnore] public long MinFileBytes => ByteSize.Parse(MinFileSize);
}

public sealed class ScanSettings
{
    /// <summary>0 = auto.</summary>
    public int Threads { get; set; }
    /// <summary>Files smaller than this are only summed, not kept in the tree.</summary>
    public string MinFileNodeSize { get; set; } = "1MB";
    /// <summary>Rule matches smaller than this are not listed (still not sent to Jev).</summary>
    public string MinSuggestionSize { get; set; } = "1MB";
}

public sealed class DockerSettings
{
    public bool Enabled { get; set; } = true;
    public bool IncludeInDriveScan { get; set; } = true;
}

public sealed class AppConfig
{
    /// <summary>Falls back to the OPENROUTER_API_KEY env var.</summary>
    public string? OpenRouterApiKey { get; set; }
    public JevSettings Jev { get; set; } = new();
    public ScanSettings Scan { get; set; } = new();
    public DockerSettings Docker { get; set; } = new();

    [JsonIgnore]
    public string? ApiKey => string.IsNullOrWhiteSpace(OpenRouterApiKey)
        ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
        : OpenRouterApiKey;

    public static AppConfig LoadOrCreate()
    {
        var cfg = Json.Load<AppConfig>(AppPaths.Config);
        if (cfg is null)
        {
            cfg = new AppConfig();
            try { Json.Save(AppPaths.Config, cfg); } catch (IOException) { }
        }
        return cfg;
    }
}
