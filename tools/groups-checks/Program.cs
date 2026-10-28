using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Utility.Server;
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

        // The order the window opens in is the service's: ordinal, case-insensitive.
        // The grid's own column sort uses the DEFAULT string comparer, which is
        // culture-aware. If the two disagree on this data, a reader who clicks the Path
        // heading would see a different order from the one the window opened in.
        var serviceOrder = groups.Select(g => g.Path).ToList();

        var gridOrder = groups
            .Select(g => g.Path)
            .OrderBy(p => p, Comparer<string>.Default)
            .ToList();

        var sameOrder = serviceOrder.SequenceEqual(gridOrder);

        Console.WriteLine($"Grid's default sort matches the service's order: {sameOrder}");

        if (!sameOrder)
        {
            var firstDiff = serviceOrder
                .Zip(gridOrder, (a, b) => (a, b))
                .Select((pair, index) => (pair, index))
                .First(x => x.pair.a != x.pair.b);

            Console.WriteLine($"  first difference at {firstDiff.index}:");
            Console.WriteLine($"    service: {firstDiff.pair.a}");
            Console.WriteLine($"    grid:    {firstDiff.pair.b}");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>The part of a name before its first hyphen.</summary>
    private static string PrefixOf(string name)
    {
        var hyphen = name.IndexOf('-', StringComparison.Ordinal);

        return hyphen < 0 ? name : name[..hyphen];
    }
}
