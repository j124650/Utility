using System.Globalization;
using System.IO;
using System.Text;
using Utility.Server.Finder;

namespace Utility.UI.Reports;

/// <summary>
/// Writes Liem's List as a single self-contained HTML file (§6).
/// </summary>
/// <remarks>
/// <para>
/// The file is inline-styled, script-free and asset-free: nothing is fetched when it is
/// opened. It is a static document — no filters, no sorting, no search — and it reads
/// like a report and prints like one.
/// </para>
/// <para>
/// The file is UTF-8 with a BOM, and declares <c>&lt;meta charset="utf-8"&gt;</c>. The
/// BOM is what Windows tools read the encoding from; the meta tag is what the browser
/// reads. Text is never transliterated and never escaped into ASCII: a file name that
/// arrives is a file name that is written.
/// </para>
/// <para>
/// The list is a whole library's worth of pairs — hundreds of thousands of them — so the
/// MARKUP is lean and the STYLES do the repeating. The four field names a pair carries
/// are drawn once in the stylesheet rather than written into the document for every pair,
/// and nothing is written twice that does not have to be. The document holds the DATA;
/// the rest is the browser's to draw.
/// </para>
/// </remarks>
public static class LiemListHtmlWriter
{
    /// <summary>
    /// Writes the report to the given path.
    /// </summary>
    /// <param name="report">The report to write.</param>
    /// <param name="path">Where the file goes.</param>
    public static void Write(LiemListReport report, string path)
    {
        // UTF-8 with a BOM. The BOM is what Windows tools read the encoding from when
        // they open the file (§6.1).
        File.WriteAllText(path, Render(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>The report as one HTML document (§6.1).</summary>
    public static string Render(LiemListReport report)
    {
        var html = new StringBuilder();

        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>");
        html.Append(Escape(report.Title));
        html.Append("</title><style>");
        html.Append(Styles);
        html.Append("</style></head><body>");

        RenderHeader(html, report);

        foreach (var section in report.Sections)
        {
            RenderSection(html, section);
        }

        html.Append("</body></html>");

        return html.ToString();
    }

    /// <summary>
    /// The header block: the title, the pass the report is on, and the breakdown of the
    /// unresolved pairs by reason (§6.2).
    /// </summary>
    /// <remarks>
    /// The pass is named in the document rather than in a footnote: the report is only as
    /// fresh as that pass.
    /// </remarks>
    private static void RenderHeader(StringBuilder html, LiemListReport report)
    {
        html.Append("<header><h1>");
        html.Append(Escape(report.Title));
        html.Append("</h1><p class=\"pass\">Pass of ");
        html.Append(Escape(report.PassAt.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)));
        html.Append(" — ");
        html.Append(report.PairsScanned.ToString("N0", CultureInfo.InvariantCulture));
        html.Append(" pair(s) scanned — ");
        html.Append(report.PairCount.ToString("N0", CultureInfo.InvariantCulture));
        html.Append(" left unresolved.</p><ul class=\"breakdown\">");

        foreach (var entry in report.Breakdown)
        {
            html.Append("<li class=\"r");
            html.Append(entry.Reason.Id);
            html.Append("\"><i style=\"background:");
            html.Append(entry.Reason.Color);
            html.Append("\"></i>");
            html.Append(Escape(entry.Reason.Text));
            html.Append("<b>");
            html.Append(entry.Count.ToString("N0", CultureInfo.InvariantCulture));
            html.Append("</b></li>");
        }

        html.Append("</ul></header>");
    }

    /// <summary>One style's section: the style as its heading, its pairs beneath (§6.2).</summary>
    private static void RenderSection(StringBuilder html, LiemListSection section)
    {
        html.Append("<section><h2>");
        html.Append(Escape(section.Style));
        html.Append("</h2>");

        foreach (var pair in section.Pairs)
        {
            RenderPair(html, pair);
        }

        html.Append("</section>");
    }

    /// <summary>
    /// One pair: a block, not a line in a grid — the reason and the candidate paths
    /// both need vertical room (§6.3).
    /// </summary>
    /// <remarks>
    /// The field names are NOT written here: the stylesheet draws them, once, for every
    /// pair alike. A pair carries its values and nothing else.
    /// </remarks>
    private static void RenderPair(StringBuilder html, LiemListPair pair)
    {
        html.Append("<article class=\"r");
        html.Append(pair.Reason.Id);
        html.Append("\"><h3>");
        html.Append(Escape(pair.FileName));
        html.Append("</h3>");

        // The angle or featured type: the child's category, and for 24 Angles the
        // variant that names the angle within it (§3, §6.3). The two are set on two
        // lines, so the value carries a line break.
        html.Append("<p>");
        html.Append(Escape(pair.AngleLines.Count > 0 ? pair.AngleLines[0] : string.Empty));

        if (pair.AngleLines.Count > 1)
        {
            html.Append('\n');
            html.Append(Escape(pair.AngleLines[1]));
        }

        html.Append("</p>");

        html.Append("<p>");
        html.Append(Escape(pair.DateAddedDisplay));
        html.Append("</p>");

        html.Append("<p>");
        html.Append(Escape(pair.OriginalSizeDisplay));
        html.Append("</p>");

        html.Append("<p>");
        html.Append(Escape(pair.Reason.Text));
        html.Append("</p>");

        RenderCandidates(html, pair);

        html.Append("</article>");
    }

    /// <summary>
    /// The candidates, in rank order, one per line (§6.4).
    /// </summary>
    /// <remarks>
    /// A candidate whose size equals the original size is marked in words, because that
    /// comparison is the decision the reader is making. The paths are shown in full and
    /// wrapped — a truncated path is useless to someone about to act on it.
    /// </remarks>
    private static void RenderCandidates(StringBuilder html, LiemListPair pair)
    {
        if (pair.Candidates.Count == 0)
        {
            // A pair with no candidates carries no list. The reason says why — "not
            // found" means the file name is not in the index at all (§4) — so there is
            // nothing to add under it.
            return;
        }

        html.Append("<ol>");

        foreach (var candidate in pair.Candidates)
        {
            html.Append("<li><b>");
            html.Append(candidate.Rank);
            html.Append("</b><span class=\"p\">");
            html.Append(Escape(candidate.Path));
            html.Append("</span><span>");
            html.Append(Escape(candidate.SizeDisplay));
            html.Append("</span><span>");
            html.Append(Escape(candidate.Dimensions));
            html.Append("</span><span class=\"x\">");
            html.Append(Escape(candidate.WhatMatched));
            html.Append("</span>");

            if (candidate.SizeMatches)
            {
                html.Append("<span class=\"y\">size matches</span>");
            }

            html.Append("</li>");
        }

        html.Append("</ol>");
    }

    /// <summary>
    /// Escapes a value for HTML.
    /// </summary>
    /// <remarks>
    /// Only the characters HTML reserves are escaped. Everything else is written as it
    /// arrived — a file name in Chinese is written in Chinese, not transliterated and
    /// not escaped into ASCII (§6.1).
    /// </remarks>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    /// <summary>
    /// The document's styles (§6.5, §6.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason is spelled out in words AND carried as the block's coloured edge — red
    /// for <i>not found</i>, amber for <i>name found but size differs</i>, indigo for
    /// <i>ambiguous</i>. The colour is the fast read; the word is what carries it in
    /// print, where the colour reads as a grey value.
    /// </para>
    /// <para>
    /// Paths, sizes and dates are set in a monospace face, so the columns align without a
    /// grid and the sizes can be compared down the column. The body is set in the system
    /// sans. Both stacks name a CJK-capable face after the Latin one, so a file name or
    /// path written in Chinese is set in Chinese glyphs and still lines up in its column.
    /// </para>
    /// <para>
    /// A pair's field names are drawn HERE, by <c>::before</c>, rather than written into
    /// the document for every pair: the names do not change from one pair to the next,
    /// and the list is long.
    /// </para>
    /// <para>
    /// Each style section starts on a fresh page and its heading is never orphaned from
    /// its first pair; a pair block never splits across a page. The footer carries the
    /// page number and the report's title.
    /// </para>
    /// </remarks>
    private const string Styles = """
        :root {
            --ink: #111827;
            --muted: #6B7280;
            --rule: #E5E7EB;
            --not-found: #B91C1C;
            --size-differs: #B45309;
            --ambiguous: #4338CA;
            --match: #15803D;
            --sans: "Segoe UI", "Microsoft YaHei", sans-serif;
            --mono: Consolas, "NSimSun", monospace;
        }

        * { box-sizing: border-box; }

        html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }

        body {
            margin: 0;
            padding: 32px 36px 48px;
            background: #FFFFFF;
            color: var(--ink);
            font-family: var(--sans);
            font-size: 13px;
            line-height: 1.45;
        }

        header { border-bottom: 2px solid var(--rule); padding-bottom: 14px; margin-bottom: 26px; }

        h1 { margin: 0 0 6px; font-size: 22px; font-weight: 600; }

        .pass { margin: 0 0 10px; color: var(--muted); font-size: 12px; }

        .breakdown { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 18px; }

        .breakdown li { display: flex; align-items: center; gap: 7px; font-size: 12px; }

        .breakdown i { width: 10px; height: 10px; border-radius: 2px; display: inline-block; }

        .breakdown b { font-family: var(--mono); font-weight: 600; }

        section { margin-bottom: 30px; }

        h2 {
            margin: 0 0 12px;
            padding-bottom: 6px;
            border-bottom: 1px solid var(--rule);
            font-family: var(--mono);
            font-size: 15px;
            font-weight: 600;
            break-after: avoid;
            page-break-after: avoid;
        }

        /*
          A pair block. Off-screen pairs are skipped by the layout engine rather than
          laid out — the list is long enough that laying all of it out at once is what
          makes the window heavy.
        */
        article {
            border-left: 4px solid var(--rule);
            padding: 10px 14px;
            margin-bottom: 14px;
            background: #FAFAFB;
            break-inside: avoid;
            page-break-inside: avoid;
            content-visibility: auto;
            contain-intrinsic-size: auto 120px;
        }

        article.r0 { border-left-color: var(--not-found); }
        article.r1 { border-left-color: var(--size-differs); }
        article.r2 { border-left-color: var(--ambiguous); }

        h3 { margin: 0 0 8px; font-size: 14px; font-weight: 600; word-break: break-all; }

        /*
          A pair's fields: the field name on the left, the value on the right. The name
          is the ::before, drawn once for every pair alike.
        */
        article > p {
            display: flex;
            align-items: flex-start;
            column-gap: 14px;
            margin: 0 0 2px;
            white-space: pre-line;
        }

        article > p::before {
            flex: 0 0 150px;
            color: var(--muted);
            font-family: var(--sans);
            font-size: 12px;
        }

        article > p:nth-of-type(1)::before { content: "Angle or featured type"; }
        article > p:nth-of-type(2)::before { content: "Date added"; }
        article > p:nth-of-type(3)::before { content: "Original size"; }
        article > p:nth-of-type(4)::before { content: "Reason"; }

        article > p:nth-of-type(2), article > p:nth-of-type(3) { font-family: var(--mono); font-size: 12px; }

        article > p:nth-of-type(4) { font-weight: 600; }

        article.r0 > p:nth-of-type(4) { color: var(--not-found); }
        article.r1 > p:nth-of-type(4) { color: var(--size-differs); }
        article.r2 > p:nth-of-type(4) { color: var(--ambiguous); }

        ol {
            list-style: none;
            margin: 12px 0 0;
            padding: 10px 0 0;
            border-top: 1px dashed var(--rule);
        }

        li {
            margin-bottom: 7px;
            display: flex;
            flex-wrap: wrap;
            align-items: baseline;
            column-gap: 14px;
        }

        li b {
            color: var(--muted);
            font-family: var(--mono);
            font-size: 12px;
            font-weight: normal;
            min-width: 14px;
        }

        li span { font-family: var(--mono); font-size: 12px; }

        li .x { color: var(--muted); font-family: var(--sans); font-size: 11px; }

        li .y { color: var(--match); font-family: var(--sans); font-size: 11px; font-weight: 600; }

        li .p { flex: 1 1 100%; word-break: break-all; overflow-wrap: anywhere; }

        @media print {
            body { padding: 0; }

            /* Nothing is skipped on paper: every pair prints, whether or not it was on screen. */
            article { content-visibility: visible; contain-intrinsic-size: none; }

            section { break-before: page; page-break-before: always; }
            section:first-of-type { break-before: auto; page-break-before: auto; }

            @page {
                margin: 16mm 14mm;

                @bottom-center {
                    content: "Liem's List — " counter(page) " of " counter(pages);
                    font-family: var(--sans);
                    font-size: 9pt;
                    color: #6B7280;
                }
            }
        }
        """;
}
