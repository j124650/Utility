using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Utility.DropBox;
using Utility.DropBox.Models;
using Utility.DropBox.Services;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// The sync in two phases: LIST, then ENRICH.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 is the walk, with no per-file metadata call: the existing walk, run with
/// <c>enrichFileMetadata: false</c>. The flush's dimension rule is what makes that safe —
/// a file whose content hash is unchanged KEEPS its dimensions, and a file whose content
/// hash changed has them cleared and is marked <c>'pending'</c>.
/// </para>
/// <para>
/// Phase 2 reads the rows marked <c>'pending'</c> and fetches their metadata, and only
/// theirs. It is the same work the walk used to do inline, with the same gates, the same
/// shared brake and the same single writer.
/// </para>
/// </remarks>
public sealed class DropBoxSyncTwoPhaseService(
    IDropBoxSyncService sync,
    IDropBoxDataService dropBox,
    IDbContextFactory<DropBoxDbContext> contextFactory,
    DropBoxWriteGate writeGate,
    ILogger<DropBoxSyncTwoPhaseService> logger) : IDropBoxSyncTwoPhaseService
{
    /// <summary>
    /// The walk's width — ONE number for every layer, and this pass is a layer of the
    /// same walk.
    /// </summary>
    private const int Width = DropBoxSyncService.WalkWidth;

    /// <summary>
    /// The batch size. The interface's default is 50 and the concrete walk's is 500; a
    /// call through the interface gets 50, which is what the app does today, so this
    /// asks for it explicitly rather than depending on which overload is picked.
    /// </summary>
    /// <summary>
    /// How many rows one flush carries. The same size the app's own walk uses.
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// How many times a rate-limited call is retried. The SDK does NOT retry a 429 — its
    /// retry budget covers server errors only — so this is what stands between a rate
    /// limit and a lost file.
    /// </summary>
    private const int MaxRateLimitAttempts = 5;

    /// <summary>
    /// The wait after a rate limit, in seconds, growing with the attempt.
    /// </summary>
    private static int RateLimitBackoffSeconds(int attempt) => Math.Min(300, 5 * (1 << (attempt - 1)));

    // ------------------------------------------------------------------ phase 1

    public Task<DropBoxSyncResult> SyncStructureFrom(
        string startPath,
        string? namespaceId = null,
        IProgress<DropBoxSyncProgress>? progress = null,
        int maxDepth = 0,
        CancellationToken ct = default) =>
        sync.SyncAsync(
            startPath,
            namespaceId,
            progress,
            batchSize: BatchSize,
            maxDepth: maxDepth,
            enrichFileMetadata: false,
            ct);

    public Task<DropBoxSyncResult> ContinueSyncStructureFrom(
        IProgress<DropBoxSyncProgress>? progress = null,
        CancellationToken ct = default) =>
        sync.ContinueAsync(
            progress,
            batchSize: BatchSize,
            maxDepth: 0,
            enrichFileMetadata: false,
            ct);

    // ------------------------------------------------------------------ phase 2

    public async Task<DropBoxTwoPhaseEnrichResult> EnrichAsync(
        string? namespaceId = null,
        string? pathPrefix = null,
        IProgress<DropBoxTwoPhaseEnrichProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // The namespace is resolved the same way the walk resolves it: a null means the
        // account's team namespace. It used to be the literal string "personal", and
        // no row carries that - so the work list matched nothing and the pass did
        // nothing, whatever the button was pressed.
        var ns = await sync.ResolveNamespaceAsync(namespaceId, ct).ConfigureAwait(false);

        // THE WORK LIST: the rows marked 'pending'. The schema already defines what
        // 'pending' means - the dimensions may be available on a re-read - so this set
        // needs no new vocabulary.
        var pending = await ReadPendingAsync(ns, pathPrefix, ct);

        if (pending.Count == 0)
        {
            sw.Stop();

            return new DropBoxTwoPhaseEnrichResult(
                Completed: true, Pending: 0, Enriched: 0, Failed: 0, StillPending: 0,
                Duration: sw.Elapsed, Error: null);
        }

        logger.LogInformation(
            "Enriching {Count} file(s) whose dimensions are unknown in {Namespace}.", pending.Count, ns);

        // The same three pieces the walk uses: a gate that bounds the in-flight calls, a
        // brake that holds EVERY worker when Dropbox rate-limits (the limit is per
        // authorization, not per request), and one writer at a time for the table.
        using var gate = new AdaptiveConcurrencyGate(Width);
        var brake = new DropboxRateLimitBrake();

        // The dimensions the workers have fetched, waiting to be written in batches.
        var queued = new ConcurrentQueue<(long Id, FileEntry Info)>();

        var enriched = 0;
        var failed = 0;

        await WorkerPool.RunAsync(
            pending,
            Width,
            async (row, ct2) =>
            {
                var ok = await EnrichOneAsync(row, ns, gate, brake, queued, ct2);

                if (ok)
                {
                    Interlocked.Increment(ref enriched);

                    // Write what has piled up, rather than waiting for the pass to end.
                    if (queued.Count >= BatchSize)
                    {
                        await FlushAsync(queued, all: false, ct2).ConfigureAwait(false);
                    }
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }

                progress?.Report(new DropBoxTwoPhaseEnrichProgress(
                    Volatile.Read(ref enriched) + Volatile.Read(ref failed),
                    pending.Count,
                    row.PathDisplay));
            },
            ct).ConfigureAwait(false);

        // Whatever is left in the queue is written now, so the count below sees the
        // pass's own work.
        await FlushAsync(queued, all: true, ct).ConfigureAwait(false);

        var stillPending = await CountPendingAsync(ns, pathPrefix, ct);

        sw.Stop();

        return new DropBoxTwoPhaseEnrichResult(
            Completed: true,
            Pending: pending.Count,
            Enriched: enriched,
            Failed: failed,
            StillPending: stillPending,
            Duration: sw.Elapsed,
            Error: null);
    }

    /// <summary>
    /// Fetches one file's metadata and writes its dimensions.
    /// <para>
    /// The dimensions are FILLED, never cleared: a file whose media info comes back
    /// empty keeps what it had, because the content did not change - only phase 1 may
    /// clear a dimension, and only because the content changed.
    /// </para>
    /// </summary>
    private async Task<bool> EnrichOneAsync(
        PendingRow row,
        string ns,
        AdaptiveConcurrencyGate gate,
        DropboxRateLimitBrake brake,
        ConcurrentQueue<(long Id, FileEntry Info)> queued,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // The brake is waited on BEFORE the permit, for the same reason as in the
            // listing: a rate limit pauses every worker, not just this one.
            await brake.WaitAsync(ct).ConfigureAwait(false);

            // The permit is taken without throwing when the pass is stopped, so a worker
            // waiting on a full gate does not raise one exception per waiter.
            if (!await gate.TryWaitAsync(ct).ConfigureAwait(false))
            {
                throw new OperationCanceledException(ct);
            }

            try
            {
                // includeMediaInfo: TRUE. Without it the API returns no media info at
                // all, and the dimensions read as NULL - which would silently clear
                // every dimension the pass touched.
                var info = await dropBox.GetFileInfoAsync(row.PathDisplay, ns, true, ct).ConfigureAwait(false);

                // The dimensions are QUEUED, not written. A write per file is two round
                // trips - the row's load and its update - and eight hundred files make
                // sixteen hundred round trips for a pass whose real work is eight
                // hundred API calls. The queue is drained in batches instead, so the
                // whole pass costs a handful of round trips.
                queued.Enqueue((row.Id, info));

                return true;
            }
            catch (DropBoxRateLimitedException ex) when (attempt < MaxRateLimitAttempts)
            {
                // The API said how long to wait: honour it, and hold every worker.
                var seconds = ex.RetryAfterSeconds > 0
                    ? ex.RetryAfterSeconds
                    : RateLimitBackoffSeconds(attempt);

                logger.LogWarning(
                    "Rate limited while reading {Path}; holding for {Seconds}s (attempt {Attempt} of {Max}).",
                    row.PathDisplay, seconds, attempt, MaxRateLimitAttempts);

                brake.Hold(seconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not enrich {Path}.", row.PathDisplay);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Writes one file's dimensions — through the SHARED write gate, so this pass and
    /// the walk can never be writing at the same time.
    /// </summary>
    private async Task WriteDimensionsAsync(
        List<(long Id, FileEntry Info)> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await writeGate.WriteAsync(async () =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // ONE load for the whole batch, and ONE update for the whole batch: two
            // round trips for five hundred files, where it used to be two for each.
            var ids = batch.Select(b => b.Id).ToList();

            var rows = await db.Nodes
                .Where(n => ids.Contains(n.Id))
                .ToDictionaryAsync(n => n.Id, ct)
                .ConfigureAwait(false);

            foreach (var (id, info) in batch)
            {
                if (!rows.TryGetValue(id, out var row))
                {
                    continue;
                }

                // The dimensions are FILLED, never cleared: a read that carried no media
                // info must not wipe what is already known.
                if (info.WidthPixels is not null)
                {
                    row.WidthPixels = (int)info.WidthPixels.Value;
                }

                if (info.HeightPixels is not null)
                {
                    row.HeightPixels = (int)info.HeightPixels.Value;
                }

                // The media kind is the schema's own vocabulary: 'photo', 'video',
                // 'pending' - and null for a file that is neither. A read that said
                // 'pending' means Dropbox deferred it, so the row keeps asking.
                if (info.MediaKind is not null)
                {
                    row.MediaInfoTag = info.MediaKind;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes what has been queued and writes it, up to a batch at a time.
    /// <para>
    /// The queue is drained by whoever gets there first - the workers as they fill it,
    /// and the pass itself at the end - so the rows are written while the pass is still
    /// running, not only when it is over.
    /// </para>
    /// </summary>
    private async Task FlushAsync(
        ConcurrentQueue<(long Id, FileEntry Info)> queued,
        bool all,
        CancellationToken ct)
    {
        while (true)
        {
            var batch = new List<(long Id, FileEntry Info)>();

            while (batch.Count < BatchSize && queued.TryDequeue(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
            {
                return;
            }

            await WriteDimensionsAsync(batch, ct).ConfigureAwait(false);

            if (!all)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ both

    public async Task<DropBoxTwoPhaseResult> SyncFrom(
        string startPath,
        string? namespaceId = null,
        IProgress<DropBoxSyncProgress>? listProgress = null,
        IProgress<DropBoxTwoPhaseEnrichProgress>? enrichProgress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var list = await SyncStructureFrom(startPath, namespaceId, listProgress, ct: ct).ConfigureAwait(false);

        if (!list.Completed)
        {
            sw.Stop();

            return new DropBoxTwoPhaseResult(
                false, list, default, sw.Elapsed, list.Error ?? "The listing did not finish.");
        }

        var enrich = await EnrichAsync(namespaceId, null, enrichProgress, ct).ConfigureAwait(false);

        sw.Stop();

        return new DropBoxTwoPhaseResult(
            enrich.Completed, list, enrich, sw.Elapsed, enrich.Error);
    }

    // ------------------------------------------------------------------ the work list

    /// <summary>One row of the work list.</summary>
    private sealed record PendingRow(long Id, string PathDisplay);

    /// <summary>
    /// THE WORK LIST: the files whose dimensions are NOT known.
    /// <para>
    /// The schema's own word for a file still asking is 'pending' - but a file the
    /// one-phase walk never got to carries NO tag at all. That walk fetched each file's
    /// metadata as it went, which is 47 hours of work for this catalogue, so it never
    /// finished: 44,271 files sit with no dimensions and no tag. A work list of
    /// 'pending' rows is therefore EMPTY while tens of thousands of files wait for
    /// their dimensions.
    /// </para>
    /// <para>
    /// The rule is the one the schema already implies - a row with unknown dimensions
    /// is still enriched, and a hash match does not make an incomplete row complete.
    /// So the set is the files whose dimensions are unknown, plus the rows still tagged
    /// 'pending', which is the same set plus the rare row that has its dimensions and
    /// is still asking.
    /// </para>
    /// </summary>
    private static IQueryable<Entities.DropBoxNode> NeedsDimensions(IQueryable<Entities.DropBoxNode> query) =>
        query.Where(n => !n.IsFolder &&
            (n.WidthPixels == null || n.HeightPixels == null || n.MediaInfoTag == "pending"));

    private async Task<List<PendingRow>> ReadPendingAsync(string ns, string? pathPrefix, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = NeedsDimensions(db.Nodes)
            .Where(n => n.NamespaceId == ns);

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            // PathLower cannot be seeked on - it is too long to be an index key - so this
            // predicate SCANS the namespace. It is a pass over the whole table, and the
            // pass is worth it: it is the work list, read once.
            var prefix = pathPrefix.ToLowerInvariant();

            query = query.Where(n => n.PathLower != null && n.PathLower.StartsWith(prefix));
        }

        return await query
            .OrderBy(n => n.Id)
            .Select(n => new PendingRow(n.Id, n.PathDisplay))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<int> CountPendingAsync(string ns, string? pathPrefix, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = NeedsDimensions(db.Nodes)
            .Where(n => n.NamespaceId == ns);

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            var prefix = pathPrefix.ToLowerInvariant();

            query = query.Where(n => n.PathLower != null && n.PathLower.StartsWith(prefix));
        }

        return await query.CountAsync(ct).ConfigureAwait(false);
    }
}
