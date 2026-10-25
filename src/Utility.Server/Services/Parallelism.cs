namespace Utility.Server.Services;

/// <summary>
/// How many things the app may do at once.
/// </summary>
public interface IParallelism
{
    /// <summary>
    /// The number of logical processors on this machine.
    /// </summary>
    /// <remarks>
    /// This is the degree of parallelism the app uses for its own work: the Finder's file
    /// pass (§19.2) reads the pairs with this many workers.
    /// </remarks>
    int LogicalProcessorCount { get; }
}

/// <summary>
/// The app's parallelism: the machine's logical processor count, read once and kept.
/// </summary>
/// <remarks>
/// The count cannot change while the app runs, and it is asked for on every parallel loop, so
/// it is read once and cached rather than asked of the system each time.
/// </remarks>
public sealed class Parallelism : IParallelism
{
    private int? _count;

    /// <summary>
    /// The number of logical processors on this machine, read once and kept.
    /// </summary>
    /// <remarks>
    /// It is the LOGICAL count — a machine with SMT reports twice its physical cores — because
    /// the work the app parallelises is a mix: decoding scales with the logical processors,
    /// and the filesystem work scales with them too.
    /// </remarks>
    public int LogicalProcessorCount => _count ??= Environment.ProcessorCount;
}
