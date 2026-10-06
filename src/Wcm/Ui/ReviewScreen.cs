using Wcm.Classification;
using Wcm.Deletion;
using Wcm.Model;
using Wcm.Scanning;
using Wcm.Storage;

namespace Wcm.Ui;

/// <summary>Main post-scan screen: list of suggestions with checkboxes.</summary>
internal sealed class ReviewScreen(ScanSession session, Pipeline pipeline)
{
    private int _selected;
    private int _top;
    private bool _sortByCategory;
    private string? _status;
    private List<CleanupItem> _rows = [];

    public void Run()
    {
        while (true)
        {
            Resort();
            Term.Draw(Render());
            var k = Term.ReadKey(Render);
            _status = null;
            var pageSize = Math.Max(1, ListHeight(Term.Height));
            var cur = _rows.Count > 0 ? _rows[Math.Clamp(_selected, 0, _rows.Count - 1)] : null;

            switch (k.Key)
            {
                case ConsoleKey.UpArrow: _selected--; break;
                case ConsoleKey.DownArrow: _selected++; break;
                case ConsoleKey.PageUp: _selected -= pageSize; break;
                case ConsoleKey.PageDown: _selected += pageSize; break;
                case ConsoleKey.Home: _selected = 0; break;
                case ConsoleKey.End: _selected = _rows.Count - 1; break;

                case ConsoleKey.Spacebar when cur is not null:
                    cur.Checked = !cur.Checked;
                    _selected++;
                    break;
                case ConsoleKey.A:
                    foreach (var i in session.Items.Where(i => i.Safety == Safety.Safe)) i.Checked = true;
                    break;
                case ConsoleKey.U:
                    foreach (var i in session.Items) i.Checked = false;
                    break;
                case ConsoleKey.S:
                    _sortByCategory = !_sortByCategory;
                    break;

                case ConsoleKey.B when session.Root is not null:
                    new BrowseScreen(session, pipeline, session.Root).Run();
                    break;
                case ConsoleKey.Enter when cur?.Node is { } node:
                    new BrowseScreen(session, pipeline, node.IsDir ? node : node.Parent ?? session.Root!).Run();
                    break;

                case ConsoleKey.R when cur is not null:
                    SaveAsRule(cur);
                    break;
                case ConsoleKey.N when cur is not null:
                    NeverSuggest(cur);
                    break;

                case ConsoleKey.Delete when k.Modifiers.HasFlag(ConsoleModifiers.Shift):
                case ConsoleKey.X:
                    DeleteFlow.Run(session, pipeline.Rules, permanent: true, Render);
                    break;
                case ConsoleKey.Delete:
                case ConsoleKey.D:
                    DeleteFlow.Run(session, pipeline.Rules, permanent: false, Render);
                    break;

                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    if (session.Items.Any(i => i.Checked) && session.Root is not null
                        && !Dialogs.Confirm(Render, "Вернуться в меню? Результаты сканирования будут потеряны.")) break;
                    return;
            }
        }
    }

    private void Resort()
    {
        _rows = _sortByCategory
            ? session.Items.OrderBy(i => i.Category).ThenByDescending(i => i.Size).ToList()
            : session.Items.OrderByDescending(i => i.Size).ToList();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
    }

    private static int ListHeight(int height) => height - 8;

    private Frame Render()
    {
        var f = new Frame();
        var selection = session.EffectiveSelection();
        var total = session.Items.Where(i => !session.IsCovered(i)).Sum(i => i.Size);

        f.Add(new Line(f.Width, Style.Header)
            .Text($" {Fmt.MiddleTrim(session.Label, f.Width / 2)}: {session.Items.Count} предложений, {ByteSize.Format(total)}")
            .Right(Summary() + " ").Fill());

        f.Add(new Line(f.Width, Style.Bold).Text("     Размер  Тип      Источник  Категория      Увер.  Путь"));

        var listHeight = ListHeight(f.Height);
        if (_selected < _top) _top = _selected;
        if (_selected >= _top + listHeight) _top = _selected - listHeight + 1;

        for (var r = _top; r < Math.Min(_rows.Count, _top + listHeight); r++)
            f.Add(RenderRow(_rows[r], r == _selected, f.Width));

        if (_rows.Count == 0) f.Add("  Ничего не найдено. B — открыть дерево и отметить вручную.", Style.Gray);

        f.FillTo(5);
        var cur = _rows.Count > 0 ? _rows[_selected] : null;
        f.Add(new Line(f.Width, Style.Dim).Text(new string('─', f.Width)));
        f.Add(cur is null ? "" : "  " + cur.Reason, cur?.Safety == Safety.Review ? Style.Yellow : Style.Green);
        f.Add(cur is null ? "" : "  " + (cur.IsDocker ? cur.Target : cur.Display) + (cur.RuleId is { } id ? $"  [{id}]" : ""), Style.Gray);
        f.Add(new Line(f.Width).Text($"  Отмечено: {selection.Count} ({ByteSize.Format(selection.Sum(i => i.Size))})", Style.Bold)
            .Text("   " + (_status ?? session.JevNote ?? session.DockerError ?? ""), Style.Yellow));
        f.Add(new Line(f.Width, Style.Footer)
            .Text(" Space отм.  A все safe  U снять  S сорт.  Enter/B дерево  R правило  N не предлагать  Del корзина  Shift+Del/X навсегда  Q назад")
            .Fill());
        return f;
    }

