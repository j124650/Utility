using System.Globalization;
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
public sealed class LiemListReportService(IFinderScanState scanState) : ILiemListReportService
{
    public Task<LiemListReport?> GetReportAsync(CancellationToken ct = default)
    {
        // The report is on the pass that already ran. No pass has run, there is nothing
        // to report on.
        if (!scanState.IsLoaded || scanState.Summary is null)
        {
            return Task.FromResult<LiemListReport?>(null);
        }

        var summary = scanState.Summary;

        // The unresolved rows of the gathered set: the verdict decides membership, and
        // nothing else does (§2).
        var unresolved = scanState.Rows
            .Where(r => FinderVerdictRules.LeavesUnresolved(r.Verdict))
            .ToList();

        var pairs = unresolved.Select(Project).ToList();

        // By style, then newest first (§5). The style is the parent's folder, verbatim.
        var sections = pairs
            .GroupBy(p => p.Style, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LiemListSection
            {
                Style = g.Key,
                Pairs = g
                    .OrderByDescending(p => p.DateAdded)
                    .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        var breakdown = LiemListReason.All
            .Select(reason => new LiemListReasonCount(
                reason,
                pairs.Count(p => p.Reason == reason)))
            .ToList();

        var report = new LiemListReport
        {
            Title = $"Liem's List — {summary.At.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}",
            PassAt = summary.At,
            PairsScanned = summary.Pairs,
            Breakdown = breakdown,
            Sections = sections
        };

        return Task.FromResult<LiemListReport?>(report);
    }

    /// <summary>
    /// The report's row for a gathered row (§3).
    /// </summary>
    /// <remarks>
    /// Every value is a field of the gathered row. Nothing is re-derived except the
    /// reason, which is read off the row's verdict and candidates (§4).
    /// </remarks>
    private static LiemListPair Project(FinderRow row)
    {
        var original = row.Parent.Size;

        var candidates = row.Candidates
            .Select(c => new LiemListCandidate
            {
                Rank = c.Rank,
                Path = c.Path,
                SizeDisplay = c.SizeDisplay,
                Dimensions = c.Dimensions,
                WhatMatched = c.WhatMatched,
                SizeMatches = original.HasValue && c.Size == original
            })
            .ToList();

        return new LiemListPair
        {
            // The style is the media library path — the folder the parent sits in (§3).
            Style = row.ParentFolder,

            FileName = row.ParentFileName,

            // The child's category names the type; for 24 Angles the variant names the
            // angle within it, and the two are set on two lines (§6.3).
            AngleLines = AngleLines(row),

            DateAdded = row.ParentDateAdded,
            DateAddedDisplay = row.ParentDateAdded?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,

            OriginalSizeDisplay = original?.ToString("N0", CultureInfo.InvariantCulture) ?? string.Empty,

            Reason = LiemListReason.For(row),

            Candidates = candidates
        };
    }

    /// <summary>
    /// The angle or featured type, as its lines (§3, §6.3).
    /// </summary>
    /// <remarks>
    /// The numbered shots are <c>24 Angles</c>, and the featured ones carry their own
    /// type. Where the category is <c>24 Angles</c> the child's variant names the
    /// particular angle within it, so the two are set on two lines.
    /// </remarks>
    private static IReadOnlyList<string> AngleLines(FinderRow row)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(row.ChildCategoryName))
        {
            lines.Add(row.ChildCategoryName);
        }

        if (IsAngles(row.ChildCategoryName) && !string.IsNullOrWhiteSpace(row.ChildVariantName))
        {
            lines.Add(row.ChildVariantName);
        }

        return lines;
    }

    /// <summary>Whether the category is the numbered shots — <c>24 Angles</c>.</summary>
    private static bool IsAngles(string? categoryName) =>
        string.Equals(categoryName, "24 Angles", StringComparison.OrdinalIgnoreCase);
}
