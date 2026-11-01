using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dropbox.Api;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Utility.DropBox;
using Utility.Server;

namespace SyncTwoPhase;

/// <summary>
/// TESTS THE TWO-PHASE SYNC against the live database.
///
/// Phase 1  LIST   - walk the subtree with the non-recursive files/list_folder, and
///                   upsert every row. A file whose listing content hash DIFFERS from
///                   the stored one is marked stale: MediaInfoTag = 'pending' and the
///                   dimensions are cleared. A file whose hash MATCHES keeps its tag and
///                   its dimensions - they are still true, because the content is the
///                   same.
/// Phase 2  ENRICH - fetch the metadata for the rows marked 'pending', and only those.
///
/// It then VERIFIES: the rows after both phases must be the same as they were before,
/// on every column that means something. The bookkeeping columns (SyncedUtc, SyncRunId)
/// are expected to move.
///
/// It also measures what the two phases cost in API calls, against what the one-phase
/// walk costs for the same subtree.
///
/// It snapshots the subtree's rows first, and restores them if the verification fails.
/// </summary>
internal static class Program
{
    private const string Subtree = "/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Umbro/F26";
    private const string Namespace = "15193064371";

    private static async Task<int> Main(string[] args)
    {
        var write = args.Contains("--write");
        var simulateChange = args.Contains("--simulate-change");
        var checkHash = args.Contains("--check-hash");
        var probeFile = args.FirstOrDefault(a => a.StartsWith("--probe-file="))?.Split('=', 2)[1];
        var scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch");
        Directory.CreateDirectory(scratch);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddUtilityServer(builder.Configuration);
        builder.Services.AddUtilityDropBox(builder.Configuration);

        using var host = builder.Build();

        var factory = host.Services.GetRequiredService<IDropBoxClientFactory>();
        var client = await factory.CreateClientForNamespaceAsync(Namespace, CancellationToken.None);
        var connectionString = await ConnectionStringAsync(host);

        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();

        if (probeFile is not null)
        {
            var info = await ReadFileInfoAsync(client, probeFile);

            Console.WriteLine($"get_metadata on {probeFile}");
            Console.WriteLine($"  media kind : {info.MediaKind ?? "(null)"}");
            Console.WriteLine($"  dimensions : {info.WidthPixels} x {info.HeightPixels}");
            Console.WriteLine();

            return 0;
        }

        // ---------------- the listing, and the cursor mechanics ----------------
        Console.WriteLine("=== THE LISTING, AND WHAT THE CURSOR ENDPOINTS ACTUALLY DO ===");
        Console.WriteLine();

        var listing = await ListFolderAsync(client, Subtree);
        Console.WriteLine($"list_folder(recursive: false) on {Subtree}");
        Console.WriteLine($"  entries      : {listing.Entries.Count:N0}");
        Console.WriteLine($"  has more     : {listing.HasMore}");
        Console.WriteLine($"  cursor       : {(string.IsNullOrEmpty(listing.Cursor) ? "(none)" : listing.Cursor[..Math.Min(60, listing.Cursor.Length)] + "…")}");
        Console.WriteLine();

        // The cursor the listing ended with. Feeding it back to list_folder/continue
        // must give the CHANGES since that point - which, right now, is none.
        var delta = await client.Files.ListFolderContinueAsync(listing.Cursor);
        Console.WriteLine("list_folder/continue with the listing's own cursor:");
        Console.WriteLine($"  entries      : {delta.Entries.Count:N0}  (the delta since the listing)");
        Console.WriteLine($"  has more     : {delta.HasMore}");
        Console.WriteLine();

        // A long-poll would tell us when something changes, without asking again. The
        // SDK carries the ARG and RESULT types but exposes NO route method for them.
        var longpollMethod = typeof(Dropbox.Api.Files.Routes.FilesAppRoutes)
            .GetMethods()
            .FirstOrDefault(m => m.Name.Contains("Longpoll") || m.Name.Contains("LatestCursor"));

        Console.WriteLine($"SDK route method for longpoll / get_latest_cursor: {(longpollMethod is null ? "NONE - the types exist, the endpoints are not exposed" : longpollMethod.Name)}");
        Console.WriteLine();

        // ---------------- the snapshot ----------------
        var before = await ReadRowsAsync(db, Subtree);
        Console.WriteLine($"=== THE DATABASE BEFORE ({before.Count:N0} row(s) under the path) ===");

        var folders = before.Count(r => r.IsFolder);
        var files = before.Count(r => !r.IsFolder);

        Console.WriteLine($"  folders: {folders:N0}   files: {files:N0}");
        Console.WriteLine($"  files with dimensions : {before.Count(r => r.WidthPixels is not null && r.HeightPixels is not null):N0}");
        Console.WriteLine($"  files tagged 'pending': {before.Count(r => r.MediaInfoTag == "pending"):N0}");
        Console.WriteLine($"  files tagged 'photo'  : {before.Count(r => r.MediaInfoTag == "photo"):N0}");
        Console.WriteLine();

        if (checkHash)
        {
            var stored = await db.QueryAsync<HashRow>(
                "SELECT PathLower, PathHash FROM ml.DropBoxNode WHERE PathLower = @p OR PathLower LIKE @like",
                new { p = Subtree.ToLowerInvariant(), like = Subtree.ToLowerInvariant() + "/%" });

            var wrong = 0;

            foreach (var row in stored)
            {
                var computed = PathHash(Namespace, row.PathLower);

                if (!computed.AsSpan().SequenceEqual(row.PathHash))
                {
                    wrong++;

                    if (wrong <= 3)
                    {
                        Console.WriteLine($"  HASH MISMATCH for {row.PathLower}");
                        Console.WriteLine($"    computed: {Convert.ToHexString(computed)}");
                        Console.WriteLine($"    stored  : {Convert.ToHexString(row.PathHash)}");
                    }
                }
            }

            Console.WriteLine($"  rows checked: {stored.Count:N0}, hash mismatches: {wrong:N0}");
            Console.WriteLine();

            return 0;
        }

        await File.WriteAllTextAsync(Path.Combine(scratch, "sync-two-phase-before.json"), Snapshot(before));

        // --- simulate a CHANGED file -------------------------------------------
        // A file whose content changed must be marked 'pending' by phase 1, lose its
        // dimensions, and be re-enriched by phase 2 - and end up exactly as it was.
        // The change is simulated in the DATABASE (the content hash is corrupted),
        // so the listing still says the file is unchanged.
        Row? corrupted = null;

        if (simulateChange)
        {
            corrupted = before.First(r => !r.IsFolder);

            await db.ExecuteAsync(
                "UPDATE ml.DropBoxNode SET ContentHash = @bogus WHERE Id = @id",
                new { bogus = "SIMULATED-CHANGE", id = corrupted.Id });

            Console.WriteLine($"SIMULATED A CHANGE: {corrupted.PathLower}");
            Console.WriteLine($"  its row's Name column is: '{corrupted.Name}'");
            Console.WriteLine($"  its row's DropboxId is : {corrupted.DropboxId}");
            Console.WriteLine($"  its stored content hash is now wrong; it was {corrupted.ContentHash}");
            Console.WriteLine();
        }

        // ---------------- PHASE 1: LIST ----------------
        Console.WriteLine("=== PHASE 1: LIST ===");

        var phase1 = await Phase1Async(client, db, before, write);
        Console.WriteLine($"  list_folder calls : {phase1.ListCalls:N0}");
        Console.WriteLine($"  rows written      : {phase1.RowsWritten:N0}");
        Console.WriteLine($"  kept (unchanged)  : {phase1.KeptUnchanged:N0}");
        Console.WriteLine($"  elapsed           : {phase1.Elapsed.TotalSeconds:N1} s");

        // How many rows are now marked 'pending' - read from the LIVE rows, because
        // that is what phase 2 will work on.
        var pendingNow = await db.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM ml.DropBoxNode WHERE IsFolder = 0 AND MediaInfoTag = 'pending' AND PathLower LIKE @p",
            new { p = Subtree.ToLowerInvariant() + "%" });

