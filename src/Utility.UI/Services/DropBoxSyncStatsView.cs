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
    private DropBoxSyncStatsView(
        DropBoxSyncStats? stats,
        bool isLoading,
        string? error)
    {
        HasStats = stats is not null;
        IsLoading = isLoading;
        Error = error;

        TotalObjects = stats?.TotalObjects ?? 0;
        Folders = stats?.Folders ?? 0;
        Files = stats?.Files ?? 0;
        FilesWithDimensions = stats?.FilesWithDimensions ?? 0;
        TotalBytes = stats?.TotalBytes ?? 0;
        LastSyncedUtc = stats?.LastSyncedUtc;
        NotSyncedCount = stats?.NotSyncedCount ?? 0;

        NotSyncedGroups = new ObservableCollection<DropBoxNotSyncedGroupView>(
            stats?.NotSynced.Select(g => new DropBoxNotSyncedGroupView(g)) ?? []);
    }

    /// <summary>
    /// An empty set of figures: what the page shows before the first read.
    /// <para>
    /// This is NOT "everything is fine" - it is "nothing is known yet". The two are
    /// different states, and the page must not show the first as the second.
    /// </para>
    /// </summary>
    public static DropBoxSyncStatsView Empty { get; } = new(null, false, null);

    /// <summary>
    /// The state of a read that has not produced figures yet: still running, or failed.
    /// </summary>
    public static DropBoxSyncStatsView NotRead(bool isLoading, string? error) =>
        new(null, isLoading, error);

    /// <summary>Builds the display figures from what the server read.</summary>
    public static DropBoxSyncStatsView From(DropBoxSyncStats stats) => new(stats, false, null);

    /// <summary>True when the figures below are real, read from the database.</summary>
    public bool HasStats { get; }

    /// <summary>True while a read is in flight.</summary>
    public bool IsLoading { get; }

    /// <summary>The failure of the last read, or null.</summary>
    public string? Error { get; }

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

    public bool HasNotSynced => HasStats && NotSyncedCount > 0;

    public string NotSyncedHeadline =>
        $"{NotSyncedCount:N0} folder(s) not fully listed";

    /// <summary>
    /// True when the figures were read and everything in them is complete.
    /// <para>
    /// It is FALSE when the figures were never read - an unread index is not an index
    /// with nothing wrong with it. The page must not claim everything is fine while it
    /// knows nothing.
    /// </para>
    /// </summary>
    public bool ShowAllGood => HasStats && NotSyncedCount == 0;

    /// <summary>True when the figures were read and something in them is not complete.</summary>
    public bool ShowPending => HasStats && NotSyncedCount > 0;

    /// <summary>True when the figures are not known yet.</summary>
    public bool ShowNoStats => !HasStats;

    /// <summary>
    /// Why the figures are not known yet, in one line: still reading, failed, or waiting
    /// for the first read.
    /// </summary>
    public string NoStatsHeadline => Error is not null
        ? "The index could not be read"
        : IsLoading
            ? "Reading the index…"
            : "No figures read yet";

    /// <summary>The detail under that headline: the error, or what it is waiting for.</summary>
    public string NoStatsDetail => Error is not null
        ? Error
        : IsLoading
            ? "The figures are on their way."
            : "Waiting for the first read of ml.DropBoxNode.";

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
