using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Utility.Server;
using Utility.Server.Finder;
using Utility.Server.Finder.Repair;
using Utility.Server.Finder.Services;

// The checks for the recovery's ORDER: the pairs closest to now are worked through first
// (§10.11). The ordering lives on IFinderService, so the checks drive the real method -
// not a re-implementation of it.

var checks = 0;
var failures = 0;

void Check(string what, bool ok, string detail = "")
{
    checks++;

    if (ok)
    {
        Console.WriteLine($"ok    {what}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL  {what}  {detail}");
    }
}

// The service is created through the container, so the checks run the shipped method.
var builder = Host.CreateApplicationBuilder();
builder.Services.AddUtilityServer(builder.Configuration);
using var host = builder.Build();
var finder = host.Services.GetRequiredService<IFinderService>();

// ---- a row, built the way the pass builds one ----

FinderRow Row(
    string name,
    DateTimeOffset? dateAdded,
    DateTimeOffset? candidateModified,
    bool withCandidate = true)
{
    var candidates = withCandidate
        ? new List<FinderCandidate>
        {
            new()
            {
                Rank = 0,
                WhatMatched = "name and size",
                Name = name,
                Size = 100,
                Dimensions = "1500x1500",
                Path = $"/Images/{name}",
                Modified = candidateModified,
            },
        }
        : new List<FinderCandidate>();

    return new FinderRow
    {
        ParentFileName = name,
        ParentFolder = "ca05434001",
        ParentId = Guid.NewGuid(),
        ChildId = Guid.NewGuid(),
        ChildCategoryName = "VCat",
        ChildVariantName = "Featured",
        ParentDateAdded = dateAdded,
        Parent = new FinderFileState
        {
            State = FileState.Missing,
            Size = 100,
            SizeCheck = null,
            Dimensions = "1500 × 1500",
        },
        Child = new FinderFileState
        {
            State = FileState.Missing,
            Size = 100,
            SizeCheck = null,
            Dimensions = "1500 × 1500",
        },
        Verdict = FinderVerdict.RestoreParent,
        VerdictNote = "",
        CheckParentOpens = new FinderCheck(false),
        CheckChildOpens = new FinderCheck(false),
        CheckCandidatesFound = new FinderCheck(true),
        CheckOriginalConfirmed = new FinderCheck(true),
        Candidates = candidates,
    };
}

var now = DateTimeOffset.Now;

var day = TimeSpan.FromDays(1);

// ---- the order is most recent first ----

var rows = new List<FinderRow>
{
    Row("OLD", now - (30 * day), now - (30 * day)),
    Row("NEW", now - (1 * day), now - (1 * day)),
    Row("MID", now - (7 * day), now - (7 * day)),
};

var ordered = finder.OrderByMostRecent(rows);

Check(
    "the most recent pair comes first",
    ordered[0].ParentFileName == "NEW",
    $"got {ordered[0].ParentFileName}");

Check(
    "the order runs from newest to oldest",
    ordered.Select(r => r.ParentFileName).SequenceEqual(new[] { "NEW", "MID", "OLD" }),
    string.Join(", ", ordered.Select(r => r.ParentFileName)));

// ---- the more recent of the two dates wins ----

var mixed = new List<FinderRow>
{
    // The row's date is old, the file's date is new: the file's date wins.
    Row("FILE_NEWER", now - (300 * day), now - (1 * day)),
    // The row's date is new, the file's date is old: the row's date wins.
    Row("ROW_NEWER", now - (1 * day), now - (300 * day)),
};

var mixedOrdered = finder.OrderByMostRecent(mixed);

Check(
    "the more recent of the two dates wins",
    mixedOrdered[0].ParentFileName == "FILE_NEWER",
    $"got {mixedOrdered[0].ParentFileName}");

// ---- a pair with only one date ----

var oneDate = new List<FinderRow>
{
    Row("NO_DATE", null, null, withCandidate: false),
    Row("ROW_ONLY", now - (2 * day), null),
    Row("FILE_ONLY", null, now - (3 * day)),
};

