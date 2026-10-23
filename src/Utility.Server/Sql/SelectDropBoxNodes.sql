-- =============================================================================
--  SelectDropBoxNodes
--
--  Reference queries for ml.DropBoxNode: the flat export, the indented tree,
--  and the subtree lookup that future work will build on.
--
--  Requires QUOTED_IDENTIFIER ON (sqlcmd -I) for the recursive CTE's
--  persisted-column read.
-- =============================================================================

SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
GO

-- -----------------------------------------------------------------------------
--  1. EVERY ROW, WITH THE FULL PATH.
--      PathDisplay already IS the full path, so this needs no join and no
--      recursive walk - just order by it.
-- -----------------------------------------------------------------------------
SELECT
    PathDisplay AS FullPath,
    Name,
    CASE WHEN IsFolder = 1 THEN N'FOLDER' ELSE N'FILE' END AS Kind,
    SizeBytes,
    CASE WHEN SizeBytes IS NULL THEN NULL
         WHEN SizeBytes >= 1073741824 THEN CONCAT(CAST(SizeBytes / 1073741824.0 AS DECIMAL(8,2)), N' GB')
         WHEN SizeBytes >= 1048576    THEN CONCAT(CAST(SizeBytes / 1048576.0    AS DECIMAL(8,2)), N' MB')
         WHEN SizeBytes >= 1024       THEN CONCAT(CAST(SizeBytes / 1024.0       AS DECIMAL(8,2)), N' KB')
         ELSE CONCAT(SizeBytes, N' B') END AS SizeHuman,
    ServerModified,
    Rev,
    ContentHash,
    DropboxId
FROM ml.DropBoxNode
ORDER BY PathDisplay;
GO

-- -----------------------------------------------------------------------------
--  2. THE SAME DATA AS AN INDENTED TREE, via a recursive CTE over ParentId.
--      MAXRECURSION 0 removes the default 100-level cap.
-- -----------------------------------------------------------------------------
WITH tree AS
(
    SELECT Id, ParentId, PathDisplay, Name, IsFolder, SizeBytes, ServerModified,
           0 AS lvl, CAST(N'' AS NVARCHAR(4000)) AS indent
    FROM ml.DropBoxNode
    WHERE ParentId IS NULL

    UNION ALL

    SELECT c.Id, c.ParentId, c.PathDisplay, c.Name, c.IsFolder, c.SizeBytes, c.ServerModified,
           t.lvl + 1, CAST(t.indent + N'    ' AS NVARCHAR(4000))
    FROM ml.DropBoxNode AS c
    INNER JOIN tree AS t ON c.ParentId = t.Id
)
SELECT lvl AS Depth, indent + Name AS Tree, IsFolder, SizeBytes, ServerModified
FROM tree
ORDER BY PathDisplay
OPTION (MAXRECURSION 0);
GO

-- -----------------------------------------------------------------------------
--  3. EVERYTHING UNDER ONE FOLDER (files only).
--      PathLower is the stored, already-lowercased path, so the prefix match is
--      a single seek on PathLower rather than a recursive descent.
--      This is the query the table exists to make fast.
-- -----------------------------------------------------------------------------
DECLARE @ns NVARCHAR(64) = N'<team namespace id>';  -- see Users/GetCurrentAccount -> root_info.root_namespace_id
DECLARE @root NVARCHAR(2048) = N'/images-catalogs team folder/lightboxpics';

SELECT PathDisplay AS FullPath, Name, SizeBytes, ServerModified, Rev
FROM ml.DropBoxNode
WHERE NamespaceId = @ns
  AND IsFolder = 0
  AND PathLower LIKE @root + N'/%'
ORDER BY PathLower;
GO

-- -----------------------------------------------------------------------------
--  4. DIRECT CHILDREN OF ONE FOLDER - the query IX_DropBoxNode_Parent serves.
-- -----------------------------------------------------------------------------
DECLARE @path NVARCHAR(2048) = N'/Images-Catalogs Team Folder/LightBoxPics';

SELECT c.PathDisplay AS FullPath, c.Name, c.IsFolder, c.SizeBytes, c.ServerModified
FROM ml.DropBoxNode AS c
WHERE c.ParentId = (SELECT Id FROM ml.DropBoxNode
                    WHERE PathDisplay = @path AND ParentId IS NULL)
ORDER BY c.IsFolder DESC, c.Name;
GO

-- -----------------------------------------------------------------------------
--  5. WHAT THE NEXT SYNC MUST DELETE - the sweep, by SyncRunId.
--      Exact set membership: immune to clock skew and to overlapping syncs.
-- -----------------------------------------------------------------------------
DECLARE @run UNIQUEIDENTIFIER = '11111111-2222-3333-4444-555555555555';

SELECT PathDisplay AS StalePath, SyncedUtc
FROM ml.DropBoxNode
WHERE SyncRunId <> @run
ORDER BY PathDisplay;
GO