# Testing the app against a sandbox database

**Status:** SPEC — nothing has been built. Every row of §8 is outstanding.

## Glossary

| Term | What it is |
|---|---|
| **the working database** | `apps_ml_dev` — the database the app is configured against. The copy is made from it, and this procedure never writes to it. |
| **the copy** | `util_scratch` — the working database restored under a second name. The app is pointed at it for the test. |
| **the sync's table** | `ml.DropBoxNode` — the one table the app writes. It holds the Dropbox folder and file hierarchy. |
| **the media library** | `ml.media` and the category tables beside it — the library the Finder matches the synced files against. Read-only to the app. |
| **the library root** | the folder `ml.Settings.MediaLibraryLocalPath` names, where the media library's files live on disk. |
| **the branch** | the Dropbox folder path in §4 — the scope of the sync in step 5, and everything beneath it. |
| **the create script** | `src/Utility.Server/Sql/CreateDropBoxNode.sql` — drops and recreates the sync's table. |
| **the repair** | the Finder's recovery action: it downloads a file from Dropbox and moves it into the library root. |
| **the read paths** | the surfaces that read the database without writing to it — the Finder's browse, the candidate matching, Liam's List, the tree, the CSV export and the busy screen. |
| **synthetic** | a tree node that exists only in a path — an ancestor the sync never listed. The tree shows it as "(not synced)". |

## §1 What this is

A procedure for running Utility against a **disposable copy** of the working database,
so that a change to the DropBox sync or the Finder can be exercised without touching
the working one.

The copy is made by backing the working database up and restoring it under a second
name. The app is pointed at the copy by changing one value in the registry, and pointed
back by changing it again.

**The copy carries the whole `ml` schema, not just the sync's table.** The Finder's
candidate lookup joins the media library's file names against the synced Dropbox file
names, so a copy without the media library has nothing for the Finder to match.

## §2 The switch

The app reads its SQL connection string from the registry, and the registry **replaces**
the configuration file: a connection string in `appsettings.json` is not read at all.

| | |
|---|---|
| The value | `HKCU\Software\BbcUtility\Sql` → `ConnectionString` |
| What changes | the `Database=` part only — every other part is copied over unchanged |
| When | at startup, so the app is **restarted** after the change |
| Confirm | the Dashboard's **Objects** figure — see §6.4 |
| Back | the same value with `Database=apps_ml_dev` |

The registry is per-user, so the switch is per-user and reversible by writing one value
back.

**The app must not be running while the value is changed.** A sync in flight holds its
connection to whichever database the value named when it started.

**Test Database does not confirm the switch.** The probe reports *"Connected."* and
nothing more — it never names the database, so it cannot tell the copy from the working
one. It confirms that the connection string is well formed and the server answers.

## §3 The order

**Restore, exercise the read paths, THEN recreate the table, THEN sync the branch.**

The order is the whole point of the procedure, and each step destroys the previous
step's starting condition:

| # | Step | What it needs | What it costs |
|---|---|---|---|
| 1 | Restore the copy | nothing | a backup file and a restore |
| 2 | Point the app at the copy (§2) | step 1 | a restart |
| 3 | Exercise the read paths — the Finder, Liem's List, the tree, the CSV export | steps 1–2, and the **full** copy | nothing |
| 4 | Recreate the sync's table with the create script | step 3 | the copy's synced rows |
| 5 | Sync one branch (§4) | step 4 | Dropbox quota |

**Step 3 comes before step 4 because step 4 empties the table.** Recreating the
sync's table leaves the copy holding one branch's rows while the media library still
holds every row it had — so the Finder's candidate lookup has almost nothing to
match, and step 3 would be testing an empty index and calling it a pass.

**Step 5 comes after step 4 because that is what step 4 is for.** A branch synced into
a table the script built proves the script's table accepts a real sync. The same branch
synced into the restored table would only exercise the upsert.

**The working database is never touched by any of this.**

## §4 The branch

The branch synced in step 5:

```
/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Champion/S27-Champion Final
```

