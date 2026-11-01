using System.Diagnostics;
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
    private const int BatchSize = 50;

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
        var ns = namespaceId ?? "personal";

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
            "Enriching {Count} row(s) marked 'pending' in {Namespace}.", pending.Count, ns);

        // The same three pieces the walk uses: a gate that bounds the in-flight calls, a
        // brake that holds EVERY worker when Dropbox rate-limits (the limit is per
        // authorization, not per request), and one writer at a time for the table.
        using var gate = new AdaptiveConcurrencyGate(Width);
        var brake = new DropboxRateLimitBrake();

        var enriched = 0;
        var failed = 0;

        await WorkerPool.RunAsync(
            pending,
            Width,
            async (row, ct2) =>
            {
                var ok = await EnrichOneAsync(row, ns, gate, brake, ct2);

                if (ok)
                {
                    Interlocked.Increment(ref enriched);
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

                await WriteDimensionsAsync(row.Id, info, ct).ConfigureAwait(false);

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
    private async Task WriteDimensionsAsync(long id, FileEntry info, CancellationToken ct)
    {
        await writeGate.WriteAsync(async () =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var row = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id, ct).ConfigureAwait(false);

            if (row is null)
            {
                return;
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

            // The media kind is the schema's own vocabulary: 'photo', 'video', 'pending'
            // - and null for a file that is neither. A read that said 'pending' means
            // Dropbox deferred it, so the row keeps asking.
            if (info.MediaKind is not null)
            {
                row.MediaInfoTag = info.MediaKind;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
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

    private async Task<List<PendingRow>> ReadPendingAsync(string ns, string? pathPrefix, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = db.Nodes
            .Where(n => n.NamespaceId == ns && !n.IsFolder && n.MediaInfoTag == "pending");

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

        var query = db.Nodes
            .Where(n => n.NamespaceId == ns && !n.IsFolder && n.MediaInfoTag == "pending");

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            var prefix = pathPrefix.ToLowerInvariant();

            query = query.Where(n => n.PathLower != null && n.PathLower.StartsWith(prefix));
        }

        return await query.CountAsync(ct).ConfigureAwait(false);
    }
}
