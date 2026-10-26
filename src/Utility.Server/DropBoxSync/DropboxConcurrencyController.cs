namespace Utility.Server.DropBoxSync;

/// <summary>
/// Sets the walk's request width from what the walk actually measures.
/// <para>
/// <b>The problem this solves.</b> Dropbox does not publish its rate limit, so the width
/// that will be accepted cannot be known in advance - it can only be discovered. And a
/// number chosen by guesswork is wrong in both directions: too low wastes hours, too
/// high spends the walk's budget on 429s and the wait-outs that follow.
/// </para>
/// <para>
/// <b>How it discovers.</b> It watches two signals, and they mean different things:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>429s</b> - the API saying "stop". This is a hard signal: the width is too wide.
/// </description></item>
/// <item><description>
/// <b>Latency</b> - the API getting slower. This is the early signal, and it arrives
/// BEFORE the 429s: a server about to refuse traffic starts slowing down first. It is
/// also the signal that works when the limit is high enough that the walk never gets
/// refused at all.
/// </description></item>
/// </list>
/// <para>
/// <b>Why the walk does not simply open the gate wide.</b> Because the whole point of
/// measuring is to find the ceiling rather than guess it, and a walk that opened wide
/// would find it by being refused - which costs the budget it is trying to spend
/// efficiently, since Dropbox counts refused requests against the limit too.
/// </para>
/// <para>
/// <b>The rule.</b> Widen while things are healthy, narrow hard when refused. Widening
/// is slow and cautious - one step at a time, and only after a settled period of good
/// behaviour - because a wrong step up is paid for with rate limits. Narrowing is fast:
/// a 429 halves the width at once, because the API has just said the current width is
/// wrong and there is no reason to argue.
/// </para>
/// </summary>
public sealed class DropboxConcurrencyController
{
    private readonly AdaptiveConcurrencyGate _gate;
    private readonly DropboxRequestRateMeter _meter;

    /// <summary>
    /// The gate that bounds how many FOLDERS are expanded at once.
    /// <para>
    /// It follows the request gate, because the two must agree: the request gate
    /// says how many Dropbox calls may be in flight, and the folder gate says how
    /// many folders may be working toward that. A folder that is expanded contributes
    /// its listing and its per-file metadata calls to the request gate, so a folder
    /// gate narrower than the request gate makes the wider request width
    /// unreachable - the walk would idle at the folder gate while the request gate
    /// sat unused.
    /// </para>
    /// </summary>
    private readonly AdaptiveConcurrencyGate? _folderGate;

    /// <summary>The width the walk starts at.</summary>
    private readonly int _startWidth;

    /// <summary>The width the walk may grow to.</summary>
    private readonly int _maxWidth;

    /// <summary>The width the walk may shrink to.</summary>
    private readonly int _minWidth;

    /// <summary>How long a window must be clean before the width grows.</summary>
    private readonly TimeSpan _settleTime;

    /// <summary>When the width was last changed.</summary>
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;

    /// <summary>When the last 429 was seen.</summary>
    private DateTimeOffset _lastRateLimit = DateTimeOffset.MinValue;

    /// <summary>The latency the walk had when it was last healthy.</summary>
    private double _healthyLatencyMs;

    /// <summary>
    /// The width the baseline latency was measured at.
    /// <para>
    /// The baseline belongs to a width, because latency is a function of the width: a
    /// wider gate is slower by construction. Comparing across widths would call a
    /// healthy walk overloaded and walk the gate back down.
    /// </para>
    /// </summary>
    private int _baselineWidth;

    /// <summary>How many times the width has been narrowed.</summary>
    private int _narrowCount;

    /// <summary>Creates a controller over a gate and a meter.</summary>
    public DropboxConcurrencyController(
        AdaptiveConcurrencyGate gate,
        DropboxRequestRateMeter meter,
        int startWidth,
        int maxWidth,
        int minWidth,
        TimeSpan settleTime,
        AdaptiveConcurrencyGate? folderGate = null)
    {
        _gate = gate;
        _meter = meter;
        _folderGate = folderGate;
        _startWidth = Math.Max(minWidth, startWidth);
        _maxWidth = Math.Max(_startWidth, maxWidth);
        _minWidth = Math.Max(1, minWidth);
        _settleTime = settleTime;
    }

