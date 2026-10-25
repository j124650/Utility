namespace Utility.Server.Finder;

/// <summary>
/// The verdict, read off the checks.
/// </summary>
/// <remarks>
/// This is the whole of the verdict's logic: four checks in, one verdict out. It lives on its
/// own so it can be read - and tested - without running a pass.
/// </remarks>
public static class FinderVerdictRules
{
    /// <summary>
    /// The verdict for a pair, from the four checks.
    /// </summary>
    /// <remarks>
    /// A healthy pair needs no repair. An unusable parent needs an original before anything
    /// can be done for it: candidates found but none confirmed by size means a human has to
    /// choose, and no candidates at all means there is nothing to choose from.
    /// </remarks>
    public static FinderVerdict Decide(
        bool parentOpens,
        bool childOpens,
        bool candidatesFound,
        bool originalConfirmed)
    {
        if (parentOpens)
        {
            return childOpens ? FinderVerdict.NothingToDo : FinderVerdict.RegenerateChild;
        }

        if (!candidatesFound)
        {
            return FinderVerdict.CannotRestore;
        }

        if (!originalConfirmed)
        {
            return FinderVerdict.NoOriginalNeedsDecision;
        }

        return childOpens
            ? FinderVerdict.RestoreParent
            : FinderVerdict.RestoreParentThenRegenerateChild;
    }

    /// <summary>
    /// The note that says why the verdict came out the way it did.
    /// </summary>
    public static string Note(
        FinderVerdict verdict,
        bool originalConfirmed,
        string? parentFault,
        string? childFault)
    {
        if (verdict == FinderVerdict.NoOriginalNeedsDecision)
        {
            return "no size match";
        }

        if (verdict == FinderVerdict.CannotRestore)
        {
            return "no candidate";
        }

        if (parentFault is not null)
        {
            return "parent corrupted";
        }

        if (childFault is not null)
        {
            return "child corrupted";
        }

        if (!originalConfirmed)
        {
            return "no size match";
        }

        return string.Empty;
    }
}
