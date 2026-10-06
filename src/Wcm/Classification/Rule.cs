using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wcm.Model;

namespace Wcm.Classification;

public enum RuleAction { Suggest, Never, Protect, Container }

public enum RuleKind { Any, Dir, File }

public sealed class RuleWhen
{
    /// <summary>Glob on names in the parent dir (project markers, subdirs, big files), e.g. "*.csproj".</summary>
    public string? SiblingExists { get; set; }
    public string? MinSize { get; set; }
    /// <summary>Newest file in the subtree must be older than this.</summary>
    public int? OlderThanDays { get; set; }
}

public sealed class Rule
{
    public string Id { get; set; } = "";

    [JsonConverter(typeof(StringOrListConverter))]
    public List<string> Match { get; set; } = [];

    public RuleKind Kind { get; set; } = RuleKind.Any;
    public RuleAction Action { get; set; } = RuleAction.Suggest;
    public RuleWhen? When { get; set; }
    public string Category { get; set; } = "";
    public Safety Safety { get; set; } = Safety.Review;
    public string Reason { get; set; } = "";

    /// <summary>Delete what's inside, keep the dir itself (Temp folders).</summary>
    public bool Contents { get; set; }

    /// <summary>May match inside protected trees (C:\Windows\Temp).</summary>
    public bool AllowInProtected { get; set; }

    [JsonIgnore] public bool IsUser { get; set; }

    private List<CompiledPattern>? _compiled;
    private Regex? _sibling;

    internal IReadOnlyList<CompiledPattern> Compiled => _compiled ??= Match.Select(CompiledPattern.Create).ToList();

    internal Regex? SiblingRegex => When?.SiblingExists is { } s ? _sibling ??= Glob.ToNameRegex(s) : null;

    internal long MinSizeBytes => When?.MinSize is { } s ? ByteSize.Parse(s) : 0;
}

internal sealed record CompiledPattern(Regex Regex, string? LastLiteral)
{
    public static CompiledPattern Create(string pattern)
    {
        var p = Glob.Normalize(Environment.ExpandEnvironmentVariables(pattern));
        var last = p[(p.LastIndexOf('\\') + 1)..];
        var literal = last.IndexOfAny(['*', '?']) < 0 && last.Length > 0 ? last : null;
        return new CompiledPattern(Glob.ToRegex(p, anchoredName: false), literal);
    }

    public bool IsMatch(string normalizedPath, string name) =>
        (LastLiteral is null || name.Equals(LastLiteral, StringComparison.OrdinalIgnoreCase)) && Regex.IsMatch(normalizedPath);
}

internal static class Glob
{
    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\');
        while (p.Length > 1 && p.EndsWith('\\')) p = p[..^1];
        return p;
    }

    /// <summary>Name glob with "|" alternatives: "*.csproj|*.fsproj".</summary>
    public static Regex ToNameRegex(string globs)
    {
        var parts = globs.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => ToRegex(g, anchoredName: true).ToString());
        return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>** = any depth, * = within a segment, ? = one char. Relative patterns match at any depth.</summary>
    public static Regex ToRegex(string glob, bool anchoredName)
    {
        var sb = new StringBuilder("^");
        var relative = !anchoredName && !glob.StartsWith("**") && !(glob.Length >= 2 && glob[1] == ':') && !glob.StartsWith("\\\\");
        if (relative) sb.Append(@"(?:.*\\)?");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**\" may also match zero segments
                if (i + 2 < glob.Length && glob[i + 2] == '\\') { sb.Append(@"(?:.*\\)?"); i += 2; }
                else { sb.Append(".*"); i++; }
            }
            else if (c == '*') sb.Append(@"[^\\]*");
            else if (c == '?') sb.Append(@"[^\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

internal sealed class StringOrListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return [reader.GetString()!];
        return JsonSerializer.Deserialize<List<string>>(ref reader) ?? [];
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1) writer.WriteStringValue(value[0]);
        else JsonSerializer.Serialize(writer, value);
    }
}
