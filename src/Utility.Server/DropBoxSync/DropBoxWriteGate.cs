using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// ONE writer at a time for <c>ml.DropBoxNode</c> — and it is SHARED by everything that
/// writes to that table.
/// </summary>
/// <remarks>
/// <para>
/// The walk expands many folders in parallel, and every one of them writes to the same
/// table — the folder's rows and the folder's own status. Those writes overlapped, and
/// SQL Server picked one worker as the deadlock victim: the transaction was rolled back,
/// the folder was abandoned, and its whole subtree was never discovered.
/// </para>
/// <para>
/// The parallelism that matters is the Dropbox traffic, and that is untouched by this:
/// the listing and every per-file metadata call still run at the walk's width. Only the
/// WRITE is serialised, and a write is a seek of a few milliseconds, so the gate is never
/// the bottleneck. One writer at a time means there is no cycle of locks between this
/// app's own workers, which is what a deadlock needs.
/// </para>
/// <para>
/// It is a SEPARATE SERVICE, and it is INJECTED, because two different operations write
/// to this table: the walk, and the two-phase sync's enrichment pass. If they had a
/// gate each, they could run at the same time and deadlock against each other — and
/// "one writer at a time" has to mean one writer for the TABLE, not one per operation.
/// </para>
/// </remarks>
public sealed class DropBoxWriteGate(ILogger<DropBoxWriteGate> logger)
{
    /// <summary>
    /// How many times a write that was chosen as a deadlock victim is retried.
    /// <para>
    /// A deadlock victim is a transient failure, not a verdict: its transaction was
    /// rolled back whole, so running the write again is safe, and is what SQL Server
    /// itself asks for.
    /// </para>
    /// </summary>
    private const int MaxWriteAttempts = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs a write against <c>ml.DropBoxNode</c> and returns its result.</summary>
    public async Task<T> WriteAsync<T>(Func<Task<T>> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt < MaxWriteAttempts)
            {
                // The transaction was rolled back whole, so nothing was written and the
                // write can be run again unchanged.
                logger.LogWarning(
                    ex,
                    "Database write was chosen as a deadlock victim (attempt {Attempt} of {Max}); retrying.",
                    attempt, MaxWriteAttempts);

                // A short, growing pause: the other writer needs to finish and release
                // its locks before this one can take them.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>Runs a write against <c>ml.DropBoxNode</c> that returns nothing.</summary>
    public async Task WriteAsync(Func<Task> write, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                await write().ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt < MaxWriteAttempts)
            {
                logger.LogWarning(
                    ex,
                    "Database write was chosen as a deadlock victim (attempt {Attempt} of {Max}); retrying.",
                    attempt, MaxWriteAttempts);

                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>SQL Server's deadlock victim error number.</summary>
    private static bool IsDeadlock(SqlException ex) => ex.Number == 1205;
}
