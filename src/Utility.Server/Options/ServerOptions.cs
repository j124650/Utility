namespace Utility.Server.Options;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// Full SQL Server connection string. The utility only reads, so this points at an
    /// existing database such as apps_ml_dev. Nothing is ever created or migrated.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Timeout applied to every query.
    /// <para>
    /// 20 minutes. The sync walk flushes a folder's rows in batches, and each batch
    /// ends with a lookup of the paths already stored - a query whose cost grows with
    /// the table. A short ceiling does not fail fast, it kills a walk that was hours
    /// in and had already written most of the tree.
    /// </para>
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 1200;

    /// <summary>
    /// Timeout applied to opening a connection.
    /// <para>
    /// 20 minutes, and separate from <see cref="CommandTimeoutSeconds"/> because it is
    /// SqlClient's timeout, not EF's: it bounds the whole login handshake. A server
    /// under memory pressure can hold a login open for minutes - it fails the login
    /// long before it fails the query, so this is the timeout that actually ends a
    /// walk.
    /// </para>
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 1200;

    /// <summary>Retry transient SQL faults (failover, network blips).</summary>
    public bool EnableRetryOnFailure { get; set; } = true;
}