using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// One node of the synced hierarchy, shaped for a WPF TreeView.
/// <para>
/// A class rather than a record because the tree needs two-way change notification for
/// <see cref="IsExpanded"/>. A record is immutable and cannot raise PropertyChanged, and
/// the usual "subscribe and forward" workaround leaks a handler on every set and
/// re-enters the very event it is trying to raise.
/// </para>
/// <para>
/// <b>Nodes can be synthetic.</b> The hierarchy is derived from the full path, so a folder
/// that was never synced still appears as an ancestor - otherwise a subtree synced from
/// a deep start would render as a floating root with no context above it. Synthetic nodes
/// have <see cref="IsSynced"/> false and no row of their own.
/// </para>
/// </summary>
public sealed class DropBoxTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public DropBoxTreeNode(
        long id,
        string name,
        string fullPath,
        bool isFolder,
        int childCount,
        long sizeBytes,
        int? widthPixels,
        int? heightPixels,
        string? mediaInfoTag,
        DateTimeOffset? serverModified,
        string syncStatus,
        bool isSynced = true)
    {
        Id = id;
        Name = name;
        FullPath = fullPath;
        IsFolder = isFolder;
        ChildCount = childCount;
        SizeBytes = sizeBytes;
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        MediaInfoTag = mediaInfoTag;
        ServerModified = serverModified;
        SyncStatus = syncStatus;
        IsSynced = isSynced;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Row id, or 0 for a synthetic folder derived from the path alone.</summary>
    public long Id { get; }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsFolder { get; }

    /// <summary>False for a folder that exists only as an ancestor in a synced path.</summary>
    public bool IsSynced { get; }

    public int ChildCount { get; }

    public long SizeBytes { get; }

    public int? WidthPixels { get; }

    public int? HeightPixels { get; }

    public string? MediaInfoTag { get; }

    public DateTimeOffset? ServerModified { get; }

    public string SyncStatus { get; }

    /// <summary>
    /// Child nodes.
    /// <para>
    /// Named Children and typed as a List so the TreeView's ItemsControl discovers it
    /// automatically. An IReadOnlyList here is silently ignored - the node renders as a
    /// leaf with no expander, and nothing in the data or the model says why.
    /// </para>
    /// </summary>
    public List<DropBoxTreeNode> Children { get; init; } = [];

    /// <summary>Two-way bound to the TreeViewItem's IsExpanded.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Folders sort first, then alphabetically - mirrors the sync's own ordering.</summary>
    public string SizeDisplay
    {
        get
        {
            if (!IsSynced)
            {
                // The row renders its own "not synced" marker column; don't say it twice.
                return "";
            }

            return IsFolder
                ? (ChildCount > 0 ? $"{ChildCount} item{(ChildCount == 1 ? "" : "s")}" : "empty")
                : FormatSize(SizeBytes);
        }
    }

    public string DimensionsDisplay =>
        WidthPixels is null || HeightPixels is null ? "" : $"{WidthPixels}×{HeightPixels}";

    public string ModifiedDisplay =>
        ServerModified is null ? "" : ServerModified.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>A folder that was never fully listed - the sweep must not descend past it.</summary>
    public bool IsUnverified =>
        IsFolder && IsSynced && !string.Equals(SyncStatus, DropBoxSyncStatus.Ok, StringComparison.Ordinal);

    /// <summary>True for image/video rows whose dimensions are known.</summary>
    public bool HasDimensions => WidthPixels is not null && HeightPixels is not null;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824)
        {
            return $"{bytes / 1_073_741_824.0:0.##} GB";
        }

        if (bytes >= 1_048_576)
        {
            return $"{bytes / 1_048_576.0:0.##} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0.##} KB";
        }

        return $"{bytes} B";
    }
}

/// <summary>Whole-tree figures for the header strip.</summary>
public sealed record DropBoxTreeSummary(
    int Folders,
    int Files,
    long TotalBytes,
    int WithDimensions,
    int UnverifiedFolders,
    DateTimeOffset? LastSyncedUtc);

public interface IDropBoxTreeService
{
    /// <summary>
    /// Builds the hierarchy from the ALREADY-SYNCED rows in ml.DropBoxNode, deriving
    /// structure from the full path. No Dropbox call is made.
    /// <para>
    /// Deriving from the path rather than following ParentId means a subtree synced from
    /// a deep start still renders with its full ancestry above it, instead of appearing as
    /// a floating root.
    /// </para>
    /// </summary>
    /// <returns>Root nodes, plus summary figures.</returns>
    Task<(IReadOnlyList<DropBoxTreeNode> Roots, DropBoxTreeSummary Summary)> GetSyncedTreeAsync(
        string? namespaceId = null,
        CancellationToken ct = default);
}