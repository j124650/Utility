namespace Utility.Server.DropBoxSync;

/// <summary>
/// Runs a level's work over a fixed pool of workers that drain a work list.
/// <para>
/// <b>Why a pool and not a fan-out.</b> The walk used to create one task per folder:
/// <c>Task.WhenAll(folders.Select(ExpandFolderAsync))</c>. That enumerates the whole
/// sequence, so every folder became a task BEFORE any of them ran - 5,456 tasks for
/// the current frontier, each holding a continuation on the gate it was queued at. Two
/// things followed from that. The backlog was held as tasks rather than as folders, so
/// the walk's memory grew with the tree; and stopping the walk cancelled every one of
/// them, so a single Stop produced thousands of cancellations.
/// </para>
/// <para>
/// <b>What this does instead.</b> The work list holds the WORK - the folder - and the
/// tasks are the workers. There are only ever as many workers as the pool size, no
/// matter how many folders are waiting. The backlog is a list of values, and the tasks
/// are a small, fixed set.
/// </para>
/// <para>
/// <b>The workers take from the list by index.</b> The list is already in memory - it is
/// the level's folders, or a folder's files - so a channel and a producer task would
/// add a task and a copy for nothing. Each worker takes the next index, does the work,
/// and takes the next.
/// </para>
/// <para>
/// <b>Stopping.</b> Cancelling the token ends the workers, and the items they had not
/// taken are simply not done. A folder that was not visited has no verdict, and a
/// folder with no verdict is picked up by the next continuation. <b>Nothing is lost.</b>
/// </para>
/// <para>
/// <b>How quiet the stop is.</b> A cancelled walk throws an exception from every task
/// it cancels, and a debugger prints each one. So the workers notice the
/// cancellation with <see cref="CancellationToken.IsCancellationRequested"/> - which
/// does not throw - and step out of the loop, and the pool reports the cancellation
/// ONCE at the end. The exception count on a stop is the number of requests that were
/// genuinely in flight, not the number of tasks the walk happened to hold.
/// </para>
/// </summary>
internal static class WorkerPool
{
    /// <summary>
    /// Runs <paramref name="work"/> for every item, over a pool of workers.
    /// <para>
    /// The pool is sized to the work: never more workers than there are items, so a
    /// level with three folders does not start sixty-four workers.
    /// </para>
    /// </summary>
    /// <param name="items">The work. Held as values, never as tasks.</param>
    /// <param name="maxWorkers">The most workers to run at once.</param>
    /// <param name="work">What to do with one item.</param>
    /// <param name="ct">Cancelling ends the workers; the items they had not taken are not done.</param>
    public static async Task RunAsync<T>(
        IReadOnlyList<T> items,
        int maxWorkers,
        Func<T, CancellationToken, Task> work,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Never more workers than there is work for.
        var workers = Math.Clamp(maxWorkers, 1, items.Count);

        // The next item's index. Shared by the workers, so each takes a different one.
        var next = -1;

        async Task WorkAsync()
        {
            while (!ct.IsCancellationRequested)
            {
                // The item is taken BEFORE the cancellation check, so a cancel cannot
                // make two workers take the same item.
                var index = Interlocked.Increment(ref next);
                if (index >= items.Count)
                {
                    return;
                }

                try
                {
                    await work(items[index], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The walk was stopped while this worker was on an item. The worker
                    // stops QUIETLY, so the pool reports the cancellation once at the
                    // end instead of once per worker.
                    //
                    // The item it was on is not done - which is safe: a folder that was
                    // not visited has no verdict, and the next continuation visits
                    // exactly those.
                    return;
                }
            }
        }

        var running = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            running[i] = WorkAsync();
        }

        await Task.WhenAll(running).ConfigureAwait(false);

        // The walk was stopped. Reported ONCE, here, so the caller sees a single
        // cancellation rather than one per worker.
        ct.ThrowIfCancellationRequested();
    }
}
