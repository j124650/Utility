using System.Text.Json;
using System.Text.Json.Serialization;

namespace Utility.DropBox.Models;

/// <summary>
/// Result of a folder-tree walk: the hierarchical nodes plus roll-up totals.
/// Serialises to JSON with <see cref="ToJson"/>.
/// </summary>
public sealed record DropBoxFolderTree(
    string RootPath,
    string NamespaceId,
    int MaxDepth,
    DateTimeOffset StartedUtc,
    long DurationMs,
    int FolderCount,
    long FileCount,
    long TotalSizeBytes,
    IReadOnlyList<DropBoxNode> Nodes)
{
    public string TotalSizeDisplay => FormatSize(TotalSizeBytes);

    /// <summary>
    /// Hierarchical JSON: folders nest via <c>children</c>, each file carrying its
    /// metadata. camelCase naming, nulls omitted to keep large trees compact.
    /// </summary>
    public string ToJson(bool indented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        return JsonSerializer.Serialize(this, options);
    }

    /// <summary>Every node in the tree, depth-first, including the top level.</summary>
    public IEnumerable<DropBoxNode> AllNodes() => Nodes.SelectMany(n => n.DescendantsAndSelf());

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}