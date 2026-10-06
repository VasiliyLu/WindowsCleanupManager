using Wcm.Classification;
using Wcm.Deletion;
using Wcm.Model;
using Wcm.Storage;

namespace Wcm.Ui;

/// <summary>Confirm → delete with progress → report → update the session.</summary>
internal static class DeleteFlow
{
    public static void Run(ScanSession session, RuleEngine rules, bool permanent, Func<Frame> background)
    {
        var items = session.EffectiveSelection();
        if (items.Count == 0)
        {
            Dialogs.Message(background, "Ничего не отмечено", "Отметьте элементы пробелом.");
            return;
        }

        var deleter = new Deleter(rules, AppPaths.DeletionLog);
        var blocked = items.Select(i => (Item: i, Why: deleter.Validate(i))).Where(x => x.Why is not null).ToList();
        if (blocked.Count > 0)
        {
            Dialogs.Message(background, $"{blocked.Count} элемент(ов) будут пропущены",
                blocked.Take(5).Select(b => $"{Fmt.MiddleTrim(b.Item.Display, 80)} — {b.Why}").ToArray());
            items = items.Except(blocked.Select(b => b.Item)).ToList();
            if (items.Count == 0) return;
        }

        if (!Confirm(items, permanent)) return;

        using var cts = new CancellationTokenSource();
        var current = "";
        var done = 0;
        var progress = new Progress<string>(s => { current = s; done++; });
        var task = deleter.DeleteAsync(items, permanent, progress, cts.Token);
        while (!task.IsCompleted)
        {
            var f = new Frame();
            f.Add(new Line(f.Width, Style.Header).Text(permanent ? " Удаление навсегда" : " Удаление в корзину").Fill());
            f.Blank();
            f.Add("  " + Fmt.Bar((double)done / items.Count, 40) + $"  {done}/{items.Count}");
            f.Blank();
            f.Add("  " + Fmt.MiddleTrim(current, f.Width - 4), Style.Gray);
            f.FillTo(1);
            f.Add(new Line(f.Width, Style.Footer).Text(" Esc — остановить после текущего элемента").Fill());
            Term.Draw(f);
            if (Term.TryReadKey() is { Key: ConsoleKey.Escape }) cts.Cancel();
            Thread.Sleep(150);
        }

        if (task.IsFaulted)
        {
            Dialogs.Message(background, "Ошибка удаления", task.Exception!.GetBaseException().Message);
            return;
        }

        var report = task.Result;
        Apply(session, report);
        ShowReport(report, permanent);
    }

    private static bool Confirm(List<CleanupItem> items, bool permanent)
    {
        var total = items.Sum(i => i.Size);
        var risky = items.Count(i => i.Safety == Safety.Review || i.Source == ItemSource.Manual);
        var volumes = items.Count(i => i.Kind == ItemKind.DockerVolume);

        Frame Bg()
        {
            var f = new Frame();
            f.Add(new Line(f.Width, permanent ? "\e[1;37;41m" : Style.Header)
                .Text($" Будет удалено {(permanent ? "НАВСЕГДА" : "в корзину")}: {items.Count} элементов, {ByteSize.Format(total)}").Fill());
            f.Blank();
            var room = Math.Max(1, f.Height - 9);
            foreach (var i in items.OrderByDescending(i => i.Size).Take(room))
            {
                var warn = i.Safety == Safety.Review || i.Source == ItemSource.Manual;
                f.Add(new Line(f.Width).Text("  " + Fmt.Size(i.Size) + "  ").Text(i.KindLabel.PadRight(9))
                    .Text(Fmt.MiddleTrim(i.Display, f.Width - 26), warn ? Style.Yellow : ""));
            }
            if (items.Count > room) f.Add($"  …и ещё {items.Count - room}", Style.Gray);
            return f;
        }

        var details = new List<string>();
        if (risky > 0) details.Add($"⚠ {risky} элемент(ов) требуют проверки (review / вручную) — выделены жёлтым.");
        if (volumes > 0) details.Add($"⚠ {volumes} Docker volume — данные в них будут потеряны безвозвратно.");
        if (items.Any(i => i.IsDocker) && !permanent) details.Add("Docker-объекты удаляются сразу, корзина к ним не применяется.");
        return Dialogs.Confirm(Bg, permanent ? "Удалить навсегда? Восстановить будет нельзя." : "Переместить в корзину?", details.ToArray());
    }

    private static void Apply(ScanSession session, DeleteReport report)
    {
        foreach (var o in report.Outcomes)
        {
            var item = o.Item;
            var node = item.Node;
            if (o.Success && item.Kind != ItemKind.FolderContents)
            {
                session.Remove(item);
                // Drop items that lived inside the deleted folder
                if (node is not null)
                {
                    foreach (var inner in session.Items.Where(i => i.Node is not null && i.Node.IsUnder(node)).ToList())
                        session.Remove(inner);
                    node.Remove();
                }
                continue;
            }

            if (node is not null)
            {
                foreach (var inner in session.Items.Where(i => i.Node is not null && !ReferenceEquals(i.Node, node) && i.Node.IsUnder(node)).ToList())
                    session.Remove(inner);
                // Something is left (locked files or contents-only delete): keep the node with its real remaining size
                node.ClearContents();
                node.SmallFilesSize = o.RemainingBytes;
                node.AddSize(o.RemainingBytes);
            }

            item.Size = o.RemainingBytes;
            item.Checked = false;
            if (o.Success || item.Size == 0) session.Remove(item);
        }
    }

    private static void ShowReport(DeleteReport report, bool permanent)
    {
        var lines = new List<string>
        {
            $"Успешно: {report.Succeeded}, с ошибками: {report.Failed}.",
            permanent
                ? $"Освобождено на дисках: {ByteSize.Format(report.FreedByDrives)}"
                : $"Перемещено в корзину: {ByteSize.Format(report.Outcomes.Where(o => o.Success && !o.Item.IsDocker).Sum(o => o.Item.Size))}. Место освободится после очистки корзины."
        };
        foreach (var o in report.Outcomes.Where(o => !o.Success).Take(6))
            lines.Add($"✗ {Fmt.MiddleTrim(o.Item.Display, 60)}: {o.Errors.FirstOrDefault() ?? "частично"}");
        if (report.DockerTouched)
            lines.Add("Docker: место освобождено внутри vhdx, но сам файл может не уменьшиться — для сжатия: wsl --shutdown, затем Optimize-VHD или diskpart compact vdisk.");
        lines.Add($"Журнал: {AppPaths.DeletionLog}");
        Dialogs.Message(() => new Frame(), "Готово", lines.ToArray());
    }
}
