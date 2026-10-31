using System.ComponentModel;
using System.Windows.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Utility.Server;
using Utility.Server.Finder;
using Utility.Server.Finder.Services;

namespace GroupsChecks;

/// <summary>
/// Checks the Groups grouping against the live library (docs/finder-groups-spec.md).
///
/// It runs the real pass through the real service and then calls the real
/// <c>GetNameGroups()</c>, so what it prints is what the button shows.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddEnvironmentVariables("UTILITY_");

        builder.Services.AddUtilityServer(builder.Configuration);

        using var host = builder.Build();

        var finder = host.Services.GetRequiredService<IFinderService>();

        Console.WriteLine("Running the pass...");
        var rows = await finder.GetMediaPairsAsync();
        Console.WriteLine($"Pass finished: {rows.Count:N0} pair(s).");

        var groups = finder.GetNameGroups();

        var names = groups.Sum(g => g.Count);
        var marked = groups.Where(g => g.HasTwoPrefixes).ToList();

        Console.WriteLine();
        Console.WriteLine($"Groups:       {groups.Count:N0}");
        Console.WriteLine($"Names:        {names:N0}");
        Console.WriteLine($"Marked:       {marked.Count:N0}");
        Console.WriteLine();

        // ---- the rules the spec states ----
        var failures = 0;

        void Fail(string message)
        {
            Console.WriteLine("FAIL: " + message);
            failures++;
        }

        // The groups are ordered by their path, case-insensitively.
        var ordered = groups.Select(g => g.Path).ToList();
        var sorted = ordered.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        if (!ordered.SequenceEqual(sorted))
        {
            Fail("the groups are not ordered by their path.");
        }

        // Every group is a distinct path, compared case-insensitively.
        if (groups.Select(g => g.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count)
        {
            Fail("two groups share a path.");
        }

        // No group is empty.
        if (groups.Any(g => g.Count <= 0))
        {
            Fail("a group holds no names.");
        }

        // The names in each path and the prefixes in it, computed in ONE pass - the way
        // the service computes them.
        var namesByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var prefixesByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var path = row.ParentFolder;

            if (!namesByPath.ContainsKey(path))
            {
                namesByPath[path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                prefixesByPath[path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            namesByPath[path].Add(row.ParentFileName);
            prefixesByPath[path].Add(PrefixOf(row.ParentFileName));
        }

        // The counts agree with the rows.
        if (namesByPath.Count != groups.Count)
        {
            Fail($"{namesByPath.Count} paths in the rows, {groups.Count} groups.");
        }

        var expectedNames = namesByPath.Values.Sum(s => s.Count);

        if (expectedNames != names)
        {
            Fail($"the names sum to {names:N0}, expected {expectedNames:N0}.");
        }

        // Each group carries the count and the mark its own names give it.
        foreach (var group in groups)
        {
            var expectedCount = namesByPath[group.Path].Count;

            if (expectedCount != group.Count)
            {
                Fail($"'{group.Path}' counts {group.Count}, expected {expectedCount}.");
                break;
            }

            var expectedMark = prefixesByPath[group.Path].Count > 1;

            if (expectedMark != group.HasTwoPrefixes)
            {
                Fail($"'{group.Path}' is marked {group.HasTwoPrefixes}, expected {expectedMark}.");
                break;
            }
        }

        Console.WriteLine("First 10 groups:");
        foreach (var group in groups.Take(10))
        {
            Console.WriteLine($"  {group.Path,-30} {group.Count,4}  {group.Mark}");
        }

        Console.WriteLine();
        Console.WriteLine($"The {marked.Count} marked groups, with the prefixes in each:");
        foreach (var group in marked)
        {
            Console.WriteLine($"  {group.Path,-30} {group.Count,4}");

            foreach (var prefix in prefixesByPath[group.Path])
            {
                Console.WriteLine($"      [{prefix}]");
            }
        }

        // A name in two paths is counted in both.
        var namesInTwoPaths = rows
            .GroupBy(r => r.ParentFileName, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Select(r => r.ParentFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        Console.WriteLine();
        Console.WriteLine($"Names in more than one path: {namesInTwoPaths:N0}");

        // ---- the sort, through WPF's OWN mechanism ----
        //
        // A DataGrid sorts through the ICollectionView behind its ItemsSource, and the
        // column's SortMemberPath becomes the SortDescription's property. So this is the
        // sort the grid performs - not a sort of our own.
        failures += CheckTheSort(groups);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Checks that sorting by the Names column sorts by the AMOUNT.
    /// </summary>
    /// <remarks>
    /// The counts run 1, 2, 3 ... 28. Sorted as TEXT, they run "1", "10", "11", "2" ...
    /// - so the two orders differ, and the check reads the order WPF's own view produces.
    /// </remarks>
    private static int CheckTheSort(IReadOnlyList<NameGroup> groups)
    {
        var failures = 0;

        // The view the grid sorts through: a ListCollectionView over the groups.
        var view = new ListCollectionView(groups.ToList());

        view.SortDescriptions.Add(new SortDescription(nameof(NameGroup.Count), ListSortDirection.Ascending));

        var ascending = view.Cast<NameGroup>().Select(g => g.Count).ToList();

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(NameGroup.Count), ListSortDirection.Descending));

        var descending = view.Cast<NameGroup>().Select(g => g.Count).ToList();

        Console.WriteLine();
        Console.WriteLine("The sort the grid does, through WPF's own view:");
        Console.WriteLine($"  ascending, first 12:   {string.Join(", ", ascending.Take(12))}");
        Console.WriteLine($"  descending, first 12:  {string.Join(", ", descending.Take(12))}");
        Console.WriteLine($"  ascending, last 6:     {string.Join(", ", ascending.Skip(ascending.Count - 6))}");
        Console.WriteLine($"  descending, last 6:    {string.Join(", ", descending.Skip(descending.Count - 6))}");

        // The AMOUNT order: the counts ascend.
        var byAmount = ascending.Zip(ascending.Skip(1), (a, b) => a <= b).All(x => x);

        if (!byAmount)
        {
            Console.WriteLine("FAIL: sorting by Names does not ascend by the amount.");
            failures++;
        }

        // The TEXT order: the counts as text, which puts "10" before "2".
        var byText = ascending
            .Select(c => c.ToString())
            .Zip(ascending.Select(c => c.ToString()).Skip(1), (a, b) => string.CompareOrdinal(a, b) <= 0)
            .All(x => x);

        if (byText)
        {
            Console.WriteLine("FAIL: sorting by Names sorts by the TEXT of the number, not the amount.");
            failures++;
        }

        // The descending order is the mirror of the ascending one.
        var mirrored = descending.SequenceEqual(ascending.AsEnumerable().Reverse());

        if (!mirrored)
        {
            Console.WriteLine("FAIL: the descending order is not the reverse of the ascending one.");
            failures++;
        }

        return failures;
    }

    /// <summary>The part of a name before its first hyphen.</summary>
    private static string PrefixOf(string name)
    {
        var hyphen = name.IndexOf('-', StringComparison.Ordinal);

        return hyphen < 0 ? name : name[..hyphen];
    }
}
