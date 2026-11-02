using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Utility.Server.Finder;
using Utility.Server.Finder.Services;

namespace Utility.UI.Services;

/// <summary>
/// Writes Liem's List to a CSV file the reader chooses the place for
/// (docs/liems-list-csv-spec.md §6).
/// </summary>
/// <remarks>
/// The report reads the media library, the Dropbox index and the database, and writes
/// one file. Nothing else is changed.
/// </remarks>
public interface ILiemListCsvExporter
{
    /// <summary>
    /// Asks where the file goes, writes it, and opens it.
    /// </summary>
    /// <returns>The path the file was written to, or null when the reader cancelled.</returns>
    Task<string?> ExportAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class LiemListCsvExporter(
    ILiemListReportService reports,
    ILogSink log) : ILiemListCsvExporter
{
    /// <summary>
    /// Where the dialog starts: the reader's own Documents folder.
    /// </summary>
    private static string? DefaultFolder
    {
        get
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            return string.IsNullOrEmpty(folder) || !Directory.Exists(folder) ? null : folder;
        }
    }

    public async Task<string?> ExportAsync(CancellationToken ct = default)
    {
        var report = await reports.GetReportAsync(ct);

        if (report is null)
        {
            throw new InvalidOperationException(
                "There is no pass to report on yet — run the Finder's pass first.");
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Liem's List",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = FileName(report),
            InitialDirectory = DefaultFolder,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        // UTF-8 WITH a BOM: Excel needs it to read a UTF-8 file's paths correctly.
        using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
        {
            LiemListCsv.Write(writer, report);
        }

        log.Info($"Liem's List written to {dialog.FileName}.");

        Open(dialog.FileName);

        return dialog.FileName;
    }

    /// <summary>
    /// Opens the file in whatever the machine has for a CSV. A machine with no handler
    /// for it still has the file.
    /// </summary>
    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // The file is written either way, and the status line says where.
        }
    }

    /// <summary>
    /// The report's file name: the list, the day and the time, with no colon.
    /// </summary>
    /// <remarks>
    /// A colon is not a legal character in a Windows file name, so the time carries no
    /// separator between the hours and the minutes.
    /// </remarks>
    private static string FileName(LiemListReport report)
    {
        var stamp = report.PassAt.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);

        return $"Liem's List {stamp}.csv";
    }
}
