using System.Globalization;

namespace Utility.Server.Finder;

/// <summary>
/// Writes Liem's List as a CSV — one row per unresolved pair
/// (docs/liems-list-csv-spec.md §2, §3).
/// </summary>
/// <remarks>
/// <para>
/// The CSV is what a reader takes to a spreadsheet, so it is written the way a
/// spreadsheet reads it: every field is quoted, an embedded quote is doubled, a line
/// ends with CRLF, and the file is UTF-8 WITH a BOM, because Excel needs the BOM to
/// read a UTF-8 file's paths correctly.
/// </para>
/// <para>
/// The rows are the report's own: its sections, in its order, and the pairs within
/// them. The report already decided which pairs the list holds and in what order.
/// </para>
/// </remarks>
public static class LiemListCsv
{
    /// <summary>The columns, in order.</summary>
    private static readonly string[] Columns =
    [
        "Path (style)",
        "Original name",
        "Angle",
        "Featured type",
        "Reason",
        "Candidates",
    ];

    /// <summary>
    /// Writes the report as CSV. The writer is NOT closed - the caller owns it.
    /// </summary>
    /// <param name="writer">Where the CSV goes.</param>
    /// <param name="report">The report to write.</param>
    /// <returns>How many rows were written.</returns>
    public static int Write(TextWriter writer, LiemListReport report)
    {
        var written = 0;

        writer.WriteLine(string.Join(",", Columns.Select(CsvText.Quote)));

        foreach (var section in report.Sections)
        {
            foreach (var pair in section.Pairs)
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    pair.Style,
                    pair.FileName,
                    pair.Angle,
                    pair.FeaturedType,
                    pair.Reason.Text,
                    Candidates(pair),
                }.Select(CsvText.Quote)));

                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// The pair's candidates, one per line: the rank, the path, the size, the
    /// dimensions and what matched, separated by " | "
    /// (docs/liems-list-csv-spec.md §4).
    /// </summary>
    /// <remarks>
    /// A pair with no candidates carries an empty cell - the reason says why.
    /// </remarks>
    private static string Candidates(LiemListPair pair) =>
        string.Join(
            Environment.NewLine,
            pair.Candidates.Select(candidate => string.Join(
                " | ",
                candidate.Rank.ToString(CultureInfo.InvariantCulture),
                candidate.Path,
                candidate.SizeDisplay,
                candidate.Dimensions,
                candidate.WhatMatched)));
}
