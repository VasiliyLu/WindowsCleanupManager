using Wcm.Scanning;

namespace Wcm.Model;

public enum ItemKind { Folder, FolderContents, File, DockerImage, DockerVolume, DockerBuildCache }

public enum ItemSource { Rule, Cache, Jev, Manual, Docker }

public enum Safety { Safe, Review }

public sealed class CleanupItem
{
    public required ItemKind Kind { get; init; }

    /// <summary>Full path for fs items, id/name for docker items.</summary>
    public required string Target { get; init; }

    public string Display { get; init; } = "";
    public long Size { get; set; }
    public string Category { get; init; } = "";
    public string Reason { get; init; } = "";
    public ItemSource Source { get; init; }
    public double? Confidence { get; init; }
    public Safety Safety { get; init; }
    public bool Checked { get; set; }
    public string? RuleId { get; init; }

    // Backlink into the scan tree, null for docker items
    public FsNode? Node { get; init; }

    public bool IsDocker => Kind is ItemKind.DockerImage or ItemKind.DockerVolume or ItemKind.DockerBuildCache;

    public string KindLabel => Kind switch
    {
        ItemKind.Folder => "папка",
        ItemKind.FolderContents => "содерж.",
        ItemKind.File => "файл",
        ItemKind.DockerImage => "image",
        ItemKind.DockerVolume => "volume",
        ItemKind.DockerBuildCache => "build$",
        _ => "?"
    };
}
