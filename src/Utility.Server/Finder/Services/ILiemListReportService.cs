using Utility.Server.Finder;

namespace Utility.Server.Finder.Services;

/// <summary>
/// Builds Liem's List — the report of the pairs the verdict leaves unresolved (§1, §11).
/// </summary>
/// <remarks>
/// It reads the rows the Finder's pass GATHERED and projects them onto the report. It
/// runs no pass of its own: the report is only as fresh as the pass it is on, and the
/// pass is named in the report (§6.2).
/// <para>
/// Membership is decided by the verdict alone (§2): the two verdicts that leave a pair
/// unresolved. A filter over the Finder's table is a filter over the display, not over
/// the data, so the report is built from the gathered rows and not from the table's.
/// </para>
/// </remarks>
public interface ILiemListReportService
{
    /// <summary>
    /// The report of the last pass: its unresolved pairs, one section per style (§6).
    /// </summary>
    /// <remarks>
    /// The report is null when no pass has run — there is nothing to report on.
    /// </remarks>
    Task<LiemListReport?> GetReportAsync(CancellationToken ct = default);
}