        Console.WriteLine($"  marked 'pending'  : {pendingNow:N0}   (the rows phase 2 must enrich)");
        Console.WriteLine();

        // What phase 1 did to the changed file.
        if (corrupted is not null)
        {
            var now = await db.QuerySingleAsync<Row>(
                "SELECT Id, NamespaceId, PathDisplay, PathLower, Name, IsFolder, ParentId, DropboxId, SizeBytes, Rev, ContentHash, WidthPixels, HeightPixels, MediaInfoTag, ServerModified, ClientModified, IsDownloadable, SyncStatus FROM ml.DropBoxNode WHERE Id = @id",
                new { id = corrupted.Id });

            Console.WriteLine($"  after phase 1, the changed file is: tag '{now.MediaInfoTag}', {now.WidthPixels} x {now.HeightPixels}");
            Console.WriteLine();
        }

        // ---------------- PHASE 2: ENRICH ----------------
        Console.WriteLine("=== PHASE 2: ENRICH ===");

        var phase2 = await Phase2Async(client, db, Subtree, write);
        Console.WriteLine($"  get_metadata calls: {phase2.Calls:N0}   (the rows tagged 'pending')");
        Console.WriteLine($"  enriched          : {phase2.Enriched:N0}");
        Console.WriteLine($"  elapsed           : {phase2.Elapsed.TotalSeconds:N1} s");
        Console.WriteLine();

