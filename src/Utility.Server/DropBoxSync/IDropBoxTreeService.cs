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
/// </summary>
public sealed class DropBoxTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

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
        string syncStatus)
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsFolder { get; }

    public int ChildCount { get; }

    public long SizeBytes { get; }

    public int? WidthPixels { get; }

    public int? HeightPixels { get; }

    public string? MediaInfoTag { get; }

    public DateTimeOffset? ServerModified { get; }

    public string SyncStatus { get; }

    public IReadOnlyList<DropBoxTreeNode> Children { get; init; } = [];

    /// <summary>Two-way bound to the TreeViewItem's IsExpanded.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Two-way bound to the TreeViewItem's IsSelected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Folders sort first, then alphabetically - mirrors the sync's own ordering.</summary>
    public string SizeDisplay => IsFolder
        ? (ChildCount > 0 ? $"{ChildCount} item{(ChildCount == 1 ? "" : "s")}" : "empty")
        : FormatSize(SizeBytes);

    public string DimensionsDisplay =>
        WidthPixels is null || HeightPixels is null ? "" : $"{WidthPixels}×{HeightPixels}";

    public string ModifiedDisplay =>
        ServerModified is null ? "" : ServerModified.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>A folder that was never fully listed - the sweep must not descend past it.</summary>
    public bool IsUnverified =>
        IsFolder && !string.Equals(SyncStatus, DropBoxSyncStatus.Ok, StringComparison.Ordinal);

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
    /// Reads the ALREADY-SYNCED hierarchy from ml.DropBoxNode. No Dropbox call is made:
    /// this renders what is in the table, which is the point of syncing it.
    /// </summary>
    /// <returns>Root nodes of the synced forest, plus the summary figures.</returns>
    Task<(IReadOnlyList<DropBoxTreeNode> Roots, DropBoxTreeSummary Summary)> GetSyncedTreeAsync(
        string? namespaceId = null,
        int maxNodes = 5000,
        CancellationToken ct = default);
}