**The path must be exactly what Dropbox reports.** It is selected by navigating the
DropBox files tab to the folder and pressing **Sync From Here**, which takes the path
from the tab's own path box — so the path cannot be mistyped. A hand-typed path with
wrong case or stray whitespace scopes the enrichment to nothing, silently.

**The branch is a scope, not a folder.** The walk covers everything beneath it, and the
enrichment is limited to the files under it by the branch path **plus a trailing
separator** — so a sibling whose name merely starts with the branch's name is not
caught.

**A branch run deletes nothing.** No code deletes rows from the sync's table, so a
branch run cannot remove rows outside the branch.

**The branch root's `ParentId` is `NULL`**, because its parent is above the sync scope.
Every row below it carries its parent's id, because a parent folder is committed
before its children are inserted. The tree service rebuilds the hierarchy from the
path and marks the never-synced ancestors as **synthetic** — that is the design, not a
defect.

**If the branch moves in Dropbox**, the path in this section is stale and the branch
sync will find nothing. Siblings under one parent differ by orders of magnitude, so a
branch's size is measured before it is chosen, never assumed from its parent's name.

## §5 The repair's effect is on the disk

**The repair writes to the disk, and the copy does not stop it.** The repair downloads
a file from Dropbox and moves it into the library root, which `ml.Settings.MediaLibraryLocalPath`
names. A copy of the database leaves that path as it is, so the repair writes real
files into the real library and spends the account's Dropbox quota. **This is the
repair's normal behaviour, not a fault in the copy** — the copy sandboxes the
database, and the repair's effect is on the disk.

So the repair is either left out of the test, or exercised with the library root
redirected in the **copy** at a throwaway folder first. `ml.Settings` is a single
keyless row, so that is a one-row UPDATE, and it changes where the repair writes and
nothing else.

**The app reads from Dropbox and does not write to it**, so the branch sync in step 5
spends quota but changes nothing in Dropbox.

**The app writes to the sync's table and to nothing else.** The rest of the `ml`
schema — the media library, the categories, the settings — is read-only to it.

## §6 The commands

Run against `<server>` with integrated authentication. `sqlcmd` needs `-I` for
`QUOTED_IDENTIFIER ON`, which a persisted computed column requires. The backup and the
restored files go on the server's own filesystem.

### 6.1 Back up and restore

```sql
BACKUP DATABASE apps_ml_dev
TO DISK = '<backup path>\util_scratch.bak'
WITH COPY_ONLY, INIT, STATS = 10;
```

`COPY_ONLY` keeps the backup from disturbing the working database's own backup chain.

```sql
RESTORE DATABASE util_scratch
FROM DISK = '<backup path>\util_scratch.bak'
WITH MOVE 'apps_ml_dev'     TO '<data path>\util_scratch.mdf',
     MOVE 'apps_ml_dev_log' TO '<log path>\util_scratch_log.ldf',
     RECOVERY, STATS = 10;
```

**The `MOVE` clauses are required.** Restoring a second copy on the same server
without them collides with the original's files.

**The restore refuses to overwrite.** It fails if a database named `util_scratch`
already exists, so a leftover copy from an earlier run is dropped first rather than
being silently replaced.

### 6.2 Point the app at the copy

Take the connection string the registry holds, change its `Database=` part to
`util_scratch`, and write it back:

```
reg add "HKCU\Software\BbcUtility\Sql" /v ConnectionString /t REG_SZ /f ^
  /d "<the current string, with Database=util_scratch>"
```

Restart the app.

**The switch is confirmed in §6.4**, before the one step that writes. **Test Database
does not confirm it** — the probe reports only *"Connected."* and never names the
database, so it cannot tell the copy from the working one.

### 6.3 Recreate the sync's table in the copy

```
sqlcmd -S <server> -d util_scratch -I -i src/Utility.Server/Sql/CreateDropBoxNode.sql
```

The copy carries the `ml` schema, so the script's prerequisite is met. The script
drops and recreates the sync's table; nothing else in the copy references it, so the
drop succeeds.

