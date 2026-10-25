SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
GO

-- =============================================================================
--  ml.DropBoxNode.NameKey
--
--  The name reduced to a comparison key: the extension dropped, upper-cased,
--  first eight characters.
--
--  This is the key the Finder's similarity widening joins on (§10's fuzzy
--  rules). It is a PERSISTED computed column so the database maintains it:
--  rows the sync writes later carry a key without this code doing anything.
--
--  The key is a POOLING key, not the decision. It says which candidates are
--  worth comparing; §10's fuzzy rules, applied afterwards, say which of them
--  are actually similar.
--
--  The rule is duplicated in C# (FinderService.Prefix). The two must agree
--  exactly, or the join finds nothing. Both take the name up to its LAST dot,
--  upper-case it, and keep the first eight characters.
-- =============================================================================

-- The index goes first: it depends on the column, and the column is about to
-- be replaced by the computed one.
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'IX_DropBoxNode_NameKey'
             AND object_id = OBJECT_ID(N'ml.DropBoxNode'))
    DROP INDEX IX_DropBoxNode_NameKey ON ml.DropBoxNode;
GO

IF COL_LENGTH(N'ml.DropBoxNode', N'NameKey') IS NOT NULL
    ALTER TABLE ml.DropBoxNode DROP COLUMN NameKey;
GO

ALTER TABLE ml.DropBoxNode
    ADD NameKey AS
    (
        LEFT(
            UPPER(
                LEFT(Name,
                    CASE WHEN CHARINDEX(N'.', REVERSE(Name)) = 0
                         THEN LEN(Name)
                         ELSE LEN(Name) - CHARINDEX(N'.', REVERSE(Name))
                    END)),
            8)
    ) PERSISTED;
GO

SELECT COUNT(*) AS Total,
       SUM(CASE WHEN NameKey IS NULL THEN 1 ELSE 0 END) AS StillNull,
       SUM(CASE WHEN LEN(NameKey) = 0 THEN 1 ELSE 0 END) AS EmptyKey,
       COUNT(DISTINCT NameKey) AS DistinctKeys
FROM ml.DropBoxNode;
GO

-- The index the similarity join seeks on.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_DropBoxNode_NameKey'
                 AND object_id = OBJECT_ID(N'ml.DropBoxNode'))
    CREATE NONCLUSTERED INDEX IX_DropBoxNode_NameKey
        ON ml.DropBoxNode (NameKey)
        INCLUDE (Name, SizeBytes, WidthPixels, HeightPixels, PathDisplay, ServerModified)
        WHERE IsFolder = 0;
GO