        // ---------------- VERIFY ----------------
        Console.WriteLine("=== VERIFY: the rows after both phases vs before ===");

        var after = await ReadRowsAsync(db, Subtree);
        var differences = Compare(before, after);

        if (differences.Count == 0)
        {
            Console.WriteLine("  the rows are IDENTICAL on every column that means something.");
        }
        else
        {
            Console.WriteLine($"  {differences.Count:N0} DIFFERENCE(S):");

            foreach (var difference in differences.Take(20))
            {
                Console.WriteLine($"    {difference}");
            }
        }

        Console.WriteLine();

        // ---------------- THE COMPARISON THAT MATTERS ----------------
        Console.WriteLine("=== THE COST OF THE TWO PHASES vs THE ONE-PHASE WALK ===");
        Console.WriteLine();
        Console.WriteLine($"  one-phase walk (today) : {phase1.ListCalls:N0} listings + {files:N0} get_metadata = {phase1.ListCalls + files:N0} calls");
        Console.WriteLine($"  two-phase              : {phase1.ListCalls:N0} listings + {phase2.Calls:N0} get_metadata = {phase1.ListCalls + phase2.Calls:N0} calls");
        Console.WriteLine($"  saved                  : {files - phase2.Calls:N0} of {files:N0} get_metadata calls ({100.0 * (files - phase2.Calls) / Math.Max(1, files):N1}%)");
        Console.WriteLine();

        if (differences.Count > 0)
        {
            Console.WriteLine("The rows differ - restoring the snapshot.");
            await RestoreAsync(db, before);
            Console.WriteLine("Restored.");
        }

        return differences.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the listing

    private sealed record Listing(List<Dropbox.Api.Files.Metadata> Entries, string Cursor, bool HasMore, int Calls);

    private static async Task<Listing> ListFolderAsync(DropboxClient client, string path)
    {
        var entries = new List<Dropbox.Api.Files.Metadata>();
        var calls = 1;

        var result = await client.Files.ListFolderAsync(path, recursive: false, includeMediaInfo: false);

        entries.AddRange(result.Entries);

        while (result.HasMore)
        {
            result = await client.Files.ListFolderContinueAsync(result.Cursor);
            calls++;
            entries.AddRange(result.Entries);
        }

        return new Listing(entries, result.Cursor, result.HasMore, calls);
    }

    // ---------------------------------------------------------------- phase 1

    private sealed record Phase1Result(int ListCalls, int RowsWritten, int MarkedPending, int KeptUnchanged, TimeSpan Elapsed);

