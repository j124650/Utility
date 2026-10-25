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
            return childOpens
                ? FinderVerdict.NothingToDo
                : FinderVerdict.RegenerateChild;
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
    /// Whether a verdict leaves the pair unresolved - the membership of Liem's List (§11).
    /// </summary>
    /// <remarks>
    /// Two verdicts leave a pair unresolved: <c>no original — needs a decision</c> and
    /// <c>cannot restore</c>.
    /// </remarks>
    public static bool LeavesUnresolved(FinderVerdict verdict) =>
        verdict == FinderVerdict.NoOriginalNeedsDecision ||
        verdict == FinderVerdict.CannotRestore;

    /// <summary>
    /// The note that says why the verdict came out the way it did.
    /// </summary>
    /// <remarks>
    /// The note names the reason in the reader's words (§19.9). A file that is there but
    /// broken is <em>corrupted</em>, and one that is not there at all is <em>missing</em> -
    /// two different problems, so the note says which.
    /// </remarks>
    public static string Note(
        FinderVerdict verdict,
        bool originalConfirmed,
        string? parentFault,
        string? childFault,
        bool tiebreakApplied)
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

        if (tiebreakApplied)
        {
            return "final tiebreak used";
        }

        return string.Empty;
    }

    /// <summary>
    /// The rules the verdict rests on, in the order of §7 (§18.6).
    /// </summary>
    /// <remarks>
    /// The candidate lines are listed only when the parent is missing or corrupted, because
    /// that is the only case where the verdict rests on them - a rule that did not apply is
    /// not marked ✕, it is simply not listed, because it was never in play. The tiebreak line
    /// is added when §9.1 rule 3 picked between several candidates, so the reader knows the
    /// choice was a rule's and not the only option.
    /// </remarks>
    public static IReadOnlyList<FinderRule> ChecksFor(FinderRow row)
    {
        var rules = new List<FinderRule>
        {
            new(row.CheckParentOpens.Mark, "parent file opens"),
            new(row.CheckChildOpens.Mark, "child file opens")
        };

        if (!row.Parent.IsOk)
        {
            rules.Add(new(row.CheckCandidatesFound.Mark, "candidates found"));
            rules.Add(new(row.CheckOriginalConfirmed.Mark, "original confirmed by size"));

            if (row.CheckTiebreakApplied is { } tiebreak)
            {
                rules.Add(new(tiebreak.Mark, "tiebreak applied"));
            }
        }

        return rules;
    }
}
