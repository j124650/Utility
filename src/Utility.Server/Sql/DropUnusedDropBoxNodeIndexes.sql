--  Drops the two indexes on ml.DropBoxNode that nothing reads.
--
--  WHY
--
--  IX_DropBoxNode_NameKey and IX_DropBoxNode_Dimensions are maintained by every row
--  that changes and are read by NOTHING. SQL Server's own counters on the live table
--  (2026-11-01):
--
--      index                       seeks  lookups  scans  updates
--      IX_DropBoxNode_NameKey          0        0      0     4,649
--      IX_DropBoxNode_Dimensions       0        0      0     3,384
--      IX_DropBoxNode_Name             1        0     54     4,649
--
--  IX_DropBoxNode_Name is left alone: it is read, so its include earns its keep.
--
--  They were built for lookups by name and by dimensions. The media library queries a
--  DIFFERENT table - ml.media, with Path, FileName, Size and DateAdded - so it was
--  never their consumer, and no other query reads them.
--
--  WHAT THEY COST
--
--  Every row UPDATE maintains them, and each carries PathDisplay nvarchar(4096) as an
--  INCLUDE - about 220 bytes of path per index entry. Three such indexes are why a row
--  update wrote about 1 KB. With them gone, the write is the clustered row and the one
--  index whose key changed.
--
--  REVERSIBLE
--
--  The exact indexes are recreated at the end of this script. To put them back, run
--  the CREATE statements alone.

SET NOCOUNT ON;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DropBoxNode_NameKey' AND object_id = OBJECT_ID('ml.DropBoxNode'))
BEGIN
    DROP INDEX IX_DropBoxNode_NameKey ON ml.DropBoxNode;
    PRINT 'dropped IX_DropBoxNode_NameKey';
END;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DropBoxNode_Dimensions' AND object_id = OBJECT_ID('ml.DropBoxNode'))
BEGIN
    DROP INDEX IX_DropBoxNode_Dimensions ON ml.DropBoxNode;
    PRINT 'dropped IX_DropBoxNode_Dimensions';
END;

-- The indexes as they were, for the way back.
--
-- CREATE NONCLUSTERED INDEX IX_DropBoxNode_NameKey
--     ON ml.DropBoxNode (NameKey)
--     INCLUDE (Name, SizeBytes, WidthPixels, HeightPixels, PathDisplay, ServerModified);
--
-- CREATE NONCLUSTERED INDEX IX_DropBoxNode_Dimensions
--     ON ml.DropBoxNode (NamespaceId, WidthPixels, HeightPixels)
--     INCLUDE (PathDisplay, SizeBytes);

-- What is left, and what it is read for.
--
-- SELECT i.name AS index_name, s.user_seeks, s.user_lookups, s.user_scans, s.user_updates
-- FROM sys.dm_db_index_usage_stats s
-- JOIN sys.indexes i ON i.object_id = s.object_id AND i.index_id = s.index_id
-- WHERE s.database_id = DB_ID('apps_ml_dev')
-- AND i.object_id = OBJECT_ID('ml.DropBoxNode')
-- ORDER BY s.user_updates DESC;