**The `-d` argument is the safety line.** The script drops the table it is pointed at,
so `-d util_scratch` is what keeps it away from the working database. Check it before
pressing enter.

The script emits one warning — the 1700-byte index key limit on the path's unique
constraint — which is inherent to the schema and not a fault.

### 6.4 Sync the branch

**Before pressing the button, confirm the app is inside the copy.** The Dashboard's
**Objects** figure counts the rows in the sync's table of whichever database the app is
connected to. The copy's table was emptied in §6.3, so:

| The figure reads | The app is on | Do this |
|---|---|---|
| nothing — the table is empty | the **copy** | press **Sync From Here** |
| the working database's full count | the **working database** | **stop** — the switch did not take (§2) |

**This check is what stands between the branch sync and the working database.** A sync
that ran against the working database would rewrite the branch's rows in place — updated
rather than inserted — and the branch's enrichment would re-fetch dimensions it already
has.

Then, in the app: the DropBox files tab, the team namespace, navigate to the branch in
§4, press **Sync From Here**.

### 6.5 Clean up

**Put the registry back first.** Write the same connection string with
`Database=apps_ml_dev` and restart the app, so it is never left pointing at a database
that is about to be dropped.

```sql
ALTER DATABASE util_scratch SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE util_scratch;
```

Then delete the backup file. **The copy and the backup file are both dropped at the
end**, so a later session does not go looking for a database that was a test.

## §7 Not in scope

- **A second copy, or a copy per feature.** One copy, used for one run.
- **A test that runs against the working database.** The procedure's whole value is
  that it does not.
- **A restore of any database other than the working one.** `apps_test` on this server
  holds a real e-commerce schema; it is not a disposable copy and is not restored over.
- **The repair, unless the library root is redirected in the copy first (§5).**
- **A full namespace sync.** The branch is the scope.
- **Anything about the schema itself.** Whether the create script still describes the
  table in the working database is a question a metadata diff answers, and it needs no
  data and no copy.

## §8 Acceptance criteria

Nothing has been measured yet. Every row is outstanding.

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The copy is created under a second name, and the working database is untouched by it | the restore succeeds; the working database's row count and file sizes are unchanged | not measured |
| 2 | The copy carries the media library | the copy's media row count matches the working database's | not measured |
| 3 | The app runs against the copy | the Dashboard's **Objects** figure reads the copy's empty table after §6.3, and the working database's count is unchanged | not measured |
| 4 | The switch is confirmed before any write | the Dashboard's **Objects** figure, read before **Sync From Here** is pressed (§6.4) | not measured |
| 5 | The read paths work against the copy | the Finder lists candidates; Liem's List exports a CSV; the tree renders | not measured |
| 6 | The create script runs in the copy | the script completes; the table is present with the script's indexes | not measured |
| 7 | The branch sync writes the branch's rows into the script's table | the row count under the branch, and the branch root's row | not measured |
| 8 | The branch sync's enrichment covers only the branch | the enriched rows are all under the branch; the count elsewhere is unchanged | not measured |
| 9 | The branch sync deletes nothing | the row count outside the branch is unchanged | not measured |
| 10 | The branch's ancestors show as synthetic, and the branch root's parent id is null | the tree pane | not measured |
| 11 | The working database's row count is unchanged after the whole procedure | the count before and after | not measured |
| 12 | The repair was not exercised, or the library root was redirected in the copy first | which of the two §5 arrangements was taken | not measured |
| 13 | The copy and the backup file are gone at the end | the database is absent; the file is absent | not measured |

## §9 Open question

**Should the branch sync's enrichment run over the whole branch, or over one folder
inside it?**

Against the script's fresh table, every file under the branch needs its dimensions
fetched, so the enrichment spends one metadata call per file — real quota on the
account, and minutes of wall clock.

*Recommendation:* the whole branch. It exercises the walk across the branch's levels
and the enrichment at a scale worth measuring, and the quota is a fraction of the
account's allowance. The cheaper variant — one folder inside the branch — proves the
same code path in seconds, and is the right choice if the point is to check that the
script's table accepts a sync at all.
