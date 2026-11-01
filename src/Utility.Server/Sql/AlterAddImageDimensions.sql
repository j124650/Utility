-- =============================================================================
--  AlterAddImageDimensions
--
--  Adds image/video dimensions to an EXISTING ml.DropBoxNode.
--
--  WHY THIS EXISTS
--    CreateDropBoxNode.sql is drop-and-recreate, so a fresh install never needs this.
--    But the table already exists in deployed databases, and recreating it would destroy
--    synced rows. This is the upgrade path for the three new columns.
--
--  WHAT IT ADDS
--    WidthPixels       int          NULL   pixels
--    HeightPixels      int          NULL   pixels
--    MediaInfoTag      nvarchar(16) NULL   'photo' | 'video' | 'pending' | NULL
--
--  WHY MediaInfoTag EXISTS - read this before querying the dimensions
--    Dropbox returns media info as a UNION with three distinct outcomes, and collapsing
--    them into "dimensions are NULL" would be a silent correctness bug:
--
--      NULL       the file is not an image/video (a .txt, a .pdf): it will NEVER have
--                 dimensions. Safe to exclude permanently.
--      'pending'  Dropbox deferred the work and may answer on a later read. This is NOT
--                 the same as NULL - the dimensions exist and can be fetched later.
--                 Rows in this state are re-claimable.
--      'photo'    real image; Width/Height are authoritative.
--      'video'    real video; Width/Height are authoritative.
--
--    So: filter on MediaInfoTag = 'photo' OR 'video' for "known dimensions", and treat
--    'pending' as "ask again later", not as "no dimensions".
--
--  NULLABLE ON PURPOSE
--    NULL is the honest default for every existing row: dimensions were never requested,
--    which is a third state distinct from both "no dimensions" and "pending". Rows synced
--    before this ALTER are simply unknown, and must not be read as images without one.
--
--  HOW TO RUN
--    sqlcmd -S <server> -d apps_ml_dev -E -I -i Sql/AlterAddImageDimensions.sql
--
--    -I (QUOTED_IDENTIFIER ON) is REQUIRED for the CHECK constraint.
--    Idempotent: safe to run repeatedly.
--
--  COST WARNING - MEASURED 2026-11-01
--    Populating these was believed to require include_media_info=true on every
--    ListFolder call. MEASURED on this account: it does not.
--
--      list_folder, include_media_info = false  : 4.27 s, 0 files with dimensions
--      list_folder, include_media_info = true   : 4.33 s, 0 files with dimensions
--      list_folder recursive, include_media_info = true : 4.11 s, 0 files
--      get_metadata, one per file               : 35.53 s, 99 files with dimensions
--
--    So the flag is INERT for files/list_folder: it costs nothing AND delivers
--    nothing. The dimensions come only from a per-file get_metadata.
--
--    IDropBoxSyncService therefore still takes includeMediaInfo and defaults it to
--    FALSE, but the reason is now the second one, not the first: the listing cannot
--    supply the dimensions however it is called, so the per-file get_metadata is the
--    only source, and it is where the sync's time goes.
-- =============================================================================

SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
GO

IF COL_LENGTH('ml.DropBoxNode', 'WidthPixels') IS NULL
    ALTER TABLE ml.DropBoxNode ADD WidthPixels INT NULL;
GO

IF COL_LENGTH('ml.DropBoxNode', 'HeightPixels') IS NULL
    ALTER TABLE ml.DropBoxNode ADD HeightPixels INT NULL;
GO

IF COL_LENGTH('ml.DropBoxNode', 'MediaInfoTag') IS NULL
    ALTER TABLE ml.DropBoxNode ADD MediaInfoTag NVARCHAR(16) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_DropBoxNode_MediaInfoTag')
    ALTER TABLE ml.DropBoxNode WITH CHECK
        ADD CONSTRAINT CK_DropBoxNode_MediaInfoTag
        CHECK (MediaInfoTag IS NULL OR MediaInfoTag IN ('photo', 'video', 'pending'));
GO

-- Supports "give me every image" without scanning the table once dimensions are in use.
-- Filtered to non-null: rows without dimensions can never match this predicate, so
-- including them would only bloat the index.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DropBoxNode_Dimensions')
    CREATE INDEX IX_DropBoxNode_Dimensions
        ON ml.DropBoxNode (NamespaceId, WidthPixels, HeightPixels)
        INCLUDE (PathDisplay, SizeBytes)
        WHERE MediaInfoTag IS NOT NULL AND MediaInfoTag <> 'pending';
GO

-- Verification.
SELECT
    (SELECT COUNT(*) FROM sys.columns
      WHERE object_id = OBJECT_ID('ml.DropBoxNode')
        AND name IN ('WidthPixels', 'HeightPixels', 'MediaInfoTag')) AS dimension_columns_expect_3,
    (SELECT COUNT(*) FROM sys.check_constraints
      WHERE name = 'CK_DropBoxNode_MediaInfoTag')                  AS check_constraint_expect_1,
    (SELECT COUNT(*) FROM sys.indexes
      WHERE object_id = OBJECT_ID('ml.DropBoxNode')
        AND name = 'IX_DropBoxNode_Dimensions')                     AS filtered_index_expect_1,
    (SELECT COUNT(*) FROM ml.DropBoxNode)                            AS existing_rows_preserved;
GO