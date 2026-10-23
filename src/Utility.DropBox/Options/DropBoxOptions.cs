namespace Utility.DropBox.Options;

public sealed class DropBoxOptions
{
    public const string SectionName = "DropBox";

    /// <summary>App key from the Dropbox App Console.</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>
    /// App secret. Optional for public PKCE clients — leave empty when the app is
    /// registered as "App folder" or "Full Dropbox" with PKCE enabled.
    /// </summary>
    public string? AppSecret { get; set; }

    /// <summary>Long-lived refresh token obtained during the initial authorization.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Short-lived access token, if you have one. Prefer RefreshToken: the SDK refreshes
    /// automatically when only a refresh token is configured.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>Dropbox path the utility is scoped to, e.g. /Apps/Utility.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Retry budget for the SDK's internal transport.</summary>
        public int MaxRetriesOnError { get; set; } = 3;

    /// <summary>True once a token (access or refresh) is available.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppKey) &&
        (!string.IsNullOrWhiteSpace(RefreshToken) || !string.IsNullOrWhiteSpace(AccessToken));
}