    private static async Task<Phase1Result> Phase1Async(
        DropboxClient client, SqlConnection db, List<Row> before, bool write)
    {
        var sw = Stopwatch.StartNew();

        var calls = 0;
        var written = 0;
        var marked = 0;
        var kept = 0;

        var byPath = before.ToDictionary(r => r.PathLower, StringComparer.Ordinal);

        // Level 1: the folders under the path.
        var root = await db.QuerySingleAsync<Row>(
            "SELECT TOP 1 Id, NamespaceId, PathDisplay, PathLower, Name, IsFolder, ParentId, DropboxId, SizeBytes, Rev, ContentHash, WidthPixels, HeightPixels, MediaInfoTag, ServerModified, ClientModified, IsDownloadable, SyncStatus FROM ml.DropBoxNode WHERE PathLower = @p",
            new { p = Subtree.ToLowerInvariant() });

        // The folders, then their files - the level order the rows need, because a
        // child's ParentId is its parent's Id.
        var folderListing = await ListFolderAsync(client, Subtree);
        calls += folderListing.Calls;

        var folderIds = new Dictionary<string, long>(StringComparer.Ordinal) { [root.PathLower] = root.Id };

        foreach (var entry in folderListing.Entries.Where(e => e.IsFolder))
        {
            var path = entry.PathDisplay;
            var lower = path.ToLowerInvariant();
            var id = await UpsertAsync(db, path, lower, entry.Name, true, folderIds[lower[..(lower.LastIndexOf('/') + 1)].TrimEnd('/')], entry, null, null, null, write);

            folderIds[lower] = id;
            written++;

            var stored = byPath.GetValueOrDefault(lower);

            if (stored is not null && stored.ContentHash is not null && stored.ContentHash == entry.AsFile.ContentHash)
            {
                kept++;
            }
        }

        // Level 2: the files under each folder.
        foreach (var folder in folderListing.Entries.Where(e => e.IsFolder))
        {
            var listing = await ListFolderAsync(client, folder.PathDisplay);
            calls += listing.Calls;

            foreach (var entry in listing.Entries.Where(e => !e.IsFolder))
            {
                var path = entry.PathDisplay;
                var lower = path.ToLowerInvariant();
                var stored = byPath.GetValueOrDefault(lower);

                var changed = stored is null || stored.ContentHash != entry.AsFile.ContentHash;

                await UpsertAsync(db, path, lower, entry.Name, false, folderIds[lower[..(lower.LastIndexOf('/') + 1)].TrimEnd('/')], entry, changed, stored?.WidthPixels, stored?.HeightPixels, write);

                written++;

                if (changed)
                {
                    marked++;
                }
                else
                {
                    kept++;
                }
            }
        }

        sw.Stop();

        return new Phase1Result(calls, written, marked, kept, sw.Elapsed);
    }

