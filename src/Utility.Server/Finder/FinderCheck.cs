namespace Utility.Server.Finder;

/// <summary>
/// One of the checks the verdict rests on, with the mark that shows whether it is respected
/// (§7, §19.1).
/// </summary>
/// <remarks>
/// The row record carries the mark, so the info area shows the row's own check and does not
/// work it out again (§18.6). The boolean is here too, because that is what the verdict is
/// decided on.
/// </remarks>
public readonly record struct FinderCheck(bool Respected) : IComparable<FinderCheck>
{
    /// <summary>✓ where the rule is respected, ✕ where it is not.</summary>
    public string Mark => Respected ? "✓" : "✕";

    public int CompareTo(FinderCheck other) => Respected.CompareTo(other.Respected);

    /// <summary>A check that is respected.</summary>
    public static FinderCheck Yes => new(true);

    /// <summary>A check that is not respected.</summary>
    public static FinderCheck No => new(false);

    public static implicit operator FinderCheck(bool respected) => new(respected);

    public override string ToString() => Mark;
}

/// <summary>
/// One line of the rules list under the verdict: the check's mark and what it is about
/// (§18.6).
/// </summary>
/// <param name="Mark">✓ where the rule is respected, ✕ where it is not.</param>
/// <param name="Text">What the check is about.</param>
public sealed record FinderRule(string Mark, string Text);
