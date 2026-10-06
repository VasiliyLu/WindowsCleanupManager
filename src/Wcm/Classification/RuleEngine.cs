using System.Reflection;
using Wcm.Scanning;
using Wcm.Storage;

namespace Wcm.Classification;

public sealed class RuleEngine
{
    private readonly List<Rule> _builtin;
    private readonly List<Rule> _user;
    private List<Rule> _ordered = [];
    private readonly string? _userRulesPath;

    public RuleEngine(IEnumerable<Rule> builtin, IEnumerable<Rule> user, string? userRulesPath = null)
    {
        _builtin = builtin.ToList();
        _user = user.ToList();
        foreach (var r in _user) r.IsUser = true;
        _userRulesPath = userRulesPath;
        Reorder();
    }

    public IReadOnlyList<Rule> UserRules => _user;
    public IReadOnlyList<Rule> AllRules => _ordered;

    public static RuleEngine Load(string? userRulesPath = null)
    {
        userRulesPath ??= AppPaths.UserRules;
        var user = Json.Load<List<Rule>>(userRulesPath) ?? [];
        return new RuleEngine(LoadBuiltin(), user, userRulesPath);
    }

    public static List<Rule> LoadBuiltin()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("builtin-rules.json")
                      ?? throw new InvalidOperationException("builtin-rules.json resource missing");
        return System.Text.Json.JsonSerializer.Deserialize<List<Rule>>(s, Json.Options) ?? [];
    }

    // Protect always wins; then user rules; then builtin. Within a group: never > suggest > container.
    private void Reorder()
    {
        static int Rank(RuleAction a) => a switch
        {
            RuleAction.Never => 0,
            RuleAction.Suggest => 1,
            _ => 2
        };

        _ordered = _builtin.Concat(_user).Where(r => r.Action == RuleAction.Protect)
            .Concat(_user.Where(r => r.Action != RuleAction.Protect).OrderBy(r => Rank(r.Action)))
            .Concat(_builtin.Where(r => r.Action != RuleAction.Protect).OrderBy(r => Rank(r.Action)))
            .ToList();
    }

    public void AddUserRule(Rule rule)
    {
        rule.IsUser = true;
        _user.RemoveAll(r => r.Id == rule.Id);
        _user.Add(rule);
        Reorder();
        if (_userRulesPath is not null) Json.Save(_userRulesPath, _user);
    }

    public Rule? Match(FsNode node, bool inProtected) => Match(Glob.Normalize(node.FullPath), node.Name, node.IsDir, inProtected);

    public Rule? Match(string normPath, string name, bool isDir, bool inProtected)
    {
        foreach (var rule in _ordered)
        {
            if (inProtected && !rule.AllowInProtected && !(rule.IsUser && rule.Action == RuleAction.Never)) continue;
            if (rule.Kind == RuleKind.Dir && !isDir) continue;
            if (rule.Kind == RuleKind.File && isDir) continue;
            foreach (var p in rule.Compiled)
                if (p.IsMatch(normPath, name)) return rule;
        }
        return null;
    }

    public static bool ConditionsMet(Rule rule, FsNode node, DateTime? nowUtc = null)
    {
        var w = rule.When;
        if (w is null) return true;

        if (rule.MinSizeBytes > 0 && node.Size < rule.MinSizeBytes) return false;

        if (w.OlderThanDays is { } days && node.LastWriteUtc > (nowUtc ?? DateTime.UtcNow).AddDays(-days)) return false;

        if (rule.SiblingRegex is { } rx)
        {
            var parent = node.Parent;
            if (parent is null) return false;
            var found = parent.Markers?.Any(rx.IsMatch) == true
                        || parent.Children.Any(c => !ReferenceEquals(c, node) && rx.IsMatch(c.Name));
            if (!found) return false;
        }

        return true;
    }

    /// <summary>Path-only safety check used right before deleting. Returns a reason if the path must not be deleted.</summary>
    public string? BlockReason(string path)
    {
        var norm = Glob.Normalize(Path.GetFullPath(path));
        if (norm.Length <= 2 || Path.GetPathRoot(path)?.TrimEnd('\\').Equals(norm, StringComparison.OrdinalIgnoreCase) == true)
            return "корень диска";

        // Walk from the root down: find a protected ancestor, then look for an allowed exception below it
        var segments = norm.Split('\\');
        var prefix = segments[0];
        var inProtected = false;
        for (var i = 1; i < segments.Length; i++)
        {
            prefix += "\\" + segments[i];
            var isLast = i == segments.Length - 1;
            var rule = Match(prefix, segments[i], isDir: !isLast || !File.Exists(prefix), inProtected);
            if (rule is null) continue;

            if (rule.Action == RuleAction.Protect && !inProtected) inProtected = true;
            else if (inProtected && rule.AllowInProtected && rule.Action == RuleAction.Suggest) return null;
            else if (isLast && rule.Action == RuleAction.Container) return $"контейнер ({rule.Id})";
        }

        return inProtected ? "защищённый путь" : null;
    }
}
