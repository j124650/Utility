namespace Utility.Server.Finder.Services;

/// <summary>
/// The fuzzy comparison of §10: how two file names are compared when they are not the same.
/// </summary>
/// <remarks>
/// In order: normalized exact, then equal after stripping a trailing <c>-&lt;digits&gt;</c>
/// token from either side, then a Levenshtein ratio on the normalized stem.
/// </remarks>
public static class FinderSimilarity
{
    /// <summary>The ratio above which two names are "similar enough" to be ranked as fuzzy.</summary>
    public const double Threshold = 0.75;

    /// <summary>
    /// The name with its extension stripped, upper-cased and its non-alphanumerics removed.
    /// </summary>
    /// <remarks>
    /// The extension goes first: "HE05682681-02.jpg" and "HE05682681-02.jpeg" are the same
    /// name.
    /// </remarks>
    public static string NormalizedStem(string name)
    {
        var stem = StripExtension(name);
        return new string(stem.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    /// <summary>The name with its extension stripped, keeping everything else.</summary>
    /// <remarks>
    /// The LAST dot is the extension's, so a name that starts with a dot has an empty
    /// stem - the same rule <c>ml.DropBoxNode.NameKey</c> uses.
    /// </remarks>
    public static string StripExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    /// <summary>
    /// The normalized stem with its trailing <c>-&lt;digits&gt;</c> token removed.
    /// </summary>
    /// <remarks>
    /// The trailing number is the variant - <c>MediaSubCategoryId</c> - so two names that
    /// differ only there are the same product, different variants.
    /// <para>
    /// The token is stripped from the stem BEFORE the separators are dropped: the dash is
    /// part of the token, so a stem that has already lost its dashes has nothing left to
    /// strip.
    /// </para>
    /// </remarks>
    public static string WithoutVariant(string name)
    {
        var stem = StripExtension(name).TrimEnd();

        var dash = stem.LastIndexOf('-');
        if (dash < 0 || dash == stem.Length - 1)
        {
            return stem.ToUpperInvariant();
        }

        var tail = stem[(dash + 1)..];
        return tail.All(char.IsDigit)
            ? stem[..dash].ToUpperInvariant()
            : stem.ToUpperInvariant();
    }

    /// <summary>
    /// How similar two names are, from 0 to 1.
    /// </summary>
    public static double Ratio(string left, string right)
    {
        var a = NormalizedStem(left);
        var b = NormalizedStem(right);

        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        // The same product, a different variant.
        if (WithoutVariant(left) == WithoutVariant(right))
        {
            return 1;
        }

        var distance = Levenshtein(a, b);
        var longest = Math.Max(a.Length, b.Length);
        return 1.0 - (double)distance / longest;
    }

    /// <summary>Whether two names are similar enough to be ranked as a fuzzy match.</summary>
    public static bool IsSimilar(string left, string right) => Ratio(left, right) >= Threshold;

    /// <summary>The Levenshtein distance between two strings.</summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
