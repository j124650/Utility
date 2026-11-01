namespace Utility.Server.Finder.Repair;

/// <summary>
/// The SHORT PHRASE for a failure reason — the high-signal words, not the raw message.
/// </summary>
/// <remarks>
/// <para>
/// A recovery log's reason is what an exception or an API said: <c>path/not_found/</c>,
/// or "the parent is not there to regenerate the child from". Neither is a thing a
/// reader scans, and neither groups well in a spreadsheet.
/// </para>
/// <para>
/// So the log keeps the reason RAW — that is the record of what happened — and the
/// browser shows a phrase instead. <see cref="Shorten"/> is the only place that
/// translation happens, so the pane and the CSV say the same thing.
/// </para>
/// <para>
/// A reason that is not in the table KEEPS ITS OWN WORDS. The table is the app's and
/// Dropbox's own vocabulary; an unknown reason is not shortened by guesswork.
/// </para>
/// </remarks>
public static class RecoveryReasonPhrase
{
    /// <summary>The Dropbox API's own codes, in the words a reader would use.</summary>
    private static readonly Dictionary<string, string> DropboxCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["path/not_found"] = "Path not found in Dropbox",
        ["path/conflict"] = "Path conflict in Dropbox",
        ["path/malformed_path"] = "Malformed path in Dropbox",
        ["path/restricted_content"] = "Restricted content in Dropbox",
        ["path/disallowed_name"] = "Disallowed name in Dropbox",
        ["path/too_long"] = "Path too long in Dropbox",
        ["from_lookup/not_found"] = "File not found in Dropbox",
        ["from_lookup/not_file"] = "Source is not a file in Dropbox",
        ["from_lookup/folder"] = "Source is a folder in Dropbox",
        ["from_lookup/malformed_path"] = "Malformed source path in Dropbox",
        ["insufficient_space"] = "Not enough space",
        ["too_many_write_operations"] = "Too many write operations",
        ["too_many_concurrent_write_operations"] = "Too many concurrent write operations",
        ["internal_error"] = "Dropbox internal error",
        ["other"] = "Dropbox error",
    };

    /// <summary>The recovery's own reasons, in the words a reader would use.</summary>
    private static readonly Dictionary<string, string> OwnReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["no candidate to restore the parent from"] = "No candidate to restore from",
        ["the candidate carries no namespace"] = "Candidate carries no namespace",
        ["the parent is not there to regenerate the child from"] = "Parent not there to regenerate from",
    };

    /// <summary>
    /// The short phrase for a reason. An empty or missing reason gives an empty phrase.
    /// </summary>
    public static string Shorten(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();

        // The Dropbox codes carry a trailing slash: "path/not_found/".
        if (text.EndsWith('/'))
        {
            text = text.TrimEnd('/');
        }

        if (OwnReasons.TryGetValue(text, out var own))
        {
            return own;
        }

        if (DropboxCodes.TryGetValue(text, out var dropbox))
        {
            return dropbox;
        }

        // A Dropbox code nobody has seen yet: name it, so it is still short and it is
        // still recognisable as Dropbox's.
        if (!text.Contains(' ') && text.Contains('/'))
        {
            return "Dropbox: " + text.Replace('/', ' ').Replace('_', ' ');
        }

        return text;
    }
}
