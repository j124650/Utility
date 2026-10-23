using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Utility.Server.DropBoxSync.Entities;

namespace Utility.Server.DropBoxSync;

/// <summary>
/// Builds a display tree from the rows already stored in ml.DropBoxNode, deriving the
/// hierarchy from each row's FULL PATH.
/// <para>
/// Why the path and not ParentId: a sync that starts deep leaves the ancestors above it
/// unsynced, so those rows have ParentId = NULL. Following ParentId alone would render
/// the deepest synced folder as a floating root with no context above it. Splitting the
/// path rebuilds the full chain and marks the never-synced ancestors as synthetic, so the
/// tree reads correctly for any subset that has been synced.
/// </para>
/// <para>
/// Reads the database only - it never calls Dropbox.
/// </para>
/// </summary>
public sealed class DropBoxTreeService(
    IDbContextFactory<DropBoxDbContext> contextFactory,
    ILogger<DropBoxTreeService> logger) : IDropBoxTreeService
{
    public async Task<(IReadOnlyList<DropBoxTreeNode> Roots, DropBoxTreeSummary Summary)> GetSyncedTreeAsync(
        string? namespaceId = null,
        CancellationToken ct = default)
    {
        await using var db = contextFactory.CreateDbContext();

        var query = db.Nodes.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(namespaceId))
        {
            var ns = namespaceId;
            query = query.Where(n => n.NamespaceId == ns);
        }

        // No row cap. The tree shows everything that has been synced - a cap would hide
        // part of the tree without saying so.
        var rows = await query
            .OrderBy(n => n.PathLower)
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

        // A mutable scaffold keyed by the ORIGINAL-CASE segment as it appears in the path.
        // Keyed on the lowercased form so "DC_SHOES" and "dc_shoes" resolve to one node -
        // Dropbox's own path_lower is the canonical comparison key, and a single folder can
        // legitimately appear with different case in two parents.
        var roots = new List<Scaffold>();

        foreach (var row in rows)
        {
            var segments = SplitPath(row.PathDisplay);
            if (segments.Count == 0)
            {
                continue;
            }

            var level = roots;
            Scaffold? node = null;
            var builtPath = new System.Text.StringBuilder();

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];

                if (i > 0)
                {
                    builtPath.Append('/');
                }

                builtPath.Append(segment);

                var found = level.FirstOrDefault(
                    c => string.Equals(c.Segment, segment, StringComparison.OrdinalIgnoreCase));

                if (found is null)
                {
                    found = new Scaffold(segment, "/" + builtPath);
                    level.Add(found);
                }

                node = found;
                level = found.Children;
            }

            // The last segment is this row itself.
            if (node is not null)
            {
                node.Row = row;
            }
        }

        var built = new List<DropBoxTreeNode>(roots.Count);
        foreach (var root in roots)
        {
            built.Add(Build(root));
        }

        var ordered = built
            .OrderByDescending(r => r.IsFolder)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (ordered, summary);
    }

    /// <summary>
    /// Splits a Dropbox path into its segments. Dropbox returns namespace-absolute paths,
    /// so the split is unambiguous and never needs the parent row to interpret it.
    /// </summary>
    private static List<string> SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return [];
        }

        return path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static DropBoxTreeNode Build(Scaffold scaffold)
    {
        var children = scaffold.Children
            .Select(Build)
            .OrderByDescending(c => c.IsFolder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var row = scaffold.Row;

        if (row is null)
        {
            // An ancestor that exists only in the path - never synced itself.
            return new DropBoxTreeNode(
                id: 0,
                name: scaffold.Segment,
                fullPath: scaffold.FullPath,
                isFolder: true,
                childCount: children.Count,
                sizeBytes: 0,
                widthPixels: null,
                heightPixels: null,
                mediaInfoTag: null,
                serverModified: null,
                syncStatus: "(not synced)",
                isSynced: false)
            {
                Children = children
            };
        }

        return new DropBoxTreeNode(
            id: row.Id,
            name: row.Name,
            fullPath: row.PathDisplay,
            isFolder: row.IsFolder,
            childCount: children.Count,
            sizeBytes: row.SizeBytes ?? 0,
            widthPixels: row.WidthPixels,
            heightPixels: row.HeightPixels,
            mediaInfoTag: row.MediaInfoTag,
            serverModified: row.ServerModified,
            syncStatus: row.SyncStatus ?? "(none)",
            isSynced: true)
        {
            Children = children
        };
    }

    /// <summary>Mutable node used while assembling the tree from paths.</summary>
    private sealed class Scaffold(string segment, string fullPath)
    {
        public string Segment { get; } = segment;

        public string FullPath { get; } = fullPath;

        public List<Scaffold> Children { get; } = [];

        /// <summary>The synced row at this exact path, or null for a synthetic ancestor.</summary>
        public DropBoxNode? Row { get; set; }
    }
}