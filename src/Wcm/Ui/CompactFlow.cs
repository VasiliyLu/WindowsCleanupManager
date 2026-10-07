using Wcm.Docker;
using Wcm.Model;
using Wcm.Storage;

namespace Wcm.Ui;

/// <summary>Confirm → compact the Docker vhdx with progress → report.</summary>
internal static class CompactFlow
{
    public static void Run(Func<Frame> background, string question)
    {
        var disks = VhdxCompactor.FindDisks();
        if (disks.Count == 0)
        {
            Dialogs.Message(background, "Docker disk", "Docker disk (vhdx) not found.");
            return;
        }

        var details = disks.Select(d => $"{Fmt.MiddleTrim(d, 70)}  {ByteSize.Format(new FileInfo(d).Length)}").ToList();
        details.Add("Docker Desktop and all WSL distros will be stopped (wsl --shutdown), running containers stop too.");
        details.Add("Windows will ask for administrator rights (diskpart). Docker Desktop is started again afterwards.");
        if (!Dialogs.Confirm(background, question, details.ToArray())) return;

        var step = "";
        var progress = new Progress<string>(s => step = s);
        var task = new VhdxCompactor(AppPaths.DeletionLog).CompactAsync(progress, CancellationToken.None);
        var frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        var i = 0;
        while (!task.IsCompleted)
        {
            var f = new Frame();
            f.Add(new Line(f.Width, Style.Header).Text(" Compacting Docker disk").Fill());
            f.Blank();
            f.Add($"  {frames[i++ % frames.Length]} {step}");
            f.FillTo(1);
            f.Add(new Line(f.Width, Style.Footer).Text(" This may take a few minutes").Fill());
            Term.Draw(f);
            Term.TryReadKey();
            Thread.Sleep(150);
        }

        if (task.IsFaulted)
        {
            Dialogs.Message(background, "Compaction error", task.Exception!.GetBaseException().Message);
            return;
        }

        var r = task.Result;
        var lines = r.Disks.Select(d => $"{Path.GetFileName(d.Path)}: {ByteSize.Format(d.Before)} → {ByteSize.Format(d.After)}, freed {ByteSize.Format(d.Freed)}").ToList();
        lines.AddRange(r.Errors.Select(e => "✗ " + e));
        Dialogs.Message(() => new Frame(), r.Cancelled ? "Compaction cancelled" : r.Errors.Count > 0 ? "Compaction finished with errors" : "Docker disk compacted", lines.ToArray());
    }
}
