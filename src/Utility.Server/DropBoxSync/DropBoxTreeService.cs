using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Utility.Server.DropBoxSync.Entities;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// Builds a display tree from the rows already stored in ml.DropBoxNode.
/// <para>
/// Reads the database only - it never calls Dropbox. That is the whole value of syncing:
/// the dashboard renders instantly from the table instead of walking the API.
/// </para>
/// <para>
/// The table is a flat adjacency list (ParentId), so the tree is assembled in memory from
/// one ordered query rather than with a recursive CTE per node - which would be N+1.
/// </para>
/// </summary>
public sealed class DropBoxTreeService(
    IDbContextFactory<DropBoxDbContext> contextFactory,
    ILogger<DropBoxTreeService> logger) : IDropBoxTreeService
{
    public async Task<(IReadOnlyList<DropBoxTreeNode> Roots, DropBoxTreeSummary Summary)> GetSyncedTreeAsync(
        string? namespaceId = null,
        int maxNodes = 5000,
        CancellationToken ct = default)
    {
        await using var db = contextFactory.CreateDbContext();

        var query = db.Nodes.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(namespaceId))
        {
            var ns = namespaceId;
            query = query.Where(n => n.NamespaceId == ns);
        }

        var rows = await query
            .OrderBy(n => n.PathLower)
            .Take(maxNodes)
            .ToListAsync(ct);

        var summary = new DropBoxTreeSummary(
            Folders: rows.Count(r => r.IsFolder),
            Files: rows.Count(r => !r.IsFolder),
            TotalBytes: rows.Where(r => !r.IsFolder).Sum(r => r.SizeBytes ?? 0),
            WithDimensions: rows.Count(r => r.WidthPixels is not null && r.HeightPixels is not null),
            UnverifiedFolders: rows.Count(r => r.IsFolder &&
                                               !string.Equals(r.SyncStatus, DropBoxSyncStatus.Ok, StringComparison.Ordinal)),
            LastSyncedUtc: rows.Count == 0 ? null : rows.Max(r => r.SyncedUtc));

        if (rows.Count == 0)
        {
            logger.LogInformation("No synced Dropbox rows to display.");
            return ([], summary);
        }

        var byId = rows.ToDictionary(r => r.Id);
        var childrenOf = new Dictionary<long, List<long>>();

        foreach (var row in rows)
        {
            if (row.ParentId is null)
            {
                continue;
            }

            // Guard against a parent that fell outside the page: an orphan must become a
            // root rather than silently vanish from the tree.
            if (!byId.ContainsKey(row.ParentId.Value))
            {
                continue;
            }

            if (!childrenOf.TryGetValue(row.ParentId.Value, out var list))
            {
                list = [];
                childrenOf[row.ParentId.Value] = list;
            }

            list.Add(row.Id);
        }

        var built = new Dictionary<long, DropBoxTreeNode>(rows.Count);

        DropBoxTreeNode Build(long id)
        {
            if (built.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var row = byId[id];

            var childIds = childrenOf.TryGetValue(id, out var kids) ? kids : [];
            var children = childIds
                .Select(Build)
                .OrderByDescending(c => c.IsFolder)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var node = new DropBoxTreeNode(
                row.Id,
                row.Name,
                row.PathDisplay,
                row.IsFolder,
                childIds.Count,
                row.SizeBytes ?? 0,
                row.WidthPixels,
                row.HeightPixels,
                row.MediaInfoTag,
                row.ServerModified,
                row.SyncStatus ?? "(none)")
            {
                Children = children
            };

            built[id] = node;
            return node;
        }

        // Rows with no parent, plus orphans whose parent was not in the page.
        var roots = rows
            .Where(r => r.ParentId is null || !byId.ContainsKey(r.ParentId.Value))
            .Select(r => Build(r.Id))
            .OrderByDescending(r => r.IsFolder)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (roots, summary);
    }
}