    private string Summary()
    {
        var parts = new List<string>();
        if (session.Root is not null) parts.Add($"скан {session.ScanTime:mm\\:ss}, ошибок {session.ScanErrors}");
        if (session.Jev is { } j) parts.Add($"Jev {j.Done} запр. ${j.Cost:0.0000}");
        if (session.JevPlan is { CacheHits: > 0 } p) parts.Add($"кэш {p.CacheHits}");
        return string.Join(" | ", parts);
    }

    private Line RenderRow(CleanupItem item, bool selected, int width)
    {
        var covered = item.Checked && session.IsCovered(item);
        var line = new Line(width, selected ? Style.Reverse : "");
        var box = !item.Checked ? "[ ]" : covered ? "[~]" : "[x]";
        line.Text(" " + box, item.Safety == Safety.Safe ? Style.Green : Style.Yellow);
        line.Text(Fmt.Size(item.Size) + "  ", Style.Bold);
        line.Text(item.KindLabel.PadRight(9));
        line.Text(SourceLabel(item.Source).PadRight(10), item.Source == ItemSource.Jev ? Style.Magenta : Style.Cyan);
        line.Text(Fmt.MiddleTrim(item.Category, 14).PadRight(15));
        line.Text((item.Confidence is { } c ? $"{c:P0}" : "").PadLeft(4) + "  ");
        line.Text(Fmt.MiddleTrim(ShortPath(item), Math.Max(10, width - line.Length - 2)), covered ? Style.Dim : "");
        return line.Fill();
    }

    // When a folder (not a whole drive) was scanned, paths relative to it are far more readable
    private string ShortPath(CleanupItem item)
    {
        if (item.IsDocker || session.Root is not { Parent: null } root) return item.Display;
        var rootPath = root.FullPath;
        if (Path.GetPathRoot(rootPath) == rootPath) return item.Display;
        return item.Display.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase)
            ? "." + item.Display[rootPath.Length..]
            : item.Display;
    }

    private static string SourceLabel(ItemSource s) => s switch
    {
        ItemSource.Rule => "правило",
        ItemSource.Cache => "jev/кэш",
        ItemSource.Jev => "jev",
        ItemSource.Manual => "вручную",
        ItemSource.Docker => "docker",
        _ => ""
    };

    private void SaveAsRule(CleanupItem item)
    {
        if (item.Node is not { } node) { _status = "Для Docker-элементов правила не поддерживаются."; return; }

        var exact = StateBuilder.Anonymize(node.FullPath);
        var parentName = node.Parent is { Parent: not null } p ? p.Name : null;
        var options = new List<string> { exact, $"**\\{node.Name}" };
        if (parentName is not null) options.Add($"**\\{parentName}\\{node.Name}");

        var idx = Dialogs.Choose(Render, "Шаблон пути для правила:", options);
        if (idx < 0) return;
        var pattern = Dialogs.Prompt(Render, "Шаблон (** — любая глубина, * — часть имени):", options[idx]);
        if (string.IsNullOrWhiteSpace(pattern)) return;

        var action = Dialogs.Choose(Render, "Что делать с совпадениями?",
            ["Предлагать удалить и отмечать (safe)", "Предлагать, но не отмечать (review)", "Никогда не предлагать"]);
        if (action < 0) return;

        var rule = new Rule
        {
            Id = "user-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Match = [pattern.Trim()],
            Kind = node.IsDir ? RuleKind.Dir : RuleKind.File,
            Action = action == 2 ? RuleAction.Never : RuleAction.Suggest,
            Category = item.Category,
            Safety = action == 0 ? Safety.Safe : Safety.Review,
            Contents = item.Kind == ItemKind.FolderContents,
            Reason = action == 2 ? "правило пользователя: не предлагать" : "правило пользователя" + (item.Source == ItemSource.Jev ? $" (по ответу Jev: {item.Category})" : "")
        };

        try { pipeline.Rules.AddUserRule(rule); }
        catch (Exception e) { _status = "Не удалось сохранить правило: " + e.Message; return; }

        if (rule.Action == RuleAction.Never)
        {
            session.Remove(item);
            node.Mark = NodeMark.Known;
        }
        _status = $"Правило {rule.Id} сохранено в {AppPaths.UserRules}";
    }

    private void NeverSuggest(CleanupItem item)
    {
        if (item.Node is not { } node) { session.Remove(item); _status = "Скрыто до следующего опроса Docker."; return; }

        var rule = new Rule
        {
            Id = "user-never-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Match = [StateBuilder.Anonymize(node.FullPath)],
            Kind = node.IsDir ? RuleKind.Dir : RuleKind.File,
            Action = RuleAction.Never,
            Reason = "пользователь отказался"
        };
        try { pipeline.Rules.AddUserRule(rule); }
        catch (Exception e) { _status = "Не удалось сохранить правило: " + e.Message; return; }

        session.Remove(item);
        node.Mark = NodeMark.Known;
        _status = $"Больше не будет предлагаться: {node.FullPath}";
    }
}
