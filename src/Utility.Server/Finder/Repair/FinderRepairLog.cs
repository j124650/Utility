using System.Text;
using Microsoft.Extensions.Logging;

namespace Utility.Server.Finder.Repair;

/// <summary>
/// Where the repair's records go.
/// </summary>
/// <remarks>
/// The recovery writes log records; where they go is the provider's business (§10.5).
/// Today the provider writes a text file; later it can write a table, and the recovery
/// does not change.
/// </remarks>
public interface IFinderRepairLogProvider
{
    /// <summary>
    /// Opens the log for a run and writes its summary.
    /// </summary>
    /// <returns>
    /// The path of the log, or null when it could not be opened. A log that cannot be
    /// written must never stop the recovery.
    /// </returns>
    Task<IFinderRepairLog?> OpenAsync(FinderRepairSummary summary, CancellationToken ct = default);
}

/// <summary>
/// An open log, one pair's record at a time.
/// </summary>
public interface IFinderRepairLog : IAsyncDisposable
{
    /// <summary>The path of the log, so the reader can be told where it is.</summary>
    string Path { get; }

    /// <summary>Writes one pair's record.</summary>
    Task WriteAsync(FinderRepairRecord record, CancellationToken ct = default);

    /// <summary>
    /// Writes the run's summary.
    /// </summary>
    /// <remarks>
    /// The log opens with the summary — the figures the confirmation window showed, so the
    /// two agree — and it is written again at the end, so the log of a run that was stopped
    /// still says how far it got.
    /// </remarks>
    Task WriteSummaryAsync(FinderRepairSummary summary, CancellationToken ct = default);
}

/// <summary>
/// Writes the repair's log to a text file beside the app's own log.
/// </summary>
/// <remarks>
/// <para>
/// The file is named for the app run it belongs in — <c>Recover 2026-10-26_18-20-01.log</c>
/// — so the two sit together and sort together, and every line in it belongs to that run.
/// </para>
/// <para>
/// The lines are flushed as they are written. The run worth reading is the one that ended
/// badly, and it ends without a clean shutdown, so a buffer would lose exactly the lines
/// that explain why.
/// </para>
/// </remarks>
public sealed class TextFinderRepairLogProvider(ILogger<TextFinderRepairLogProvider> logger) : IFinderRepairLogProvider
{
    /// <summary>Where the log lives: beside the app's own log.</summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Utility",
        "logs");

    public async Task<IFinderRepairLog?> OpenAsync(
        FinderRepairSummary summary,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            // The stamp is the app run's, not the recovery's: a recovery happens inside a
            // run, and the run's stamp is what the app's own log carries.
            var stamp = DateTime.Now;
            var path = Path.Combine(LogDirectory, $"Recover {stamp:yyyy-MM-dd_HH-mm-ss}.log");

            var writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync($"Recovery run - {stamp:yyyy-MM-dd HH:mm}");
            await writer.WriteLineAsync($"  Filtered set      {summary.Shown:N0} pair(s)");
            await writer.WriteLineAsync($"  Recoverable       {summary.Recoverable:N0}");
            await writer.WriteLineAsync($"  Folders           {summary.Folders:N0}");
            await writer.WriteLineAsync();

            logger.LogInformation("Recovery log opened at {Path}.", path);

            return new TextFinderRepairLog(path, writer);
        }
        catch (Exception ex)
        {
            // A log that cannot be written must never stop the recovery.
            logger.LogError(ex, "The recovery log could not be opened.");
            return null;
        }
    }
}

/// <summary>
/// The log, as a text file of one record per group of lines.
/// </summary>
/// <remarks>
/// One pair's record is a group of lines, and the first line names the pair (§10.5.3).
/// The lines that follow carry the detail, in the order of the workflow.
/// </remarks>
internal sealed class TextFinderRepairLog(string path, StreamWriter writer) : IFinderRepairLog
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public async Task WriteAsync(FinderRepairRecord record, CancellationToken ct = default)
    {
        var lines = Format(record);

        lock (_gate)
        {
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        await Task.CompletedTask;
    }

    public async Task WriteSummaryAsync(FinderRepairSummary summary, CancellationToken ct = default)
    {
        var lines = new List<string>
        {
            string.Empty,
            $"Recovery run finished - {DateTime.Now:yyyy-MM-dd HH:mm}",
            $"  Filtered set      {summary.Shown:N0} pair(s)",
            $"  Recovered         {summary.Recovered:N0}",
            $"    parent restored   {summary.ParentRestored:N0}",
            $"    child regenerated {summary.ChildRegenerated:N0}",
            $"    both              {summary.Both:N0}",
            $"  Not recovered     {summary.NotRecovered:N0}",
            $"  Failed            {summary.Failed:N0}",
            $"  Downloaded        {summary.DownloadedBytes / 1024.0 / 1024.0 / 1024.0:N1} GB",
            $"  Replaced          {summary.Replaced:N0} existing file(s)",
            $"  Log               {Path}",
        };

        lock (_gate)
        {
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// The record's lines: the first names the pair, the rest carry the detail.
    /// </summary>
    private static List<string> Format(FinderRepairRecord record)
    {
        var stamp = record.When.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = record.Recovered ? "Information" : "Error";
        var outcome = record.Recovered
            ? $"{DescribeOutcome(record)}."
            : $"not recovered. reason={record.Reason}";

        var lines = new List<string>
        {
            $"{stamp} [{level}] Recovery: {outcome} parent={record.ParentId} child={record.ChildId} " +
            $"parent_name={record.ParentName} folder={record.Folder}"
        };

        if (record.Source is not null)
        {
            lines.Add(
                $"{stamp} [{level}] Recovery:   node={record.NodeId} dropbox_id={record.DropboxId} " +
                $"namespace={record.NamespaceId} content_hash={record.ContentHash}");
        }

        if (record.Source is not null)
        {
            lines.Add($"{stamp} [{level}] Recovery:   source={record.Source}");
        }

        if (record.CopiedTo is not null)
        {
            lines.Add(
                $"{stamp} [{level}] Recovery:   copied_to={record.CopiedTo} copied_bytes={record.CopiedBytes:N0}");
        }

        if (record.ResizedTo is not null)
        {
            lines.Add(
                $"{stamp} [{level}] Recovery:   resized_from={record.ResizedFrom} " +
                $"resized_for={record.ResizedFor} resized_to={record.ResizedTo} target={record.Target}");
        }

        if (record.Result is not null)
        {
            lines.Add($"{stamp} [{level}] Recovery:   result={record.Result}");
        }

        return lines;
    }

    private static string DescribeOutcome(FinderRepairRecord record)
    {
        var parts = new List<string>();

        if (record.CopiedTo is not null)
        {
            parts.Add("parent restored");
        }

        if (record.ResizedTo is not null)
        {
            parts.Add("child regenerated");
        }

        return parts.Count > 0 ? string.Join(", then ", parts) : "recovered";
    }

    public async ValueTask DisposeAsync()
    {
        // The writer is disposed outside the lock: an await cannot sit inside one, and the
        // writer is only touched here - the run has finished by the time this is called.
        await writer.DisposeAsync().ConfigureAwait(false);
    }
}