    /// <summary>
    /// Upserts one row, and returns its Id.
    ///
    /// The CASE is the whole design: when the listing's content hash EQUALS the stored
    /// one, the row's dimensions and its media kind are KEPT - the content is the same,
    /// so they are still true. When it DIFFERS, the row is marked 'pending' and its
    /// dimensions are cleared, because they may no longer be true.
    /// </summary>
    private static async Task<long> UpsertAsync(
        SqlConnection db, string path, string lower, string name, bool isFolder, long parentId,
        Dropbox.Api.Files.Metadata entry, bool? changed, long? width, long? height, bool write)
    {
        var hash = PathHash(Namespace, lower);

        var id = isFolder ? entry.AsFolder.Id : entry.AsFile.Id;
        var rev = isFolder ? null : entry.AsFile.Rev;
        var size = isFolder ? (long?)null : (long)entry.AsFile.Size;
        var serverModified = isFolder ? (DateTimeOffset?)null : new DateTimeOffset(entry.AsFile.ServerModified, TimeSpan.Zero);
        var clientModified = isFolder ? (DateTimeOffset?)null : new DateTimeOffset(entry.AsFile.ClientModified, TimeSpan.Zero);
        var downloadable = isFolder ? (bool?)null : entry.AsFile.IsDownloadable;
        var contentHash = isFolder ? null : entry.AsFile.ContentHash;

        if (!write)
        {
            return 0;
        }

        // The UPDATE sees the OLD row, so ContentHash = @ch compares the old hash with
        // the new one.
        var affected = await db.ExecuteAsync(
            """
            UPDATE ml.DropBoxNode
               SET PathDisplay = @pd, PathLower = @pl, Name = @nm, IsFolder = @f, ParentId = @pid,
                   DropboxId = @dbi, SizeBytes = @sz, Rev = @rev, ContentHash = @ch,
                   ServerModified = @sm, ClientModified = @cm, IsDownloadable = @dl,
                   MediaInfoTag  = CASE WHEN @f = 1 THEN NULL
                                        WHEN ContentHash = @ch THEN MediaInfoTag ELSE 'pending' END,
                   WidthPixels   = CASE WHEN @f = 1 THEN NULL
                                        WHEN ContentHash = @ch THEN WidthPixels ELSE NULL END,
                   HeightPixels  = CASE WHEN @f = 1 THEN NULL
                                        WHEN ContentHash = @ch THEN HeightPixels ELSE NULL END,
                   SyncStatus = 'Ok', SyncStatusReason = NULL, SyncedUtc = SYSUTCDATETIME()
                 WHERE NamespaceId = @ns AND PathHash = @hash
            """,
            new { pd = path, pl = lower, nm = name, f = isFolder, pid = parentId, dbi = id, sz = size, rev = rev, ch = contentHash, sm = serverModified, cm = clientModified, dl = downloadable, ns = Namespace, hash });

        if (affected > 0)
        {
            return await db.QuerySingleAsync<long>("SELECT Id FROM ml.DropBoxNode WHERE NamespaceId = @ns AND PathHash = @hash", new { ns = Namespace, hash });
        }

        await db.ExecuteAsync(
            """
            INSERT INTO ml.DropBoxNode
                (NamespaceId, PathDisplay, PathLower, Name, IsFolder, ParentId, DropboxId, SizeBytes, Rev,
                 ContentHash, ServerModified, ClientModified, IsDownloadable, MediaInfoTag, SyncStatus, SyncedUtc)
            VALUES
                (@ns, @pd, @pl, @nm, @f, @pid, @dbi, @sz, @rev, @ch, @sm, @cm, @dl,
                 CASE WHEN @f = 1 THEN NULL ELSE 'pending' END, 'Ok', SYSUTCDATETIME())
            """,
            new { ns = Namespace, pd = path, pl = lower, nm = name, f = isFolder, pid = parentId, dbi = id, sz = size, rev = rev, ch = contentHash, sm = serverModified, cm = clientModified, dl = downloadable });

        return await db.QuerySingleAsync<long>("SELECT Id FROM ml.DropBoxNode WHERE NamespaceId = @ns AND PathHash = @hash", new { ns = Namespace, hash });
    }

    // ---------------------------------------------------------------- phase 2

    private sealed record Phase2Result(int Calls, int Enriched, TimeSpan Elapsed);

