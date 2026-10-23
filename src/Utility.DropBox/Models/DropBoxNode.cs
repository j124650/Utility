using Utility.DropBox.Models;

namespace Utility.DropBox.Models;

/// <summary>
/// One node of a browsed folder tree: a folder (with children) or a file (leaf).
/// Serializes to hierarchical JSON for structure + file metadata.
/// </summary>
public sealed record DropBoxNode(
    string Name,
    string PathDisplay,
    bool IsFolder,
    long SizeBytes,
    string SizeDisplay,
    DateTimeOffset? ClientModified,
    DateTimeOffset? ServerModified,
    string ModifiedDisplay,
    string? Id,
    string? Rev,
    string? ContentHash,
    bool IsDownloadable,
    int ChildCount,
    long TotalSizeBytes,
    long TotalFileCount,
    IReadOnlyList<DropBoxNode> Children)
{
    /// <summary>Builds a node from a listing entry; folders start with no children.</summary>
    public static DropBoxNode From(FileEntry entry) => new(
        entry.Name,
        entry.PathDisplay,
        entry.IsFolder,
        entry.SizeBytes,
        entry.SizeDisplay,
        entry.IsFolder ? null : entry.ClientModified,
        entry.IsFolder ? null : entry.ServerModified,
        entry.ModifiedDisplay,
        entry.Id,
        entry.Rev,
        entry.ContentHash,
        entry.IsDownloadable,
        ChildCount: 0,
        TotalSizeBytes: entry.SizeBytes,
        TotalFileCount: entry.IsFolder ? 0 : 1,
        Children: Array.Empty<DropBoxNode>());

    /// <summary>
    /// Returns this node with children and roll-up totals filled in. Totals are computed
    /// once here so the UI and the JSON do not each have to re-walk the subtree.
    /// </summary>
    public DropBoxNode WithChildren(IReadOnlyList<DropBoxNode> children)
    {
        long subtreeBytes = 0;
        long subtreeFiles = IsFolder ? 0 : 1;

        foreach (var child in children)
        {
            subtreeBytes += child.TotalSizeBytes;
            subtreeFiles += child.TotalFileCount;
        }

        return this with
        {
            Children = children,
            ChildCount = children.Count,
            TotalSizeBytes = subtreeBytes,
            TotalFileCount = subtreeFiles
        };
    }

    /// <summary>Depth-first walk over this node and its descendants.</summary>
    public IEnumerable<DropBoxNode> DescendantsAndSelf()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }
}