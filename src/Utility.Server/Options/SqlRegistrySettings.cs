using Microsoft.Win32;

namespace Utility.Server.Options;

/// <summary>
/// Reads and writes the SQL Server settings from the Windows registry:
/// <c>HKEY_CURRENT_USER\Software\BbcUtility\Sql</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the sibling of the Dropbox key (<c>Software\BbcUtility\DropBox</c>), and it
/// carries the same four values the <c>Server</c> section of the configuration file
/// carried: <c>ConnectionString</c>, <c>CommandTimeoutSeconds</c>,
/// <c>ConnectTimeoutSeconds</c> and <c>EnableRetryOnFailure</c>.
/// </para>
/// <para>
/// The registry REPLACES the configuration file as the source: a value that is not in the
/// registry falls back to the default on <see cref="ServerOptions"/>, not to a file.
/// </para>
/// </remarks>
public interface ISqlRegistrySettings
{
    /// <summary>The registry key path these settings are read from.</summary>
    string RegistryKeyPath { get; }

    /// <summary>True when the key exists and carries a connection string.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Snapshots the registry into <see cref="ServerOptions"/>.
    /// Returns false when the key is absent or unusable — the options keep their
    /// defaults rather than being blanked by a missing key.
    /// </summary>
    bool TryApplyTo(ServerOptions options);

    /// <summary>
    /// Writes the settings to the registry, so the Settings page and the registry never
    /// disagree about which values are in force.
    /// </summary>
    void Save(ServerOptions options);
}

public sealed class SqlRegistrySettings : ISqlRegistrySettings
{
    public const string SubKeyPath = @"Software\BbcUtility\Sql";

    public const string ConnectionStringValueName = "ConnectionString";
    public const string CommandTimeoutValueName = "CommandTimeoutSeconds";
    public const string ConnectTimeoutValueName = "ConnectTimeoutSeconds";
    public const string RetryOnFailureValueName = "EnableRetryOnFailure";

    public string RegistryKeyPath => $@"HKEY_CURRENT_USER\{SubKeyPath}";

    // The registry is Windows-only while this library also targets plain net10.0, so every
    // entry point guards with OperatingSystem.IsWindows() inline: the platform-compat
    // analyser understands that call directly, but not a helper property that wraps it.
    // On any other OS these report "not configured" instead of throwing.

    public bool IsConfigured
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SubKeyPath);
                return key is not null &&
                       !string.IsNullOrWhiteSpace(ReadString(key, ConnectionStringValueName));
            }
            catch (Exception)
            {
                // Registry access can be denied by policy; treat that as "not configured"
                // rather than taking the whole app down at startup.
                return false;
            }
        }
    }

    public bool TryApplyTo(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKeyPath);
            if (key is null)
            {
                return false;
            }

            var connectionString = ReadString(key, ConnectionStringValueName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return false;
            }

            options.ConnectionString = connectionString;

            // The three tuning values are OPTIONAL: a registry that carries only the
            // connection string leaves the options' own defaults in force. That is the
            // point of the defaults being on the options and not in a file.
            if (TryReadInt(key, CommandTimeoutValueName, out var commandTimeout))
            {
                options.CommandTimeoutSeconds = commandTimeout;
            }

            if (TryReadInt(key, ConnectTimeoutValueName, out var connectTimeout))
            {
                options.ConnectTimeoutSeconds = connectTimeout;
            }

            if (TryReadBool(key, RetryOnFailureValueName, out var retryOnFailure))
            {
                options.EnableRetryOnFailure = retryOnFailure;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Save(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "SQL settings can only be written to the registry on Windows.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(SubKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key {RegistryKeyPath}.");

        key.SetValue(ConnectionStringValueName, options.ConnectionString ?? string.Empty, RegistryValueKind.String);
        key.SetValue(CommandTimeoutValueName, options.CommandTimeoutSeconds, RegistryValueKind.DWord);
        key.SetValue(ConnectTimeoutValueName, options.ConnectTimeoutSeconds, RegistryValueKind.DWord);
        key.SetValue(RetryOnFailureValueName, options.EnableRetryOnFailure, RegistryValueKind.DWord);
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var value = key.GetValue(name);
        return value as string;
    }

    /// <summary>
    /// Reads an integer, accepting the string form as well as the numeric one — a value
    /// typed into the registry by hand is a string until it is saved by the app.
    /// </summary>
    private static bool TryReadInt(RegistryKey key, string name, out int value)
    {
        value = 0;

        var raw = key.GetValue(name);
        if (raw is null)
        {
            return false;
        }

        if (raw is int number)
        {
            value = number;
            return true;
        }

        return int.TryParse(raw.ToString(), out value);
    }

    private static bool TryReadBool(RegistryKey key, string name, out bool value)
    {
        value = false;

        var raw = key.GetValue(name);
        if (raw is null)
        {
            return false;
        }

        if (raw is bool flag)
        {
            value = flag;
            return true;
        }

        return bool.TryParse(raw.ToString(), out value);
    }
}