    private static async Task<Phase2Result> Phase2Async(DropboxClient client, SqlConnection db, string path, bool write)
    {
        var sw = Stopwatch.StartNew();

        var pending = await db.QueryAsync<PendingRow>(
            """
            SELECT Id, PathDisplay FROM ml.DropBoxNode
             WHERE IsFolder = 0 AND MediaInfoTag = 'pending' AND PathLower LIKE @p
            """,
            new { p = path.ToLowerInvariant() + "%" });

        var calls = 0;
        var enriched = 0;

        foreach (var row in pending)
        {
            FileEntry info;

            try
            {
                info = await ReadFileInfoAsync(client, row.PathDisplay);
                calls++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAILED {row.PathDisplay}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            enriched++;

            if (!write)
            {
                continue;
            }

            await db.ExecuteAsync(
                """
                UPDATE ml.DropBoxNode
                   SET WidthPixels = @w, HeightPixels = @h, MediaInfoTag = @tag
                 WHERE Id = @id
                """,
                new
                {
                    w = info.WidthPixels,
                    h = info.HeightPixels,
                    tag = info.MediaKind ?? (info.WidthPixels is null ? "pending" : "photo"),
                    id = row.Id,
                });
        }

        sw.Stop();

        return new Phase2Result(calls, enriched, sw.Elapsed);
    }

    private static async Task<FileEntry> ReadFileInfoAsync(DropboxClient client, string path)
    {
        // includeMediaInfo: TRUE - without it the API returns no media info at all,
        // and the dimensions read as NULL. This is the flag the app's own
        // GetFileInfoAsync passes.
        var metadata = await client.Files.GetMetadataAsync(path, includeMediaInfo: true);

        long? width = null;
        long? height = null;
        string? kind = null;

        var mediaInfo = metadata.IsFile ? metadata.AsFile.MediaInfo : null;

        if (mediaInfo is not null)
        {
            if (mediaInfo.IsPending)
            {
                kind = "pending";
            }
            else if (mediaInfo.IsMetadata)
            {
                var media = mediaInfo.AsMetadata.Value;
                kind = media.IsPhoto ? "photo" : media.IsVideo ? "video" : null;

                if (media.Dimensions is not null)
                {
                    width = (long)media.Dimensions.Width;
                    height = (long)media.Dimensions.Height;
                }
            }
        }

        return new FileEntry(width, height, kind);
    }

    private sealed record FileEntry(long? WidthPixels, long? HeightPixels, string? MediaKind);

    // ---------------------------------------------------------------- the rows

    private sealed record PendingRow(long Id, string PathDisplay);

    private sealed record HashRow(string PathLower, byte[] PathHash);

    private sealed record Row(
        long Id, string NamespaceId, string PathDisplay, string PathLower, string Name, bool IsFolder,
        long? ParentId, string? DropboxId, long? SizeBytes, string? Rev, string? ContentHash,
        long? WidthPixels, long? HeightPixels, string? MediaInfoTag,
        DateTimeOffset? ServerModified, DateTimeOffset? ClientModified, bool? IsDownloadable, string? SyncStatus);

    private static async Task<List<Row>> ReadRowsAsync(SqlConnection db, string path) =>
        await db.QueryAsync<Row>(
            """
            SELECT Id, NamespaceId, PathDisplay, PathLower, Name, IsFolder, ParentId, DropboxId, SizeBytes, Rev,
                   ContentHash, WidthPixels, HeightPixels, MediaInfoTag, ServerModified, ClientModified,
                   IsDownloadable, SyncStatus
              FROM ml.DropBoxNode
             WHERE PathLower = @p OR PathLower LIKE @like
             ORDER BY PathLower
            """,
            new { p = path.ToLowerInvariant(), like = path.ToLowerInvariant() + "/%" });

    /// <summary>The columns that mean something. The bookkeeping ones are expected to move.</summary>
    private static List<string> Compare(List<Row> before, List<Row> after)
    {
        var differences = new List<string>();
        var afterByPath = after.ToDictionary(r => r.PathLower, StringComparer.Ordinal);

        foreach (var row in before)
        {
            if (!afterByPath.TryGetValue(row.PathLower, out var now))
            {
                differences.Add($"MISSING: {row.PathLower}");
                continue;
            }

            void Check(string column, object? was, object? isNow)
            {
                if (!Equals(was, isNow))
                {
                    differences.Add($"{row.PathLower}: {column} {was ?? "(null)"} -> {isNow ?? "(null)"}");
                }
            }

            Check("PathDisplay", row.PathDisplay, now.PathDisplay);
            Check("Name", row.Name, now.Name);
            Check("IsFolder", row.IsFolder, now.IsFolder);
            Check("ParentId", row.ParentId, now.ParentId);
            Check("DropboxId", row.DropboxId, now.DropboxId);
            Check("SizeBytes", row.SizeBytes, now.SizeBytes);
            Check("Rev", row.Rev, now.Rev);
            Check("ContentHash", row.ContentHash, now.ContentHash);
            Check("WidthPixels", row.WidthPixels, now.WidthPixels);
            Check("HeightPixels", row.HeightPixels, now.HeightPixels);
            Check("MediaInfoTag", row.MediaInfoTag, now.MediaInfoTag);
            Check("ServerModified", row.ServerModified, now.ServerModified);
            Check("ClientModified", row.ClientModified, now.ClientModified);
            Check("IsDownloadable", row.IsDownloadable, now.IsDownloadable);
        }

        return differences;
    }

    private static async Task RestoreAsync(SqlConnection db, List<Row> rows)
    {
        foreach (var row in rows)
        {
            await db.ExecuteAsync(
                """
                UPDATE ml.DropBoxNode
                   SET PathDisplay = @pd, Name = @nm, IsFolder = @f, ParentId = @pid, DropboxId = @dbi,
                       SizeBytes = @sz, Rev = @rev, ContentHash = @ch, WidthPixels = @w, HeightPixels = @h,
                       MediaInfoTag = @tag, ServerModified = @sm, ClientModified = @cm, IsDownloadable = @dl,
                       SyncStatus = @st
                 WHERE Id = @id
                """,
                new
                {
                    pd = row.PathDisplay, nm = row.Name, f = row.IsFolder, pid = row.ParentId, dbi = row.DropboxId,
                    sz = row.SizeBytes, rev = row.Rev, ch = row.ContentHash, w = row.WidthPixels, h = row.HeightPixels,
                    tag = row.MediaInfoTag, sm = row.ServerModified, cm = row.ClientModified, dl = row.IsDownloadable,
                    st = row.SyncStatus, id = row.Id,
                });
        }
    }

    private static string Snapshot(List<Row> rows) =>
        string.Join("\n", rows.Select(r =>
            $"{r.Id}|{r.PathLower}|{r.ContentHash}|{r.WidthPixels}|{r.HeightPixels}|{r.MediaInfoTag}|{r.SyncStatus}"));

    // ---------------------------------------------------------------- plumbing

    /// <summary>The same hash the table stores: SHA-256 over UTF-16LE of ns + '|' + lower.</summary>
    private static byte[] PathHash(string ns, string lower) =>
        SHA256.HashData(Encoding.Unicode.GetBytes(ns + "|" + lower));

    private static async Task<string> ConnectionStringAsync(IHost host)
    {
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Utility.Server.Options.ServerOptions>>();

        return options.Value.ConnectionString;
    }
}

// ---------------------------------------------------------------- sql helpers

internal static class SqlExtensions
{
    public static async Task<int> ExecuteAsync(this SqlConnection db, string sql, object parameters)
    {
        using var command = new SqlCommand(sql, db);
        AddParameters(command, parameters);

        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> QuerySingleAsync<T>(this SqlConnection db, string sql, object parameters)
    {
        using var command = new SqlCommand(sql, db);
        AddParameters(command, parameters);

        using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("No row.");
        }

        return Read<T>(reader);
    }

