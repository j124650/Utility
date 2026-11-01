using System.Diagnostics;
using Dropbox.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Utility.DropBox;
using Utility.DropBox.Models;
using Utility.DropBox.Services;
using Utility.Server;
using Utility.Server.DropBoxSync;

namespace TreeCompare;

/// <summary>
/// MEASURES the tree browser's walk against the database, for one subtree.
///
/// The question this answers: the tree browser lists a folder tree in seconds. The
/// sync walk takes much longer. Is the tree browser's listing a CHEAPER OPERATION, or
/// could it replace the sync's listing?
///
/// It walks the REAL tree with the REAL service, saves the JSON exactly as the
/// "Save JSON…" button does, and then compares every file node against the row the
/// sync wrote for it — field by field.
///
/// It also measures the thing that decides the answer: how long a per-file
/// <c>get_metadata</c> call takes, because that is the work the tree browser does NOT
/// do and the sync does.
/// </summary>
internal static class Program
{
    private const string Subtree = "/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Umbro/F26";

    /// <summary>
    /// THE QUESTION: does listing a folder give the media info - the width and the
    /// height - or must every file be asked for separately?
    ///
    /// It lists the SAME folders three ways and counts what came back:
    ///   1. list_folder with include_media_info = false   (what the tree browser does)
    ///   2. list_folder with include_media_info = true    (what the SQL file says is
    ///      "extra per-file work" and was never benchmarked)
    ///   3. one get_metadata per file                     (what the sync does today)
    /// </summary>
    private static async Task ProbeMediaAsync(
        IDropBoxDataService dropBox, string? namespaceId, List<DropBoxNode> folders,
        DropboxClient rawClient)
    {
        Console.WriteLine("--- DOES A LISTING GIVE THE MEDIA INFO? ---");
        Console.WriteLine();

        var files = new List<FileEntry>();

        foreach (var include in new[] { false, true })
        {
            var sw = Stopwatch.StartNew();
            var collected = new List<FileEntry>();

            foreach (var folder in folders)
            {
                var entries = await dropBox.ListFolderAsync(folder.PathDisplay, namespaceId, include, CancellationToken.None);
                collected.AddRange(entries.Where(e => !e.IsFolder));
            }

            sw.Stop();

            Console.WriteLine($"list_folder, includeMediaInfo = {include.ToString().ToLowerInvariant(),-5}");
            Console.WriteLine($"  elapsed        : {sw.Elapsed.TotalSeconds:N2} s for {collected.Count:N0} file(s)");
            Console.WriteLine($"  with dimensions: {collected.Count(e => e.HasDimensions):N0}");

            foreach (var group in collected.GroupBy(e => e.MediaKind ?? "(null)").OrderBy(g => g.Key))
            {
                Console.WriteLine($"  mediaKind {group.Key,-10}: {group.Count():N0}");
            }

            Console.WriteLine();

            if (!include)
            {
                files = collected;
            }
        }

        // The way the sync does it today: one get_metadata per file.
        var enrichSw = Stopwatch.StartNew();
        var withDims = 0;

        foreach (var file in files)
        {
            var info = await dropBox.GetFileInfoAsync(file.PathDisplay, namespaceId, true, CancellationToken.None);

            if (info.HasDimensions)
            {
                withDims++;
            }
        }

        enrichSw.Stop();

        Console.WriteLine("get_metadata, one per file (what the sync does today)");
        Console.WriteLine($"  elapsed        : {enrichSw.Elapsed.TotalSeconds:N2} s for {files.Count:N0} file(s)");
        Console.WriteLine($"  with dimensions: {withDims:N0}");
        Console.WriteLine();

        // The fourth way: ONE recursive listing of the whole subtree, asking for the
        // media info. If this delivers the dimensions, the per-file calls could all
        // disappear - which would beat the hash skip by a wider margin.
        foreach (var include in new[] { false, true })
        {
            var sw = Stopwatch.StartNew();
            var result = await rawClient.Files.ListFolderAsync(Subtree, recursive: true, includeMediaInfo: include);
            var entries = result.Entries.Count;

            while (result.HasMore)
            {
                result = await rawClient.Files.ListFolderContinueAsync(result.Cursor);
                entries += result.Entries.Count;
            }

            sw.Stop();

            var recFiles = result.Entries.Where(e => e.IsFile).Select(e => e.AsFile).ToList();
            var recWithDims = recFiles.Count(f => f.MediaInfo?.IsMetadata == true
                && f.MediaInfo.AsMetadata.Value.Dimensions is not null);

            Console.WriteLine($"RECURSIVE list_folder, includeMediaInfo = {include.ToString().ToLowerInvariant(),-5}");
            Console.WriteLine($"  elapsed        : {sw.Elapsed.TotalSeconds:N2} s");
            Console.WriteLine($"  entries        : {entries:N0}");
            Console.WriteLine($"  with dimensions: {recWithDims:N0}");
            Console.WriteLine();
        }
    }

