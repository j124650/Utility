using System.Collections.ObjectModel;
using Utility.Server.DropBoxSync;

namespace Utility.UI.Services;

/// <summary>
/// The dashboard's figures, shaped for display.
/// <para>
/// The server hands back raw values; this turns them into the strings the page shows
/// and keeps the not-synced groups as a list the XAML can bind to directly.
/// </para>
/// </summary>
public sealed class DropBoxSyncStatsView
{
    private DropBoxSyncStatsView(DropBoxSyncStats stats)
    {
        TotalObjects = stats.TotalObjects;
        Folders = stats.Folders;
        Files = stats.Files;
        FilesWithDimensions = stats.FilesWithDimensions;
        TotalBytes = stats.TotalBytes;
        LastSyncedUtc = stats.LastSyncedUtc;
        NotSyncedCount = stats.NotSyncedCount;

        NotSyncedGroups = new ObservableCollection<DropBoxNotSyncedGroupView>(
            stats.NotSynced.Select(g => new DropBoxNotSyncedGroupView(g)));
    }

    /// <summary>An empty set of figures: what the page shows before the first read.</summary>
    public static DropBoxSyncStatsView Empty { get; } =
        new DropBoxSyncStatsView(new DropBoxSyncStats(0, 0, 0, 0, 0, null, []));

    /// <summary>Builds the display figures from what the server read.</summary>
    public static DropBoxSyncStatsView From(DropBoxSyncStats stats) => new(stats);

    public int TotalObjects { get; }

    public int Folders { get; }

    public int Files { get; }

    public int FilesWithDimensions { get; }

    public long TotalBytes { get; }

    public DateTimeOffset? LastSyncedUtc { get; }

    public int NotSyncedCount { get; }

    public ObservableCollection<DropBoxNotSyncedGroupView> NotSyncedGroups { get; }

    public string TotalObjectsDisplay => TotalObjects.ToString("N0");

    public string FoldersDisplay => Folders.ToString("N0");

    public string FilesDisplay => Files.ToString("N0");

    public string FilesWithDimensionsDisplay => FilesWithDimensions.ToString("N0");

    public string TotalBytesDisplay => FormatSize(TotalBytes);

    public string LastSyncedDisplay => LastSyncedUtc is null
        ? "never synced"
        : LastSyncedUtc.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public bool HasNotSynced => NotSyncedCount > 0;

    public string NotSyncedHeadline =>
        $"{NotSyncedCount:N0} folder(s) not fully listed";

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

/// <summary>One group of folders whose listing did not complete, shaped for display.</summary>
public sealed class DropBoxNotSyncedGroupView
{
    public DropBoxNotSyncedGroupView(DropBoxNotSyncedGroup group)
    {
        Status = group.Status;
        Reason = group.Reason;
        Count = group.Count;
    }

    /// <summary>The raw SyncStatus, or null when the folder was never expanded.</summary>
    public string? Status { get; }

    /// <summary>The raw SyncStatusReason, or null when the sync recorded none.</summary>
    public string? Reason { get; }

    public int Count { get; }

    /// <summary>
    /// The status in words. A NULL status is not an error - it is a folder the walk
    /// recorded but never expanded.
    /// </summary>
    public string StatusDisplay => Status switch
    {
        null => "never expanded",
        "Ok" => "listed",
        "Skipped" => "skipped",
        "Failed" => "failed",
        "Partial" => "partial",
        _ => Status
    };

    public string ReasonDisplay => Reason is null ? "" : Reason;

    public string CountDisplay => Count.ToString("N0");
}
