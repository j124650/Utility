using System.Text;

namespace Utility.Server.Finder;

/// <summary>
/// Quotes a field the way a spreadsheet reads it: always quoted, an embedded quote
/// doubled, so a path with a comma or a quote in it survives the round trip.
/// </summary>
/// <remarks>
/// The rule is shared, so every CSV the app writes quotes the same way. A file written
/// by one writer and read by another must not disagree about what a comma means.
/// </remarks>
public static class CsvText
{
    /// <summary>
    /// Quotes a value: always in quotes, with an embedded quote doubled.
    /// </summary>
    /// <param name="value">The value to quote. Empty is written as two quotes.</param>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);

        builder.Append('"');

        foreach (var character in value)
        {
            if (character == '"')
            {
                builder.Append('"');
            }

            builder.Append(character);
        }

        builder.Append('"');

        return builder.ToString();
    }
}
