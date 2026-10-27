using System.IO;
using Utility.Server.Finder;

namespace Utility.UI.Services;

/// <summary>
/// One thing the repair will do, with the real paths.
/// </summary>
/// <remarks>
/// The row of the confirmation window's details table (§10.1.4). An action is a step,
/// not a pair: a pair whose parent and child both need work produces two of them.
/// </remarks>
public sealed record RecoverAction
{
    /// <summary>The action's position in the execution order.</summary>
    public required int Position { get; init; }

    /// <summary>The parent's file name - which pair the action belongs to.</summary>
    public required string Pair { get; init; }

    /// <summary>The pair's folder.</summary>
    public required string Folder { get; init; }

    /// <summary>What the action does: restore the parent, or regenerate the child.</summary>
    public required string Action { get; init; }

    /// <summary>Where the bytes come from - the Dropbox path, or the parent's path on disk.</summary>
    public required string From { get; init; }

    /// <summary>The real path on disk the action writes to.</summary>
    public required string To { get; init; }

    /// <summary>The candidate's size in bytes, or the child's target dimensions.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// The actions the repair will perform, in the order it will perform them.
/// </summary>
/// <remarks>
/// <para>
/// The order is the execution order: the pairs in the order the filters left them, and
/// within a pair the parent before the child, because the child is derived from it.
/// </para>
/// <para>
/// The list is BOUNDED - it holds at most <see cref="Bound"/> actions, the first ones of
/// the run. This is a confirmation window, not a place to browse a job: the figures above
/// the table are what the reader decides on, and the table is a sample of what they check
/// those figures against. A run over the whole library is about 400,000 actions, and
/// listing all of them would make the window a data grid with a dialog wrapped around it.
/// </para>
/// </remarks>
public sealed class RecoverActionList
{
    /// <summary>How many actions the table shows at most.</summary>
    public const int Bound = 200;

    private readonly IReadOnlyList<RecoverAction> _actions;

    private RecoverActionList(IReadOnlyList<RecoverAction> actions, int total)
    {
        _actions = actions;
        Total = total;
    }

    /// <summary>The actions shown.</summary>
    public IReadOnlyList<RecoverAction> Actions => _actions;

    /// <summary>How many actions the run has in all.</summary>
    public int Total { get; }

    /// <summary>How many of them are shown.</summary>
    public int Shown => _actions.Count;

    /// <summary>Whether every action is shown.</summary>
    public bool IsComplete => Total == Shown;

    /// <summary>
    /// The line above the table: how many actions there are, and how many are shown.
    /// </summary>
    public string Caption => IsComplete
        ? $"{Total:N0} action(s), in the order they will be performed"
        : $"{Total:N0} action(s), in the order they will be performed - showing the first {Shown:N0}";

    /// <summary>
    /// Builds the list from the rows the repair will act on.
    /// </summary>
    /// <param name="rows">The rows the filters left.</param>
    /// <param name="libraryRoot">Where the library lives - the root the files are written to.</param>
    public static RecoverActionList From(IReadOnlyList<FinderRow> rows, string libraryRoot)
    {
        var subject = rows.Where(r => r.Verdict.Id is 1 or 2 or 3).ToList();

        var actions = new List<RecoverAction>();
        var total = 0;

        foreach (var row in subject)
        {
            var parentPath = Path.Combine(
                libraryRoot, row.ParentFolder, row.ParentId.ToString().ToUpperInvariant());

            var childPath = Path.Combine(
                libraryRoot, row.ParentFolder, row.ChildId.ToString().ToUpperInvariant());

            // The parent first, when it needs restoring: the child is derived from it.
            if (row.Verdict.Id is 1 or 3)
            {
                var candidate = row.BestCandidate;

                total++;

                if (actions.Count < Bound)
                {
                    actions.Add(new RecoverAction
                    {
                        Position = total,
                        Pair = row.ParentFileName,
                        Folder = row.ParentFolder,
                        Action = "restore the parent",
                        From = candidate?.Path ?? string.Empty,
                        To = parentPath,
                        Detail = candidate?.Size is { } size ? $"{size:N0} bytes" : string.Empty,
                    });
                }
            }

            if (row.Verdict.Id is 2 or 3)
            {
                total++;

                if (actions.Count < Bound)
                {
                    var target = row.ChildWidth.HasValue && row.ChildHeight.HasValue
                        ? $"{row.ChildWidth} × {row.ChildHeight}"
                        : string.Empty;

                    actions.Add(new RecoverAction
                    {
                        Position = total,
                        Pair = row.ParentFileName,
                        Folder = row.ParentFolder,
                        Action = "regenerate the child",
                        From = parentPath,
                        To = childPath,
                        Detail = target,
                    });
                }
            }
        }

        return new RecoverActionList(actions, total);
    }
}
