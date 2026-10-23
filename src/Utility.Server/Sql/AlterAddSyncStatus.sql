-- =============================================================================
--  AlterAddSyncStatus
--
--  Upgrades an EXISTING ml.DropBoxNode (the original 18-column table) to the
--  current 20-column shape by adding per-folder sync status.
--
--  WHY THIS EXISTS
--    CreateDropBoxNode.sql is drop-and-recreate and already includes SyncStatus, so a
--    fresh install never needs this file. But the table was created before SyncStatus
--    existed, so anyone with a deployed database has no recorded way to reach the current
--    shape - recreating would destroy synced rows. This is that missing path.
--
--  WHAT IT ADDS
--    SyncStatus        nvarchar(16)  NULL   Ok | Skipped | Failed | Partial, or NULL
--    SyncStatusReason  nvarchar(256) NULL   short diagnostic for a non-Ok status
--    CK_DropBoxNode_SyncStatus          CHECK constraint on the four values
--    IX_DropBoxNode_SyncStatus          filtered index, folders only
--
--  SEMANTICS
--    Status is per-folder and means "what happened to this folder in the current run".
--    NULL on files: status is a folder-only concept, so files carry no meaningless value
--    and an empty file (0 bytes) stays distinguishable from a folder.
--
--      Ok       listed completely          -> the sweep MAY delete stale rows beneath it
--      Skipped  404 or no access           -> sweep must NOT descend
--      Failed   error during expansion      -> sweep must NOT descend
--      Partial  some children failed        -> sweep must NOT descend
--
--    A folder already in the table from before this column existed has NULL status, which
--    ProtectsSubtree() treats as protected - so the first sync after upgrading is
--    conservative and deletes nothing until each folder is re-listed. That is the safe
--    direction to fail.
--
--  HOW TO RUN
--    sqlcmd -S <server> -d apps_ml_dev -E -I -i Sql/AlterAddSyncStatus.sql
--
--    -I (QUOTED_IDENTIFIER ON) is REQUIRED for the CHECK constraint.
--    Idempotent: safe to run repeatedly. Re-running changes nothing.
--
--  GOTCHA WORTH KNOWING
--    Each ALTER is its own batch on purpose. Writing several ALTERs inside a single
--    IF...GO block half-applies when the parser splits them, which is how this was first
--    written - SyncStatusReason appeared while SyncStatus and the constraint did not.
-- =============================================================================

SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
GO

IF COL_LENGTH('ml.DropBoxNode', 'SyncStatus') IS NULL
    ALTER TABLE ml.DropBoxNode ADD SyncStatus NVARCHAR(16) NULL;
GO

IF COL_LENGTH('ml.DropBoxNode', 'SyncStatusReason') IS NULL
    ALTER TABLE ml.DropBoxNode ADD SyncStatusReason NVARCHAR(256) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_DropBoxNode_SyncStatus')
    ALTER TABLE ml.DropBoxNode WITH CHECK
        ADD CONSTRAINT CK_DropBoxNode_SyncStatus
        CHECK (SyncStatus IS NULL OR SyncStatus IN ('Ok','Skipped','Failed','Partial'));
GO

-- Filtered on IsFolder = 1: files never carry a status, so including them would bloat
-- the index with rows that can never participate.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DropBoxNode_SyncStatus')
    CREATE INDEX IX_DropBoxNode_SyncStatus
        ON ml.DropBoxNode (NamespaceId, SyncStatus)
        INCLUDE (PathLower)
        WHERE IsFolder = 1;
GO

-- Verification: both columns present, constraint and index present.
SELECT
    (SELECT COUNT(*) FROM sys.columns
      WHERE object_id = OBJECT_ID('ml.DropBoxNode')
        AND name IN ('SyncStatus', 'SyncStatusReason'))              AS status_columns_expect_2,
    (SELECT COUNT(*) FROM sys.check_constraints
      WHERE name = 'CK_DropBoxNode_SyncStatus')                      AS check_constraint_expect_1,
    (SELECT COUNT(*) FROM sys.indexes
      WHERE object_id = OBJECT_ID('ml.DropBoxNode')
        AND name = 'IX_DropBoxNode_SyncStatus')                     AS filtered_index_expect_1,
    (SELECT COUNT(*) FROM ml.DropBoxNode)                            AS existing_rows_preserved;
GO