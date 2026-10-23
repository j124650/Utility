namespace Utility.Server.DropBoxSync;

/// <summary>
/// Per-folder outcome of a sync run. Stored in <c>ml.DropBoxNode.SyncStatus</c>, which is
/// a <c>nvarchar</c> with a CHECK constraint rather than a SQL enum type.
/// <para>
/// The rule that makes the sweep safe: only <see cref="Ok"/> may have stale rows deleted
/// beneath it. Any other value means the folder was not fully listed, so its children are
/// unverified — not absent — and must be left alone.
/// </para>
/// </summary>
public static class DropBoxSyncStatus
{
    /// <summary>Listed completely. Only this status permits sweeping beneath it.</summary>
    public const string Ok = "Ok";

    /// <summary>404 or no access; the folder was not listed. Protects its subtree.</summary>
    public const string Skipped = "Skipped";

    /// <summary>An error occurred while listing or expanding. Protects its subtree.</summary>
    public const string Failed = "Failed";

    /// <summary>Some children failed while others succeeded. Protects its subtree.</summary>
    public const string Partial = "Partial";

    /// <summary>Every value the CHECK constraint permits, for validation and tests.</summary>
    public static readonly string[] All = [Ok, Skipped, Failed, Partial];

    /// <summary>True when a folder in this state must not have rows deleted beneath it.</summary>
    public static bool ProtectsSubtree(string? status) =>
        !string.Equals(status, Ok, StringComparison.Ordinal);
}