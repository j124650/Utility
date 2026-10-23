using System.Diagnostics;

namespace Utility.Server.DropBoxSync;

/// <summary>Progress emitted while a sync runs.</summary>
/// <param name="CurrentPath">Folder currently being listed.</param>
/// <param name="FoldersSeen">Folders stored so far in this run.</param>
/// <param name="FilesSeen">Files stored so far in this run.</param>
/// <param name="RowsWritten">Rows committed so far.</param>
/// <param name="Elapsed">Wall-clock time since the run started.</param>
/// <param name="Sweeping">True once the walk is done and the sweep is under way.</param>
/// <param name="Depth">1-based depth of the folder currently being listed.</param>
public readonly record struct DropBoxSyncProgress(
    string CurrentPath,
    int FoldersSeen,
    int FilesSeen,
    int RowsWritten,
    TimeSpan Elapsed,
    bool Sweeping,
    int Depth = 0);

/// <summary>Outcome of a completed (or cancelled) sync.</summary>
/// <param name="Completed">
/// False when the walk was cancelled or threw. The sweep is skipped in that case, so
/// previously synced rows survive untouched.
/// </param>
/// <param name="DepthReached">Deepest level the walk actually listed.</param>
public readonly record struct DropBoxSyncResult(
    bool Completed,
    bool Swept,
    int FoldersWritten,
    int FilesWritten,
    int RowsDeleted,
    int DepthReached,
    TimeSpan Duration,
    string? Error)
{
    public int TotalWritten => FoldersWritten + FilesWritten;
}

public interface IDropBoxSyncService
{
    /// <summary>
    /// Walks the Dropbox subtree under <paramref name="startPath"/> and stores its
    /// structure (folders, files and metadata) in <c>ml.DropBoxNode</c>. No file content
    /// is downloaded.
    /// <para>
    /// A folder's contents are stored and committed before any of its children are
    /// expanded; sibling folders at the same level then expand in parallel, bounded by
    /// <paramref name="maxConcurrency"/>. Listing a single folder stays serial because
    /// Dropbox pages by an opaque cursor.
    /// </para>
    /// <para>
    /// Safe to run repeatedly: rows are upserted on their natural key, so an unchanged
    /// subtree is a no-op. Rows that vanished from Dropbox are removed by a sweep scoped
    /// to <paramref name="startPath"/> — never outside it, so syncing one subtree can
    /// never delete another.
    /// </para>
    /// <para>
    /// The sweep runs ONLY when the walk completed, and never beneath a folder whose
    /// SyncStatus is not <c>Ok</c>: a skipped or failed folder's children are unverified,
    /// not absent.
    /// </para>
    /// </summary>
    Task<DropBoxSyncResult> SyncAsync(
        string startPath,
        string? namespaceId,
        IProgress<DropBoxSyncProgress>? progress = null,
        int batchSize = 50,
        int maxDepth = 0,
        int maxConcurrency = 6,
        CancellationToken ct = default);
}