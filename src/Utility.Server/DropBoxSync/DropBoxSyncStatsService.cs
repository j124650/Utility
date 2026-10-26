using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Utility.Server.Options;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// Reads the dashboard's figures from the synced Dropbox index.
/// <para>
/// <b>Aggregates only.</b> The index holds hundreds of thousands of rows; reading them
/// to count them is what made the old dashboard take twenty minutes. Every figure here
/// comes from an aggregate scan that returns one row, and the whole set of figures is
/// read in well under a second.
/// </para>
/// <para>
/// Reads <c>ml.DropBoxNode</c> directly through ADO.NET, like the Finder's set-based
/// queries: the shapes these queries need are not what a query builder produces, and a
/// single round trip is the point.
/// </para>
/// </summary>
public sealed class DropBoxSyncStatsService(
    IOptions<ServerOptions> options,
    ILogger<DropBoxSyncStatsService> logger) : IDropBoxSyncStatsService
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private DropBoxSyncStats? _stats;
    private bool _isLoading;
    private string? _error;

    public DropBoxSyncStats? Stats
    {
        get => _stats;
        private set => Set(ref _stats, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>
    /// The totals: one row, one scan, no row materialization.
    /// </summary>
    private const string TotalsSql = """
        SELECT COUNT(*)                                                      AS TotalObjects,
               SUM(CASE WHEN IsFolder = 1 THEN 1 ELSE 0 END)                 AS Folders,
               SUM(CASE WHEN IsFolder = 0 THEN 1 ELSE 0 END)                 AS Files,
               SUM(CASE WHEN WidthPixels IS NOT NULL
                         AND HeightPixels IS NOT NULL THEN 1 ELSE 0 END)      AS FilesWithDimensions,
               SUM(CASE WHEN IsFolder = 0 THEN ISNULL(SizeBytes, 0) ELSE 0 END) AS TotalBytes,
               MAX(SyncedUtc)                                               AS LastSyncedUtc
        FROM ml.DropBoxNode
        """;

    /// <summary>
    /// The folders whose listing did not complete, grouped by why.
    /// <para>
    /// A folder qualifies when its status is not 'Ok'. A NULL status qualifies too: the
    /// sync writes one provisionally and only replaces it once the folder is expanded,
    /// so NULL means "recorded, never listed" - the same rule
    /// <c>DropBoxSyncStatus.ProtectsSubtree</c> uses to decide what a sweep may delete.
    /// </para>
    /// </summary>
    private const string NotSyncedSql = """
        SELECT SyncStatus, SyncStatusReason, COUNT(*) AS Cnt
        FROM ml.DropBoxNode
        WHERE IsFolder = 1
          AND (SyncStatus IS NULL OR SyncStatus <> N'Ok')
        GROUP BY SyncStatus, SyncStatusReason
        ORDER BY Cnt DESC
        """;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Error = null;
        Raise();

        try
        {
            await using var connection = await OpenConnectionAsync(ct);

            var totals = await ReadTotalsAsync(connection, ct);
            var notSynced = await ReadNotSyncedAsync(connection, ct);

            var stats = new DropBoxSyncStats(
                totals.TotalObjects,
                totals.Folders,
                totals.Files,
                totals.FilesWithDimensions,
                totals.TotalBytes,
                totals.LastSyncedUtc,
                notSynced);

            Stats = stats;

            logger.LogInformation(
                "Dropbox index: {Objects} object(s) - {Folders} folder(s), {Files} file(s), " +
                "{NotSynced} not fully listed.",
                stats.TotalObjects, stats.Folders, stats.Files, stats.NotSyncedCount);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            logger.LogError("Reading the Dropbox index stats failed: {Message}", ex.Message);
        }
        finally
        {
            IsLoading = false;
            Raise();
        }
    }

    private async Task<(int TotalObjects, int Folders, int Files, int FilesWithDimensions,
        long TotalBytes, DateTimeOffset? LastSyncedUtc)> ReadTotalsAsync(
        SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand(TotalsSql, connection);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5));
    }

    private async Task<List<DropBoxNotSyncedGroup>> ReadNotSyncedAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var groups = new List<DropBoxNotSyncedGroup>();

        await using var command = new SqlCommand(NotSyncedSql, connection);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            groups.Add(new DropBoxNotSyncedGroup(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2)));
        }

        return groups;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(options.Value.ConnectionString)
        {
            ConnectTimeout = options.Value.ConnectTimeoutSeconds
        }.ConnectionString);

        await connection.OpenAsync(ct);
        return connection;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
