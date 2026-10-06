using Wcm.Model;
using Wcm.Scanning;

namespace Wcm.Ui;

/// <summary>ncdu-style tree browser with manual marking.</summary>
internal sealed class BrowseScreen(ScanSession session, Pipeline pipeline, FsNode start)
{
    private FsNode _dir = start;
    private int _selected;
    private int _top;
    private string? _status;
    // Remember the cursor per directory so going back up lands where we were
    private readonly Dictionary<FsNode, int> _cursor = new(ReferenceEqualityComparer.Instance);

    public void Run()
    {
        while (true)
        {
            var rows = Rows();
            _selected = Math.Clamp(_selected, 0, Math.Max(0, rows.Count - 1));
            Term.Draw(Render(rows));
            var k = Term.ReadKey(() => Render(Rows()));
            _status = null;
            var cur = rows.Count > 0 ? rows[_selected] : null;
            var page = Math.Max(1, Term.Height - 6);

            switch (k.Key)
            {
                case ConsoleKey.UpArrow: _selected--; break;
                case ConsoleKey.DownArrow: _selected++; break;
                case ConsoleKey.PageUp: _selected -= page; break;
                case ConsoleKey.PageDown: _selected += page; break;
                case ConsoleKey.Home: _selected = 0; break;
                case ConsoleKey.End: _selected = rows.Count - 1; break;

                case ConsoleKey.Enter:
                case ConsoleKey.RightArrow:
                    if (cur is { IsDir: true }) Enter(cur);
                    break;
                case ConsoleKey.LeftArrow:
                case ConsoleKey.Backspace:
                    Up();
                    break;

                case ConsoleKey.Spacebar when cur is not null:
                    Toggle(cur);
                    _selected++;
                    break;

                case ConsoleKey.Delete when k.Modifiers.HasFlag(ConsoleModifiers.Shift):
                case ConsoleKey.X:
                    DeleteFlow.Run(session, pipeline.Rules, permanent: true, () => Render(Rows()));
                    break;
                case ConsoleKey.Delete:
                case ConsoleKey.D:
                    DeleteFlow.Run(session, pipeline.Rules, permanent: false, () => Render(Rows()));
                    break;

                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return;
            }

            // A deletion may have detached the current dir
            while (_dir.Parent is null && !ReferenceEquals(_dir, session.Root) && session.Root is not null)
                _dir = session.Root;
        }
    }

    private List<FsNode> Rows() => _dir.Children.OrderByDescending(c => c.Size).ToList();

    private void Enter(FsNode dir)
    {
        _cursor[_dir] = _selected;
        _dir = dir;
        _selected = _cursor.GetValueOrDefault(dir);
        _top = 0;
    }

    private void Up()
    {
        if (_dir.Parent is null) return;
        _cursor[_dir] = _selected;
        var child = _dir;
        _dir = _dir.Parent;
        _selected = Math.Max(0, Rows().IndexOf(child));
        _top = 0;
    }

    private void Toggle(FsNode node)
    {
        if (session.ByNode.TryGetValue(node, out var existing))
        {
            existing.Checked = !existing.Checked;
            return;
        }

        if (node.Mark == NodeMark.Protected || node.ContainsProtected)
        {
            _status = "Protected path (or contains protected ones) — can't delete.";
            return;
        }
        if (pipeline.Rules.BlockReason(node.FullPath) is { } why)
        {
            _status = $"Not allowed: {why}.";
            return;
        }

        session.Add([new CleanupItem
        {
            Kind = node.IsDir ? ItemKind.Folder : ItemKind.File,
            Target = node.FullPath,
            Display = node.FullPath,
            Size = node.Size,
            Category = "manual",
            Reason = "checked manually",
            Source = ItemSource.Manual,
            Safety = Safety.Review,
            Checked = true,
            Node = node
        }]);
    }

    private Dictionary<FsNode, long> SuggestedInside()
    {
        var acc = new Dictionary<FsNode, long>(ReferenceEqualityComparer.Instance);
        foreach (var i in session.Items)
            for (var n = i.Node?.Parent; n is not null; n = n.Parent)
                acc[n] = acc.GetValueOrDefault(n) + i.Size;
        return acc;
    }

    private Frame Render(List<FsNode> rows)
    {
        var f = new Frame();
        var selection = session.EffectiveSelection();
        f.Add(new Line(f.Width, Style.Header).Text($" {Fmt.MiddleTrim(_dir.FullPath, f.Width - 30)}").Right($"{ByteSize.Format(_dir.Size)}, {_dir.FileCount:N0} files ").Fill());

        var listHeight = f.Height - 4;
        if (_selected < _top) _top = _selected;
        if (_selected >= _top + listHeight) _top = _selected - listHeight + 1;

        var parentSize = Math.Max(1, _dir.Size);
        var inside = SuggestedInside();
        for (var r = _top; r < Math.Min(rows.Count, _top + listHeight); r++)
        {
            var n = rows[r];
            var sel = r == _selected;
            var line = new Line(f.Width, sel ? Style.Reverse : "");

            session.ByNode.TryGetValue(n, out var item);
            var (mark, markStyle) = item switch
            {
                { Checked: true } => ("[x]", item.Safety == Safety.Safe ? Style.Green : Style.Yellow),
                not null => ("[ ]", Style.Yellow),
                _ when n.Mark == NodeMark.Protected => (" P ", Style.Dim),
                _ when n.ContainsProtected => (" p ", Style.Dim),
                _ => ("   ", "")
            };
            line.Text(" " + mark + " ", markStyle);
            line.Text(Fmt.Size(n.Size) + " ", Style.Bold);
            var frac = (double)n.Size / parentSize;
            line.Text("[" + Fmt.Bar(frac, 12) + "]", Style.Cyan);
            line.Text($"{frac,6:P1}  ");
            var name = n.IsDir ? n.Name + "\\" : n.Name;
            line.Text(name, n.Mark == NodeMark.Protected ? Style.Dim : n.IsDir ? Style.Blue + Style.Bold : "");
            if (item is not null) line.Text($"  {item.Category}", Style.Gray);
            else if (inside.TryGetValue(n, out var sub)) line.Text($"  suggested inside: {ByteSize.Format(sub)}", Style.Gray);
            if (n.AccessDenied) line.Text("  (access denied)", Style.Red);
            f.Add(line.Fill());
        }

        if (_dir.SmallFilesCount > 0 && f.Remaining > 3)
            f.Add(new Line(f.Width).Text("     " + Fmt.Size(_dir.SmallFilesSize) + "  ").Text($"{_dir.SmallFilesCount:N0} small files", Style.Gray));

        f.FillTo(2);
        f.Add(new Line(f.Width).Text($"  Checked: {selection.Count} ({ByteSize.Format(selection.Sum(i => i.Size))})", Style.Bold)
            .Text("   " + (_status ?? ""), Style.Yellow));
        f.Add(new Line(f.Width, Style.Footer).Text(" ↑↓ select  Enter/→ open  ←/Backspace up  Space check  Del recycle  Shift+Del/X permanently  Q to list").Fill());
        return f;
    }
}
