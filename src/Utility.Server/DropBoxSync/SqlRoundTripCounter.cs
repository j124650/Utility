using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// COUNTS THE DATABASE ROUND TRIPS.
/// <para>
/// One command is one round trip: EF batches a <c>SaveChanges</c> into one command, so
/// the number of commands is the number of times the app went to the server. The
/// server's own work is a different thing - that is what the DMVs report - and this
/// counts the trips, because the trips are what the walk's throughput is bounded by:
/// every write to <c>ml.DropBoxNode</c> is serialised behind one gate.
/// </para>
/// <para>
/// The commands are counted by their kind, because the kinds are what an optimisation
/// is aimed at: the tracked query that loads a chunk, the updates that write it, the
/// folder's status write, the insert of a row that did not exist.
/// </para>
/// <para>
/// The counter is cumulative for the life of the process. A run that wants its own
/// figure takes a snapshot before it starts and subtracts.
/// </para>
/// </summary>
public sealed class SqlRoundTripCounter : DbCommandInterceptor
{
    private int _commands;
    private long _selects;
    private long _updates;
    private long _inserts;
    private long _deletes;
    private long _others;

    /// <summary>Every command the app has sent.</summary>
    public int Commands => Volatile.Read(ref _commands);

    /// <summary>The commands that read.</summary>
    public long Selects => Volatile.Read(ref _selects);

    /// <summary>The commands that update.</summary>
    public long Updates => Volatile.Read(ref _updates);

    /// <summary>The commands that insert.</summary>
    public long Inserts => Volatile.Read(ref _inserts);

    /// <summary>The commands that delete.</summary>
    public long Deletes => Volatile.Read(ref _deletes);

    /// <summary>The commands that are none of the above - a transaction, a batch.</summary>
    public long Others => Volatile.Read(ref _others);

    /// <summary>Forgets everything counted so far.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _commands, 0);
        Interlocked.Exchange(ref _selects, 0);
        Interlocked.Exchange(ref _updates, 0);
        Interlocked.Exchange(ref _inserts, 0);
        Interlocked.Exchange(ref _deletes, 0);
        Interlocked.Exchange(ref _others, 0);
    }

    /// <summary>The counter's figures as they are right now.</summary>
    public SqlRoundTripSnapshot Snapshot() => new(Commands, Selects, Updates, Inserts, Deletes, Others);

    private void Count(DbCommand command)
    {
        Interlocked.Increment(ref _commands);

        switch (FirstWord(command.CommandText))
        {
            case "SELECT":
                Interlocked.Increment(ref _selects);

                break;
            case "UPDATE":
                Interlocked.Increment(ref _updates);

                break;
            case "INSERT":
                Interlocked.Increment(ref _inserts);

                break;
            case "DELETE":
                Interlocked.Increment(ref _deletes);

                break;
            default:
                Interlocked.Increment(ref _others);

                break;
        }
    }

    /// <summary>
    /// The command's first word, upper-cased. The count is by statement kind, so this
    /// only has to be right about the first word.
    /// </summary>
    private static string FirstWord(string text)
    {
        var trimmed = text.AsSpan().TrimStart();

        var end = 0;

        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }

        return trimmed[..end].ToString().ToUpperInvariant();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count(command);

        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        Count(command);

        return new ValueTask<InterceptionResult<DbDataReader>>(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count(command);

        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Count(command);

        return new ValueTask<InterceptionResult<int>>(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count(command);

        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    {
        Count(command);

        return new ValueTask<InterceptionResult<object>>(result);
    }
}

/// <summary>
/// The round trips counted, at a moment.
/// <para>
/// The counter is cumulative, so a run takes a snapshot before it starts and asks for
/// the difference. That difference is the run's own cost.
/// </para>
/// </summary>
public readonly record struct SqlRoundTripSnapshot(
    int Commands,
    long Selects,
    long Updates,
    long Inserts,
    long Deletes,
    long Others)
{
    /// <summary>What happened between two snapshots.</summary>
    public static SqlRoundTripSnapshot Since(SqlRoundTripSnapshot before, SqlRoundTripSnapshot now) => new(
        now.Commands - before.Commands,
        now.Selects - before.Selects,
        now.Updates - before.Updates,
        now.Inserts - before.Inserts,
        now.Deletes - before.Deletes,
        now.Others - before.Others);

    /// <summary>The figures as one line of text, for a log.</summary>
    public string Describe() =>
        $"{Commands:N0} round trip(s) ({Selects:N0} select, {Updates:N0} update, {Inserts:N0} insert, {Deletes:N0} delete, {Others:N0} other)";
}
