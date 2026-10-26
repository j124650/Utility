using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Utility.DropBox;
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

    /// <summary>
    /// ONE writer at a time for <c>ml.DropBoxNode</c>.
    /// <para>
    /// The walk expands six folders in parallel, and every one of them writes to the
    /// same table - the folder's rows and the folder's own status. Those writes
    /// overlapped, and SQL Server picked one worker as the deadlock victim: the
    /// transaction was rolled back, the folder was abandoned, and its whole subtree
    /// was never discovered.
    /// </para>
    /// <para>
    /// The parallelism that matters is the Dropbox traffic, and that is untouched by
    /// this: the listing and every per-file metadata call still run six at a time. Only
    /// the WRITE is serialised, and a write is now a seek of a few milliseconds, so the
    /// gate is never the bottleneck. One writer at a time means there is no cycle of
    /// locks between this app's own workers, which is what a deadlock needs.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// How many times a write that was chosen as a deadlock victim is retried.
    /// <para>
    /// A deadlock victim is a transient failure, not a verdict: its transaction was
    /// rolled back whole, so running it again is safe and is what SQL Server itself
    /// asks for ("Rerun the transaction"). Retrying here - rather than letting the
    /// exception escape - is what stops a lost folder from taking its subtree with it.
    /// </para>
    /// </summary>
    private const int MaxWriteAttempts = 5;

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
    /// True once a stop has been requested, until the walk has fully wound down.
    /// <para>
    /// Kept apart from <see cref="_isRunning"/> because the two mean different things:
    /// the walk is still RUNNING while it unwinds, but the user has already asked it to
    /// stop, and the screen must not offer a Close button in that window.
    /// </para>
    /// </summary>
    private volatile bool _stopRequested;

    /// <summary>
    /// Dropbox calls sent but not yet returned. Incremented before a request goes out
    /// and decremented when it comes back, so the count falls as the walk unwinds.
    /// </summary>
    private int _activeDownloaders;

    /// <summary>
    /// The brake every worker of a walk waits at when Dropbox rate-limits one of them.
    /// <para>
    /// One brake for the whole walk, because Dropbox's limit is per authorization (per
    /// user, or per team) and not per request: a 429 means "stop, all of you".
    /// </para>
    /// </summary>
    private readonly DropboxRateLimitBrake _rateLimitBrake = new();

    /// <summary>
    /// What the walk's own traffic looks like: how fast it is going, how much of it is
    /// being refused, and how long each call takes.
    /// </summary>
    private readonly DropboxRequestRateMeter _rateMeter = new();

    /// <summary>
    /// Sets the request width from what the meter measured. Null between runs.
    /// </summary>
    private DropboxConcurrencyController? _controller;

    /// <summary>
    /// How many times a single request may be retried after a rate limit before the
    /// folder is given up on.
    /// <para>
    /// Few on purpose: a rate limit that survives a handful of attempts is not going to
    /// be waited out by retrying harder, and the walk has a whole tree to get through.
    /// The folder is then recorded as <c>Failed</c>, which the next continuation picks
    /// up again.
    /// </para>
    /// </summary>
    private const int MaxRateLimitAttempts = 5;

    /// <summary>
    /// The width of the walk: how many Dropbox requests may be in flight at once.
    /// <para>
    /// ONE number, and it does not move. The request gate, the folder gate and the
    /// worker pool are all this width, so there is nothing to reconcile between them
    /// and nothing to discover at runtime.
    /// </para>
    /// <para>
    /// The walk was written to climb towards whatever rate Dropbox would accept, but
    /// that needed a second number - the ceiling - and the width the walk started at,
    /// and the folder width and the request width were separate things that had to be
    /// kept in step. Measured against this account, the API tolerates far more than
    /// this: the latency stays flat at about 370 ms and no width produced a rate
    /// limit, up to 256 concurrent. So this is a choice, not a measurement - the walk
    /// is simply capped, and nothing adapts.
    /// </para>
    /// </summary>
    private const int WalkWidth = 24;

    /// <summary>
    /// The wait used when Dropbox did not say how long to wait.
    /// <para>
    /// Grows with the attempt, and starts well above a second: a rate limit with no
    /// stated duration is the API telling us the budget is spent, and a short retry
    /// only makes it worse - rate-limited requests count against the limit too.
    /// </para>
    /// </summary>
    private static int RateLimitBackoffSeconds(int attempt)
    {
        var seconds = 5 * (1 << Math.Min(attempt - 1, 5));
        return seconds > 300 ? 300 : seconds;
    }

    /// <summary>
    /// Counts the runs. A snapshot carries the number of the run that produced it, so a
    /// snapshot left over from a previous run is discarded instead of overwriting the
    /// state of the current one.
    /// </summary>
    private int _runGeneration;

    /// <summary>Published state. Written only on the publishing context.</summary>
    private bool _isRunning;
    private bool _hasPendingResult;

    /// <summary>
    /// True while the run is a CONTINUATION: it started from the folders a previous walk
    /// left unexpanded rather than from the root. The busy screen says so, because the
    /// two runs mean different things.
    /// </summary>
    private bool _isContinuation;
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

    /// <summary>
    /// True while a stop has been requested but the walk is still winding down.
    /// <para>
    /// The walk is still running in this state, but it is on its way out, so the screen
    /// must not offer a Close button: the user would be closing a screen whose walk had
    /// not finished unwinding.
    /// </para>
    /// </summary>
    public bool IsStopping => _isRunning && _stopRequested;

    /// <summary>True while the walk is running and has not been asked to stop.</summary>
    public bool CanStop => _isRunning && !_stopRequested;

    /// <summary>Dropbox calls still in flight.</summary>
    public int ActiveDownloaders => Volatile.Read(ref _activeDownloaders);

    /// <summary>
    /// How many Dropbox requests the current run may have in flight at once.
    /// <para>
    /// This is the request gate's width: one permit per in-flight HTTP call, taken for
    /// the duration of that call and released immediately. It bounds the load on
    /// Dropbox's API, so it is worth showing - a run that looks stalled may simply be
    /// waiting for its permits.
    /// </para>
    /// <para>
    /// The walk is capped at <see cref="WalkWidth"/>, so this is that cap - unless the
    /// controller has narrowed it because Dropbox rate-limited the walk, which it may
    /// do at any time.
    /// </para>
    /// </summary>
    public int MaxConcurrency => _controller is not null
        ? _controller.Gate.Limit
        : WalkWidth;

    /// <summary>
    /// The walk's own traffic, measured.
    /// <para>
    /// Read straight from the meter, not copied into a published field: it changes
    /// constantly, and the busy screen re-reads it whenever it repaints.
    /// </para>
    /// </summary>
    public DropboxRateSnapshot RateSnapshot => _rateMeter.Snapshot();

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
            if (IsStopping)
            {
                // The count is the number of Dropbox calls still in flight. It falls to
                // zero as they return, and the screen closes itself when it does.
                return $"Stopping download processes: {ActiveDownloaders:N0}";
            }

            if (_isRunning)
            {
                return _isContinuation ? "Continuing the sync" : "Syncing from Dropbox";
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

    /// <summary>
    /// Walks the whole subtree under <paramref name="startPath"/>, from the root down to
    /// the leaves.
    /// </summary>
    public Task<DropBoxSyncResult> SyncAsync(
        string startPath,
        string? namespaceId = null,
        IProgress<DropBoxSyncProgress>? progress = null,
        int batchSize = 500,
        int maxDepth = 0,
        bool enrichFileMetadata = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        return RunAsync(
            rootPath: startPath.TrimEnd('/'),
            namespaceId: namespaceId,
            continueFromFrontier: false,
            progress: progress,
            batchSize: batchSize,
            maxDepth: maxDepth,
            enrichFileMetadata: enrichFileMetadata,
            ct: ct);
    }

    /// <summary>
    /// Continues the sync: lists the folders the last walk wrote but never expanded, and
    /// carries on down from there to the leaves.
    /// <para>
    /// A walk that did not reach the leaves leaves folders with NO status - written,
    /// never expanded. Their contents were never listed, and everything below them was
    /// never discovered, so listing them is what makes the tree complete. The part that
    /// already succeeded is not listed again.
    /// </para>
    /// <para>
    /// A folder whose status is <c>Skipped</c> is included too: its listing was tried
    /// and refused, so its contents are still unlisted.
    /// </para>
    /// </summary>
    public async Task<DropBoxSyncResult> ContinueAsync(
        IProgress<DropBoxSyncProgress>? progress = null,
        int batchSize = 500,
        int maxDepth = 0,
        bool enrichFileMetadata = true,
        CancellationToken ct = default)
    {
        // Everything the continuation needs - the namespace, the frontier folders - is
        // resolved INSIDE the run, after the busy screen has already appeared. Doing it
        // here instead would leave the button looking dead for the duration.
        return await RunAsync(
            rootPath: null,
            namespaceId: null,
            continueFromFrontier: true,
            progress: progress,
            batchSize: batchSize,
            maxDepth: maxDepth,
            enrichFileMetadata: enrichFileMetadata,
            ct: ct);
    }

    /// <summary>
    /// The walk itself, with its first level chosen by the caller: the whole subtree from
    /// a root, or the folders a previous walk never expanded.
    /// </summary>
    private async Task<DropBoxSyncResult> RunAsync(
        string? rootPath,
        string? namespaceId,
        bool continueFromFrontier,
        IProgress<DropBoxSyncProgress>? progress,
        int batchSize,
        int maxDepth,
        bool enrichFileMetadata,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        // One walk at a time. The flag is cleared in the finally block.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new DropBoxSyncResult(false, 0, 0, 0, TimeSpan.Zero, "A sync is already running.");
        }

        var sw = Stopwatch.StartNew();
        var runId = Guid.NewGuid();

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
        _isContinuation = continueFromFrontier;
        _hasPendingResult = false;
        CurrentPathSet(rootPath ?? "continuing the sync");
        Publish(force: true);

        try
        {
            // Team folders are invisible from the personal space, so the walk must be
            // scoped to the team namespace.
            var ns = await ResolveNamespaceAsync(namespaceId, token).ConfigureAwait(false);

            // The resume set: the folders whose contents were never listed.
            //
            // Resolved HERE, inside the run, so the busy screen is already up while this
            // query runs. Resolving it before the run would leave the button looking
            // dead for the duration of the query.
            List<SeedFolder>? seeds = null;

            if (continueFromFrontier)
            {
                seeds = await LoadUnexpandedFoldersAsync(ns, token).ConfigureAwait(false);

                if (seeds.Count == 0)
                {
                    // Nothing left to continue - the walk already reached the leaves.
                    sw.Stop();

                    var empty = new DropBoxSyncResult(true, 0, 0, 0, sw.Elapsed, null);

                    _pendingRunning = false;
                    _pendingHasResult = true;
                    _pendingLastResult = empty;
                    _pendingError = null;
                    return empty;
                }

                // Their old verdict is cleared FIRST, so a folder being listed now does
                // not keep the verdict of a run that never listed it.
                await ResetSeedStatusAsync(ns, seeds, token).ConfigureAwait(false);
            }

            // Two gates, and the distinction between them matters:
            //
            //   requestGate - bounds concurrent Dropbox HTTP requests. It is taken for the
            //                 duration of ONE request and released immediately, so a
            //                 task never holds one while asking for another.
            //   folderGate  - bounds how many folders are expanded at once. It is held
            //                 for a whole folder, so it also bounds how many database
            //                 contexts are open.
            //
            // They are separate on purpose. Sharing one would mean a task holding it
            // for a whole folder and asking for it again per request - which deadlocks
            // the moment every permit is held by a request waiting for another.
            //
            // Both are the walk's width: ONE number, and it does not move. The
            // controller may NARROW it from what the walk measures - a 429, or the
            // latency rising - but it never widens it. Dropbox does not publish its
            // limit, so a refused request is the only thing that says the width is too
            // wide.
            //
            // The writes are serialised behind their own gate, so the contexts these
            // folders open are opened one at a time anyway.
            using var requestGate = new AdaptiveConcurrencyGate(WalkWidth);
            using var folderGate = new AdaptiveConcurrencyGate(WalkWidth);

            // The controller watches the measured traffic and moves the width. Its
            // ceiling is the width the walk starts at, so the width can only ever be
            // NARROWED - never widened. A 429 or a latency rise takes it down; it does
            // not come back up.
            var controller = new DropboxConcurrencyController(
                requestGate,
                _rateMeter,
                startWidth: WalkWidth,
                maxWidth: WalkWidth,
                minWidth: 1,
                settleTime: TimeSpan.FromSeconds(30),
                folderGate: folderGate);

            // Published so the busy screen can show the width the walk settled on.
            _controller = controller;

            // The first level of the walk. Either the single root folder of a full sync,
            // or the folders a previous walk never expanded - the resume points.
            //
            // A resume folder ALREADY has a row: its parent listed it, so the row is
            // there with its Id. That Id is what its children need as their ParentId,
            // so the row is read, not re-created.
            var discovered = new ConcurrentBag<ChildRef>();

            if (seeds is null)
            {
                // The root folder must be upserted first: its Id is what its children get
                // as ParentId. Its own parent is outside the sync scope, so ParentId stays
                // null.
                var rootId = await UpsertFolderAsync(ns, runId, rootPath!, NameOf(rootPath!), null, token)
                    .ConfigureAwait(false);
                // Level 1 is the start folder itself: one folder, listed once.
                Volatile.Write(ref GetLevel(1).FoldersTotal, 1);

                await ExpandFolderAsync(ns, runId, rootPath!, rootId, depth: 1,
                                        unlimited, maxDepth, enrichFileMetadata, requestGate, folderGate,
                                        counts, discovered, progress, sw, batchSize, token).ConfigureAwait(false);
            }
            else
            {
                // The resume set: the folders a previous walk never expanded.
                //
                // Their rows exist already - their parents listed them - so each one is
                // READ with its Id, and that Id is what its children get as ParentId.
                // Their status is reset to null first: a folder that is being listed now
                // must not keep the verdict of a run that never listed it.
                Volatile.Write(ref GetLevel(1).FoldersTotal, seeds.Count);

                // The seeds are handed to the workers through a work list, not turned
                // into one task each. There are 5,456 of them, and a task each meant
                // 5,456 tasks alive before any of them ran - and 5,456 cancellations
                // when the walk was stopped.
                //
                // The pool is sized to the WIDEST the walk may go, not to the width it
                // starts at. The request gate is what actually limits the concurrent
                // calls, and it is adaptive; a pool pinned to the starting width would
                // cap the walk there and the wider width could never be reached.
                await WorkerPool.RunAsync(
                    seeds,
                    WalkWidth,
                    (seed, ct) => ExpandFolderAsync(
                        ns, runId, seed.Path, seed.Id, depth: 1,
                        unlimited, maxDepth, enrichFileMetadata, requestGate, folderGate,
                        counts, discovered, progress, sw, batchSize, ct),
                    token).ConfigureAwait(false);
            }

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

                // The level's folders go through a work list, so the number of live tasks
                // is the number of workers and not the number of folders. The widest
                // level of the team tree holds thousands of folders, and a task each
                // meant thousands of tasks alive at once.
                //
                // The pool is sized to the widest the walk may go; the request gate and
                // the folder gate - which follow it - are what limit how much of it is
                // used at once.
                await WorkerPool.RunAsync(
                    toExpand,
                    WalkWidth,
                    (child, ct) => ExpandFolderAsync(
                        ns, runId, child.Path, child.Id, level,
                        unlimited, maxDepth, enrichFileMetadata,
                        requestGate, folderGate,
                        counts, nextLevel, progress, sw, batchSize, ct),
                    token).ConfigureAwait(false);

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
                rootPath ?? "the unfinished folders", sw.ElapsedMilliseconds, counts.Folders, counts.Files,
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

            // A CANCELLED run does NOT leave a pending result. The user asked for the
            // screen to close itself once the walk has unwound, and a pending result is
            // what puts a Close button on it. The reason is still carried on the result
            // object, so the caller can report it.
            _pendingHasResult = false;
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
            _isContinuation = false;

            // The controller is dropped with the gate it controls: the gate is disposed
            // by the using above, and a controller must not widen a disposed gate.
            _controller = null;

            // The meter is cleared for the next run, so its figures describe the run
            // that is running and not the one before it.
            _rateMeter.Reset();
            _rateLimitBrake.Reset();

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

        // The stop is a REQUEST, not a kill. The walk keeps running until the calls that
        // are already in flight return, and the busy screen has to show that. Set the
        // flag and publish it BEFORE cancelling, so the screen switches to the stopping
        // state immediately instead of sitting on "Syncing" until the wind-down ends.
        _stopRequested = true;
        Publish(force: true);

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
        AdaptiveConcurrencyGate requestGate, AdaptiveConcurrencyGate folderGate,
        SyncCounts counts,
        ConcurrentBag<ChildRef> childrenOut,
        IProgress<DropBoxSyncProgress>? progress, Stopwatch sw, int batchSize, CancellationToken ct)
    {
        // The level this folder belongs to. Its total was set when the level opened.
        var level = GetLevel(depth);

        // The gate is taken WITHOUT throwing when the walk is stopped. A pool of
        // workers waiting on a full gate would otherwise raise one exception per
        // waiting worker, and a debugger prints every one of them.
        if (!await folderGate.TryWaitAsync(ct).ConfigureAwait(false))
        {
            return;
        }

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
        AdaptiveConcurrencyGate requestGate, LevelProgress level,
        SyncCounts counts,
        ConcurrentBag<ChildRef> childrenOut,
        IProgress<DropBoxSyncProgress>? progress, Stopwatch sw, int batchSize, CancellationToken ct)
    {
        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await ListFolderAsync(path, ns, requestGate, ct).ConfigureAwait(false);
        }
        catch (DropBoxNotFoundException ex)
        {
            // The path itself is gone or invisible. That is a property of the PATH,
            // not of the moment: listing it again would fail the same way. So it is
            // Skipped - the folder's contents are unverified, and the sweep must not
            // delete them.
            counts.AddNotOk();
            Interlocked.Increment(ref level.FoldersListed);
            logger.LogWarning(ex, "Skipping folder {Path} during sync: not found.", path);
            await RecordOutcomeAsync(ns, path, DropBoxSyncStatus.Skipped, Truncate(ex.Message), ct)
                .ConfigureAwait(false);
            Report(path, depth, counts, sw, progress);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An ERROR, not a missing path: a dropped connection, a TLS failure, a
            // server fault, a rate limit that would not clear.
            //
            // This is a property of the MOMENT. The folder may well list fine on the
            // next run, so it is recorded as Failed - "not listed this time" - and
            // NOT as Skipped, which would claim the folder is permanently
            // inaccessible. Both protect the subtree from the sweep, and both are
            // picked up by the next continuation, so nothing is lost either way; the
            // difference is that the verdict tells the truth.
            //
            // The reason keeps the whole exception chain: "The SSL connection could
            // not be established" on its own says nothing about WHY.
            counts.AddNotOk();
            Interlocked.Increment(ref level.FoldersListed);
            logger.LogWarning(ex, "Could not list folder {Path} during sync.", path);
            await RecordOutcomeAsync(ns, path, DropBoxSyncStatus.Failed, Truncate(DescribeError(ex)), ct)
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
        //
        // The files go through the queue too, for the same reason the folders do. A
        // wide folder holds thousands of files, and a task each meant thousands of
        // tasks alive for that one folder. The queue's width is this folder's SHARE of
        // the request gate - the gate is what really bounds the calls, and the queue
        // only bounds the tasks.
        if (enrichFileMetadata && fileRows.Count > 0)
        {
            await EnrichFilesAsync(fileRows, ns, counts, requestGate, ct).ConfigureAwait(false);
        }

        // The rows are written BEFORE the verdict, and a failure to write them must
        // not be treated as "the folder could not be listed" - that verdict abandons
        // the subtree. So the flush's own failure is caught here, recorded, and the
        // folder is left unlisted for the next continuation to pick up.
        try
        {
            await FlushAsync(ns, runId, folderId, path, pending, counts, batchSize, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The rows were not written. The folder is NOT verified, and saying it was
            // would be a lie the next continuation would act on.
            counts.AddNotOk();
            Interlocked.Increment(ref level.FoldersListed);
            logger.LogError(ex, "Could not write the rows of folder {Path}.", path);
            await RecordOutcomeAsync(ns, path, DropBoxSyncStatus.Failed, Truncate(DescribeError(ex)), ct)
                .ConfigureAwait(false);
            Report(path, depth, counts, sw, progress);
            return;
        }

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
        //
        // The verdict is recorded through RecordOutcomeAsync: a failure to WRITE the
        // verdict must not abandon the folder, and the rows are already committed by
        // this point - so the folder is verified even if the verdict could not be
        // stored. The next continuation will list it again, which is harmless.
        await RecordOutcomeAsync(ns, path, DropBoxSyncStatus.Ok, null, ct).ConfigureAwait(false);

        // The folder's records belong to THIS level: the entries found inside it.
        // Counted only after the flush, so the numbers are the rows actually stored.
        Interlocked.Increment(ref level.FoldersListed);
        Interlocked.Add(ref level.Records, entries.Count);
        Interlocked.Add(ref level.FoldersDiscovered, childFolders);

        Report(path, depth, counts, sw, progress);
    }

    /// <summary>
    /// Lists one folder, holding the gate for the call and no longer.
    /// <para>
    /// A rate limit is retried HERE, at the request boundary, and not further up: the
    /// whole point is that the same folder gets listed again rather than abandoned. The
    /// SDK does not retry a 429 itself - its retry budget covers server errors only - so
    /// without this the folder would be recorded as un-listable and its entire subtree
    /// would never be discovered.
    /// </para>
    /// <para>
    /// The wait is shared: see <see cref="DropboxRateLimitBrace"/>.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<FileEntry>> ListFolderAsync(
        string path, string ns, AdaptiveConcurrencyGate gate, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Every worker waits at the brake before it takes a permit, so a rate
            // limit pauses the whole walk and not just the worker that hit it.
            await _rateLimitBrake.WaitAsync(ct).ConfigureAwait(false);

            // The permit is taken without throwing when the walk is stopped, so a
            // pool of workers waiting on a full gate does not raise one exception
            // per waiting worker.
            if (!await gate.TryWaitAsync(ct).ConfigureAwait(false))
            {
                throw new OperationCanceledException(ct);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                // Counted as a downloader for the same reason as the metadata call: the
                // request is in flight until it returns, and cannot be aborted.
                Interlocked.Increment(ref _activeDownloaders);
                try
                {
                    var entries = await dropBox.ListFolderAsync(path, ns, false, ct).ConfigureAwait(false);

                    _rateMeter.Record(
                        Stopwatch.GetElapsedTime(started),
                        DropboxRequestOutcome.Success);

                    return entries;
                }
                catch (DropBoxRateLimitedException ex)
                {
                    _rateMeter.Record(
                        Stopwatch.GetElapsedTime(started),
                        DropboxRequestOutcome.RateLimited);

                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _rateMeter.Record(
                        Stopwatch.GetElapsedTime(started),
                        DropboxRequestOutcome.Failed);

                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeDownloaders);
                    PublishDownloads();
                }
            }
            catch (DropBoxRateLimitedException ex)
            {
                if (attempt >= MaxRateLimitAttempts)
                {
                    throw;
                }

                // The brake holds for the wait Dropbox asked for. When it did not say,
                // the backoff grows instead - but the brake still holds every worker,
                // which is what keeps the retry storm from becoming the storm.
                var wait = ex.RetryAfterSeconds > 0
                    ? ex.RetryAfterSeconds
                    : RateLimitBackoffSeconds(attempt);

                _rateLimitBrake.Hold(wait);

                logger.LogWarning(
                    "Dropbox rate limited while listing {Path} (attempt {Attempt}/{Max}); holding the walk for {Wait}s.",
                    path, attempt, MaxRateLimitAttempts, wait);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Publishes the downloader count after one finishes.
    /// <para>
    /// Only while stopping: the count is what the busy screen shows, and the screen is
    /// the only thing that displays it. Publishing on every request would flood the
    /// dispatcher for a number nobody is looking at.
    /// </para>
    /// </summary>
    private void PublishDownloads()
    {
        if (_stopRequested)
        {
            Publish(force: true);
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
        // Adapt the request width to what was measured. This runs on every publish, and
        // the controller decides for itself whether anything needs to change - so the
        // cost of asking is one snapshot of the meter.
        _controller?.Adapt();

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
        Raise(nameof(IsStopping));
        Raise(nameof(CanStop));
        Raise(nameof(ActiveDownloaders));
        Raise(nameof(MaxConcurrency));

        // The rate snapshot is a struct read straight from the meter, so the binding
        // would cache the first value it saw. Raising it here makes the busy screen
        // re-read it on every publish - which is every time the walk moves.
        Raise(nameof(RateSnapshot));
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

        // The meter and the brake start empty, so their figures describe THIS run.
        // The meter is cleared HERE rather than at the end of the last one, so the
        // final publish of a finished run still shows what that run cost.
        _rateMeter.Reset();
        _rateLimitBrake.Reset();

        _levels.Clear();
        Volatile.Write(ref _currentPath, string.Empty);

        // A new run is not a stopped run: the flag belongs to the previous walk.
        _stopRequested = false;

        _pendingLastResult = null;
        _pendingError = null;
        _lastResult = null;
        _error = null;
        Interlocked.Exchange(ref _lastPublishTicks, 0);
    }

    /// <summary>Sets the folder being listed. Safe from any thread.</summary>
    private void CurrentPathSet(string path) => Volatile.Write(ref _currentPath, path);

    /// <summary>
    /// The whole exception chain in one line.
    /// <para>
    /// The outer message alone often says nothing: "The SSL connection could not be
    /// established, see inner exception" names the symptom, and the cause is one level
    /// down. The chain keeps the cause, which is what makes the stored reason worth
    /// reading.
    /// </para>
    /// </summary>
    private static string DescribeError(Exception ex)
    {
        var parts = new List<string>();
        var current = ex;
        var depth = 0;

        while (current is not null && depth < 5)
        {
            var message = current.Message?.Trim();
            if (!string.IsNullOrEmpty(message) && !parts.Contains(message))
            {
                parts.Add(message);
            }

            current = current.InnerException;
            depth++;
        }

        return parts.Count > 0 ? string.Join(" -> ", parts) : ex.GetType().Name;
    }

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
        var hash = PathHashOf(ns, lower);

        // ONE writer at a time, like every other write to this table. See WriteAsync.
        return await WriteAsync(async () =>
        {
            await using var db = CreateContext();

            // Found by the path HASH: PathLower cannot be seeked on, so a predicate on
            // it would scan the whole namespace. See PathHashOf.
            var existing = await db.Nodes
                .FirstOrDefaultAsync(n => n.NamespaceId == ns && n.PathHash == hash, ct)
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
        }, ct).ConfigureAwait(false);
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

        // ONE writer at a time, and the same path the status write takes. The flush and
        // the status write both touch this folder's rows; running them concurrently is
        // what deadlocked the walk. The Dropbox side of the folder - the listing and
        // every per-file metadata call - is already done by the time this runs, and is
        // unaffected.
        await WriteAsync(async () =>
        {
            foreach (var chunk in Chunk(pending, batchSize))
            {
                ct.ThrowIfCancellationRequested();

                await using var db = CreateContext();
                var keys = chunk.Select(p => p.PathLower).ToList();

                // The rows are found by their PATH HASH, not by their path: PathLower is
                // too long to be an index key, so a predicate on it cannot seek and the
                // lookup scans the whole namespace. The hash is what the natural key
                // index is built on. See PathHashOf.
                var hashes = chunk.Select(p => PathHashOf(ns, p.PathLower)).ToList();

                // ONE tracked query for the whole chunk: loading tracked means the updates
                // below need no per-row fetch.
                var existing = await db.Nodes
                    .Where(n => n.NamespaceId == ns && hashes.Contains(n.PathHash))
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
        }, ct).ConfigureAwait(false);

        pending.Clear();
    }

    /// <summary>
    /// How many folders are enriching their files right now.
    /// <para>
    /// The request gate's width is shared by all of them, so each folder takes a share
    /// of it rather than the whole of it. Without the share, twenty-four folders would
    /// each ask for the full width and queue behind each other for permits they could
    /// have had - and the live task count would be the width squared.
    /// </para>
    /// </summary>
    private int _activeEnrichers;

    /// <summary>
    /// The width of one folder's enrichment pool.
    /// <para>
    /// The request gate is the real limit on how many Dropbox calls are in flight, and
    /// it is shared. So a folder's pool takes a SHARE of it: with twenty-four folders
    /// enriching, each takes one permit's worth; with one folder alone, it takes the
    /// whole width, because nothing else wants those permits.
    /// </para>
    /// <para>
    /// The floor is one, so a folder always makes progress on its own files. A higher
    /// floor would multiply the live task count - twenty-four folders at a floor of
    /// four is a hundred enrichment workers for twenty-four permits - and the permits
    /// are what actually limit the calls, so the extra workers only queue.
    /// </para>
    /// </summary>
    private int EnrichPoolWidth(AdaptiveConcurrencyGate requestGate, int fileCount)
    {
        var width = requestGate.Limit;
        var sharing = Volatile.Read(ref _activeEnrichers);

        var share = sharing <= 1
            ? width
            : Math.Clamp(width / sharing, 1, width);

        return Math.Min(fileCount, share);
    }

    /// <summary>
    /// Enriches one folder's files, over a pool sized to this folder's share of the
    /// request gate.
    /// <para>
    /// The files go through a queue rather than a fan-out, so the number of live tasks
    /// is the pool's width and not the number of files. A wide folder holds thousands
    /// of files, and a task each meant thousands of tasks alive for that one folder -
    /// and thousands of cancellations when the walk was stopped.
    /// </para>
    /// </summary>
    private async Task EnrichFilesAsync(
        List<DropBoxNodeEntity> fileRows, string ns, SyncCounts counts,
        AdaptiveConcurrencyGate requestGate, CancellationToken ct)
    {
        if (fileRows.Count == 0)
        {
            return;
        }

        // The folder counts itself BEFORE it works out its share, so a folder that
        // arrives late sees the others and takes a smaller share. A folder that
        // arrived first would otherwise take the whole width and leave the rest of
        // the level queuing behind it.
        Interlocked.Increment(ref _activeEnrichers);
        try
        {
            // The pool's width is this folder's SHARE of the request gate - the gate is
            // what really bounds the calls, and the pool only bounds the tasks.
            var poolWidth = EnrichPoolWidth(requestGate, fileRows.Count);

            await WorkerPool.RunAsync(
                fileRows,
                poolWidth,
                (row, ct2) => EnrichFileAsync(row, ns, counts, requestGate, ct2),
                ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeEnrichers);
        }
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
        DropBoxNodeEntity row, string ns, SyncCounts counts, AdaptiveConcurrencyGate gate, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // The brake is waited on BEFORE the permit, for the same reason as in the
            // listing: a rate limit pauses every worker, not just this one.
            await _rateLimitBrake.WaitAsync(ct).ConfigureAwait(false);

            // The permit is taken without throwing when the walk is stopped, so a pool
            // of enrichment workers waiting on a full gate does not raise one exception
            // per waiting worker.
            if (!await gate.TryWaitAsync(ct).ConfigureAwait(false))
            {
                throw new OperationCanceledException(ct);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                // Counted as a downloader: the get_metadata call is in flight from here until
                // it returns, and the SDK does not take a cancellation token, so a stop has
                // to wait for it.
                Interlocked.Increment(ref _activeDownloaders);
                FileEntry info;
                try
                {
                    info = await dropBox.GetFileInfoAsync(row.PathDisplay, ns, true, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeDownloaders);
                    PublishDownloads();
                }

                _rateMeter.Record(
                    Stopwatch.GetElapsedTime(started),
                    DropboxRequestOutcome.Success);

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

                return;
            }
            catch (DropBoxRateLimitedException ex)
            {
                _rateMeter.Record(
                    Stopwatch.GetElapsedTime(started),
                    DropboxRequestOutcome.RateLimited);

                // Same rule as the listing: retry the call rather than lose the row's
                // dimensions. Enrichment failure is never fatal - but a rate limit is
                // not a failure, it is a queue, so it is waited out.
                if (attempt >= MaxRateLimitAttempts)
                {
                    counts.AddEnrichFailed();
                    logger.LogDebug(ex, "Media enrichment rate limited for {Path}; row kept without dimensions.", row.PathDisplay);
                    return;
                }

                var wait = ex.RetryAfterSeconds > 0
                    ? ex.RetryAfterSeconds
                    : RateLimitBackoffSeconds(attempt);

                _rateLimitBrake.Hold(wait);

                logger.LogWarning(
                    "Dropbox rate limited while reading metadata for {Path} (attempt {Attempt}/{Max}); holding the walk for {Wait}s.",
                    row.PathDisplay, attempt, MaxRateLimitAttempts, wait);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _rateMeter.Record(
                    Stopwatch.GetElapsedTime(started),
                    DropboxRequestOutcome.Failed);

                // Never let an enrichment failure abort the run or drop the row.
                counts.AddEnrichFailed();
                logger.LogDebug(ex, "Media enrichment failed for {Path}; row kept without dimensions.", row.PathDisplay);
                return;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Runs a write against <c>ml.DropBoxNode</c>, alone and with retries.
    /// <para>
    /// <b>Alone.</b> One writer at a time. The walk expands six folders in parallel and
    /// they all write to this table, and those overlapping writes deadlocked: SQL
    /// Server chose one as the victim, rolled its transaction back, and the folder was
    /// abandoned with its whole subtree. There is no cycle of locks between this app's
    /// own workers when only one of them writes at a time.
    /// </para>
    /// <para>
    /// <b>With retries.</b> A deadlock can still be declared against a writer OUTSIDE
    /// this app, and the victim's transaction is rolled back whole, so the write is
    /// simply run again - which is what SQL Server itself asks for.
    /// </para>
    /// <para>
    /// The DROPBOX side of a folder - the listing and every per-file metadata call -
    /// is not touched by any of this, and still runs six at a time.
    /// </para>
    /// </summary>
    private async Task<T> WriteAsync<T>(Func<Task<T>> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt < MaxWriteAttempts)
            {
                // The transaction was rolled back whole, so nothing was written and the
                // write can be run again unchanged.
                logger.LogWarning(
                    ex,
                    "Database write was chosen as a deadlock victim (attempt {Attempt} of {Max}); retrying.",
                    attempt, MaxWriteAttempts);

                // A short, growing pause: the other writer needs to finish and release
                // its locks before this one can take them.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    /// <summary>
    /// Runs a write against <c>ml.DropBoxNode</c> that returns nothing.
    /// </summary>
    private async Task WriteAsync(Func<Task> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await write().ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt < MaxWriteAttempts)
            {
                // The transaction was rolled back whole, so nothing was written and the
                // write can be run again unchanged.
                logger.LogWarning(
                    ex,
                    "Database write was chosen as a deadlock victim (attempt {Attempt} of {Max}); retrying.",
                    attempt, MaxWriteAttempts);

                // A short, growing pause: the other writer needs to finish and release
                // its locks before this one can take them.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    /// <summary>SQL Server's deadlock victim error number.</summary>
    private static bool IsDeadlock(SqlException ex) => ex.Number == 1205;

    /// <summary>
    /// The path hash the database computes for its natural key.
    /// <para>
    /// <c>PathHash</c> is a PERSISTED computed column:
    /// <c>hashbytes('SHA2_256', CONVERT(varbinary(8000), NamespaceId + '|' + PathLower))</c>.
    /// The CONVERT to varbinary makes the bytes UTF-16LE, NOT UTF-8 - so the hash is
    /// taken over <see cref="Encoding.Unicode"/> bytes, exactly.
    /// </para>
    /// <para>
    /// Why this matters: <c>PathLower</c> is nvarchar(2048), which is longer than
    /// SQL Server's 1700-byte index key limit, so it CANNOT be an index key column. It
    /// is only an INCLUDE on <c>IX_DropBoxNode_SyncStatus</c>, and a predicate on an
    /// included column cannot seek. Every status write therefore SCANNED the whole
    /// namespace - 240,000 rows - taking shared locks across all of them, which is
    /// what deadlocked the walk. The hash is short, indexed, and seeks in single-digit
    /// milliseconds.
    /// </para>
    /// </summary>
    private static byte[] PathHashOf(string namespaceId, string pathLower) =>
        SHA256.HashData(Encoding.Unicode.GetBytes(namespaceId + "|" + pathLower));

    /// <summary>
    /// Records a folder's outcome, and NEVER lets the recording fail the folder.
    /// <para>
    /// This is the write that used to be able to kill a whole subtree. A deadlock
    /// victim - or any other failure writing the status - propagated out of here, and
    /// the folder was abandoned with everything under it still undiscovered. That is
    /// the opposite of what a transient database error should do.
    /// </para>
    /// <para>
    /// So the write is retried when SQL Server names us a deadlock victim, and if it
    /// still fails, the failure is RECORDED and the walk carries on. The folder's
    /// status stays whatever it was, which for a folder that was never listed is
    /// null - and a null status is exactly what the next continuation picks up. The
    /// subtree is therefore still discovered by the next run, instead of being lost.
    /// </para>
    /// </summary>
    private async Task RecordOutcomeAsync(
        string ns, string path, string status, string? reason, CancellationToken ct)
    {
        try
        {
            await SetFolderStatusAsync(ns, path, status, reason, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The verdict could not be stored. The folder keeps the status it had, so
            // the next continuation lists it again - which is the correct outcome.
            logger.LogError(
                ex,
                "Could not record {Status} for folder {Path}; the folder stays unlisted and will be retried.",
                status, path);
        }
    }

    /// <summary>
    /// Sets a folder's outcome for this run.
    /// <para>
    /// A single UPDATE, not a SELECT followed by an UPDATE: this runs once per folder in
    /// the tree, and the team tree holds thousands of them. The row always exists - it
    /// was committed by the parent's flush before this folder was expanded.
    /// </para>
    /// <para>
    /// The row is found by <c>PathHash</c>, not by <c>PathLower</c>: see
    /// <see cref="PathHashOf"/> for why the path itself cannot be seeked on.
    /// </para>
    /// </summary>
    private async Task SetFolderStatusAsync(
        string ns, string path, string status, string? reason, CancellationToken ct)
    {
        var lower = path.ToLowerInvariant();
        var hash = PathHashOf(ns, lower);

        await WriteAsync(async () =>
        {
            await using var db = CreateContext();

            await db.Nodes
                .Where(n => n.NamespaceId == ns && n.PathHash == hash)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.SyncStatus, status)
                    .SetProperty(n => n.SyncStatusReason, reason), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
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

        // ONE writer at a time, like every other write to this table. This one is a
        // BULK update over thousands of rows, so overlapping another writer's locks
        // over the same rows is exactly how a deadlock is built. See WriteAsync.
        await WriteAsync(async () =>
        {
            await using var db = CreateContext();

            // Found by the path HASH: PathLower cannot be seeked on, so a predicate on
            // it would scan the whole namespace. See PathHashOf.
            var hashes = children.Select(c => PathHashOf(ns, c.Path.ToLowerInvariant())).ToList();

            await db.Nodes
                .Where(n => n.NamespaceId == ns && hashes.Contains(n.PathHash))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.SyncStatus, DropBoxSyncStatus.Partial)
                    .SetProperty(n => n.SyncStatusReason, (string?)"depth cap"), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
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

    /// <summary>
    /// A folder a previous walk wrote but never expanded - the resume point of a
    /// continued sync.
    /// <para>
    /// Its row already exists: its parent listed it, so the row carries the Id that its
    /// children need as their ParentId. That is why the Id travels with the path.
    /// </para>
    /// </summary>
    private readonly record struct SeedFolder(long Id, string Path);

    /// <summary>
    /// The team namespace the walk is scoped to.
    /// <para>
    /// Team folders are invisible from the personal space, so the walk must be scoped to
    /// the team root. Its id belongs to the account, so it is resolved here rather than
    /// pinned in this source file.
    /// </para>
    /// </summary>
    private async Task<string> ResolveNamespaceAsync(string? namespaceId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(namespaceId))
        {
            return namespaceId;
        }

        var namespaces = await dropBox.GetNamespacesAsync(ct).ConfigureAwait(false);
        var team = namespaces.FirstOrDefault(n => !n.IsPersonal);
        if (team is null)
        {
            throw new InvalidOperationException(
                "This account has no team namespace, and the personal space cannot be synced by path.");
        }

        logger.LogInformation("Sync scoped to team namespace {NamespaceId}.", team.Id);
        return team.Id;
    }

    /// <summary>
    /// The folders the index holds whose contents were never listed - the resume points
    /// of a continued sync.
    /// <para>
    /// Two kinds qualify. A folder with NO status was written by its parent's listing and
    /// never expanded, so nothing below it was ever discovered. A folder marked
    /// <c>Skipped</c> was expanded and refused (the path is gone), so its contents are
    /// still unlisted. A folder marked <c>Failed</c> hit an ERROR - a rate limit, a
    /// dropped connection - so its contents are unlisted too, and it may well list
    /// fine next time. A folder marked <c>Ok</c> is complete and is not included.
    /// </para>
    /// <para>
    /// So a continuation retries everything that is not <c>Ok</c>: the unexpanded
    /// frontier, the gone paths, and the folders that merely errored. The last group is
    /// exactly the one a retry can fix.
    /// </para>
    /// <para>
    /// The query is served by <c>IX_DropBoxNode_SyncStatus</c>, which is filtered on
    /// <c>IsFolder = 1</c> for exactly this purpose.
    /// </para>
    /// </summary>
    private async Task<List<SeedFolder>> LoadUnexpandedFoldersAsync(string ns, CancellationToken ct)
    {
        await using var db = CreateContext();

        return await db.Nodes
            .AsNoTracking()
            .Where(n => n.NamespaceId == ns
                        && n.IsFolder
                        && (n.SyncStatus == null || n.SyncStatus != DropBoxSyncStatus.Ok))
            .Select(n => new SeedFolder(n.Id, n.PathDisplay))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the verdict of the folders a continued sync is about to list again.
    /// <para>
    /// A folder that is being listed now must not keep the verdict of a run that never
    /// listed it - <c>Ok</c> would claim its contents were verified, and that is exactly
    /// what is not yet known. The status is set again by the listing itself.
    /// </para>
    /// </summary>
    private async Task ResetSeedStatusAsync(string ns, IReadOnlyList<SeedFolder> seeds, CancellationToken ct)
    {
        if (seeds.Count == 0)
        {
            return;
        }

        // ONE writer at a time, like every other write to this table. This one clears
        // the verdict of EVERY seed at once - thousands of rows - so it must not
        // overlap another writer's locks over the same rows. See WriteAsync.
        await WriteAsync(async () =>
        {
            await using var db = CreateContext();

            // The same predicate as the seed query, not a list of ids: the id list would be
            // one parameter per seed, and there are thousands of them - well past SQL
            // Server's 2100-parameter limit.
            await db.Nodes
                .Where(n => n.NamespaceId == ns
                            && n.IsFolder
                            && (n.SyncStatus == null || n.SyncStatus != DropBoxSyncStatus.Ok))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.SyncStatus, (string?)null)
                    .SetProperty(n => n.SyncStatusReason, (string?)null), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
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
