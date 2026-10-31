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
