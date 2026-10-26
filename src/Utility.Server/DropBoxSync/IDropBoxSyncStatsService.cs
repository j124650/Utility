using System.ComponentModel;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// One group of folders whose listing did not complete.
/// <para>
/// <see cref="Status"/> is the raw <c>ml.DropBoxNode.SyncStatus</c> value, or null when
/// the folder was written but never expanded. <see cref="Reason"/> is the raw
/// <c>SyncStatusReason</c>, or null when the sync recorded no detail.
/// </para>
/// </summary>
public sealed record DropBoxNotSyncedGroup(string? Status, string? Reason, int Count);

/// <summary>
/// The figures the dashboard shows about the synced Dropbox index.
/// <para>
/// Every value is an aggregate over <c>ml.DropBoxNode</c>. Nothing here is a row - the
/// index holds hundreds of thousands of them, and reading them to count them is the
/// difference between a dashboard that opens instantly and one that does not.
/// </para>
/// </summary>
public sealed record DropBoxSyncStats(
    int TotalObjects,
    int Folders,
    int Files,
    int FilesWithDimensions,
    long TotalBytes,
    DateTimeOffset? LastSyncedUtc,
    IReadOnlyList<DropBoxNotSyncedGroup> NotSynced)
{
    /// <summary>Folders whose contents were never confirmed: status is not "Ok".</summary>
    public int NotSyncedCount => NotSynced.Sum(g => g.Count);
}

/// <summary>
/// Reads the dashboard's figures from the synced Dropbox index.
/// <para>
/// A service method rather than a repository: the intent is "the dashboard's numbers",
/// and the SQL behind it is an implementation detail of this answer - one aggregate
/// scan, not a row-by-row read.
/// </para>
/// </summary>
public interface IDropBoxSyncStatsService : INotifyPropertyChanged
{
    /// <summary>The last figures read, or null before the first read.</summary>
    DropBoxSyncStats? Stats { get; }

    /// <summary>True while a read is in flight.</summary>
    bool IsLoading { get; }

    /// <summary>The failure of the last read, or null.</summary>
    string? Error { get; }

    /// <summary>
    /// Reads the figures. Never throws: a failure is reported through <see cref="Error"/>.
    /// </summary>
    Task LoadAsync(CancellationToken ct = default);
}
