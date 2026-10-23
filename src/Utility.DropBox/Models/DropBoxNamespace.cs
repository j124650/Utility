namespace Utility.DropBox.Models;

/// <summary>
/// A namespace the account can browse. Dropbox resolves paths per namespace: the
/// personal space, and — for Business accounts — one entry per team root.
/// </summary>
public sealed record DropBoxNamespace(
    string Id,
    string DisplayName,
    bool IsPersonal,
    string? TeamName = null)
{
    public static DropBoxNamespace Personal(string homeNamespaceId) =>
        new(homeNamespaceId, "Personal", IsPersonal: true);

    /// <summary>Label shown in the namespace picker.</summary>
    public string DisplayLabel => IsPersonal
        ? "Personal"
        : TeamName is null || TeamName.Length == 0
            ? DisplayName
            : $"{DisplayName} ({TeamName})";
}