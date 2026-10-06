using Wcm.Classification;
using Wcm.Model;
using Wcm.Scanning;
using Wcm.Tests.Helpers;
using static Wcm.Tests.Helpers.TreeBuilder;

namespace Wcm.Tests;

public class RuleTests
{
    private static RuleEngine Engine(params Rule[] user) => new(RuleEngine.LoadBuiltin(), user);

    [Theory]
    [InlineData(@"**\node_modules", @"C:\a\b\node_modules", true)]
    [InlineData(@"**\node_modules", @"C:\node_modules", true)]
    [InlineData(@"**\node_modules", @"C:\a\node_modules_x", false)]
    [InlineData(@"?:\Users\*\AppData\Local\Temp", @"D:\Users\bob\AppData\Local\Temp", true)]
    [InlineData(@"?:\Users\*\AppData\Local\Temp", @"C:\Users\bob\x\AppData\Local\Temp", false)]
    [InlineData(@"?:\", @"C:", true)]
    [InlineData(@"bin", @"C:\x\bin", true)]
    [InlineData(@"**\*.dmp", @"C:\x\y\crash.DMP", true)]
    public void Glob_matches(string pattern, string path, bool expected)
    {
        var p = CompiledPattern.Create(pattern);
        Assert.Equal(expected, p.IsMatch(Glob.Normalize(path), path[(path.LastIndexOf('\\') + 1)..]));
    }

    [Fact]
    public void Builtin_rules_load_and_compile()
    {
        var rules = RuleEngine.LoadBuiltin();
        Assert.True(rules.Count > 30);
        foreach (var r in rules)
        {
            Assert.NotEmpty(r.Compiled);
            _ = r.SiblingRegex;
            _ = r.MinSizeBytes;
        }
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Node_modules_needs_package_json_and_age()
    {
        var root = Root(@"C:\Dev");
        var proj = root.Dir("app", "package.json");
        var nm = proj.Dir("node_modules");
        nm.SmallFiles(200 * MB);

        var orphan = root.Dir("loose").Dir("node_modules");
        orphan.SmallFiles(200 * MB);

        var items = new Classifier(Engine()).ApplyRules(root, new DateTime(2026, 1, 1));

        var item = Assert.Single(items);
        Assert.Same(nm, item.Node);
        Assert.Equal("node-modules", item.RuleId);
        Assert.True(item.Checked);
        Assert.Equal(NodeMark.Suggested, nm.Mark);

        // Fresh project: matched but not suggested, and not left for Jev either
        var fresh = new Classifier(Engine()).ApplyRules(root, new DateTime(2020, 1, 5));
        Assert.Empty(fresh);
        Assert.Equal(NodeMark.Known, nm.Mark);
    }

    [Fact]
    public void Bin_obj_need_project_file()
    {
        var root = Root(@"C:\Dev");
        var proj = root.Dir("Svc", "Svc.csproj");
        proj.Dir("bin").SmallFiles(50 * MB);
        root.Dir("tools").Dir("bin").SmallFiles(50 * MB);

        var items = new Classifier(Engine()).ApplyRules(root, new DateTime(2026, 1, 1));
        Assert.Equal(@"C:\Dev\Svc\bin", Assert.Single(items).Target);
    }

    [Fact]
    public void Protected_tree_only_allows_explicit_exceptions()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var root = Root(Path.GetPathRoot(win)!);
        var windows = root.Dir(Path.GetFileName(win));
        windows.Dir("System32").SmallFiles(5 * GB);
        windows.Dir("Temp").SmallFiles(300 * MB);
        // A user rule must not reach into protected trees
        var engine = Engine(new Rule { Id = "u", Match = [@"**\System32"], Action = RuleAction.Suggest, Safety = Safety.Safe });

        var items = new Classifier(engine).ApplyRules(root);

        var item = Assert.Single(items);
        Assert.Equal("windows-temp", item.RuleId);
        Assert.Equal(ItemKind.FolderContents, item.Kind);
        Assert.True(root.ContainsProtected);
    }

    [Fact]
    public void Git_dir_makes_project_undeletable_as_a_whole()
    {
        var root = Root(@"C:\Dev");
        var proj = root.Dir("proj");
        proj.Dir(".git").SmallFiles(10 * MB);
        proj.Dir("src").SmallFiles(10 * MB);

        new Classifier(Engine()).ApplyRules(root);
        Assert.True(proj.ContainsProtected);
        Assert.False(proj.Children.Single(c => c.Name == "src").ContainsProtected);
    }

    [Fact]
    public void User_never_rule_beats_builtin_suggest()
    {
        var root = Root(@"C:\Dev");
        var proj = root.Dir("keep", "Cargo.toml");
        proj.Dir("target").SmallFiles(GB);

        var engine = Engine(new Rule { Id = "keep", Match = [@"C:\Dev\keep\target"], Action = RuleAction.Never });
        Assert.Empty(new Classifier(engine).ApplyRules(root));
    }

    [Fact]
    public void Small_matches_are_not_listed()
    {
        var root = Root(@"C:\Dev");
        root.Dir("p", "x.csproj").Dir("obj").SmallFiles(10_000);
        var items = new Classifier(Engine(), minItemSize: MB).ApplyRules(root, new DateTime(2026, 1, 1));
        Assert.Empty(items);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Windows\System32", true)]
    [InlineData(@"C:\Windows\Temp\foo", false)]
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"C:\Program Files\App", true)]
    [InlineData(@"C:\Users\someone\Documents\x.docx", true)]
    [InlineData(@"C:\Users\someone", true)]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\x", false)]
    [InlineData(@"C:\Dev\proj\.git", true)]
    [InlineData(@"C:\Dev\proj\node_modules", false)]
    public void Block_reason_for_paths(string path, bool blocked)
    {
        if (!Environment.GetFolderPath(Environment.SpecialFolder.Windows).Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase))
            return;
        Assert.Equal(blocked, Engine().BlockReason(path) is not null);
    }
}
