namespace Utility.Server.DropBoxSync;

/// <summary>
/// The sync, in TWO PHASES: list, then enrich.
/// </summary>
/// <remarks>
/// <para>
/// A walk that fetched each file's metadata as it went spent about 97% of its time on
/// one call per file — and that call buys ONE field, the file's dimensions. Measured on
/// this account: 19,851 folder listings take about 5.7 minutes, and 399,357 per-file
/// metadata calls take about 1.9 hours.
/// </para>
/// <para>
/// The two phases split the work, and the database holds the handoff between them.
/// </para>
/// <list type="number">
/// <item>
/// <b>LIST</b> — the walk, with no per-file call at all. Every row is written from the
/// listing. A file whose content hash is UNCHANGED keeps its dimensions, because the
/// content is the same, so they are still true. A file whose content hash CHANGED has
/// its dimensions cleared and is marked <c>'pending'</c>.
/// </item>
/// <item>
/// <b>ENRICH</b> — the metadata for the rows marked <c>'pending'</c>, and only those. The
/// work list is <c>WHERE IsFolder = 0 AND MediaInfoTag = 'pending'</c>, a set the schema
/// already defines: <c>'pending'</c> already means "the dimensions may be available on a
/// re-read".
/// </item>
/// </list>
/// <para>
/// The marking is SELF-HEALING: a row left <c>'pending'</c> by a run whose enrichment
/// failed keeps its tag on the next listing, so the next enrichment picks it up again.
/// </para>
/// <para>
/// The two phases can be run together or apart, which is the point: the listing is
/// cheap and can be repeated, and the enrichment is only ever as big as the set of files
/// that actually changed.
/// </para>
/// </remarks>
public interface IDropBoxSyncTwoPhaseService
{
    /// <summary>
    /// THE WHOLE THING, from a path: the structure is synced, and then whatever the sync
    /// marked is enriched.
    /// </summary>
    /// <param name="startPath">Where the sync starts.</param>
    /// <param name="namespaceId">The team namespace, or null for the personal space.</param>
    /// <param name="listProgress">The listing phase's progress.</param>
    /// <param name="enrichProgress">The enrichment phase's progress.</param>
    /// <param name="ct">Cancelling ends the sync.</param>
    Task<DropBoxTwoPhaseResult> SyncFrom(
        string startPath,
        string? namespaceId = null,
        IProgress<DropBoxSyncProgress>? listProgress = null,
        IProgress<DropBoxTwoPhaseEnrichProgress>? enrichProgress = null,
        CancellationToken ct = default);

    /// <summary>
    /// PHASE 1 — walks the tree from a path and writes every row, with no per-file
    /// metadata call.
    /// </summary>
    /// <param name="startPath">Where the walk starts.</param>
    /// <param name="namespaceId">The team namespace, or null for the personal space.</param>
    /// <param name="progress">The walk's progress.</param>
    /// <param name="maxDepth">How many levels to walk. Zero means all of them.</param>
    /// <param name="ct">Cancelling ends the walk.</param>
    Task<DropBoxSyncResult> SyncStructureFrom(
        string startPath,
        string? namespaceId = null,
        IProgress<DropBoxSyncProgress>? progress = null,
        int maxDepth = 0,
        CancellationToken ct = default);

    /// <summary>
    /// PHASE 1, continued — carries on from the frontier, so a walk that did not finish
    /// picks up where it stopped.
    /// </summary>
    Task<DropBoxSyncResult> ContinueSyncStructureFrom(
        IProgress<DropBoxSyncProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// PHASE 2 — fetches the metadata for the rows marked <c>'pending'</c>, and only those.
    /// </summary>
    /// <param name="namespaceId">The team namespace, or null for the personal space.</param>
    /// <param name="pathPrefix">
    /// Restricts the work to the rows under this path. Null means the whole namespace.
    /// </param>
    /// <param name="progress">The pass's progress.</param>
    /// <param name="ct">Cancelling ends the pass.</param>
    Task<DropBoxTwoPhaseEnrichResult> EnrichAsync(
        string? namespaceId = null,
        string? pathPrefix = null,
        IProgress<DropBoxTwoPhaseEnrichProgress>? progress = null,
        CancellationToken ct = default);

}

/// <summary>Progress of the enrichment pass.</summary>
/// <param name="Done">Files enriched so far.</param>
/// <param name="Total">Files the work list held when the pass started.</param>
/// <param name="CurrentPath">The file being read.</param>
/// <param name="InFlight">
/// How many Dropbox calls this pass has in flight right now.
/// <para>
/// The enrichment has its OWN gate - it does not share the walk's - so the walk's
/// counters say nothing while this pass runs. Without these the busy screen would
/// report the walk's idle numbers over an enrichment that is working.
/// </para>
/// </param>
/// <param name="Width">How many Dropbox calls this pass may have in flight at once.</param>
public readonly record struct DropBoxTwoPhaseEnrichProgress(
    int Done,
    int Total,
    string CurrentPath,
    int InFlight,
    int Width);

/// <summary>Outcome of the enrichment pass.</summary>
/// <param name="Completed">False when the pass was cancelled or threw.</param>
/// <param name="Pending">The rows the work list held.</param>
/// <param name="Enriched">The rows whose metadata was fetched and written.</param>
/// <param name="Failed">The rows whose metadata could not be fetched.</param>
/// <param name="StillPending">
/// The rows still marked <c>'pending'</c> afterwards — the files whose media info
/// Dropbox deferred, which are asked again next time.
/// </param>
/// <param name="Duration">How long the pass took.</param>
/// <param name="Error">The failure, when there was one.</param>
public readonly record struct DropBoxTwoPhaseEnrichResult(
    bool Completed,
    int Pending,
    int Enriched,
    int Failed,
    int StillPending,
    TimeSpan Duration,
    string? Error);

/// <summary>Outcome of both phases.</summary>
/// <param name="Completed">False when either phase was cancelled or threw.</param>
/// <param name="List">The listing phase's outcome.</param>
/// <param name="Enrich">The enrichment phase's outcome.</param>
/// <param name="Duration">How long both phases took.</param>
/// <param name="Error">The failure, when there was one.</param>
public readonly record struct DropBoxTwoPhaseResult(
    bool Completed,
    DropBoxSyncResult List,
    DropBoxTwoPhaseEnrichResult Enrich,
    TimeSpan Duration,
    string? Error);
