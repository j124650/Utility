using System.Globalization;

namespace Utility.Server.Finder.Repair;

/// <summary>
/// Writes a recovery run's FAILURES as a CSV — one row per failed operation.
/// </summary>
/// <remarks>
/// <para>
/// The CSV is what a reader takes to a spreadsheet, so it is written the way a
/// spreadsheet reads it: every field is quoted, an embedded quote is doubled, a line
/// ends with CRLF, and the file is UTF-8 WITH a BOM, because Excel needs the BOM to
/// read a UTF-8 file's paths correctly.
/// </para>
/// <para>
/// The reason in the CSV is the SHORT reason — the high-signal phrase — not the raw
/// exception message. The raw reason stays in the log, which the reader can always
/// open.
/// </para>
/// </remarks>
public static class RecoveryFailureCsv
{
    /// <summary>The columns, in order.</summary>
    private static readonly string[] Columns =
    [
        "Number",
        "Pair",
        "Folder",
        "Plan",
        "Outcome",
        "Reason",
        "Source",
        "Into",
        "Started",
        "Ended",
        "Duration (s)",
    ];

    /// <summary>
    /// Writes the failures as CSV. The writer is NOT closed - the caller owns it.
    /// </summary>
    /// <param name="writer">Where the CSV goes.</param>
    /// <param name="failures">The operations to write. Only the failed ones are written.</param>
    /// <returns>How many rows were written.</returns>
    public static int Write(TextWriter writer, IEnumerable<RecoveryLogOperation> failures)
    {
        var written = 0;

        writer.WriteLine(string.Join(",", Columns.Select(CsvText.Quote)));

        foreach (var operation in failures)
        {
            if (!operation.Failed)
            {
                continue;
            }

            writer.WriteLine(string.Join(",", new[]
            {
                operation.Number.ToString(CultureInfo.InvariantCulture),
                operation.ParentName,
                operation.Folder,
                operation.Plan,
                operation.Outcome,
                operation.ShortReason,
                operation.Source ?? string.Empty,
                operation.CopiedTo ?? string.Empty,
                operation.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                operation.EndedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                operation.Duration?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
            }.Select(CsvText.Quote)));

            written++;
        }

        return written;
    }
}
