namespace Utility.Server.Finder.Services;

/// <summary>
/// Holds the last pass's rows, so the Finder tab and Liem's List share one pass.
/// </summary>
/// <remarks>
/// The pass is long - it opens every parent and child file in the library - and its result
/// is big, so it is read once and kept. Liem's List is a report on the SAME scan (§11), not
/// a second one, and it is only as fresh as this.
/// </remarks>
public sealed class FinderScanState : IFinderScanState
{
    public IReadOnlyList<FinderRow> Rows { get; private set; } = [];

    public FinderScanSummary? Summary { get; private set; }

    public bool IsLoaded { get; private set; }

    public void Publish(IReadOnlyList<FinderRow> rows, FinderScanSummary summary)
    {
        Rows = rows;
        Summary = summary;
        IsLoaded = true;
    }
}
