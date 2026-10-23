-- =============================================================================
--  ResolveDropBoxNodeParents
--
--  Repairs ml.DropBoxNode.ParentId after a bulk load.
--
--  WHY THIS EXISTS
--    Resolving a parent inside the same INSERT that inserts the child is a
--    trap. SQL Server gives no ordering guarantee across a join, so when you
--    INSERT..SELECT from a staging table and try to match each row to its
--    parent row, the parent often is not in the table yet. The insert reports
--    success and every ParentId silently lands as NULL - a table that looks
--    populated but has no hierarchy at all.
--
--    Run the load first, then run this. It is idempotent and order-independent:
--    it derives the parent from the path, so it does not care what order the
--    rows were inserted in.
--
--  HOW IT WORKS
--    A node's parent path is its own path minus its own Name segment:
--      /Team/LightBoxPics/Virtual Catalog  ->  /Team/LightBoxPics
--    For the namespace root that computes to the empty string, which matches no
--    row, so the root correctly keeps ParentId NULL.
--
--    Requires QUOTED_IDENTIFIER ON (sqlcmd -I).
--
--  USAGE
--    sqlcmd -S <server> -d apps_ml_dev -E -I -i Sql/ResolveDropBoxNodeParents.sql
-- =============================================================================

SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
GO

UPDATE c
SET c.ParentId = p.Id
FROM ml.DropBoxNode AS c
INNER JOIN ml.DropBoxNode AS p
        ON p.NamespaceId  = c.NamespaceId
       AND p.PathDisplay = LEFT(c.PathDisplay, LEN(c.PathDisplay) - LEN(c.Name) - 1);
GO

-- Verification: exactly one root (the configured start folder) per namespace,
-- and no orphans. Both should report 0 problems for a well-formed load.
SELECT
    (SELECT COUNT(*) FROM ml.DropBoxNode) AS total_rows,
    (SELECT COUNT(DISTINCT NamespaceId) FROM ml.DropBoxNode WHERE ParentId IS NULL) AS roots_should_be_1,
    (SELECT COUNT(*) FROM ml.DropBoxNode c
      WHERE c.ParentId IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM ml.DropBoxNode p WHERE p.Id = c.ParentId)) AS orphan_parents_should_be_0,
    (SELECT COUNT(*) FROM ml.DropBoxNode
      WHERE ParentId IS NULL
        AND EXISTS (SELECT 1 FROM ml.DropBoxNode ch WHERE ch.ParentId = ml.DropBoxNode.Id)
          AND EXISTS (SELECT 1 FROM ml.DropBoxNode g
                      WHERE g.ParentId IS NULL AND g.Id <> ml.DropBoxNode.Id)) AS extra_roots_should_be_0;
GO