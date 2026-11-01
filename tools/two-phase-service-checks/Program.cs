using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Utility.DropBox;
using Utility.Server;
using Utility.Server.DropBoxSync;

namespace TwoPhaseServiceChecks;

/// <summary>
/// CHECKS <see cref="DropBoxSyncTwoPhaseService"/> against the live database.
///
/// The service is the real one, resolved from the app's own DI composition, and it runs
/// against the real Dropbox account and the real ml.DropBoxNode. The subtree is small -
/// one brand's season folder - so a run costs about ten listing calls.
///
/// It checks the three things the design rests on:
///
///   1. PHASE 1 keeps the dimensions of a file whose content has NOT changed, and
///      marks a file whose content HAS changed as 'pending' with its dimensions cleared.
///   2. PHASE 2 enriches the rows marked 'pending' and only those.
///   3. The two phases together leave the database in the state the one-phase walk
///      leaves it in.
///
/// It snapshots the subtree first and restores it if anything differs.
/// </summary>
internal static class Program
{
    private const string Subtree = "/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Umbro/F26";
    private const string Namespace = "15193064371";

    private static async Task<int> Main()
    {
        var failures = 0;
        var checks = 0;

        void Check(string what, bool ok, string detail = "")
        {
            checks++;

            if (!ok)
            {
                failures++;
                Console.WriteLine($"FAIL  {what}  {detail}");
            }
            else
            {
                Console.WriteLine($"ok    {what}  {detail}");
            }
        }

        // The app's own composition.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddUtilityServer(builder.Configuration);
        builder.Services.AddUtilityDropBox(builder.Configuration);

        using var host = builder.Build();

        var service = host.Services.GetRequiredService<IDropBoxSyncTwoPhaseService>();
        var options = host.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Utility.Server.Options.ServerOptions>>();

        await using var db = new SqlConnection(options.Value.ConnectionString);
        await db.OpenAsync();

        Console.WriteLine($"subtree: {Subtree}");
        Console.WriteLine($"namespace: {Namespace}");
        Console.WriteLine();

        // ---------------- the snapshot ----------------
        var before = await ReadRowsAsync(db, Subtree);
        var beforeWithDims = before.Count(r => r.WidthPixels is not null);

        Console.WriteLine($"=== BEFORE ({before.Count:N0} row(s)) ===");
        Console.WriteLine($"  files with dimensions : {beforeWithDims:N0}");
        Console.WriteLine($"  files tagged 'pending': {before.Count(r => r.MediaInfoTag == "pending"):N0}");
        Console.WriteLine();

        Check("the subtree has rows to work on", before.Count > 0, $"{before.Count} rows");

        // ---------------- PHASE 1: LIST ----------------
        Console.WriteLine("=== PHASE 1: LIST (nothing changed) ===");

        var list = await service.ListAsync(Subtree, Namespace);
        Console.WriteLine($"  completed: {list.Completed}, folders: {list.FoldersWritten:N0}, files: {list.FilesWritten:N0}, {list.Duration.TotalSeconds:N1}s");
        Console.WriteLine();

        Check("the listing completes", list.Completed, list.Error ?? string.Empty);
        Check("the listing writes the folder's files", list.FilesWritten > 0, $"{list.FilesWritten} files");

        var afterList = await ReadRowsAsync(db, Subtree);
        var pendingAfterList = afterList.Count(r => r.MediaInfoTag == "pending");
        var dimsAfterList = afterList.Count(r => r.WidthPixels is not null);

        Check("the dimensions of an unchanged file are KEPT", dimsAfterList >= beforeWithDims,
            $"{dimsAfterList} with dimensions, against {beforeWithDims} before");
        Check("nothing is marked 'pending' when nothing changed", pendingAfterList == 0, $"{pendingAfterList} pending");

        // ---------------- a CHANGED file ----------------
        Console.WriteLine("=== A CHANGED FILE ===");

        var changed = afterList.First(r => !r.IsFolder && r.ContentHash is not null);
        var originalHash = changed.ContentHash!;

        await ExecuteAsync(db, "UPDATE ml.DropBoxNode SET ContentHash = @bogus WHERE Id = @id",
            new { bogus = "SIMULATED-CHANGE", id = changed.Id });

        Console.WriteLine($"  simulated a change in {changed.PathDisplay}");
        Console.WriteLine();

        var list2 = await service.ListAsync(Subtree, Namespace);
        Check("the second listing completes", list2.Completed, list2.Error ?? string.Empty);

        var afterChange = await ReadRowsAsync(db, Subtree);
        var marked = afterChange.First(r => r.Id == changed.Id);

        Check("the changed file is marked 'pending'", marked.MediaInfoTag == "pending", marked.MediaInfoTag ?? "(null)");
        Check("the changed file's dimensions are cleared",
            marked.WidthPixels is null && marked.HeightPixels is null,
            $"{marked.WidthPixels} x {marked.HeightPixels}");
        Check("only the changed file is marked 'pending'",
            afterChange.Count(r => r.MediaInfoTag == "pending") == 1,
            $"{afterChange.Count(r => r.MediaInfoTag == "pending")} pending");

        // ---------------- PHASE 2: ENRICH ----------------
        Console.WriteLine("=== PHASE 2: ENRICH ===");

        var enrich = await service.EnrichAsync(Namespace, Subtree);
        Console.WriteLine($"  completed: {enrich.Completed}, pending: {enrich.Pending}, enriched: {enrich.Enriched}, failed: {enrich.Failed}, still pending: {enrich.StillPending}, {enrich.Duration.TotalSeconds:N1}s");
        Console.WriteLine();

        Check("the enrichment completes", enrich.Completed, enrich.Error ?? string.Empty);
        Check("the enrichment worked on the pending rows only", enrich.Pending == 1, $"{enrich.Pending} pending");
        Check("the pending file was enriched", enrich.Enriched == 1, $"{enrich.Enriched} enriched");
        Check("nothing is left pending", enrich.StillPending == 0, $"{enrich.StillPending} still pending");

        // ---------------- VERIFY ----------------
        Console.WriteLine("=== VERIFY ===");

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

        Check("the two phases leave the database as the one-phase walk leaves it",
            differences.Count == 0, $"{differences.Count} differences");

        // The whole table's figures.
        var totalWithDims = await ScalarAsync(db,
            "SELECT COUNT(*) FROM ml.DropBoxNode WHERE WidthPixels IS NOT NULL");
        var totalPending = await ScalarAsync(db,
            "SELECT COUNT(*) FROM ml.DropBoxNode WHERE MediaInfoTag = 'pending'");

        Console.WriteLine($"  the whole table: {totalWithDims:N0} with dimensions, {totalPending:N0} pending");
        Console.WriteLine();

        Check("the whole table has no rows left pending", totalPending == 0, $"{totalPending} pending");

        if (differences.Count > 0)
        {
            Console.WriteLine("  restoring the snapshot.");
            await RestoreAsync(db, before);
        }

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed.");

        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the rows

    private sealed record Row(
        long Id, string PathDisplay, string PathLower, string Name, bool IsFolder,
        long? SizeBytes, string? Rev, string? ContentHash,
        long? WidthPixels, long? HeightPixels, string? MediaInfoTag,
        DateTimeOffset? ServerModified, DateTimeOffset? ClientModified, bool? IsDownloadable);

    private static async Task<List<Row>> ReadRowsAsync(SqlConnection db, string path) =>
        await QueryAsync<Row>(db,
            "SELECT Id, PathDisplay, PathLower, Name, IsFolder, SizeBytes, Rev, ContentHash, WidthPixels, HeightPixels, MediaInfoTag, ServerModified, ClientModified, IsDownloadable FROM ml.DropBoxNode WHERE PathLower = @p OR PathLower LIKE @like ORDER BY PathLower",
            new { p = path.ToLowerInvariant(), like = path.ToLowerInvariant() + "/%" });

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
            await ExecuteAsync(db,
                "UPDATE ml.DropBoxNode SET PathDisplay = @pd, Name = @nm, IsFolder = @f, SizeBytes = @sz, Rev = @rev, ContentHash = @ch, WidthPixels = @w, HeightPixels = @h, MediaInfoTag = @tag, ServerModified = @sm, ClientModified = @cm, IsDownloadable = @dl WHERE Id = @id",
                new
                {
                    pd = row.PathDisplay, nm = row.Name, f = row.IsFolder, sz = row.SizeBytes, rev = row.Rev,
                    ch = row.ContentHash, w = row.WidthPixels, h = row.HeightPixels, tag = row.MediaInfoTag,
                    sm = row.ServerModified, cm = row.ClientModified, dl = row.IsDownloadable, id = row.Id,
                });
        }
    }

    // ------------------------------------------------------------------ sql helpers

    private static async Task ExecuteAsync(SqlConnection db, string sql, object parameters)
    {
        using var command = new SqlCommand(sql, db);
        AddParameters(command, parameters);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqlConnection db, string sql)
    {
        using var command = new SqlCommand(sql, db);

        var value = await command.ExecuteScalarAsync();

        return Convert.ToInt64(value);
    }

    private static async Task<List<T>> QueryAsync<T>(SqlConnection db, string sql, object parameters)
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
        var constructors = type.GetConstructors();

        if (constructors.Length == 0)
        {
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
                : target == typeof(DateTimeOffset)
                    ? value is DateTimeOffset moment
                        ? moment
                        : new DateTimeOffset((DateTime)value)
                    : Convert.ChangeType(value, target);
        }

        return (T)constructor.Invoke(values);
    }
}
