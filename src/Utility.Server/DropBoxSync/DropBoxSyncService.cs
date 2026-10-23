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
/// Streams a Dropbox subtree into <c>ml.DropBoxNode</c>.
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
/// <b>The sweep is gated and status-aware.</b> It runs only when the walk completed, and
/// it never deletes beneath a folder whose SyncStatus is anything other than 'Ok' - a
/// skipped or failed folder's children are unverified, not absent.
/// </para>
/// </summary>
public sealed class DropBoxSyncService(
    IDropBoxDataService dropBox,
    IDbContextFactory<DropBoxDbContext> contextFactory,
    ILogger<DropBoxSyncService> logger) : IDropBoxSyncService
{
    private readonly IDbContextFactory<DropBoxDbContext> _contextFactory = contextFactory;

    public async Task<DropBoxSyncResult> SyncAsync(
        string startPath,
        string? namespaceId,
        IProgress<DropBoxSyncProgress>? progress = null,
        int batchSize = 5,
        int maxDepth = 0,
        int maxConcurrency = 6,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        var sw = Stopwatch.StartNew();
        var runId = Guid.NewGuid();
        var ns = string.IsNullOrWhiteSpace(namespaceId) ? "personal" : namespaceId;
        var rootPath = startPath.TrimEnd('/');
        var rootPathLower = rootPath.ToLowerInvariant();

        // 0 = unlimited depth; used to bound runs during testing.
        var unlimited = maxDepth <= 0;
        var counts = new SyncCounts();
        var level = 1;

        try
        {
            // Every level shares one semaphore, so total in-flight requests stay bounded no
            // matter how wide the tree gets. A DbContext is NOT thread-safe, so each
            // worker takes its own from the factory.
            using var gate = new SemaphoreSlim(maxConcurrency);

            // The root folder must be upserted first: its Id is what its children get as
            // ParentId. Its own parent is outside the sync scope, so ParentId stays null.
            var rootId = await UpsertFolderAsync(ns, runId, rootPath, NameOf(rootPath), null, ct);

            // Discovered folders queue for the next level. A ConcurrentBag because from
            // the second level on, sibling folders expand in parallel and each appends to
            // it; List<T> is not thread-safe and concurrent Add can lose entries.
            var discovered = new System.Collections.Concurrent.ConcurrentBag<ChildRef>();
            await ExpandFolderAsync(ns, runId, rootPath, rootId, depth: 1,
                                    unlimited, maxDepth, gate, counts, discovered,
                                    progress, sw, ct);

            // Each depth level is its own parallel fan-out: every folder discovered at this
            // level expands concurrently, and only once all of them are stored does the
            // next level start.
            while (discovered.Count > 0 && (unlimited || level < maxDepth))
            {
                ct.ThrowIfCancellationRequested();

                // Materialise and clear BEFORE expanding: ExpandFolderAsync appends to
                // 'discovered', so iterating it while it grows would never terminate.
                var toExpand = discovered.ToList();
                discovered.Clear();
                level++;

                var nextLevel = new System.Collections.Concurrent.ConcurrentBag<ChildRef>();
                await Task.WhenAll(toExpand.Select(async child =>
                {
                    await ExpandFolderAsync(ns, runId, child.Path, child.Id, level,
                                            unlimited, maxDepth, gate, counts, nextLevel,
                                            progress, sw, ct);
                }));

                // Bag order is non-deterministic; sort for a stable, readable walk order.
                foreach (var child in nextLevel.OrderBy(c => c.Path, StringComparer.Ordinal))
                {
                    discovered.Add(child);
                }
            }

            // Anything left in 'discovered' hit the depth cap and was never expanded. Mark those
            // folders Partial so the sweep knows their subtrees are unverified.
            if (!unlimited && discovered.Count > 0)
            {
                var bounded = discovered.ToList();
                await MarkUnvisitedAsync(ns, bounded, ct);
                counts.AddBounded(bounded.Count);
            }

            var skipped = await SweepAsync(ns, runId, rootPathLower, ct);

            sw.Stop();
            logger.LogInformation(
                "Dropbox sync of {Path} completed in {ElapsedMs} ms: {Folders} folder(s), {Files} file(s), {Skipped} folder(s) not fully listed, {Bounded} bounded by depth, {Swept} row(s) swept.",
                rootPath, sw.ElapsedMilliseconds, counts.Folders, counts.Files,
                counts.NotOk, counts.BoundedFolders, skipped);

            return new DropBoxSyncResult(
                Completed: true, Swept: true,
                FoldersWritten: counts.Folders, FilesWritten: counts.Files,
                RowsDeleted: skipped, DepthReached: level, Duration: sw.Elapsed, Error: null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning("Dropbox sync of {Path} cancelled; sweep skipped, existing rows kept.", startPath);
            return new DropBoxSyncResult(false, false, counts.Folders, counts.Files, 0, level, sw.Elapsed, "Cancelled.");
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

            logger.LogError(ex, "Dropbox sync of {Path} failed; sweep skipped, existing rows kept.", startPath);
            return new DropBoxSyncResult(false, false, counts.Folders, counts.Files, 0,
                                          level, sw.Elapsed, chain.ToString().Trim());
        }
    }

    /// <summary>
    /// Lists one folder, stores every entry in it with <paramref name="parentId"/>, commits,
    /// and returns the child folders to expand next.
    /// <para>
    /// The folder's own SyncStatus is set from the outcome: Ok when the listing succeeded,
    /// Skipped when it 404s. That status is what tells the sweep whether rows beneath this
    /// folder may be deleted.
    /// </para>
    /// </summary>
    private async Task ExpandFolderAsync(
        string ns, Guid runId, string path, long folderId, int depth,
        bool unlimited, int maxDepth, SemaphoreSlim gate, SyncCounts counts,
        System.Collections.Concurrent.ConcurrentBag<ChildRef> childrenOut,
        IProgress<DropBoxSyncProgress>? progress, Stopwatch sw, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new DropBoxSyncProgress(
                path, counts.Folders, counts.Files, counts.Written, sw.Elapsed, false, depth));

            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = await dropBox.ListFolderAsync(path, ns, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 404 or no access. Mark it and move on - the folder's subtree is now
                // unverified, and its SyncStatus stops the sweep from deleting it.
                counts.AddNotOk();
                logger.LogWarning(ex, "Skipping folder {Path} during sync.", path);
                await SetFolderStatusAsync(ns, path, DropBoxSyncStatus.Skipped, Truncate(ex.Message), ct);
                return;
            }

            var pending = new List<DropBoxNodeEntity>(entries.Count);

            // Rows for the child folders of THIS listing, kept locally so their
            // generated Ids can be read back after the flush. Deriving them from this local
            // list matters: childrenOut is SHARED across the parallel workers on a level,
            // so scanning or mutating it here would touch (and remove) other workers'
            // entries.
            var queuedFolders = new List<(ChildRef Ref, DropBoxNodeEntity Row)>();

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.IsFolder)
                {
                    // Depth cap: record the folder but do not expand it. Partial, so the
                    // sweep leaves whatever is beneath it alone.
                    var atCap = !unlimited && depth >= maxDepth;
                    var folderRow = ToEntity(ns, runId, entry, folderId,
                        atCap ? DropBoxSyncStatus.Partial : null, null);
                    pending.Add(folderRow);

                    counts.AddFolders();
                    if (!atCap)
                    {
                        queuedFolders.Add((new ChildRef(entry.PathDisplay, 0), folderRow));
                    }
                }
                else
                {
                    // Files never carry a status.
                    pending.Add(ToEntity(ns, runId, entry, folderId, null, null));
                    counts.AddFiles();
                }
            }

            await FlushAsync(ns, runId, folderId, path, pending, counts, ct);

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

            // Listing succeeded: this folder is fully verified, so the sweep may clean
            // beneath it.
            await SetFolderStatusAsync(ns, path, DropBoxSyncStatus.Ok, null, ct);
        }
        finally
        {
            gate.Release();
        }
    }

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
            .FirstOrDefaultAsync(n => n.NamespaceId == ns && n.PathLower == lower, ct);

        if (existing is not null)
        {
            existing.SyncRunId = runId;
            existing.SyncedUtc = DateTimeOffset.UtcNow;
            existing.Name = name;
            existing.SyncStatus = null;   // provisional; ExpandFolderAsync sets the real one
            existing.SyncStatusReason = null;
            await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);
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
        ServerModified = entry.IsFolder ? null : entry.ServerModified,
        ClientModified = entry.IsFolder ? null : entry.ClientModified,
        IsDownloadable = entry.IsFolder ? null : entry.IsDownloadable,
        SyncStatus = status,
        SyncStatusReason = reason,
        SyncRunId = runId,
        SyncedUtc = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Commits a folder's rows in batches. Existing rows are refreshed (their SyncRunId is
    /// what tells the sweep they still exist); new rows are inserted.
    /// </summary>
    private async Task FlushAsync(
        string ns, Guid runId, long folderId, string folderPath,
        List<DropBoxNodeEntity> pending, SyncCounts counts, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var chunk in Chunk(pending, 50))
        {
            ct.ThrowIfCancellationRequested();

            await using var db = CreateContext();
            var keys = chunk.Select(p => p.PathLower).ToList();

            // ONE tracked query for the whole chunk: loading tracked means the updates
            // below need no per-row fetch.
            var existing = await db.Nodes
                .Where(n => n.NamespaceId == ns && keys.Contains(n.PathLower))
                .ToDictionaryAsync(n => n.PathLower, StringComparer.OrdinalIgnoreCase, ct);

            foreach (var row in chunk)
            {
                if (existing.TryGetValue(row.PathLower, out var tracked))
                {
                    tracked.PathDisplay = row.PathDisplay;
                    tracked.Name = row.Name;
                    tracked.IsFolder = row.IsFolder;
                    tracked.ParentId = row.ParentId ?? tracked.ParentId;
                    tracked.DropboxId = row.DropboxId;
                    tracked.SizeBytes = row.SizeBytes;
                    tracked.Rev = row.Rev;
                    tracked.ContentHash = row.ContentHash;
                    tracked.ServerModified = row.ServerModified;
                    tracked.ClientModified = row.ClientModified;
                    tracked.IsDownloadable = row.IsDownloadable;
                    // Only overwrite an existing status when the incoming row actually carries one.
                    // A folder queued for expansion arrives with null (its real status is
                    // set once it has been listed), and clobbering here would erase a
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

            await db.SaveChangesAsync(ct);
            counts.AddWritten(chunk.Count);
        }

        pending.Clear();
    }

    /// <summary>Sets a folder's outcome for this run.</summary>
    private async Task SetFolderStatusAsync(
        string ns, string path, string status, string? reason, CancellationToken ct)
    {
        await using var db = CreateContext();
        var lower = path.ToLowerInvariant();

        var row = await db.Nodes.FirstOrDefaultAsync(
            n => n.NamespaceId == ns && n.PathLower == lower, ct);

        if (row is null)
        {
            return;
        }

        row.SyncStatus = status;
        row.SyncStatusReason = reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Folders left unexpanded because the depth cap stopped the walk. They must not look
    /// 'Ok' or the sweep would delete subtrees that were never examined.
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
                .SetProperty(n => n.SyncStatusReason, (string?)"depth cap"), ct);
    }

    /// <summary>
    /// Deletes rows this run did not touch, bounded to the start path, and never beneath a
    /// folder that was not fully listed.
    /// <para>
    /// A single DELETE handles the whole subtree: SQL Server checks the self-referencing
    /// FK per row, so deleting a parent and its children together is allowed even though
    /// the FK is NO ACTION. Deleting a parent alone would be blocked, so this must stay
    /// one statement.
    /// </para>
    /// </summary>
    private async Task<int> SweepAsync(string ns, Guid runId, string rootPathLower, CancellationToken ct)
    {
        await using var db = CreateContext();

        // Protected prefixes: folders whose status is anything other than 'Ok' have
        // unverified subtrees, so nothing beneath them may be deleted. The guard is
        // evaluated in SQL against the table itself (not an in-memory list), so it stays
        // correct even when several syncs run as separate processes.
        //
        // LIKE wildcards inside a stored path are escaped with the standard [...] form, so
        // a folder literally named "DC_SHOES" cannot match "DCxSHOES".
        // A NON-interpolated raw string, so '{{0}}' is not interpolated here; EF's
        // ExecuteSqlRawAsync turns each {n} into a real parameter. A '$' prefix would make
        // the braces ambiguous to the raw-string parser (CS9006), so the doubling that a
        // '$' would require must be undone here.
        var sql = """
            DELETE n
            FROM ml.DropBoxNode AS n
            WHERE n.NamespaceId = {0}
              AND n.SyncRunId <> {1}
              AND (n.PathLower = {2} OR n.PathLower LIKE {3})
              AND NOT EXISTS (
                    SELECT 1
                    FROM ml.DropBoxNode AS guard
                    WHERE guard.NamespaceId = n.NamespaceId
                      AND guard.IsFolder = 1
                      AND guard.SyncStatus IS NOT NULL
                      AND guard.SyncStatus <> {4}
                      AND guard.PathLower <> n.PathLower
                      AND n.PathLower LIKE guard.PathLower + N'/%'
              )
            """;

        var deleted = await db.Database.ExecuteSqlRawAsync(sql,
            ns,
            runId.ToString(),
            rootPathLower,
            rootPathLower + "/%",
            DropBoxSyncStatus.Ok);

        db.ChangeTracker.Clear();
        return deleted;
    }

    /// <summary>Escapes LIKE wildcards so a folder name cannot widen the match.</summary>
    private static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

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

    /// <summary>Mutable counters shared across the parallel workers.</summary>
    private sealed class SyncCounts
    {
        private int _folders;
        private int _files;
        private int _written;
        private int _notOk;
        private int _bounded;

        // Properties are plain reads; mutation goes through the Add* methods. A property
        // with an Interlocked side effect would increment on every log line that reads it.
        public int Folders => Volatile.Read(ref _folders);
        public int Files => Volatile.Read(ref _files);
        public int Written => Volatile.Read(ref _written);
        public int NotOk => Volatile.Read(ref _notOk);
        public int BoundedFolders => Volatile.Read(ref _bounded);

        public void AddFolders(int n = 1) => Interlocked.Add(ref _folders, n);
        public void AddFiles(int n = 1) => Interlocked.Add(ref _files, n);
        public void AddWritten(int n) => Interlocked.Add(ref _written, n);
        public void AddNotOk(int n = 1) => Interlocked.Add(ref _notOk, n);
        public void AddBounded(int n) => Interlocked.Add(ref _bounded, n);
    }

    private static string Truncate(string message, int max = 256) =>
        string.IsNullOrEmpty(message) ? string.Empty
            : message.Length <= max ? message : message[..max];
}