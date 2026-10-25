using System.IO;
using System.Windows;
using Utility.Server.Finder;
using Utility.Server.Finder.Services;
using Utility.UI.Reports;
using Utility.UI.Views;

namespace Utility.UI.Services;

/// <summary>
/// Writes Liem's List to a file and shows it in a window (docs/liems-list-spec.md §6).
/// </summary>
/// <remarks>
/// The report file is written to a temporary folder and opened in the app's own window.
/// Nothing in the media library or the Dropbox index is changed — the report reads them
/// and writes one file, and that file is the report.
/// </remarks>
public interface ILiemListReportPresenter
{
    /// <summary>
    /// Writes the report and shows it in a modal window owned by the main window.
    /// </summary>
    /// <returns>The path the report was written to.</returns>
    Task<string> ShowAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class LiemListReportPresenter(
    ILiemListReportService reports,
    ILogSink log) : ILiemListReportPresenter
{
    /// <summary>
    /// Where the report file is written.
    /// </summary>
    /// <remarks>
    /// The system temp folder, under a folder of the app's own so the report does not
    /// land loose among the reader's other temporary files.
    /// </remarks>
    private static string ReportFolder => Path.Combine(Path.GetTempPath(), "Utility");

    public async Task<string> ShowAsync(CancellationToken ct = default)
    {
        var report = await reports.GetReportAsync(ct);

        if (report is null)
        {
            throw new InvalidOperationException(
                "There is no pass to report on yet — run the Finder's pass first.");
        }

        var path = Path.Combine(ReportFolder, FileName(report));

        Directory.CreateDirectory(ReportFolder);
        LiemListHtmlWriter.Write(report, path);

        log.Info($"Liem's List written to {path}.");

        // The window is modal and owned by the main window, so the Finder cannot be used
        // while the report is up, and focus returns to the Finder when it closes.
        var window = new LiemListWindow(path, report.Title)
        {
            Owner = Application.Current.MainWindow
        };

        window.ShowDialog();

        return path;
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
        var stamp = report.PassAt.ToString("yyyy-MM-dd HHmm");

        return $"Liem's List {stamp}.html";
    }
}
