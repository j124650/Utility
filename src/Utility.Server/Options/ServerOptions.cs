namespace Utility.Server.Options;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// Full SQL Server connection string. The utility only reads, so this points at an
    /// existing database such as apps_ml_dev. Nothing is ever created or migrated.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Timeout applied to every query.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Retry transient SQL faults (failover, network blips).</summary>
    public bool EnableRetryOnFailure { get; set; } = true;
}