    public static async Task<List<T>> QueryAsync<T>(this SqlConnection db, string sql, object parameters)
    {
        using var command = new SqlCommand(sql, db);
        AddParameters(command, parameters);

        using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();

        while (await reader.ReadAsync())
        {
            rows.Add(Read<T>(reader));
        }

        return rows;
    }

    private static void AddParameters(SqlCommand command, object parameters)
    {
        foreach (var property in parameters.GetType().GetProperties())
        {
            command.Parameters.AddWithValue("@" + property.Name, property.GetValue(parameters) ?? DBNull.Value);
        }
    }

    private static T Read<T>(SqlDataReader reader)
    {
        var type = typeof(T);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTuple<,>))
        {
            var args = type.GetGenericArguments();
            var first = Convert.ChangeType(reader.GetValue(0), Nullable.GetUnderlyingType(args[0]) ?? args[0]);
            var second = Convert.ChangeType(reader.GetValue(1), Nullable.GetUnderlyingType(args[1]) ?? args[1]);

            return (T)(object)(first, second);
        }

        var constructors = type.GetConstructors();

        if (constructors.Length == 0)
        {
            // A primitive - long, int, string. It has no constructor to read through.
            var raw = reader.IsDBNull(0) ? null : reader.GetValue(0);

            if (raw is null)
            {
                return default!;
            }

            var primitive = Nullable.GetUnderlyingType(type) ?? type;

            if (primitive == typeof(DateTimeOffset))
            {
                return (T)(object)(raw is DateTimeOffset moment ? moment : new DateTimeOffset((DateTime)raw));
            }

            return (T)Convert.ChangeType(raw, primitive);
        }

        var constructor = constructors.First();
        var parameters = constructor.GetParameters();
        var values = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            var target = Nullable.GetUnderlyingType(parameters[i].ParameterType) ?? parameters[i].ParameterType;

            values[i] = value is null
                ? null
                : target == typeof(byte[])
                    ? value
                    : target == typeof(DateTimeOffset)
                    ? value is DateTimeOffset moment
                        ? moment
                        : new DateTimeOffset((DateTime)value)
                    : target.IsEnum
                        ? Enum.ToObject(target, value)
                        : Convert.ChangeType(value, target);
        }

        return (T)constructor.Invoke(values);
    }
}
