using System.ComponentModel;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// One level of the walk: how many folders the level has, how many are done, and how
/// many records it has written.
/// <para>
/// A level's folder count is known when the level opens - the folders at level N were
/// all discovered at level N-1, which finished first. So "x of y" is an exact fraction
/// from the first folder onward, with no estimate anywhere.
/// </para>
/// </summary>
public sealed record DropBoxSyncLevel(int Depth, int FoldersTotal, int FoldersListed, int Records)
{
    /// <summary>Column heading, e.g. "level-3".</summary>
    public string DepthDisplay => $"level-{Depth}";

    /// <summary>Folders listed of folders at this level, e.g. "340 of 340".</summary>
    public string Fraction => $"{FoldersListed:N0} of {FoldersTotal:N0}";

    /// <summary>Records written at this level so far.</summary>
    public string RecordsDisplay => $"{Records:N0} rec.";
}

/// <summary>Progress emitted while a sync runs.</summary>
/// <param name="CurrentPath">Folder currently being listed.</param>
/// <param name="FoldersSeen">Folders stored so far in this run.</param>
/// <param name="FilesSeen">Files stored so far in this run.</param>
/// <param name="RowsWritten">Rows committed so far.</param>
/// <param name="NotListed">Folders that could not be listed (404 or no access).</param>
/// <param name="Elapsed">Wall-clock time since the run started.</param>
/// <param name="Depth">1-based depth of the folder currently being listed.</param>
public readonly record struct DropBoxSyncProgress(
    string CurrentPath,
    int FoldersSeen,
    int FilesSeen,
    int RowsWritten,
    int NotListed,
    TimeSpan Elapsed,
    int Depth = 0);

/// <summary>Outcome of a completed (or cancelled) sync.</summary>
/// <param name="Completed">False when the walk was cancelled or threw.</param>
/// <param name="DepthReached">Deepest level the walk actually listed.</param>
public readonly record struct DropBoxSyncResult(
    bool Completed,
    int FoldersWritten,
    int FilesWritten,
    int DepthReached,
    TimeSpan Duration,
    string? Error)
{
    public int TotalWritten => FoldersWritten + FilesWritten;
}

public interface IDropBoxSyncService : INotifyPropertyChanged
{
    /// <summary>True while a walk is running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// True while a walk is running, or while a finished run is waiting to be
    /// acknowledged by the UI.
    /// <para>
    /// The busy screen stays up for the second case too, so the outcome can be read
    /// before it is dismissed.
    /// </para>
    /// </summary>
    bool IsBusy { get; }

    /// <summary>True when a run has finished and its result has not been acknowledged yet.</summary>
    bool HasPendingResult { get; }

    /// <summary>
    /// True while a stop has been requested but the walk is still winding down.
    /// <para>
    /// The walk cannot be killed outright: the Dropbox SDK's calls do not take a
    /// cancellation token, so a request that has already been sent runs to completion.
    /// This is true for that window, and it is why the busy screen shows how many
    /// downloaders are still running instead of offering a Close button.
    /// </para>
    /// </summary>
    bool IsStopping { get; }

    /// <summary>
    /// True while the walk is running and has not been asked to stop. The Stop button
    /// is bound to this, so it disappears the moment the stop is requested.
    /// </summary>
    bool CanStop { get; }

    /// <summary>
    /// Dropbox calls still in flight. Every request that has been sent but not yet
    /// returned counts as one, so the number falls to zero as the walk winds down.
    /// </summary>
    int ActiveDownloaders { get; }

    /// <summary>
    /// How many Dropbox requests the current run may have in flight at once - the
    /// request gate's width. A run that looks stalled may simply be waiting for its
    /// permits.
    /// </summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// The walk's own traffic, measured: requests per minute, how many were refused,
    /// and the mean latency.
    /// </summary>
    DropboxRateSnapshot RateSnapshot { get; }

    /// <summary>The folder currently being listed.</summary>
    string CurrentPath { get; }

