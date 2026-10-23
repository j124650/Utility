-- =============================================================================
--  ml.DropBoxNode
--
--  Full Dropbox hierarchy for the configured account/namespace.
--  One row per folder/file; ParentId carries the structure.
--  This app OWNS this table and has full CRUD on it.
--
--  Design rule: store what Dropbox reports, nothing derived. Any aggregate
--  (folder totals, depth, subtree counts) is a QUERY, not a column, until a
--  real query needs it written down.
--
--  HOW TO RUN
--    sqlcmd -S <server> -d apps_ml_dev -E -I -i Sql/CreateDropBoxNode.sql
--
--    -I (QUOTED_IDENTIFIER ON) is REQUIRED. SSMS has it on by default, but
--    sqlcmd has it OFF, and SQL Server rejects a PERSISTED computed column
--    under OFF with a misleading "Incorrect syntax near the keyword 'AS'".
--
--    PREREQUISITE: the ml schema must already exist (Msg 2760 otherwise). The
--    script creates the table inside it but deliberately does not create the
--    schema, because ml is shared with tables this app does not own.
--
--    The script is idempotent: it drops and recreates the table, so it also
--    destroys any synced rows. Do not run it casually against real data.
--
--  GOTCHAS ENCOUNTERED AND WHY THEY ARE WRITTEN DOWN HERE
--    * ON DELETE CASCADE is impossible on a self-referencing FK (Msg 1785,
--      "may cause cycles or multiple cascade paths"). Hence NO ACTION, and
--      deleting a subtree is the caller's job: walk children before parents.
--    * PathHash must have its type INFERRED. Declaring it BINARY(32)
--      explicitly is a syntax error inside a computed-column definition.
--    * PathHash exists only because NVARCHAR(2048) cannot be part of an index
--      key (1700-byte limit), so the natural key needs a compact form.
--    * ParentId MUST be resolved in a second pass. A one-shot
--      INSERT..SELECT that tries to resolve parents in the same statement
--      silently inserts every ParentId as NULL, because SQL Server gives no
--      ordering guarantee across the join. See ResolveDropBoxNodeParents.sql.
-- =============================================================================

SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'ml.DropBoxNode', N'U') IS NOT NULL
    DROP TABLE ml.DropBoxNode;
GO

CREATE TABLE ml.DropBoxNode
(
    -- Surrogate key. Dropbox ids are opaque strings and are a business key,
    -- never the primary key.
    Id              BIGINT IDENTITY(1,1) NOT NULL,

    -- Namespace the node lives in: 'personal', or a team namespace id.
    -- The same Dropbox folder can exist in more than one namespace, so this
    -- is part of the node's identity.
    NamespaceId     NVARCHAR(64)    NOT NULL,

    -- Path exactly as Dropbox reports it. PathLower is Dropbox's own canonical
    -- lowercase form and is the comparison key; PathDisplay is for humans.
    -- Both stored verbatim - never re-derived from Name.
    PathDisplay     NVARCHAR(2048)  NOT NULL,
    PathLower       NVARCHAR(2048)  NOT NULL,

    -- SHA-256 over NamespaceId + '|' + PathLower: the compact form of the
    -- natural key, persisted because it is part of UQ_DropBoxNode_Path.
    PathHash        AS (HASHBYTES('SHA2_256',
                        CONVERT(VARBINARY(8000), NamespaceId + N'|' + PathLower))) PERSISTED,

    Name            NVARCHAR(255)   NOT NULL,
    IsFolder        BIT             NOT NULL,

    -- NULL only for the root of a namespace. ON DELETE NO ACTION, not
    -- CASCADE: SQL Server rejects a self-referencing cascade (Msg 1785).
    ParentId        BIGINT          NULL,

    -- File metadata. NULL for folders, which carry none.
    -- SizeBytes is NULL for a folder but 0 for a genuinely empty file -
    -- do not conflate the two.
    DropboxId       NVARCHAR(128)   NULL,
    SizeBytes       BIGINT          NULL,
    Rev             NVARCHAR(64)    NULL,
    ContentHash     NVARCHAR(64)    NULL,
    ServerModified  DATETIMEOFFSET  NULL,
    ClientModified  DATETIMEOFFSET  NULL,
    IsDownloadable  BIT             NULL,

    -- Present on shared/team folders.
    SharedFolderId  NVARCHAR(64)    NULL,

    -- Per-folder sync outcome for the CURRENT run.
    --
    -- The sweep deletes rows this run did not touch. If a folder is skipped or fails,
    -- its children are never reached, their rows keep the old SyncRunId, and an
    -- unconditional sweep would delete real data. This makes the hazard explicit: only
    -- 'Ok' may have stale rows deleted beneath it; anything else protects its subtree.
    --
    --   Ok       listed completely          -> sweep may clean beneath it
    --   Skipped  404 / no access            -> protected
    --   Failed   error during expansion     -> protected
    --   Partial  some children failed       -> protected
    --
    -- NULL for files: status is a folder-only concept, so files carry no meaningless
    -- value and an empty file (0 bytes) stays distinguishable from a folder.
    SyncStatus      NVARCHAR(16)    NULL
        CONSTRAINT CK_DropBoxNode_SyncStatus
            CHECK (SyncStatus IS NULL OR SyncStatus IN ('Ok','Skipped','Failed','Partial')),
    SyncStatusReason NVARCHAR(256)  NULL,

    -- Sync bookkeeping. SyncRunId gives exact set membership for a sweep
    -- ("delete everything this run did NOT touch"), which is immune to clock
    -- skew and to two syncs running at once.
    SyncRunId       UNIQUEIDENTIFIER NOT NULL,
    SyncedUtc       DATETIMEOFFSET   NOT NULL
        CONSTRAINT DF_DropBoxNode_SyncedUtc DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT PK_DropBoxNode PRIMARY KEY CLUSTERED (Id),

    -- One row per node per namespace. This makes an upsert deterministic:
    -- the same object at two paths is two rows, and a moved or renamed object
    -- is a delete + an insert.
    CONSTRAINT UQ_DropBoxNode_Path UNIQUE (NamespaceId, PathHash),

    CONSTRAINT FK_DropBoxNode_Parent FOREIGN KEY (ParentId)
        REFERENCES ml.DropBoxNode (Id) ON DELETE NO ACTION
);
GO

-- SQL Server does not create an index for a foreign key. This one is
-- structural, not speculative: "children of this folder" is the query the
-- whole ParentId design exists to serve, and an FK check on delete/update
-- would otherwise scan the table.
CREATE NONCLUSTERED INDEX IX_DropBoxNode_Parent
    ON ml.DropBoxNode (ParentId);
GO

-- Lets the sweep find protected (non-'Ok') folders quickly. Folders only: files carry
-- no status. Filtered, since files never qualify.
CREATE NONCLUSTERED INDEX IX_DropBoxNode_SyncStatus
    ON ml.DropBoxNode (NamespaceId, SyncStatus)
    INCLUDE (PathLower)
    WHERE IsFolder = 1;
GO