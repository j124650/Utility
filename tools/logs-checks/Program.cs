using Utility.Server.Finder.Repair;

namespace LogsChecks;

/// <summary>
/// Checks the recovery log READER (docs/logs-spec.md) — the thing the Logs tab uses to
/// read a recovery log back, and the thing that has to notice a damaged one.
/// </summary>
/// <remarks>
/// <para>
/// The reader is the only place that parses a recovery log, so it is the first thing
/// that can see a broken log. Three defects are checked here: an operation that began
/// and never ended, a line that is not valid JSON, and an operation whose lines do not
/// fit — an end with no begin, and two ends for one operation.
/// </para>
/// <para>
/// The logs are written here, so the reader is exercised against the real format and
/// not against a summary of it.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main()
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

        var scratch = Path.Combine(Path.GetTempPath(), "Utility", "logs-checks");
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }

        Directory.CreateDirectory(scratch);

        // A log with every kind of record in it.
        var path = Path.Combine(scratch, "Recover 2026-10-31_20-48-59.log");

        var lines = new List<string>
        {
            """{"type":"header","format":1,"started":"2026-10-31T20:48:59.0000000+00:00","shown":363856,"recoverable":292400,"folders":27736}""",

            // A successful operation.
            """{"type":"begin","n":1,"ts":"2026-10-31T20:48:59.1000000+00:00","parent":"11111111-1111-1111-1111-111111111111","child":"22222222-2222-2222-2222-222222222222","parent_name":"M80608F0-Featured-Top.jpg","folder":"m80608f0","plan":"restore the parent, then regenerate the child"}""",
            """{"type":"candidate","n":1,"ts":"2026-10-31T20:48:59.2000000+00:00","node":"id:abc","dropbox_id":"id:abc","namespace":"12345","content_hash":"h1","source":"/Images-Catalogs Team Folder/a/b/M80608F0-Featured-Top.jpg"}""",
            """{"type":"parent_restored","n":1,"ts":"2026-10-31T20:49:01.0000000+00:00","copied_to":"C:\\_Recover\\media\\m80608f0\\3333","copied_bytes":2023419}""",
            """{"type":"child_regenerated","n":1,"ts":"2026-10-31T20:49:05.0000000+00:00","resized_from":"C:\\_Recover\\media\\m80608f0\\3333","resized_for":"C:\\_Recover\\media\\m80608f0\\3334","resized_to":"C:\\_Recover\\media\\m80608f0\\3334","target":1500,"result":"52324 bytes, 1500 x 1500"}""",
            """{"type":"end","n":1,"ts":"2026-10-31T20:49:05.1000000+00:00","outcome":"recovered"}""",

            // A failed operation.
            """{"type":"begin","n":2,"ts":"2026-10-31T20:49:20.0000000+00:00","parent":"44444444-4444-4444-4444-444444444444","child":"55555555-5555-5555-5555-555555555555","parent_name":"Y42408OD-Featured-Bottom.jpg","folder":"y42408od","plan":"restore the parent, then regenerate the child"}""",
            """{"type":"candidate","n":2,"ts":"2026-10-31T20:49:20.1000000+00:00","node":"id:def","dropbox_id":"id:def","namespace":"12345","content_hash":"h2","source":"/Images-Catalogs Team Folder/c/d/Y42408OD-Featured-Bottom.jpg"}""",
            """{"type":"end","n":2,"ts":"2026-10-31T20:49:21.0000000+00:00","outcome":"not recovered","reason":"path/not_found/"}""",

            // An operation that BEGAN AND NEVER ENDED — the interrupted one.
            """{"type":"begin","n":3,"ts":"2026-10-31T20:49:30.0000000+00:00","parent":"66666666-6666-6666-6666-666666666666","child":"77777777-7777-7777-7777-777777777777","parent_name":"A1-interrupted.jpg","folder":"a1","plan":"restore the parent, then regenerate the child"}""",

            // A line that is NOT valid JSON.
            """this is not json at all""",

            // An operation that ENDED WITHOUT BEGINNING.
            """{"type":"end","n":4,"ts":"2026-10-31T20:49:40.0000000+00:00","outcome":"recovered"}""",

            // An operation with TWO ends.
            """{"type":"begin","n":5,"ts":"2026-10-31T20:49:50.0000000+00:00","parent":"88888888-8888-8888-8888-888888888888","child":"99999999-9999-9999-9999-999999999999","parent_name":"A2-twice.jpg","folder":"a2","plan":"restore the parent, then regenerate the child"}""",
            """{"type":"end","n":5,"ts":"2026-10-31T20:49:55.0000000+00:00","outcome":"recovered"}""",
            """{"type":"end","n":5,"ts":"2026-10-31T20:49:56.0000000+00:00","outcome":"recovered"}""",

            """{"type":"summary","ts":"2026-10-31T20:49:59.0000000+00:00","recovered":1,"failed":1,"downloaded_bytes":2023419,"replaced":2}""",
        };

        File.WriteAllLines(path, lines);

        // --- the format ---
        Check("the file is recognized as JSON Lines", RecoveryLogReader.IsJsonLines(path));

        var read = RecoveryLogReader.Read(path);

        Check("the format version is read", read.Format == 1, $"format={read.Format}");
        Check("the header's figures are read", read.Shown == 363856 && read.Recoverable == 292400 && read.Folders == 27736,
            $"shown={read.Shown} recoverable={read.Recoverable} folders={read.Folders}");
        Check("the summary's figures are read", read.Recovered == 1 && read.Failed == 1 && read.DownloadedBytes == 2023419 && read.Replaced == 2,
            $"recovered={read.Recovered} failed={read.Failed} bytes={read.DownloadedBytes} replaced={read.Replaced}");

        // --- every operation is there ---
        Check("EVERY operation is read - none is dropped", read.Operations.Count == 5, $"count={read.Operations.Count}");
        Check("the operations are in the log's order",
            read.Operations.Select(o => o.Number).SequenceEqual(new[] { 1, 2, 3, 4, 5 }),
            string.Join(",", read.Operations.Select(o => o.Number)));

        // --- the successful one ---
        var success = read.Operations[0];
        Check("the successful operation's pair is read", success.ParentName == "M80608F0-Featured-Top.jpg", success.ParentName);
        Check("the successful operation's plan is read", success.Plan.Contains("restore the parent"), success.Plan);
        Check("the successful operation's source is read", success.Source!.EndsWith("M80608F0-Featured-Top.jpg"), success.Source);
        Check("the successful operation's copy is read", success.CopiedBytes == 2023419 && success.CopiedTo!.EndsWith("3333"), success.CopiedTo);
        Check("the successful operation's result is read", success.Result!.Contains("1500 x 1500"), success.Result);
        Check("the successful operation's outcome is read", success.Outcome == "recovered", success.Outcome);
        Check("the successful operation's duration is derived", success.Duration == TimeSpan.FromSeconds(6), success.Duration?.ToString());
        Check("the successful operation is not a failure", !success.Failed);

        // --- the failed one ---
        var failed = read.Operations[1];
        Check("the failed operation's outcome is read", failed.Outcome == "not recovered", failed.Outcome);
        Check("the failed operation's reason is read", failed.Reason == "path/not_found/", failed.Reason);
        Check("the failed operation counts as a failure", failed.Failed);
        Check("the failed operation is not interrupted", !failed.IsInterrupted);

        // --- the interrupted one ---
        var interrupted = read.Operations[2];
        Check("the interrupted operation is detected", interrupted.IsInterrupted, interrupted.Reason);
        Check("the interrupted operation counts as a failure", interrupted.Failed);
        Check("the interrupted operation has no duration", interrupted.Duration is null);
        Check("the interrupted operation's pair is read", interrupted.ParentName == "A1-interrupted.jpg", interrupted.ParentName);

        // --- the malformed line ---
        Check("the malformed line is counted, not skipped silently", read.MalformedLines == 1, $"count={read.MalformedLines}");

        // --- the end with no begin ---
        var orphan = read.Operations[3];
        Check("an end with no begin is detected", orphan.IsMalformed, orphan.Reason);
        Check("an end with no begin counts as a failure", orphan.Failed);

        // --- the two ends ---
        var twice = read.Operations[4];
        Check("two ends for one operation is detected", twice.IsMalformed);
        Check("two ends for one operation is not interrupted", !twice.IsInterrupted);

        // --- the older format ---
        var oldPath = Path.Combine(scratch, "Recover 2026-10-28_15-22-07.log");
        File.WriteAllLines(oldPath, new[]
        {
            "=== Recover 2026-10-28 15:22:07 ===",
            "id=11111111-1111-1111-1111-111111111111 id=22222222-2222-2222-2222-222222222222 name=a.jpg folder=a",
            "success id=11111111-1111-1111-1111-111111111111",
        });

        Check("the older format is NOT recognized as JSON Lines", !RecoveryLogReader.IsJsonLines(oldPath));

        // --- a log that is not a log at all ---
        var emptyPath = Path.Combine(scratch, "Recover 2026-10-27_09-00-00.log");
        File.WriteAllText(emptyPath, string.Empty);
        Check("an empty file is not JSON Lines", !RecoveryLogReader.IsJsonLines(emptyPath));
        var emptyRead = RecoveryLogReader.Read(emptyPath);
        Check("an empty file reads as no operations", emptyRead.Operations.Count == 0, $"count={emptyRead.Operations.Count}");

        // --- a big log, to show the reader keeps up ---
        var bigPath = Path.Combine(scratch, "Recover 2026-11-01_10-00-00.log");
        using (var writer = new StreamWriter(bigPath))
        {
            writer.WriteLine("""{"type":"header","format":1,"started":"2026-11-01T10:00:00.0000000+00:00","shown":100000,"recoverable":100000,"folders":10}""");

            for (var n = 1; n <= 100000; n++)
            {
                writer.WriteLine($$"""{"type":"begin","n":{{n}},"ts":"2026-11-01T10:00:00.0000000+00:00","parent":"p{{n}}","child":"c{{n}}","parent_name":"f{{n}}.jpg","folder":"f{{n}}","plan":"restore the parent, then regenerate the child"}""");
                writer.WriteLine($$"""{"type":"end","n":{{n}},"ts":"2026-11-01T10:00:01.0000000+00:00","outcome":"recovered"}""");
            }
        }

        var started = DateTime.UtcNow;
        var bigRead = RecoveryLogReader.Read(bigPath);
        var elapsed = DateTime.UtcNow - started;

        Check("a 100,000-operation log reads every operation", bigRead.Operations.Count == 100000, $"count={bigRead.Operations.Count}");
        Check("a 100,000-operation log reads in a reasonable time", elapsed < TimeSpan.FromSeconds(30), $"{elapsed.TotalSeconds:N1}s");
        Console.WriteLine($"      (100,000 operations read in {elapsed.TotalSeconds:N1}s)");

        Directory.Delete(scratch, recursive: true);

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed.");

        return failures == 0 ? 0 : 1;
    }
}
