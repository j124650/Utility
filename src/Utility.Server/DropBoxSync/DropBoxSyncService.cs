using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Utility.DropBox.Models;
using Utility.DropBox.Services;
// Aliased: Utility.DropBox.Models also declares a DropBoxNode (the in-memory tree model).
// Every reference here MUST use this alias. Bare `DropBoxNode` silently binds to the tree
// model instead of the entity, and ChangeTracker.Entries<DropBoxNode>() then asks the
// context for a type that is not in its model - which finds nothing and makes every
// flush a silent no-op.
using DropBoxNodeEntity = Utility.Server.DropBoxSync.Entities.DropBoxNode;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// Streams a Dropbox subtree into <c>ml.DropBoxNode</c>, and publishes its own live state
/// while it does.
/// <para>
/// <b>Walk shape.</b> A folder's contents are stored and committed BEFORE any of its
/// children are expanded, then the children expand in parallel:
/// <code>
/// upsert C          -> C's row committed, C.Id known
/// list C            -> every entry in C stored (ParentId = C.Id), committed
///   expand child1   -> parallel
///   expand child2
///   expand childN
/// </code>
/// </para>
/// <para>
/// Listing ONE folder is always serial - Dropbox paginates by opaque cursor and page N
/// is unobtainable without page N-1. So parallelism sits above a folder, never inside
/// one.
/// </para>
/// <para>
/// <b>ParentId is assigned, never derived.</b> Because a parent row is committed before
/// its children are inserted, each child's ParentId is simply the parent folder's known
/// Id. No path arithmetic is involved, so there is no second "link parents" pass.
/// </para>
/// <para>
/// <b>Nothing is ever deleted.</b> A run only writes rows. A file that disappears from
/// Dropbox leaves its row behind - the table reflects what was last SEEN, and grows.
/// </para>
/// <para>
/// <b>The service is the progress source.</b> It owns the run state - the level table, the
/// counters, whether it is running - and raises PropertyChanged for it. The UI binds to
/// this object directly; no second service copies the progress record.
/// </para>
/// </summary>
public sealed class DropBoxSyncService(
    IDropBoxDataService dropBox,
    IDbContextFactory<DropBoxDbContext> contextFactory,
    ILogger<DropBoxSyncService> logger) : IDropBoxSyncService
{
    private readonly IDbContextFactory<DropBoxDbContext> _contextFactory = contextFactory;

    /// <summary>Guards against a second walk while one is in flight.</summary>
    private int _running;

    /// <summary>The run's cancellation source, so <see cref="Cancel"/> can stop it.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// The context the state is published on. Captured when the run starts, which is the
    /// UI thread; the walk itself runs on the thread pool.
    /// </summary>
    private SynchronizationContext? _publishContext;

    /// <summary>Per-level counters of the run in progress, keyed by depth.</summary>
    private readonly ConcurrentDictionary<int, LevelProgress> _levels = new();

    /// <summary>
    /// The counters of the run in progress, and the clock it is measured against.
    /// <para>
    /// Held as references rather than copied field by field: the walk's workers update
    /// the counters concurrently, and copying them out one at a time let an older value
    /// land after a newer one, so the display could go backwards.
    /// </para>
    /// </summary>
    private SyncCounts? _activeCounts;
    private Stopwatch? _activeClock;

    /// <summary>The folder currently being listed.</summary>
    private string _currentPath = string.Empty;

    // The state the next publish carries. Written by the walk thread, applied on the
    // publishing context.
    private volatile bool _pendingRunning;
    private volatile bool _pendingHasResult;
    private DropBoxSyncResult? _pendingLastResult;
    private string? _pendingError;

    /// <summary>
    /// Counts the runs. A snapshot carries the number of the run that produced it, so a
    /// snapshot left over from a previous run is discarded instead of overwriting the
    /// state of the current one.
    /// </summary>
    private int _runGeneration;

    /// <summary>Published state. Written only on the publishing context.</summary>
    private bool _isRunning;
    private bool _hasPendingResult;
    private DropBoxSyncResult? _lastResult;
    private string? _error;
    private IReadOnlyList<DropBoxSyncLevel> _levelView = [];

    /// <summary>
    /// Throttles how often the state is published to the UI.
    /// <para>
    /// A long, read atomically: the walk's workers publish concurrently, and a torn
    /// DateTime read would make the throttle fire early (or not at all).
    /// </para>
    /// </summary>
    private long _lastPublishTicks;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while a walk is running.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// True while a walk is running, or while a finished run is waiting to be
    /// acknowledged by the UI.
    /// <para>
    /// The busy screen stays up for the second case too, so the outcome can be read
    /// before it is dismissed.
    /// </para>
    /// </summary>
    public bool IsBusy => _isRunning || _hasPendingResult;

    /// <summary>True when a run has finished and its result has not been acknowledged yet.</summary>
    public bool HasPendingResult => _hasPendingResult;

    /// <summary>The folder currently being listed.</summary>
    public string CurrentPath => Volatile.Read(ref _currentPath);

    /// <summary>
    /// One entry per level of the walk, in depth order.
    /// <para>
    /// The list is REPLACED on every publish, not mutated in place: a binding re-reads
    /// the whole list rather than diffing it, and there are only a handful of levels.
    /// </para>
    /// </summary>
    public IReadOnlyList<DropBoxSyncLevel> Levels => _levelView;

    /// <summary>Folders stored so far in this run.</summary>
    public int FoldersSeen => Volatile.Read(ref _activeCounts)?.Folders ?? 0;

    /// <summary>Files stored so far in this run.</summary>
    public int FilesSeen => Volatile.Read(ref _activeCounts)?.Files ?? 0;

    /// <summary>Rows committed so far.</summary>
    public int RowsWritten => Volatile.Read(ref _activeCounts)?.Written ?? 0;

    /// <summary>Folders that could not be listed.</summary>
    public int NotListed => Volatile.Read(ref _activeCounts)?.NotOk ?? 0;

    /// <summary>Wall-clock time since the run started.</summary>
    public TimeSpan Elapsed => Volatile.Read(ref _activeClock)?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Elapsed time as "2m 04s".</summary>
    public string ElapsedDisplay
    {
        get
        {
            var e = Elapsed;
            return e.TotalHours >= 1
                ? $"{(int)e.TotalHours}h {e.Minutes:00}m"
                : $"{e.Minutes:00}m {e.Seconds:00}s";
        }
    }

    /// <summary>Committed rows per second.</summary>
    public double RowsPerSecond
    {
        get
        {
            var seconds = Elapsed.TotalSeconds;
            return seconds > 0.5 ? RowsWritten / seconds : 0;
        }
    }

    /// <summary>The outcome of the last run, or null if none has finished.</summary>
    public DropBoxSyncResult? LastResult => _lastResult;

    /// <summary>The failure detail of the last run, or null.</summary>
    public string? Error => _error;

    /// <summary>Non-empty only when some folder could not be listed.</summary>
    public string NotListedDisplay =>
        NotListed > 0 ? $"{NotListed:N0} folder(s) could not be listed" : string.Empty;

    /// <summary>
    /// The headline of the busy screen: what the run is doing right now, or how it ended.
    /// </summary>
    public string Headline
    {
        get
        {
            if (_isRunning)
            {
                return "Syncing from Dropbox";
            }

            if (_error is not null)
            {
                return _error == "Cancelled." ? "Sync stopped" : "Sync failed";
            }

            return "Synced from Dropbox";
        }
    }

    /// <summary>
    /// The one-line summary of a finished run. Empty while the run is still going.
    /// </summary>
    public string OutcomeLine
    {
        get
        {
            var result = _lastResult;
            if (result is null || !result.Value.Completed)
            {
                return string.Empty;
            }

            var r = result.Value;
            return $"✓ {r.TotalWritten:N0} rows written — {r.FoldersWritten:N0} folders, " +
                   $"{r.FilesWritten:N0} files — in {FormatDuration(r.Duration)}";
        }
    }

    /// <summary>A duration as "2m 04s" or "1h 05m".</summary>
    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes:00}m"
            : $"{value.Minutes:00}m {value.Seconds:00}s";

    public async Task<DropBoxSyncResult> SyncAsync(
        string startPath,
        string? namespaceId = null,
        IProgress<DropBoxSyncProgress>? progress = null,
        int batchSize = 500,
        int maxDepth = 0,
        int maxConcurrency = 6,
        bool enrichFileMetadata = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        // One walk at a time. The flag is cleared in the finally block.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new DropBoxSyncResult(false, 0, 0, 0, TimeSpan.Zero, "A sync is already running.");
        }

        var sw = Stopwatch.StartNew();
        var runId = Guid.NewGuid();
        var rootPath = startPath.TrimEnd('/');

        // 0 = unlimited depth; the walk goes from the root folder down to the leaves.
        var unlimited = maxDepth <= 0;
        var counts = new SyncCounts();
        var level = 1;

        // Captured here, on the caller's thread - the UI thread - so the state can be
        // published there. The walk itself continues on the thread pool (every await
        // below uses ConfigureAwait(false)), so a long traversal never blocks the UI.
        _publishContext = SynchronizationContext.Current;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = linked;
        var token = linked.Token;

        ResetRunState();
        Volatile.Write(ref _activeCounts, counts);
        Volatile.Write(ref _activeClock, sw);
        _pendingRunning = true;
        _pendingHasResult = false;
        _isRunning = true;
        _hasPendingResult = false;
        CurrentPathSet(rootPath);
        Publish(force: true);

        try
        {
            // Team folders are invisible from the personal space, so the walk must be
            // scoped to the team namespace. Its id belongs to the account, so it is
            // resolved here rather than pinned in this source file.
            var ns = namespaceId;
            if (string.IsNullOrWhiteSpace(ns))
            {
                var namespaces = await dropBox.GetNamespacesAsync(token).ConfigureAwait(false);
                var team = namespaces.FirstOrDefault(n => !n.IsPersonal);
                if (team is null)
                {
                    throw new InvalidOperationException(
                        "This account has no team namespace, and the personal space cannot be synced by path.");
                }

                ns = team.Id;
                logger.LogInformation("Sync scoped to team namespace {NamespaceId}.", ns);
            }

            // Two semaphores, and the distinction between them matters:
            //
            //   requestGate - bounds concurrent Dropbox HTTP requests. It is taken for
            //                 the duration of ONE request and released immediately, so a
            //                 task never holds one while asking for another.
            //   folderGate  - bounds how many folders are expanded at once. It is held
            //                 for a whole folder, so it also bounds how many database
            //                 contexts are open: the team tree has 8000+ folders on its
            //                 widest level, and one context each would swamp SQL Server.
            //
            // They are separate semaphores on purpose. Sharing one would mean a task
            // holding it for a whole folder and asking for it again per request - which
            // deadlocks the moment every permit is held by a request waiting for another.
            using var requestGate = new SemaphoreSlim(maxConcurrency);
            using var folderGate = new SemaphoreSlim(maxConcurrency);

            // The root folder must be upserted first: its Id is what its children get as
            // ParentId. Its own parent is outside the sync scope, so ParentId stays null.
            var rootId = await UpsertFolderAsync(ns, runId, rootPath, NameOf(rootPath), null, token)
                .ConfigureAwait(false);

            // Level 1 is the start folder itself: one folder, listed once.
            Volatile.Write(ref GetLevel(1).FoldersTotal, 1);

            // Discovered folders queue for the next level. A ConcurrentBag because from
            // the second level on, sibling folders expand in parallel and each appends to
            // it; List<T> is not thread-safe and concurrent Add can lose entries.
            var discovered = new ConcurrentBag<ChildRef>();
            await ExpandFolderAsync(ns, runId, rootPath, rootId, depth: 1,
                                    unlimited, maxDepth, enrichFileMetadata, requestGate, folderGate,
                                    counts, discovered, progress, sw, batchSize, token).ConfigureAwait(false);

            // Each depth level is its own parallel fan-out: every folder discovered at this
            // level expands concurrently, and only once all of them are stored does the
            // next level start.
            while (discovered.Count > 0 && (unlimited || level < maxDepth))
            {
                token.ThrowIfCancellationRequested();

                // Materialise and clear BEFORE expanding: ExpandFolderAsync appends to
                // 'discovered', so iterating it while it grows would never terminate.
                var toExpand = discovered.ToList();
                discovered.Clear();
                level++;

                // The level's folder count is known HERE, before the level starts: every
                // folder at this level was discovered at the level above, which has
                // finished. So "x of y" is an exact fraction from the first folder on.
                Volatile.Write(ref GetLevel(level).FoldersTotal, toExpand.Count);
                Publish(force: true);

                var nextLevel = new ConcurrentBag<ChildRef>();
                await Task.WhenAll(toExpand.Select(async child =>
                {
                    await ExpandFolderAsync(ns, runId, child.Path, child.Id, level,
                                            unlimited, maxDepth, enrichFileMetadata,
                                            requestGate, folderGate,
                                            counts, nextLevel, progress, sw, batchSize, token)
                        .ConfigureAwait(false);
                })).ConfigureAwait(false);

                // Bag order is non-deterministic; sort for a stable, readable walk order.
                foreach (var child in nextLevel.OrderBy(c => c.Path, StringComparer.Ordinal))
                {
                    discovered.Add(child);
                }
            }

            // Anything left in 'discovered' hit the depth cap and was never expanded. Mark
            // those folders Partial so the tree shows that their contents were never listed.
            if (!unlimited && discovered.Count > 0)
            {
                var bounded = discovered.ToList();
                await MarkUnvisitedAsync(ns, bounded, token).ConfigureAwait(false);
                counts.AddBounded(bounded.Count);
            }

            sw.Stop();

            var result = new DropBoxSyncResult(
                Completed: true,
                FoldersWritten: counts.Folders,
                FilesWritten: counts.Files,
                DepthReached: level,
                Duration: sw.Elapsed,
                Error: null);

            logger.LogInformation(
                "Dropbox sync of {Path} completed in {ElapsedMs} ms: {Folders} folder(s), {Files} file(s) " +
                "({Enriched} with dimensions, {PendingMedia} pending, {EnrichFailed} enrichment failure(s)), " +
                "{Skipped} folder(s) not fully listed, {Bounded} bounded by depth.",
                rootPath, sw.ElapsedMilliseconds, counts.Folders, counts.Files,
                counts.Enriched, counts.PendingMedia, counts.EnrichFailed,
                counts.NotOk, counts.BoundedFolders);

            _pendingRunning = false;
            _pendingHasResult = true;
            _pendingLastResult = result;
            _pendingError = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning("Dropbox sync of {Path} cancelled; rows already written are kept.", rootPath);
            _pendingRunning = false;
            _pendingHasResult = true;
            _pendingError = "Cancelled.";
            var cancelled = new DropBoxSyncResult(
                false, counts.Folders, counts.Files, level, sw.Elapsed, "Cancelled.");
            _pendingLastResult = cancelled;
            return cancelled;
        }
        catch (Exception ex)
        {
            sw.Stop();

            // Surface the whole exception chain. EF wraps the real cause (constraint
            // name, SQL text, timeout) in an inner exception, and "An error occurred
            // while saving the entity changes" alone is undiagnosable.
            var chain = new System.Text.StringBuilder();
            for (var e = ex; e is not null; e = e.InnerException)
            {
                chain.AppendLine($"  {e.GetType().Name}: {e.Message}");
            }

            logger.LogError(ex, "Dropbox sync of {Path} failed; rows already written are kept.", rootPath);
            _pendingRunning = false;
            _pendingHasResult = true;
            _pendingError = chain.ToString().Trim();
            var failed = new DropBoxSyncResult(
                false, counts.Folders, counts.Files, level, sw.Elapsed, _pendingError);
            _pendingLastResult = failed;
            return failed;
        }
        finally
        {
            _cts = null;
            Volatile.Write(ref _running, 0);

            // The walk has left the thread pool by now, so the final state is published
            // from the caller's context - the UI thread - and the busy screen updates
            // before the caller continues.
            Publish(force: true);
            _publishContext = null;
        }
    }

    public void Cancel()
    {
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        cts.Cancel();
    }

    /// <summary>
    /// Marks the finished run's result as seen, so the busy screen can be dismissed.
    /// </summary>
    public void AcknowledgeResult()
    {
        _pendingHasResult = false;
        _hasPendingResult = false;
        Raise(nameof(HasPendingResult));
        Raise(nameof(IsBusy));
    }

    /// <summary>
    /// Lists one folder, stores every entry in it with <paramref name="parentId"/>, commits,
    /// and returns the child folders to expand next.
    /// <para>
    /// The folder's own SyncStatus is set from the outcome: Ok when the listing succeeded,
    /// Skipped when it 404s. That status marks the folder as not fully listed, so the tree
    /// can show it.
    /// </para>
    /// <para>
    /// <b>The gates are taken one request at a time, never twice.</b> Listing holds the
    /// request gate for the LIST call only; each file's metadata call takes it for its
    /// own call. A request that holds a permit while asking for a second one deadlocks
    /// as soon as every permit is held by a request waiting for another - which is
    /// exactly what happens on a wide level, where all the permits are taken by folders
    /// that have just been listed and are now enriching their files.
    /// </para>
    /// </summary>
    private async Task ExpandFolderAsync(
        string ns, Guid runId, string path, long folderId, int depth,
        bool unlimited, int maxDepth, bool enrichFileMetadata,
        SemaphoreSlim requestGate, SemaphoreSlim folderGate,
        SyncCounts counts,
        ConcurrentBag<ChildRef> childrenOut,
        IProgress<DropBoxSyncProgress>? progress, Stopwatch sw, int batchSize, CancellationToken ct)
    {
        // The level this folder belongs to. Its total was set when the level opened.
        var level = GetLevel(depth);

        await folderGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExpandFolderCoreAsync(ns, runId, path, folderId, depth, unlimited, maxDepth,
                                        enrichFileMetadata, requestGate, level, counts,
                                        childrenOut, progress, sw, batchSize, ct).ConfigureAwait(false);
        }
        finally
        {
            folderGate.Release();
        }
    }

    private async Task ExpandFolderCoreAsync(
        string ns, Guid runId, string path, long folderId, int depth,
        bool unlimited, int maxDepth, bool enrichFileMetadata,
        SemaphoreSlim requestGate, LevelProgress level,
        SyncCounts counts,
        ConcurrentBag<ChildRef> childrenOut,
        IProgress<DropBoxSyncProgress>? progress, Stopwatch sw, int batchSize, CancellationToken ct)
    {
        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await ListFolderAsync(path, ns, requestGate, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 404 or no access. Mark it and move on - the folder's subtree is
            // unverified, and its SyncStatus records that.
            counts.AddNotOk();
            Interlocked.Increment(ref level.FoldersListed);
            logger.LogWarning(ex, "Skipping folder {Path} during sync.", path);
            await SetFolderStatusAsync(ns, path, DropBoxSyncStatus.Skipped, Truncate(ex.Message), ct)
                .ConfigureAwait(false);
            Report(path, depth, counts, sw, progress);
            return;
        }

        var pending = new List<DropBoxNodeEntity>(entries.Count);

        // Rows for the child folders of THIS listing, kept locally so their
        // generated Ids can be read back after the flush. Deriving them from this local
        // list matters: childrenOut is SHARED across the parallel workers on a level,
        // so scanning or mutating it here would touch (and remove) other workers'
        // entries.
        var queuedFolders = new List<(ChildRef Ref, DropBoxNodeEntity Row)>();
        var fileRows = new List<DropBoxNodeEntity>();
        var childFolders = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsFolder)
            {
                // Depth cap: record the folder but do not expand it. Partial, so the
                // tree shows that its contents were never listed.
                var atCap = !unlimited && depth >= maxDepth;
                var folderRow = ToEntity(ns, runId, entry, folderId,
                    atCap ? DropBoxSyncStatus.Partial : null, null);
                pending.Add(folderRow);

                counts.AddFolders();
                childFolders++;
                if (!atCap)
                {
                    queuedFolders.Add((new ChildRef(entry.PathDisplay, 0), folderRow));
                }
            }
            else
            {
                // Files never carry a status.
                var fileRow = ToEntity(ns, runId, entry, folderId, null, null);
                pending.Add(fileRow);
                fileRows.Add(fileRow);
                counts.AddFiles();
            }
        }

        // The listing cannot supply dimensions - Dropbox ignores include_media_info on
        // files/list_folder. Only a per-file get_metadata returns them, so that is where
        // they come from.
        //
        // The files of ONE folder are enriched in parallel, and each call takes the
        // request gate for its own duration and releases it immediately. A task never
        // holds a permit while asking for another, so there is no deadlock - and the
        // gate still caps the total number of in-flight Dropbox requests at
        // maxConcurrency no matter how many files a folder holds.
        if (enrichFileMetadata && fileRows.Count > 0)
        {
            await Task.WhenAll(fileRows.Select(row =>
                EnrichFileAsync(row, ns, counts, requestGate, ct))).ConfigureAwait(false);
        }

        await FlushAsync(ns, runId, folderId, path, pending, counts, batchSize, ct).ConfigureAwait(false);

        // Hand the next level the REAL Ids. SaveChanges has just written
        // entity.Id from the generated identity, so reading it back is exact and costs
        // no extra query.
        //
        // Queuing a placeholder 0 instead is what broke the deeper levels: the next
        // level then wrote every child with ParentId = 0, violating the self-referencing
        // FK and failing the whole SaveChanges (2 of 8382 files survived that run).
        foreach (var (queued, row) in queuedFolders)
        {
            if (row.Id > 0)
            {
                childrenOut.Add(queued with { Id = row.Id });
            }
            else
            {
                logger.LogWarning(
                    "Folder {Path} was written but has no identity; it will not be expanded.",
                    queued.Path);
            }
        }

        // Listing succeeded: this folder is fully verified.
        await SetFolderStatusAsync(ns, path, DropBoxSyncStatus.Ok, null, ct).ConfigureAwait(false);

        // The folder's records belong to THIS level: the entries found inside it.
        // Counted only after the flush, so the numbers are the rows actually stored.
        Interlocked.Increment(ref level.FoldersListed);
        Interlocked.Add(ref level.Records, entries.Count);
        Interlocked.Add(ref level.FoldersDiscovered, childFolders);

        Report(path, depth, counts, sw, progress);
    }

    /// <summary>
    /// Lists one folder, holding the gate for the call and no longer.
    /// </summary>
    private async Task<IReadOnlyList<FileEntry>> ListFolderAsync(
        string path, string ns, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await dropBox.ListFolderAsync(path, ns, false, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Publishes the state after a folder has been processed.
    /// <para>
    /// The report goes out AFTER the listing and the flush, so the counters are the rows
    /// actually stored - not the ones the folder was about to produce. That is what makes
    /// the numbers exact rather than one folder behind.
    /// </para>
    /// </summary>
    private void Report(string path, int depth, SyncCounts counts, Stopwatch sw,
        IProgress<DropBoxSyncProgress>? progress)
    {
        Volatile.Write(ref _currentPath, path);

        Publish(force: false);

        progress?.Report(new DropBoxSyncProgress(
            path, counts.Folders, counts.Files, counts.Written, counts.NotOk, sw.Elapsed, depth));
    }

    /// <summary>
    /// Pushes the current state to whoever is listening.
    /// <para>
    /// Throttled: the walk reports per folder, so an unthrottled publish would flood the
    /// UI with changes it cannot paint anyway. The level boundaries and the end of the run
    /// are always published, because those are the moments the display must not miss.
    /// </para>
    /// </summary>
    private void Publish(bool force)
    {
        // The throttle is claimed with a compare-and-swap, so exactly one worker per
        // interval gets to publish. Without the CAS every parallel worker would pass the
        // check and post its own snapshot, flooding the dispatcher.
        var nowTicks = DateTime.UtcNow.Ticks;
        if (!force)
        {
            var last = Interlocked.Read(ref _lastPublishTicks);
            if (nowTicks - last < TimeSpan.TicksPerMillisecond * 250)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastPublishTicks, nowTicks, last) != last)
            {
                return;
            }
        }
        else
        {
            Interlocked.Exchange(ref _lastPublishTicks, nowTicks);
        }

        // The level table is rebuilt wholesale on the walk thread: a level's counts change
        // constantly, and there are only a handful of levels.
        var levels = _levels.Values
            .OrderBy(l => l.Depth)
            .Select(l => new DropBoxSyncLevel(
                l.Depth,
                Volatile.Read(ref l.FoldersTotal),
                Volatile.Read(ref l.FoldersListed),
                Volatile.Read(ref l.Records)))
            .ToArray();

        var generation = Volatile.Read(ref _runGeneration);
        var context = _publishContext;
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            ApplySnapshot(levels, generation);
            return;
        }

        context.Post(_ => ApplySnapshot(levels, generation), null);
    }

    /// <summary>Applies a published snapshot and notifies the UI. Runs on the publishing context.</summary>
    private void ApplySnapshot(IReadOnlyList<DropBoxSyncLevel> levels, int generation)
    {
        // A snapshot from a previous run must not overwrite the current run's state: the
        // publish is posted, so it can still be sitting in the queue when the next run
        // starts and resets the counters.
        if (generation != _runGeneration)
        {
            return;
        }

        _levelView = levels;
        _isRunning = _pendingRunning;
        _hasPendingResult = _pendingHasResult;
        _lastResult = _pendingLastResult;
        _error = _pendingError;

        Raise(nameof(Levels));
        Raise(nameof(IsRunning));
        Raise(nameof(IsBusy));
        Raise(nameof(HasPendingResult));
        Raise(nameof(LastResult));
        Raise(nameof(Error));
        Raise(nameof(Headline));
        Raise(nameof(OutcomeLine));
        Raise(nameof(FoldersSeen));
        Raise(nameof(FilesSeen));
        Raise(nameof(RowsWritten));
        Raise(nameof(NotListed));
        Raise(nameof(NotListedDisplay));
        Raise(nameof(Elapsed));
        Raise(nameof(ElapsedDisplay));
        Raise(nameof(RowsPerSecond));
        Raise(nameof(CurrentPath));
    }

    /// <summary>Clears the counters before a new run starts.</summary>
    private void ResetRunState()
    {
        // A new generation: any snapshot still queued from the previous run is now stale
        // and will be discarded when it lands.
        Interlocked.Increment(ref _runGeneration);

        _levels.Clear();
        Volatile.Write(ref _currentPath, string.Empty);
        _pendingLastResult = null;
        _pendingError = null;
        _lastResult = null;
        _error = null;
        Interlocked.Exchange(ref _lastPublishTicks, 0);
    }

    /// <summary>Sets the folder being listed. Safe from any thread.</summary>
    private void CurrentPathSet(string path) => Volatile.Write(ref _currentPath, path);

    /// <summary>The counters of one level of the walk.</summary>
    private LevelProgress GetLevel(int depth) =>
        _levels.GetOrAdd(depth, d => new LevelProgress(d));

    /// <summary>
    /// Upserts one folder row and returns its Id. The row must exist before its children
    /// are inserted, since they carry its Id as ParentId.
    /// </summary>
    private async Task<long> UpsertFolderAsync(
        string ns, Guid runId, string path, string name, long? parentId, CancellationToken ct)
    {
        var lower = path.ToLowerInvariant();

        await using var db = CreateContext();

        var existing = await db.Nodes
            .FirstOrDefaultAsync(n => n.NamespaceId == ns && n.PathLower == lower, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.SyncRunId = runId;
            existing.SyncedUtc = DateTimeOffset.UtcNow;
            existing.Name = name;
            existing.SyncStatus = null;   // provisional; ExpandFolderAsync sets the real one
            existing.SyncStatusReason = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing.Id;
        }

        var row = new DropBoxNodeEntity
        {
            NamespaceId = ns,
            PathDisplay = path,
            PathLower = lower,
            Name = name,
            IsFolder = true,
            ParentId = parentId,
            SyncRunId = runId,
            SyncedUtc = DateTimeOffset.UtcNow
        };

        db.Nodes.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    private static DropBoxNodeEntity ToEntity(
        string ns, Guid runId, FileEntry entry, long? parentId, string? status, string? reason) => new()
    {
        NamespaceId = ns,
        PathDisplay = entry.PathDisplay,
        PathLower = entry.PathDisplay.ToLowerInvariant(),
        Name = entry.Name,
        IsFolder = entry.IsFolder,
        // Assigned from the folder we just listed, never derived from the path string.
        ParentId = parentId,
        DropboxId = entry.Id,
        // Folders carry no size or timestamps in Dropbox metadata: leave them null so an
        // empty file (0) stays distinguishable from a folder.
        SizeBytes = entry.IsFolder ? null : entry.SizeBytes,
        Rev = entry.Rev,
        ContentHash = entry.ContentHash,
        // Null unless the run requested media info; MediaInfoTag distinguishes
        // "not an image" (null) from "ask again later" ('pending').
        WidthPixels = entry.HasDimensions ? (int?)entry.WidthPixels : null,
        HeightPixels = entry.HasDimensions ? (int?)entry.HeightPixels : null,
        MediaInfoTag = entry.IsFolder ? null : entry.MediaKind,
        ServerModified = entry.IsFolder ? null : entry.ServerModified,
        ClientModified = entry.IsFolder ? null : entry.ClientModified,
        IsDownloadable = entry.IsFolder ? null : entry.IsDownloadable,
        SyncStatus = status,
        SyncStatusReason = reason,
        SyncRunId = runId,
        SyncedUtc = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Commits a folder's rows in batches. Existing rows are refreshed; new rows are
    /// inserted.
    /// </summary>
    private async Task FlushAsync(
        string ns, Guid runId, long folderId, string folderPath,
        List<DropBoxNodeEntity> pending, SyncCounts counts, int batchSize, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var chunk in Chunk(pending, batchSize))
        {
            ct.ThrowIfCancellationRequested();

            await using var db = CreateContext();
            var keys = chunk.Select(p => p.PathLower).ToList();

            // ONE tracked query for the whole chunk: loading tracked means the updates
            // below need no per-row fetch.
            var existing = await db.Nodes
                .Where(n => n.NamespaceId == ns && keys.Contains(n.PathLower))
                .ToDictionaryAsync(n => n.PathLower, StringComparer.OrdinalIgnoreCase, ct)
                .ConfigureAwait(false);

            foreach (var row in chunk)
            {
                if (existing.TryGetValue(row.PathLower, out var tracked))
                {
                    // Copy the identity back onto the pending row. The tracked entity
                    // carries the row's Id; the detached one does not, and EF never fills
                    // it in for an entity that was not added to the context.
                    //
                    // This matters more than it looks: the caller uses row.Id to queue a
                    // FOLDER for expansion at the next level. Without the copy the Id
                    // stays 0 on every folder that already exists in the table, so it is
                    // silently dropped from the walk - and a re-sync of an already-synced
                    // subtree stops after the first level and reports success.
                    row.Id = tracked.Id;

                    tracked.PathDisplay = row.PathDisplay;
                    tracked.Name = row.Name;
                    tracked.IsFolder = row.IsFolder;
                    tracked.ParentId = row.ParentId ?? tracked.ParentId;
                    tracked.DropboxId = row.DropboxId;
                    tracked.SizeBytes = row.SizeBytes;
                    tracked.Rev = row.Rev;
                    tracked.ContentHash = row.ContentHash;
                    // Dimensions are overwritten unconditionally, including back to NULL.
                    // A row synced with media info that is later re-synced without it must
                    // NOT keep the old width/height: those would then describe an unknown
                    // state that no read ever confirmed.
                    tracked.WidthPixels = row.WidthPixels;
                    tracked.HeightPixels = row.HeightPixels;
                    tracked.MediaInfoTag = row.MediaInfoTag;
                    tracked.ServerModified = row.ServerModified;
                    tracked.ClientModified = row.ClientModified;
                    tracked.IsDownloadable = row.IsDownloadable;
                    // Only overwrite an existing status when the incoming row actually carries one.
                    // A folder queued for expansion arrives with null (its real status is
                    // set once it is listed), and clobbering here would erase a
                    // previously stored 'Partial' or 'Ok' on every re-run.
                    if (row.SyncStatus is not null)
                    {
                        tracked.SyncStatus = row.SyncStatus;
                        tracked.SyncStatusReason = row.SyncStatusReason;
                    }
                    else if (!row.IsFolder)
                    {
                        // A file must never keep a status from when it was a folder.
                        tracked.SyncStatus = null;
                        tracked.SyncStatusReason = null;
                    }

                    tracked.SyncRunId = row.SyncRunId;
                    tracked.SyncedUtc = row.SyncedUtc;
                }
                else
                {
                    db.Nodes.Add(row);
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            counts.AddWritten(chunk.Count);
        }

        pending.Clear();
    }

    /// <summary>
    /// Fills a file row's media metadata (dimensions, media kind) via a per-file
    /// <c>get_metadata</c> call.
    /// <para>
    /// One extra call per file, which is the price of dimensions: Dropbox ignores
    /// <c>include_media_info</c> on <c>list_folder</c> (verified) but honours it here.
    /// A failure is non-fatal - the row is still written with whatever the listing knew,
    /// and MediaInfoTag stays null, which reads as "not known" rather than "no
    /// dimensions". Enrichment must never cost us the structural data.
    /// </para>
    /// </summary>
    private async Task EnrichFileAsync(
        DropBoxNodeEntity row, string ns, SyncCounts counts, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await dropBox.GetFileInfoAsync(row.PathDisplay, ns, true, ct).ConfigureAwait(false);

            row.WidthPixels = info.HasDimensions ? (int?)info.WidthPixels : null;
            row.HeightPixels = info.HasDimensions ? (int?)info.HeightPixels : null;
            row.MediaInfoTag = info.MediaKind;

            if (info.HasDimensions)
            {
                counts.AddEnriched();
            }
            else if (info.IsMediaInfoPending)
            {
                counts.AddPending();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let an enrichment failure abort the run or drop the row.
            counts.AddEnrichFailed();
            logger.LogDebug(ex, "Media enrichment failed for {Path}; row kept without dimensions.", row.PathDisplay);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sets a folder's outcome for this run.
    /// <para>
    /// A single UPDATE, not a SELECT followed by an UPDATE: this runs once per folder in
    /// the tree, and the team tree holds thousands of them. The row always exists - it
    /// was committed by the parent's flush before this folder was expanded.
    /// </para>
    /// </summary>
    private async Task SetFolderStatusAsync(
        string ns, string path, string status, string? reason, CancellationToken ct)
    {
        await using var db = CreateContext();
        var lower = path.ToLowerInvariant();

        await db.Nodes
            .Where(n => n.NamespaceId == ns && n.PathLower == lower)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.SyncStatus, status)
                .SetProperty(n => n.SyncStatusReason, reason), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Folders left unexpanded because the depth cap stopped the walk. They must not look
    /// 'Ok', so the tree shows that their contents were never listed.
    /// </summary>
    private async Task MarkUnvisitedAsync(string ns, List<ChildRef> children, CancellationToken ct)
    {
        if (children.Count == 0)
        {
            return;
        }

        await using var db = CreateContext();
        var keys = children.Select(c => c.Path.ToLowerInvariant()).ToList();

        await db.Nodes
            .Where(n => n.NamespaceId == ns && keys.Contains(n.PathLower))
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.SyncStatus, DropBoxSyncStatus.Partial)
                .SetProperty(n => n.SyncStatusReason, (string?)"depth cap"), ct)
            .ConfigureAwait(false);
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }

    /// <summary>Opens a short-lived context; DbContext is not thread-safe, so each worker gets its own.</summary>
    private DropBoxDbContext CreateContext() => _contextFactory.CreateDbContext();

    /// <summary>Last segment of a path, used as a folder's own name.</summary>
    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>A folder discovered at a level, to be expanded on the next one.</summary>
    private readonly record struct ChildRef(string Path, long Id);

    /// <summary>Per-level counters of the run in progress.</summary>
    private sealed class LevelProgress(int depth)
    {
        public int Depth { get; } = depth;

        /// <summary>Folders at this level. Known before the level starts.</summary>
        public int FoldersTotal;

        /// <summary>Folders of this level that have been processed.</summary>
        public int FoldersListed;

        /// <summary>Records written at this level.</summary>
        public int Records;

        /// <summary>Child folders found at this level - they become the next level's folders.</summary>
        public int FoldersDiscovered;
    }

    /// <summary>Mutable counters shared across the parallel workers.</summary>
    private sealed class SyncCounts
    {
        private int _folders;
        private int _files;
        private int _written;
        private int _notOk;
        private int _bounded;
        private int _enriched;
        private int _pendingMedia;
        private int _enrichFailed;

        // Properties are plain reads; mutation goes through the Add* methods. A property
        // with an Interlocked side effect would increment on every log line that reads it.
        public int Folders => Volatile.Read(ref _folders);
        public int Files => Volatile.Read(ref _files);
        public int Written => Volatile.Read(ref _written);
        public int NotOk => Volatile.Read(ref _notOk);
        public int BoundedFolders => Volatile.Read(ref _bounded);
        public int Enriched => Volatile.Read(ref _enriched);
        public int PendingMedia => Volatile.Read(ref _pendingMedia);
        public int EnrichFailed => Volatile.Read(ref _enrichFailed);

        public void AddFolders(int n = 1) => Interlocked.Add(ref _folders, n);
        public void AddFiles(int n = 1) => Interlocked.Add(ref _files, n);
        public void AddWritten(int n) => Interlocked.Add(ref _written, n);
        public void AddNotOk(int n = 1) => Interlocked.Add(ref _notOk, n);
        public void AddBounded(int n = 1) => Interlocked.Add(ref _bounded, n);
        public void AddEnriched(int n = 1) => Interlocked.Add(ref _enriched, n);
        public void AddPending(int n = 1) => Interlocked.Add(ref _pendingMedia, n);
        public void AddEnrichFailed(int n = 1) => Interlocked.Add(ref _enrichFailed, n);
    }

    private static string Truncate(string message, int max = 256) =>
        string.IsNullOrEmpty(message) ? string.Empty
            : message.Length <= max ? message : message[..max];

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
