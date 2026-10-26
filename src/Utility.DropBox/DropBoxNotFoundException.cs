namespace Utility.DropBox;

/// <summary>
/// Dropbox says the path is not there: it was deleted, renamed, or this account may not
/// see it.
/// <para>
/// <b>Different from an error.</b> An error (a dropped connection, a rate limit, a
/// server fault) is a property of the MOMENT and may well succeed next time. A path
/// that is not there is a property of the PATH itself, and listing it again would
/// fail the same way.
/// </para>
/// <para>
/// App-owned on purpose: the SDK's own types are not part of this library's contract.
/// </para>
/// </summary>
public sealed class DropBoxNotFoundException(string message, Exception? innerException = null)
    : Exception(message, innerException);
