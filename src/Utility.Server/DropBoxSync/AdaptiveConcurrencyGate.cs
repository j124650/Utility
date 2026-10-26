namespace Utility.Server.DropBoxSync;

/// <summary>
/// A gate whose width can change while requests are in flight.
/// <para>
/// <b>Why not a <see cref="SemaphoreSlim"/>.</b> A semaphore's maximum is fixed when it
/// is built, and the walk builds its gate once per run. A gate that can be widened and
/// narrowed is what lets the walk find the rate Dropbox will actually accept, instead of
/// committing to a number chosen by guesswork before the first request is sent.
/// </para>
/// <para>
/// <b>How it behaves.</b> A caller takes a permit if one is free, otherwise it waits.
/// Widening the gate lets waiting callers in immediately; narrowing it does NOT evict
/// callers already holding a permit - they finish what they started, and the narrower
/// width applies to the next ones. That is deliberate: a request already sent cannot be
/// recalled, so there is nothing to be gained by pretending.
/// </para>
/// <para>
/// <b>Cancellation.</b> A caller that gives up while queued is removed from the queue and
/// takes no permit. The race where a permit is handed over at the same moment the caller
/// cancels is handled: the permit is handed back rather than lost.
/// </para>
/// </summary>
public sealed class AdaptiveConcurrencyGate : IDisposable
{
    private readonly object _sync = new();

    private bool _disposed;

    /// <summary>How many permits may be held at once. Read under the lock.</summary>
    private int _limit;

    /// <summary>How many permits are held right now, including ones handed to a waiter.</summary>
    private int _inUse;

    private readonly LinkedList<Waiter> _waiters = new();

    /// <summary>Creates a gate with the given width.</summary>
    public AdaptiveConcurrencyGate(int limit) => _limit = Math.Max(1, limit);

    /// <summary>How many permits may be held at once.</summary>
    public int Limit
    {
        get { lock (_sync) { return _limit; } }
    }

    /// <summary>How many permits are held right now.</summary>
    public int InUse
    {
        get { lock (_sync) { return _inUse; } }
    }

    /// <summary>How many callers are waiting for a permit.</summary>
    public int Waiting
    {
        get { lock (_sync) { return _waiters.Count; } }
    }

    /// <summary>
    /// Takes a permit, waiting if the gate is full.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        Waiter waiter;

        lock (_sync)
        {
            if (_inUse < _limit)
            {
                _inUse++;
                return;
            }

            waiter = new Waiter();
            waiter.Node = _waiters.AddLast(waiter);
        }

        try
        {
            await waiter.Source.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled while queued. Two cases, and they need different handling:
            //
            //   not granted - the waiter is still in the queue, so take it out and
            //                 nothing else happens; no permit was ever reserved.
            //   granted    - a permit was handed over at the same moment the
            //                 cancellation landed, so give that permit back.
            lock (_sync)
            {
                if (waiter.Granted)
                {
                    waiter.Node?.List?.Remove(waiter.Node);
                    ReleaseCore();
                    return;
                }

                waiter.Node?.List?.Remove(waiter.Node);
            }

            throw;
        }
    }

    /// <summary>
    /// Takes a permit if one is free, and returns false if the walk was cancelled
    /// instead.
    /// <para>
    /// This is <see cref="WaitAsync"/> without the exception. A cancelled walk throws
    /// from every task it cancels, and a debugger prints each one - so a pool of
    /// workers waiting on a full gate would print one exception per waiting worker,
    /// which is the noise the pool was built to remove.
    /// </para>
    /// <para>
    /// The caller checks the flag and steps aside. The wait ends the same way either
    /// way: the token is cancelled, the walk is stopping, and the permit is released
    /// by whoever holds it.
    /// </para>
    /// </summary>
    public async Task<bool> TryWaitAsync(CancellationToken ct)
    {
        Waiter waiter;

        lock (_sync)
        {
            if (_inUse < _limit)
            {
                _inUse++;
                return true;
            }

            waiter = new Waiter();
            waiter.Node = _waiters.AddLast(waiter);
        }

        try
        {
            await waiter.Source.Task.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                if (waiter.Granted)
                {
                    waiter.Node?.List?.Remove(waiter.Node);
                    ReleaseCore();
                    return false;
                }

                waiter.Node?.List?.Remove(waiter.Node);
            }

            return false;
        }
    }

    /// <summary>
    /// Gives a permit back.
    /// </summary>
    public void Release()
    {
        lock (_sync)
        {
            ReleaseCore();
        }
    }

    /// <summary>
    /// Changes the width.
    /// <para>
    /// Widening lets waiting callers in at once. Narrowing only affects the next
    /// permit: callers already holding one are left alone, because their requests are
    /// already sent.
    /// </para>
    /// </summary>
    public void SetLimit(int limit)
    {
        var wanted = Math.Max(1, limit);

        lock (_sync)
        {
            _limit = wanted;

            // Hand permits to the waiters the new width allows.
            while (_inUse < _limit && _waiters.Count > 0)
            {
                var waiter = _waiters.First!.Value;
                _waiters.RemoveFirst();

                waiter.Granted = true;
                _inUse++;

                // The continuation runs asynchronously, so the caller is not resumed
                // while the lock is held.
                waiter.Source.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Releases a permit and passes it to the next waiter, if there is one.
    /// Called with the lock held.
    /// </summary>
    private void ReleaseCore()
    {
        _inUse--;

        if (_waiters.Count > 0 && _inUse < _limit)
        {
            var waiter = _waiters.First!.Value;
            _waiters.RemoveFirst();

            waiter.Granted = true;
            _inUse++;

            waiter.Source.TrySetResult();
        }
    }

    /// <summary>
    /// Releases the gate. Any caller still waiting is let through rather than left
    /// hanging, so a disposed gate cannot strand a worker.
    /// </summary>
    public void Dispose()
    {
        List<Waiter> pending;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _waiters.ToList();
            _waiters.Clear();
            _inUse = 0;
        }

        foreach (var waiter in pending)
        {
            waiter.Granted = true;
            waiter.Source.TrySetResult();
        }
    }

    /// <summary>One queued caller.</summary>
    private sealed class Waiter
    {
        public TaskCompletionSource Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        /// <summary>True once a permit has been handed to this waiter.</summary>
        public bool Granted { get; set; }
    }
}