    /// <summary>
    /// Adapts the width to what was measured. Safe to call often.
    /// </summary>
    public void Adapt()
    {
        var snapshot = _meter.Snapshot();

        // Nothing measured yet: there is no signal to act on, and the width stays where
        // it was put.
        if (snapshot.RequestsPerMinute <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // A 429 is the API saying the width is too wide. It halves it, at once.
        if (snapshot.RateLimitedPerMinute > 0)
        {
            _lastRateLimit = now;
            _narrowCount++;

            var halved = _gate.Limit / 2;
            var target = Math.Max(_minWidth, halved);

            if (target < _gate.Limit)
            {
                _gate.SetLimit(target);
                _folderGate?.SetLimit(target);
                _lastChange = now;
                _healthyLatencyMs = 0;
                _baselineWidth = 0;
            }

            return;
        }

        // Too soon after a narrowing: give the API room before trying again.
        if (now - _lastChange < _settleTime)
        {
            return;
        }

        // The early signal: the API is getting slower. That means the width is at the
        // edge, even though nothing has been refused yet.
        //
        // The comparison is against the latency this walk saw AT THIS WIDTH. That
        // matters: latency is a function of the width, so a wider gate is SLOWER by
        // construction - comparing a width-12 latency against a width-6 baseline would
        // call a healthy walk overloaded.
        //
        // So the baseline is tied to the width it was measured at, and it is forgotten
        // whenever the width changes. It is also an AVERAGE, not a minimum: a minimum
        // ratchets down to the single luckiest window ever seen, and then every
        // ordinary window looks like a rise - which would walk the gate down to one
        // and keep it there.
        if (_baselineWidth != _gate.Limit)
        {
            // The width moved: this window becomes the baseline for the new width, and
            // there is nothing to compare it against yet.
            //
            // The settle clock is NOT restarted here. It was restarted when the width
            // changed, and restarting it again would make every step cost TWO settle
            // periods - so a climb from six to thirty-two took twenty-six minutes,
            // and the walk spent all that time at the width it started with.
            _baselineWidth = _gate.Limit;
            _healthyLatencyMs = snapshot.MeanLatencyMs;
            return;
        }

        if (_healthyLatencyMs > 0 && snapshot.MeanLatencyMs > _healthyLatencyMs * LatencyRiseFactor)
        {
            var target = Math.Max(_minWidth, _gate.Limit - 1);
            if (target < _gate.Limit)
            {
                _gate.SetLimit(target);
                _folderGate?.SetLimit(target);
                _lastChange = now;
                _healthyLatencyMs = 0;
                _baselineWidth = 0;
            }

            return;
        }

        // Healthy: track the latency as an average for this width, and widen - but only
        // one step, and only after a settled period.
        if (_healthyLatencyMs == 0)
        {
            _healthyLatencyMs = snapshot.MeanLatencyMs;
        }
        else
        {
            // An exponential moving average: recent windows weigh more, so one slow
            // window does not redefine "normal", and the baseline still follows a real
            // change in the API's behaviour.
            _healthyLatencyMs = (_healthyLatencyMs * 0.7) + (snapshot.MeanLatencyMs * 0.3);
        }

        if (_gate.Limit < _maxWidth && now - _lastRateLimit > _settleTime)
        {
            _gate.SetLimit(_gate.Limit + 1);

            // The folder gate follows the request gate, so the wider width is
            // actually reachable: folders are what feed the request gate, and a
            // folder gate left at the old width would cap the walk there.
            _folderGate?.SetLimit(_gate.Limit);

            _lastChange = now;

            // The baseline belonged to the old width: it is forgotten, so the next
            // window is measured afresh at the new width.
            _healthyLatencyMs = 0;
            _baselineWidth = 0;
        }
    }

    /// <summary>
    /// How much the latency must rise, over the healthy baseline, before the width is
    /// trimmed.
    /// <para>
    /// Half again. A small rise is noise - a busy moment, one slow call. A large one is
    /// the queue building up, and by then every further request makes it worse.
    /// </para>
    /// </summary>
    private const double LatencyRiseFactor = 1.5;

    /// <summary>The width the walk starts at.</summary>
    public int StartWidth => _startWidth;

    /// <summary>The width the walk may grow to.</summary>
    public int MaxWidth => _maxWidth;

    /// <summary>The width the walk may shrink to.</summary>
    public int MinWidth => _minWidth;

    /// <summary>How many times the width was narrowed.</summary>
    public int NarrowCount => _narrowCount;

    /// <summary>The gate whose width this controller sets.</summary>
    public AdaptiveConcurrencyGate Gate => _gate;

    /// <summary>Resets the controller for a new run.</summary>
    public void Reset()
    {
        _lastChange = DateTimeOffset.MinValue;
        _lastRateLimit = DateTimeOffset.MinValue;
        _healthyLatencyMs = 0;
        _narrowCount = 0;
    }
}
