-- =============================================================================
--  AlterAddNameIndex
--
--  Adds IX_DropBoxNode_Name to an EXISTING ml.DropBoxNode.
--
--  WHY THIS EXISTS
--    CreateDropBoxNode.sql is drop-and-recreate, so a fresh install never needs this.
--    But the table already exists in deployed databases, and recreating it would destroy
--    synced rows. This is the upgrade path for the index the Finder needs.
--
--  WHAT IT ADDS
--    IX_DropBoxNode_Name   nonclustered index on Name
--
--  WHY IT IS NOT SPECULATIVE
--    The Finder's candidate lookup joins ml.Media.FileName to ml.DropBoxNode.Name. That
--    join runs once over the whole index, so it needs a path to Name - without this index
--    the join has none, and a per-parent lookup would scan the whole table every time.
--
--    The INCLUDEs are the candidate's own fields (IsFolder for the files-only filter,
--    SizeBytes for the size rule, WidthPixels/HeightPixels for the dimension comparison,
--    ServerModified for the tiebreak order), so the lookup is served by the index alone.
--
--    Name and ml.Media.FileName are both CI_AS, so the equality join is case-insensitive
--    and needs no collation override.
--
--  HOW TO RUN
--    sqlcmd -S <server> -d apps_ml_dev -E -I -i Sql/AlterAddNameIndex.sql
--
--    Idempotent: safe to run repeatedly.
--
--  COST
--    On ~230k rows the index builds in seconds and costs ~18 MB. Building it rewrites
--    every row, so it takes a brief exclusive lock on the table - do not run it during
--    a sync.
-- =============================================================================

SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DropBoxNode_Name')
    CREATE INDEX IX_DropBoxNode_Name
        ON ml.DropBoxNode (Name)
        INCLUDE (IsFolder, SizeBytes, WidthPixels, HeightPixels, ServerModified);
GO

-- Verification: index present, rows preserved.
SELECT
    (SELECT COUNT(*) FROM sys.indexes
      WHERE object_id = OBJECT_ID('ml.DropBoxNode')
        AND name = 'IX_DropBoxNode_Name')                     AS name_index_expect_1,
    (SELECT COUNT(*) FROM ml.DropBoxNode)                     AS existing_rows_preserved;
GO