    private static async Task<int> Main(string[] args)
    {
        var depth = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 2;
        var sampleSize = args.Length > 1 && int.TryParse(args[1], out var sampled) ? sampled : 60;

        // --media asks the one question the whole speed-up rests on: does LISTING a
        // folder give the media info, and what does asking for it cost?
        var probeOnly = args.Contains("--media");

        var scratch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hermes", "cache", "scratch");

        Directory.CreateDirectory(scratch);

        // The app's own composition: the server (which brings the sync's DbContext
        // factory) and the Dropbox client layer.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddUtilityServer(builder.Configuration);
        builder.Services.AddUtilityDropBox(builder.Configuration);

        using var host = builder.Build();

        var dropBox = host.Services.GetRequiredService<IDropBoxDataService>();
        var clientFactory = host.Services.GetRequiredService<IDropBoxClientFactory>();
        var contexts = host.Services.GetRequiredService<IDbContextFactory<DropBoxDbContext>>();

        // --- the namespace ---
        var namespaces = await dropBox.GetNamespacesAsync();
        var team = namespaces.FirstOrDefault(x => !x.IsPersonal) ?? namespaces.First();
        var namespaceId = team.IsPersonal ? null : team.Id;

        Console.WriteLine($"namespace   : {team.DisplayLabel} ({team.Id})");
        Console.WriteLine($"path        : {Subtree}");
        Console.WriteLine($"depth       : {depth}");
        Console.WriteLine();

        // The RAW client, so the recursive listing can be asked for directly - the
        // service's own listing is not recursive.
        var rawClient = await clientFactory.CreateClientForNamespaceAsync(namespaceId, CancellationToken.None);

        // --- the walk ---
        var sw = Stopwatch.StartNew();
        var tree = await dropBox.GetFolderTreeAsync(Subtree, namespaceId, depth, 24, false, CancellationToken.None);
        sw.Stop();

        var all = tree.AllNodes().ToList();
        var files = all.Where(n => !n.IsFolder).ToList();
        var folders = all.Where(n => n.IsFolder).ToList();

        Console.WriteLine($"--- THE WALK ---");
        Console.WriteLine($"elapsed        : {sw.Elapsed.TotalSeconds:N1} s");
        Console.WriteLine($"folders        : {folders.Count:N0}");
        Console.WriteLine($"files          : {files.Count:N0}");
        Console.WriteLine($"listing calls  : ~{folders.Count:N0} (one per folder, plus cursor pages)");
        Console.WriteLine($"per-file calls : 0  <-- the tree browser makes NONE");
        Console.WriteLine();

        if (probeOnly)
        {
            await ProbeMediaAsync(dropBox, namespaceId, folders, rawClient);
            return 0;
        }

        // --- the JSON, saved exactly as the button saves it ---
        var jsonPath = Path.Combine(scratch, "umbro-f26-tree.json");
        await File.WriteAllTextAsync(jsonPath, tree.ToJson());

        Console.WriteLine($"json saved     : {jsonPath} ({new FileInfo(jsonPath).Length:N0} bytes)");
        Console.WriteLine();

        // --- what a FILE node carries ---
        if (files.Count > 0)
        {
            Console.WriteLine("--- WHAT A FILE NODE CARRIES ---");
            foreach (var field in files[0].GetType().GetProperties())
            {
                Console.WriteLine($"  {field.Name,-16} = {field.GetValue(files[0])}");
            }

            Console.WriteLine();
        }

        // --- the database ---
        await using var db = await contexts.CreateDbContextAsync();

        var prefix = Subtree.ToLowerInvariant();
        var dbSw = Stopwatch.StartNew();
        var rows = await db.Nodes
            .Where(n => n.PathLower != null && n.PathLower.StartsWith(prefix))
            .AsNoTracking()
            .ToListAsync();
        dbSw.Stop();

        var dbFiles = rows.Where(r => !r.IsFolder).ToList();
        var dbFolders = rows.Where(r => r.IsFolder).ToList();

        Console.WriteLine($"--- THE DATABASE (ml.DropBoxNode under the same path) ---");
        Console.WriteLine($"read time      : {dbSw.Elapsed.TotalSeconds:N1} s");
        Console.WriteLine($"rows           : {rows.Count:N0}  ({dbFolders.Count:N0} folder(s), {dbFiles.Count:N0} file(s))");
        Console.WriteLine($"total bytes    : {dbFiles.Sum(r => r.SizeBytes ?? 0):N0}");
        Console.WriteLine();

        var byPath = dbFiles.ToDictionary(r => r.PathLower!, StringComparer.Ordinal);

        // --- the comparison, file by file ---
        var present = 0;
        var missing = 0;
        var sizeMismatch = 0;
        var hashMismatch = 0;
        var hashMissingInDb = 0;
        var revMismatch = 0;
        var timeMismatch = 0;
        var withDims = 0;
        var hashMatches = 0;
        var hashMatchesWithDims = 0;

        foreach (var file in files)
        {
            if (!byPath.TryGetValue(file.PathDisplay.ToLowerInvariant(), out var row))
            {
                missing++;
                continue;
            }

            present++;

            if (row.SizeBytes != file.SizeBytes) sizeMismatch++;
            if (row.Rev != file.Rev) revMismatch++;
            if (row.ContentHash != file.ContentHash) hashMismatch++;
            if (string.IsNullOrEmpty(row.ContentHash)) hashMissingInDb++;

            if (row.ServerModified is null || file.ServerModified is null
                || Math.Abs((row.ServerModified.Value - file.ServerModified.Value).TotalSeconds) > 1)
            {
                timeMismatch++;
            }

            if (row.WidthPixels is not null && row.HeightPixels is not null) withDims++;

            // The point of the whole exercise: if the CONTENT HASH is the same, the
            // content is the same, so the dimensions cannot have changed, so the
            // per-file get_metadata call could have been SKIPPED.
            if (!string.IsNullOrEmpty(file.ContentHash)
                && string.Equals(row.ContentHash, file.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                hashMatches++;

                if (row.WidthPixels is not null && row.HeightPixels is not null)
                {
                    hashMatchesWithDims++;
                }
            }
        }

        Console.WriteLine($"--- FILE BY FILE ({files.Count:N0} file(s) in the tree) ---");
        Console.WriteLine($"in the tree AND in the database : {present:N0}");
        Console.WriteLine($"in the tree but NOT in the db   : {missing:N0}");
        Console.WriteLine($"size differs                    : {sizeMismatch:N0}");
        Console.WriteLine($"rev differs                     : {revMismatch:N0}");
        Console.WriteLine($"content hash differs            : {hashMismatch:N0}");
        Console.WriteLine($"content hash missing in the db  : {hashMissingInDb:N0}");
        Console.WriteLine($"server modified differs         : {timeMismatch:N0}");
        Console.WriteLine($"db row HAS dimensions           : {withDims:N0}");
        Console.WriteLine();

        Console.WriteLine("--- WHAT THE HASH SKIP WOULD SAVE ---");
        Console.WriteLine($"content hash matches the db     : {hashMatches:N0} of {present:N0}");
        Console.WriteLine($"  ...and the db row has dims    : {hashMatchesWithDims:N0}");
        Console.WriteLine($"  -> those need NO get_metadata : {hashMatchesWithDims:N0}");
        Console.WriteLine();

        // --- what the per-file call costs ---
        Console.WriteLine($"--- THE PER-FILE get_metadata CALL ({sampleSize} file(s), sampled) ---");

        var sample = files.Take(sampleSize).ToList();
        var enrichSw = Stopwatch.StartNew();

        var withMedia = 0;

        foreach (var file in sample)
        {
            try
            {
                var info = await dropBox.GetFileInfoAsync(file.PathDisplay, namespaceId, true, CancellationToken.None);

                if (info.HasDimensions)
                {
                    withMedia++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAILED {file.PathDisplay}: {ex.GetType().Name} {ex.Message}");
            }
        }

        enrichSw.Stop();

        var perCall = enrichSw.Elapsed.TotalMilliseconds / Math.Max(1, sample.Count);

        Console.WriteLine($"elapsed        : {enrichSw.Elapsed.TotalSeconds:N1} s for {sample.Count} file(s)");
        Console.WriteLine($"per file       : {perCall:N0} ms");
        Console.WriteLine($"with dimensions: {withMedia} of {sample.Count}");
        Console.WriteLine($"whole subtree  : {perCall * files.Count / 1000:N0} s of extra calls for {files.Count:N0} file(s)");
        Console.WriteLine();

        // --- the folders ---
        var dbFolderPaths = dbFolders.Select(r => r.PathLower!).ToHashSet(StringComparer.Ordinal);

        var treeFoldersWithStatus = dbFolderPaths.Count;

        Console.WriteLine("--- FOLDER STATUS IN THE DATABASE ---");
        foreach (var group in rows.Where(r => r.IsFolder).GroupBy(r => r.SyncStatus ?? "(null)").OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Key,-10} : {group.Count(),6:N0}");
        }

        Console.WriteLine($"  folders with a status: {treeFoldersWithStatus:N0} of {dbFolders.Count:N0}");
        Console.WriteLine();

        return 0;
    }
}
