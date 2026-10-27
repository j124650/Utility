using Microsoft.Win32;

namespace Utility.DropBox.Options;

/// <summary>
/// Reads Dropbox credentials from the Windows registry:
/// <c>HKEY_CURRENT_USER\Software\BbcUtility\DropBox</c>.
/// <para>
/// The registry stores three string values: <c>AppKey</c>, <c>AppSecret</c> and
/// <c>Token</c>. <c>Token</c> is a <b>refresh</b> token, not an access token —
/// the OAuth <c>refresh_token</c> grant is the only thing that accepts it — so it
/// maps onto <see cref="DropBoxOptions.RefreshToken"/>.
/// </para>
/// </summary>
public interface IDropBoxRegistrySettings
{
    /// <summary>The registry key path these settings are read from.</summary>
    string RegistryKeyPath { get; }

    /// <summary>True when the key exists and carries both an app key and a token.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Snapshots the registry into <see cref="DropBoxOptions"/>.
    /// Returns false when the key is absent or unusable — callers keep their
    /// configuration-file values rather than being blanked by a missing key.
    /// </summary>
    bool TryApplyTo(DropBoxOptions options);

    /// <summary>
    /// Writes the current settings back to the registry, so the Settings page and the
    /// registry never disagree about which values are in force.
    /// </summary>
    void Save(DropBoxOptions options);
}

public sealed class DropBoxRegistrySettings : IDropBoxRegistrySettings
{
    public const string SubKeyPath = @"Software\BbcUtility\DropBox";

    public const string AppKeyValueName = "AppKey";
    public const string AppSecretValueName = "AppSecret";
    public const string TokenValueName = "Token";
    public const string RootPathValueName = "RootPath";
    public const string MaxRetriesValueName = "MaxRetriesOnError";

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
                       !string.IsNullOrWhiteSpace(ReadString(key, AppKeyValueName)) &&
                       !string.IsNullOrWhiteSpace(ReadString(key, TokenValueName));
            }
            catch (Exception)
            {
                // Registry access can be denied by policy; treat that as "not configured"
                // rather than taking the whole app down at startup.
                return false;
            }
        }
    }

    public bool TryApplyTo(DropBoxOptions options)
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

            var appKey = ReadString(key, AppKeyValueName);
            var token = ReadString(key, TokenValueName);

            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            options.AppKey = appKey;
            options.RefreshToken = token;

            // The secret is optional (PKCE apps have none) so only overwrite when present,
            // which keeps a secret set elsewhere usable.
            var secret = ReadString(key, AppSecretValueName);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                options.AppSecret = secret;
            }

            // The root path and the retry budget are optional too: a registry that carries
            // only the credentials leaves the options' own defaults in force.
            var rootPath = ReadString(key, RootPathValueName);
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                options.RootPath = rootPath;
            }

            if (TryReadInt(key, MaxRetriesValueName, out var maxRetries))
            {
                options.MaxRetriesOnError = maxRetries;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Persists the current credentials back to the registry so the Settings page and
    /// the registry never disagree about which token is in force.
    /// </summary>
    public void Save(DropBoxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Dropbox settings can only be written to the registry on Windows.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(SubKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key {RegistryKeyPath}.");

        key.SetValue(AppKeyValueName, options.AppKey ?? string.Empty, RegistryValueKind.String);
        key.SetValue(TokenValueName, options.RefreshToken ?? options.AccessToken ?? string.Empty, RegistryValueKind.String);

        if (!string.IsNullOrWhiteSpace(options.AppSecret))
        {
            key.SetValue(AppSecretValueName, options.AppSecret!, RegistryValueKind.String);
        }

        key.SetValue(RootPathValueName, options.RootPath ?? string.Empty, RegistryValueKind.String);
        key.SetValue(MaxRetriesValueName, options.MaxRetriesOnError, RegistryValueKind.DWord);
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
}