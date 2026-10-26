namespace Utility.DropBox;

/// <summary>
/// Dropbox refused the request because the caller is going too fast.
/// <para>
/// <b>App-owned on purpose.</b> The SDK raises its own type for this, but that type is
/// not part of this library's contract — and the layer above (the sync walk) must be
/// able to handle a rate limit without knowing anything about the SDK.
/// </para>
/// <para>
/// The SDK does NOT retry a rate limit. Its retry budget
/// (<c>DropBoxOptions.MaxRetriesOnError</c>) covers server errors only: a 500 is
/// retried, a 429 is raised straight through. So this exception is what the caller
/// has to act on.
/// </para>
/// </summary>
public sealed class DropBoxRateLimitedException(
    int retryAfterSeconds,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// How long to wait before the next attempt, in seconds, as Dropbox asked.
    /// <para>
    /// Zero when Dropbox did not say — the caller then picks its own backoff.
    /// </para>
    /// </summary>
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
