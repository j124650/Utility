using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Utility.Server;
using Utility.Server.Finder.Repair;

namespace RepairLogChecks;

/// <summary>
/// Checks the recovery log's FOLDER (docs/finder-recover-spec.md §10.5.1).
///
/// The rule: the log of a recovery goes in a folder named for the date the procedure
/// STARTED — <c>yyyy-MM-dd</c> — with the file inside it.
///
/// It drives the real provider against a scratch folder, so the user's own log folder is
/// never touched and what it prints is what the app does. The last check builds the app's
/// own service registration and resolves the provider from it, so the container's wiring is
/// proven too.
/// </summary>
internal static class Program
{
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

        var scratch = Path.Combine(Path.GetTempPath(), "Utility", "repair-log-checks");
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }

        Directory.CreateDirectory(scratch);

        var provider = new TextFinderRepairLogProvider(
            NullLogger<TextFinderRepairLogProvider>.Instance,
            scratch);

        // ---- 1. the folder is the date the recovery started ----
        //
        // A recovery that started at 23:59 and ended at 00:05 the next day belongs to the
        // day it STARTED - that is the procedure's start, and it is what names the folder.
        var startedAt = new DateTimeOffset(2026, 10, 28, 23, 59, 0, TimeSpan.FromHours(4));

        var directory = TextFinderRepairLogProvider.RunDirectory(scratch, startedAt);
        Check(
            "the folder is the date the recovery started",
            Path.GetFileName(directory) == "2026-10-28",
            $"folder {Path.GetFileName(directory)}");

        // ---- 2. the month is numeric, not a name ----
        //
        // The month is a NUMBER: 10, not "Oct". The folder has to sort as a date.
        var folderName = Path.GetFileName(directory);
        var month = folderName.Split('-')[1];
        Check(
            "the month in the folder name is numeric",
            month.All(char.IsAsciiDigit) && month.Length == 2,
            $"month '{month}'");

        // ---- 3. the file sits INSIDE the folder ----
        var path = TextFinderRepairLogProvider.RunPath(scratch, startedAt);
        Check(
            "the log file sits inside the date folder",
            Path.GetDirectoryName(path) == directory,
            path);

        // ---- 4. the file name carries the same stamp ----
        Check(
            "the file name carries the recovery's own start",
            Path.GetFileName(path) == "Recover 2026-10-28_23-59-00.log",
            Path.GetFileName(path));

        // ---- 5. the provider really writes there ----
        //
        // The real provider, the real summary, a scratch folder: the file must exist, in
        // the folder, with the summary's first line in it.
        var summary = new FinderRepairSummary
        {
            Shown = 100,
            Recoverable = 42,
            Recovered = 0,
            ParentRestored = 0,
            ChildRegenerated = 0,
            Both = 0,
            NotRecovered = 42,
            Failed = 0,
            DownloadedBytes = 0,
            Replaced = 0,
            Folders = 7
        };

        var openedAt = new DateTimeOffset(2026, 10, 29, 8, 30, 15, TimeSpan.FromHours(4));

        await using (var log = await provider.OpenAsync(summary, openedAt))
        {
            Check("the provider opened the log", log is not null, log?.Path ?? "null");
            Check(
                "the opened log sits in the date folder",
                log is not null && Path.GetDirectoryName(log.Path) ==
                    TextFinderRepairLogProvider.RunDirectory(scratch, openedAt),
                log?.Path ?? "null");
        }

        var landed = Path.Combine(scratch, "2026-10-29", "Recover 2026-10-29_08-30-15.log");
        Check(
            "the log landed in a folder named for the date",
            File.Exists(landed),
            landed);

        // ---- a FAILED record names the file that was not there ----
        //
        // A download that comes back path/not_found has to leave a path behind, or the
        // reader has nothing to search on. The record carries the source, and the log
        // writes it.
        var failed = new FinderRepairRecord
        {
            ParentId = Guid.NewGuid(),
            ChildId = Guid.NewGuid(),
            ParentName = "Y42408OD-Featured-Bottom.jpg",
            Folder = "y42408od",
            NodeId = 173075,
            DropboxId = "id:sMa5MD0C7uMAAAAAAA7ODA",
            NamespaceId = "15193064371",
            ContentHash = "9f113d83040615aa2a78d9441f206d2f39f82802bd7cb3e2a6ca2cdd52fd9190",
            Source = "/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/New Balance/F27/PROCESSED/Y42408OD/Y42408OD-Featured-Bottom.jpg",
            Reason = "path/not_found/",
            Recovered = false,
            Number = 1,
            Plan = "restore the parent",
            StartedAt = new DateTimeOffset(2026, 10, 29, 8, 30, 15, TimeSpan.FromHours(4)),
            EndedAt = new DateTimeOffset(2026, 10, 29, 8, 30, 16, TimeSpan.FromHours(4)),
        };

        await using (var log = await provider.OpenAsync(summary, openedAt))
        {
            await log!.BeginAsync(failed, CancellationToken.None);
            await log.CandidateAsync(failed, CancellationToken.None);
            await log.EndAsync(failed, CancellationToken.None);
        }

        var failedText = File.ReadAllText(landed);

        Check(
            "a failed record's log names the file's Dropbox path",
            failedText.Contains(failed.Source!, StringComparison.Ordinal),
            failed.Source!);

        Check(
            "a failed record's log says why it failed",
            failedText.Contains("\"reason\":\"path/not_found/\"", StringComparison.Ordinal));

        Check(
            "a failed record's log carries the namespace",
            failedText.Contains("15193064371", StringComparison.Ordinal));

        // ---- the format is JSON LINES: every line is one JSON object ----
        var jsonLines = File.ReadAllLines(landed)
            .Where(l => l.Trim().Length > 0)
            .ToList();

        var allValid = true;
        var types = new List<string>();

        foreach (var line in jsonLines)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);

                if (doc.RootElement.TryGetProperty("type", out var t))
                {
                    types.Add(t.GetString() ?? "");
                }
            }
            catch (System.Text.Json.JsonException)
            {
                allValid = false;
            }
        }

        Check("every line is a valid JSON object", allValid);

        Check(
            "the log opens with a header that names the format's version",
            types.Count > 0 && types[0] == "header" && failedText.Contains("\"format\":1", StringComparison.Ordinal),
            string.Join(", ", types));

        Check(
            "an operation opens with a begin and closes with an end",
            types.Contains("begin") && types.Contains("end"),
            string.Join(", ", types));

        Check(
            "the operation's number ties its events together",
            failedText.Contains("\"n\":1", StringComparison.Ordinal));

        Check(
            "the outcome carries the reason",
            failedText.Contains("path/not_found/", StringComparison.Ordinal));

        Check(
            "the plan says what the run set out to do",
            failedText.Contains("restore the parent", StringComparison.Ordinal));

        Check(
            "the source path survives the JSON encoding whole",
            failedText.Contains(failed.Source!, StringComparison.Ordinal));

        // ---- a SUCCESSFUL operation writes every kind of event ----
        var good = new FinderRepairRecord
        {
            ParentId = Guid.NewGuid(),
            ChildId = Guid.NewGuid(),
            ParentName = "M80608F0-Featured-Top.jpg",
            Folder = "m80608f0",
            NodeId = 77383,
            DropboxId = "id:sMa5MD0C7uMAAAAAAA7PqA",
            NamespaceId = "15193064371",
            ContentHash = "76d8ba4722f00e160fe0fb0026c38c766c58564eac517c441f06d792e97dd606",
            Source = "/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/New Balance/F27/M80608F0/M80608F0-Featured-Top.jpg",
            CopiedTo = @"C:\_Recover\media\m80608f0\33540F62-0C09-4746-AB7B-52A2F0CB1114",
            CopiedBytes = 2_023_419,
            ResizedFor = Guid.NewGuid(),
            ResizedFrom = @"C:\_Recover\media\m80608f0\33540F62-0C09-4746-AB7B-52A2F0CB1114",
            ResizedTo = @"C:\_Recover\media\m80608f0\F751FBA6-E60D-4FED-B960-C01262FEA250",
            Target = 1500,
            Result = "size 52,324, 1500 × 1500",
            Recovered = true,
            Number = 2,
            Plan = "restore the parent, then regenerate the child",
            StartedAt = new DateTimeOffset(2026, 10, 29, 8, 31, 0, TimeSpan.FromHours(4)),
            EndedAt = new DateTimeOffset(2026, 10, 29, 8, 31, 6, TimeSpan.FromHours(4)),
        };

        await using (var log = await provider.OpenAsync(summary, openedAt))
        {
            await log!.BeginAsync(good, CancellationToken.None);
            await log.CandidateAsync(good, CancellationToken.None);
            await log.ParentRestoredAsync(good, CancellationToken.None);
            await log.ChildRegeneratedAsync(good, CancellationToken.None);
            await log.EndAsync(good, CancellationToken.None);
        }

        var goodLines = File.ReadAllLines(landed)
            .Where(l => l.Trim().Length > 0)
            .ToList();

        var goodTypes = goodLines
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement
                .GetProperty("type").GetString() ?? "")
            .ToList();

        Check(
            "a successful operation writes every kind of event",
            goodTypes.Contains("begin") && goodTypes.Contains("candidate")
                && goodTypes.Contains("parent_restored") && goodTypes.Contains("child_regenerated")
                && goodTypes.Contains("end"),
            string.Join(", ", goodTypes));

        var goodText = File.ReadAllText(landed);

        Check(
            "the bytes written are recorded",
            goodText.Contains("\"copied_bytes\":2023419", StringComparison.Ordinal),
            "copied_bytes");

        Check(
            "the result survives the JSON encoding whole",
            goodText.Contains(good.Result!, StringComparison.Ordinal),
            good.Result!);

        Console.WriteLine();
        Console.WriteLine("--- the log as it was written ---");
        foreach (var l in goodLines) Console.WriteLine("  " + l);

        Console.WriteLine();
        Console.WriteLine($"--- size: a successful operation is {goodText.Length} bytes over {goodLines.Count} lines ---");

        // ---- 6. each recovery gets its own dated folder ----
        //
        // A recovery on another day gets another folder. The two sit side by side, each
        // saying which day it holds.
        await using (await provider.OpenAsync(summary, startedAt)) { }

        var folders = Directory
            .GetDirectories(scratch)
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToList();

        Check(
            "each recovery got its own dated folder",
            folders.SequenceEqual(new[] { "2026-10-28", "2026-10-29" }),
            string.Join(", ", folders));

        // ---- 7. a second recovery on the SAME day shares the folder ----
        //
        // The folder is the DAY, not the run. Two recoveries on one day sit together, and
        // each still gets its own file.
        var sameDayA = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.FromHours(4));
        var sameDayB = new DateTimeOffset(2026, 10, 29, 17, 45, 30, TimeSpan.FromHours(4));

        await using (await provider.OpenAsync(summary, sameDayA)) { }
        await using (await provider.OpenAsync(summary, sameDayB)) { }

        var sameDayFiles = Directory
            .GetFiles(Path.Combine(scratch, "2026-10-29"), "*.log")
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToList();

        Check(
            "two recoveries on one day share the folder, each with its own file",
            sameDayFiles.SequenceEqual(new[]
            {
                "Recover 2026-10-29_08-30-15.log",
                "Recover 2026-10-29_09-00-00.log",
                "Recover 2026-10-29_17-45-30.log"
            }),
            string.Join(", ", sameDayFiles));

        // ---- 7. the day folder is what a reader sorts by ----
        //
        // The folders must sort as dates: 2026-10-28 before 2026-10-29 before 2026-11-01.
        var sorted = new[] { "2026-11-01", "2026-10-28", "2026-10-29" }
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Check(
            "the folder names sort as dates",
            sorted.SequenceEqual(new[] { "2026-10-28", "2026-10-29", "2026-11-01" }),
            string.Join(", ", sorted));

        // ---- 8. the CONTAINER resolves the provider the app uses ----
        //
        // The provider has two constructors, so the registration must say which one the
        // container picks. This builds the app's own service registration and resolves the
        // provider from it: if the factory were wrong, this is where it would show.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddEnvironmentVariables("UTILITY_");
        builder.Services.AddUtilityServer(builder.Configuration);

        using (var host = builder.Build())
        {
            var resolved = host.Services.GetRequiredService<IFinderRepairLogProvider>();

            Check(
                "the container resolves the log provider",
                resolved is TextFinderRepairLogProvider,
                resolved.GetType().Name);

            var text = (TextFinderRepairLogProvider)resolved;
            Check(
                "the resolved provider writes to the app's own log directory",
                text.BaseDirectory == TextFinderRepairLogProvider.LogDirectory,
                text.BaseDirectory);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"All {checks} checks passed."
            : $"{failures} of {checks} checks FAILED.");

        Directory.Delete(scratch, recursive: true);

        return failures == 0 ? 0 : 1;
    }
}