    /// <summary>
    /// One entry per level of the walk, in depth order. Replaced wholesale on every
    /// update, so a binding re-reads it rather than diffing it.
    /// </summary>
    IReadOnlyList<DropBoxSyncLevel> Levels { get; }

    /// <summary>Folders stored so far in this run.</summary>
    int FoldersSeen { get; }

    /// <summary>Files stored so far in this run.</summary>
    int FilesSeen { get; }

    /// <summary>Rows committed so far.</summary>
    int RowsWritten { get; }

    /// <summary>Folders that could not be listed.</summary>
    int NotListed { get; }

    /// <summary>Non-empty only when some folder could not be listed.</summary>
    string NotListedDisplay { get; }

    /// <summary>Wall-clock time since the run started.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Elapsed time as "2m 04s".</summary>
    string ElapsedDisplay { get; }

    /// <summary>Committed rows per second.</summary>
    double RowsPerSecond { get; }

    /// <summary>The outcome of the last run, or null if none has finished.</summary>
    DropBoxSyncResult? LastResult { get; }

    /// <summary>The failure detail of the last run, or null.</summary>
    string? Error { get; }

    /// <summary>
    /// The headline of the busy screen: what the run is doing right now, or how it ended.
    /// </summary>
    string Headline { get; }

    /// <summary>
    /// The one-line summary of a finished run. Empty while the run is still going.
    /// </summary>
    string OutcomeLine { get; }

    /// <summary>Requests cancellation of the running walk.</summary>
    void Cancel();

    /// <summary>
    /// Marks a finished run's result as seen, so the busy screen can be dismissed.
    /// </summary>
    void AcknowledgeResult();

    /// <summary>
    /// Walks the Dropbox subtree under <paramref name="startPath"/> and stores its
    /// structure (folders, files and metadata) in <c>ml.DropBoxNode</c>. No file content
    /// is downloaded.
    /// <para>
    /// The walk goes from <paramref name="startPath"/> down to the leaves: it is
    /// level-by-level, all folders of a level expand in parallel before the next level
    /// starts, and there is no depth limit.
    /// </para>
    /// <para>
    /// <paramref name="namespaceId"/> is the team namespace the walk is scoped to; when
    /// omitted it is resolved from the account. Team folders are invisible from the
    /// personal space, so the walk must be scoped to the team root.
    /// </para>
    /// <para>
    /// <paramref name="enrichFileMetadata"/> fetches each FILE's own metadata, which is
    /// the only way to get image dimensions: Dropbox ignores <c>include_media_info</c> on
    /// <c>files/list_folder</c> (verified across recursive, non-recursive and the personal
    /// namespace) but honours it on <c>files/get_metadata</c>. That costs one extra
    /// request per file, so it roughly doubles a walk - set it false for a fast
    /// structure-only sync.
    /// </para>
    /// </summary>
    Task<DropBoxSyncResult> SyncAsync(
        string startPath,
        string? namespaceId = null,
        IProgress<DropBoxSyncProgress>? progress = null,
        int batchSize = 50,
        int maxDepth = 0,
        int maxConcurrency = 6,
        bool enrichFileMetadata = true,
        CancellationToken ct = default);

    /// <summary>
    /// Continues a sync that did not finish, and carries it down to the leaves.
    /// <para>
    /// A walk that stopped partway leaves folders in <c>ml.DropBoxNode</c> whose
    /// contents were never listed: their rows exist because their parents listed them,
    /// but nothing below them was ever discovered. This finds those folders in the
    /// index and lists them, then keeps going down into whatever they contain - so the
    /// tree ends up complete instead of stopping at the frontier where the walk broke.
    /// </para>
    /// <para>
    /// The part that is already complete is NOT listed again: only the folders whose
    /// contents are missing are visited, and everything below them that is new.
    /// </para>
    /// </summary>
    Task<DropBoxSyncResult> ContinueAsync(
        IProgress<DropBoxSyncProgress>? progress = null,
        int batchSize = 50,
        int maxDepth = 0,
        int maxConcurrency = 6,
        bool enrichFileMetadata = true,
        CancellationToken ct = default);
}