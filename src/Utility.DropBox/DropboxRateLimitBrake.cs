namespace Utility.DropBox;

/// <summary>
/// A brake that holds back EVERY worker of a run when Dropbox rate-limits one of them.
/// <para>
/// <b>Why one shared brake and not a per-worker backoff.</b> Dropbox's limit is per
/// authorization - per user, or per team - not per request. When a run is working on one
/// team folder, every request counts against the same budget. So a 429 is not one
/// unlucky request: it is the API saying "stop, all of you". A per-worker backoff would
/// let the other five keep hammering while the sixth waits, and Dropbox counts
/// rate-limited requests against the limit too - so the retry storm would keep the limit
/// in place.
/// </para>
/// <para>
/// <b>How it works.</b> A worker that is refused notes the wait on the brake. Every
/// worker - including the ones already holding a request permit - waits at the brake
/// before its next request, so the whole fleet pauses together and resumes together.
/// </para>
/// <para>
/// The wait is the one Dropbox asked for, when it said. When it did not say, the caller
/// supplies a fallback, and the brake takes the LONGEST wait anyone asked for - so a
/// worker that asked for 60 seconds holds everyone for 60 seconds, not for the shortest
/// wait on the table.
/// </para>
/// </summary>
public sealed class DropboxRateLimitBrake
{
    /// <summary>
    /// Ticks (from <see cref="DateTimeOffset.UtcNow"/>) before which no request may be
    /// sent. Zero means "no hold".
    /// </summary>
    private long _holdUntilTicks;

    /// <summary>
    /// How many times the brake has been held. Published so the busy screen can say the
    /// walk was throttled rather than silently slow.
    /// </summary>
    private int _throttleCount;

    /// <summary>
    /// Holds every worker until <paramref name="seconds"/> have passed.
    /// <para>
    /// The LONGEST wait wins: if one worker was told to wait 60 seconds and another 5,
    /// the brake holds for 60. Releasing earlier than somebody asked for would put the
    /// limit straight back.
    /// </para>
    /// </summary>
    public void Hold(int seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _throttleCount);

        var until = DateTimeOffset.UtcNow.AddSeconds(seconds).Ticks;

        // Compare-and-swap loop: the longest hold must win, and several workers can
        // be holding at the same moment.
        while (true)
        {
            var current = Interlocked.Read(ref _holdUntilTicks);
            if (until <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _holdUntilTicks, until, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>How many times the brake has been held.</summary>
    public int ThrottleCount => Volatile.Read(ref _throttleCount);

    /// <summary>True while the brake is holding.</summary>
    public bool IsHolding => DateTimeOffset.UtcNow.Ticks < Interlocked.Read(ref _holdUntilTicks);

    /// <summary>
    /// Seconds left on the hold, or zero when there is none.
    /// </summary>
    public int RemainingSeconds
    {
        get
        {
            var remaining = Interlocked.Read(ref _holdUntilTicks) - DateTimeOffset.UtcNow.Ticks;
            return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining / (double)TimeSpan.TicksPerSecond);
        }
    }

    /// <summary>
    /// Waits until the brake releases.
    /// <para>
    /// Re-reads the hold after each wait, because another worker may have extended it
    /// while this one was sleeping.
    /// </para>
    /// </summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            var until = Interlocked.Read(ref _holdUntilTicks);
            var remaining = until - DateTimeOffset.UtcNow.Ticks;
            if (remaining <= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromTicks(remaining), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Clears the hold. Called when a run starts.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _holdUntilTicks, 0);
        Interlocked.Exchange(ref _throttleCount, 0);
    }
}