var oneDateOrdered = finder.OrderByMostRecent(oneDate);

Check(
    "a pair with only a row date is dated by it",
    oneDateOrdered[0].ParentFileName == "ROW_ONLY",
    $"got {oneDateOrdered[0].ParentFileName}");

Check(
    "a pair with only a file date is dated by it",
    oneDateOrdered[1].ParentFileName == "FILE_ONLY",
    $"got {oneDateOrdered[1].ParentFileName}");

Check(
    "a pair with neither date goes last",
    oneDateOrdered[2].ParentFileName == "NO_DATE",
    $"got {oneDateOrdered[2].ParentFileName}");

// ---- ties are broken by the name, case-insensitively ----

var ties = new List<FinderRow>
{
    Row("bravo", now - (1 * day), now - (1 * day)),
    Row("ALPHA", now - (1 * day), now - (1 * day)),
    Row("charlie", now - (1 * day), now - (1 * day)),
};

var tieOrdered = finder.OrderByMostRecent(ties);

Check(
    "ties are broken by the name, case-insensitively",
    tieOrdered.Select(r => r.ParentFileName).SequenceEqual(new[] { "ALPHA", "bravo", "charlie" }),
    string.Join(", ", tieOrdered.Select(r => r.ParentFileName)));

// ---- the order is deterministic: the same set gives the same order ----

var again = finder.OrderByMostRecent(ties);

Check(
    "the same set gives the same order",
    again.Select(r => r.ParentFileName).SequenceEqual(tieOrdered.Select(r => r.ParentFileName)));

// ---- nothing is lost or duplicated ----

var many = Enumerable.Range(0, 500)
    .Select(i => Row($"PAIR{i:D4}", now - (TimeSpan.FromDays(i % 90)), now - (TimeSpan.FromDays(i % 30))))
    .ToList();

var manyOrdered = finder.OrderByMostRecent(many);

Check(
    "every row is kept",
    manyOrdered.Count == many.Count,
    $"{manyOrdered.Count} of {many.Count}");

Check(
    "no row is duplicated",
    manyOrdered.Select(r => r.ParentFileName).Distinct().Count() == many.Count);

// The effective date is the MORE RECENT of the two, so the order is checked on that -
// not on the row's date alone.
DateTimeOffset Effective(FinderRow r) =>
    r.BestCandidate?.Modified > r.ParentDateAdded
        ? r.BestCandidate!.Modified!.Value
        : r.ParentDateAdded!.Value;

Check(
    "the dates come out in descending order",
    manyOrdered
        .Select(Effective)
        .Zip(manyOrdered.Skip(1).Select(Effective), (a, b) => a >= b)
        .All(x => x));

// ---- the remaining-time estimate (§10.4.3) ----

Check(
    "there is no estimate before a pair has finished",
    FinderRepairService.EstimateRemaining(100, 0, TimeSpan.Zero) is null);

Check(
    "the estimate is the last pair's time times the pairs to go",
    FinderRepairService.EstimateRemaining(100, 40, TimeSpan.FromSeconds(2))
        == TimeSpan.FromSeconds(2 * 60));

Check(
    "the estimate is zero when nothing is left",
    FinderRepairService.EstimateRemaining(100, 100, TimeSpan.FromSeconds(2))
        == TimeSpan.Zero);

Check(
    "the estimate is not negative when more are done than there are",
    FinderRepairService.EstimateRemaining(100, 120, TimeSpan.FromSeconds(2))
        == TimeSpan.Zero);

// The estimate must never be negative, whatever the numbers.
var negatives = 0;

for (var done = 0; done <= 150; done++)
{
    var e = FinderRepairService.EstimateRemaining(100, done, TimeSpan.FromSeconds(3));

    if (e is { } value && value < TimeSpan.Zero)
    {
        negatives++;
    }
}

Check(
    "the estimate is never negative",
    negatives == 0,
    $"{negatives} negative values");

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"All {checks} checks passed."
    : $"{failures} of {checks} checks FAILED.");

return failures == 0 ? 0 : 1;
