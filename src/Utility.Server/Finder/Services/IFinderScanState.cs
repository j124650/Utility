namespace Utility.Server.Finder.Services;

/// <summary>
/// The last pass's rows, so the Finder tab and Liem's List share one pass.
/// </summary>
/// <remarks>
/// The pass is long - it opens every parent and child file in the library - and its result
/// is big, so it is read once and kept. Liem's List is a report on the SAME scan (§11), not
/// a second one, and it is only as fresh as this.
/// </remarks>
public interface IFinderScanState
{
    /// <summary>The rows the last pass produced.</summary>
    IReadOnlyList<FinderRow> Rows { get; }

    /// <summary>What the last pass found, in numbers.</summary>
    FinderScanSummary? Summary { get; }

    /// <summary>True once a pass has produced rows.</summary>
    bool IsLoaded { get; }

    /// <summary>Publishes a completed pass.</summary>
    void Publish(IReadOnlyList<FinderRow> rows, FinderScanSummary summary);
}

/// <summary>What a pass found.</summary>
/// <param name="Pairs">How many pairs the pass covered.</param>
/// <param name="ByVerdict">How many pairs carry each verdict.</param>
public sealed record FinderScanSummary(int Pairs, IReadOnlyDictionary<FinderVerdict, int> ByVerdict);
