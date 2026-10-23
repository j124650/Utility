using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Utility.Server.ML.Data;

namespace Utility.Server;

/// <summary>
/// Connection health check for the app's database.
/// <para>
/// The utility only reads pre-existing tables, so there is nothing to migrate and this
/// never issues DDL. <see cref="ProbeAsync"/> is what the Settings page's
/// "Test connection" button calls.
/// </para>
/// </summary>
public interface IDatabaseProbe
{
    Task<DatabaseProbeResult> ProbeAsync(CancellationToken ct = default);
}

public sealed record DatabaseProbeResult(bool Success, string Message, long ElapsedMs)
{
    public string Display => Success
        ? $"Connected in {ElapsedMs} ms."
        : $"Failed: {Message}";
}

public sealed class DatabaseProbe(MlDbContext db, ILogger<DatabaseProbe> logger) : IDatabaseProbe
{
    public async Task<DatabaseProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (!await db.Database.CanConnectAsync(ct))
            {
                return new DatabaseProbeResult(false, "Cannot connect to the database.", sw.ElapsedMilliseconds);
            }

            // Round-trip a real read so the check covers more than the socket.
            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);

            logger.LogInformation("Database probe succeeded in {Elapsed} ms.", sw.ElapsedMilliseconds);

            return new DatabaseProbeResult(true, "Connected.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database probe failed.");
            return new DatabaseProbeResult(false, ex.Message, sw.ElapsedMilliseconds);
        }
    }
}