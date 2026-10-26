namespace Utility.Server.DropBoxSync;

/// <summary>How a single Dropbox request ended.</summary>
public enum DropboxRequestOutcome
{
    /// <summary>The call returned normally.</summary>
    Success,

    /// <summary>Dropbox refused it with HTTP 429.</summary>
    RateLimited,

    /// <summary>It failed for any other reason - a dropped connection, a TLS failure, a server fault.</summary>
    Failed,
}

/// <summary>
/// Measures the walk's own request rate, so the walk can see what it is doing to Dropbox
/// instead of guessing.
/// <para>
/// <b>Why measure at all.</b> Dropbox does not publish its limits - "while Dropbox does not
/// publish exact rate limits, these limits are not designed to inhibit normal
/// applications" - and the API's only signal is a 429. So the limit has to be
/// discovered from the traffic, and the traffic is the only thing that can be measured.
/// </para>
/// <para>
/// <b>Why not just count.</b> A rate is not a count: "how many requests" says nothing
/// about "how fast". The walk's throughput is set by how many requests are in flight and
/// how long each takes, and only the second number changes when the walk starts
/// competing with something else for the same budget.
/// </para>
/// <para>
/// <b>Cost.</b> One lock and a handful of additions per request, against a request that
/// costs a network round trip. The measurement is free next to the thing it measures.
/// </para>
/// </summary>
public sealed class DropboxRequestRateMeter
{
    /// <summary>One bucket per second. The window is a minute long.</summary>
    private const int BucketSeconds = 1;

    /// <summary>How many seconds of history are kept.</summary>
    private const int BucketCount = 60;

    private readonly object _gate = new();

    /// <summary>The second each bucket holds, so a stale bucket is not counted.</summary>
    private readonly long[] _stamp = new long[BucketCount];

    private readonly long[] _requests = new long[BucketCount];
    private readonly long[] _rateLimited = new long[BucketCount];
    private readonly long[] _failures = new long[BucketCount];
    private readonly long[] _latencyTicks = new long[BucketCount];

    private long _totalRequests;
    private long _totalRateLimited;
    private long _totalFailures;

    /// <summary>
    /// Records one request and how it ended.
    /// </summary>
    public void Record(TimeSpan elapsed, DropboxRequestOutcome outcome)
    {
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var slot = (int)(second % BucketCount);

        lock (_gate)
        {
            // A bucket that belongs to an older second starts over: the ring buffer
            // wraps, and the counts in it are for a minute that has already gone.
            if (_stamp[slot] != second)
            {
                _stamp[slot] = second;
                _requests[slot] = 0;
                _rateLimited[slot] = 0;
                _failures[slot] = 0;
                _latencyTicks[slot] = 0;
            }

            _requests[slot]++;
            _latencyTicks[slot] += elapsed.Ticks;

            switch (outcome)
            {
                case DropboxRequestOutcome.RateLimited:
                    _rateLimited[slot]++;
                    _totalRateLimited++;
                    break;

                case DropboxRequestOutcome.Failed:
                    _failures[slot]++;
                    _totalFailures++;
                    break;
            }

            _totalRequests++;
        }
    }

    /// <summary>
    /// The current picture of the walk's traffic: what it is doing now, and what it has
    /// done altogether.
    /// </summary>
    public DropboxRateSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var windowStart = now - BucketCount + 1;

        long requests = 0;
        long rateLimited = 0;
        long failures = 0;
        long latencyTicks = 0;
        var counted = 0;

        lock (_gate)
        {
            for (var i = 0; i < BucketCount; i++)
            {
                if (_stamp[i] < windowStart)
                {
                    continue;
                }

                requests += _requests[i];
                rateLimited += _rateLimited[i];
                failures += _failures[i];
                latencyTicks += _latencyTicks[i];
                counted++;
            }

            return new DropboxRateSnapshot(
                RequestsPerMinute: (int)requests,
                RateLimitedPerMinute: (int)rateLimited,
                FailuresPerMinute: (int)failures,
                MeanLatencyMs: requests > 0 ? (int)(latencyTicks / requests / TimeSpan.TicksPerMillisecond) : 0,
                SampleSeconds: counted,
                TotalRequests: (int)_totalRequests,
                TotalRateLimited: (int)_totalRateLimited,
                TotalFailures: (int)_totalFailures);
        }
    }

    /// <summary>Clears everything. Called when a run starts.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_stamp);
            Array.Clear(_requests);
            Array.Clear(_rateLimited);
            Array.Clear(_failures);
            Array.Clear(_latencyTicks);

            _totalRequests = 0;
            _totalRateLimited = 0;
            _totalFailures = 0;
        }
    }
}

/// <summary>
/// The walk's traffic, measured.
/// <para>
/// The per-minute figures cover the last minute; the totals cover the run. Both are
/// there because they answer different questions: the window says what is happening
/// NOW, and the totals say what the whole walk cost.
/// </para>
/// </summary>
public readonly record struct DropboxRateSnapshot(
    int RequestsPerMinute,
    int RateLimitedPerMinute,
    int FailuresPerMinute,
    int MeanLatencyMs,
    int SampleSeconds,
    int TotalRequests,
    int TotalRateLimited,
    int TotalFailures)
{
    /// <summary>Requests per second, averaged over the window.</summary>
    public double RequestsPerSecond => SampleSeconds > 0 ? RequestsPerMinute / (double)SampleSeconds : 0;

    /// <summary>
    /// The average number of requests in flight over the window.
    /// <para>
    /// Little's Law: the average number of requests in flight is the throughput times
    /// the average time each one takes. This is the number that says whether the walk is
    /// ACTUALLY using the width it was given - and it is the honest one, where the
    /// instantaneous count is not. A gate of six with one request in flight reads as
    /// "one of six" on the screen, but the walk may simply have had nothing to do at
    /// that instant.
    /// </para>
    /// </summary>
    public double AverageConcurrency => RequestsPerSecond * (MeanLatencyMs / 1000.0);

    /// <summary>
    /// The rate limit as a share of the traffic: the one number that says whether the
    /// walk is at the API's limit or merely near it.
    /// </summary>
    public double RateLimitedShare => RequestsPerMinute > 0 ? RateLimitedPerMinute / (double)RequestsPerMinute : 0;

    /// <summary>The window's figures as one line, for the busy screen.</summary>
    public string Display =>
        $"{RequestsPerMinute:N0}/min · {AverageConcurrency:0.0} in flight · {RateLimitedPerMinute} limited · {MeanLatencyMs} ms avg";
}
