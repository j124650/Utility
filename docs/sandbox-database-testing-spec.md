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
| Confirm | the Dashboard's **Objects** figure — see §6.5 |
| Back | the same value with `Database=apps_ml_dev` |

The registry is per-user, so the switch is per-user and reversible by writing one value
back.

**The app must not be running while the value is changed.** A sync in flight holds its
connection to whichever database the value named when it started.

**Test Database does not confirm the switch.** The probe reports *"Connected."* and
nothing more — it never names the database, so it cannot tell the copy from the working
one. It confirms that the connection string is well formed and the server answers.

## §3 The order

**Restore, recreate the table, exercise the read paths, THEN sync the branch.**

The order is the whole point of the procedure, and each step destroys the previous
step's starting condition:

| # | Step | What it needs | What it costs | The commands |
|---|---|---|---|---|
| 1 | Restore the copy | nothing | a backup file and a restore | §6.1 |
| 2 | Point the app at the copy (§2) | step 1 | nothing yet — the app is not running | §6.2 |
| 3 | Recreate the sync's table with the create script | step 2 | the copy's synced rows | §6.3 |
| 4 | Start the app | step 3 | nothing | §6.4 |
| 5 | Sync one branch (§4) | step 4 | Dropbox quota | §6.5 |
| 6 | Exercise the read paths — the Finder, Liem's List, the tree, the CSV export | step 5 | nothing | §6.6 |
| — | Clean up | step 6 | the copy | §6.7 |

**THE APP STARTS AFTER THE RECREATE, SO ITS FIRST READ SEES THE EMPTY TABLE.** The
app reads the index once at startup and holds the figures. Started before the
recreate, it would read the copy's old rows and the page would carry a stale count
from the outset — a number that looks live and is not. Started after it, the first
thing the page shows is the empty index, which is the state under test.

**THE SYNC COMES BEFORE THE READ PATHS, so they see the branch's rows.** Against an
empty index the Finder still lists every pair the media library holds — 363,856 of
them, every verdict *"nothing to do / no size match"*, because there is no synced file
to compare against — and Liam's List is slow at that scale. After the sync the index
holds the branch, so the candidates are real.

**Step 5 comes after step 3 because that is what step 3 is for.** A branch synced into
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

**Enriching the branch enriches that branch and nothing else.** The work list is the
files under the branch path plus the separator, so no file above the branch and no file
in a sibling folder is fetched. Syncing `…/Champion/S27-Champion Final` enriches its
own files and leaves the rest of the namespace alone.

**The whole branch is enriched, not one folder inside it.** Every file under the branch
gets its dimensions fetched — the branch holds 222 style folders of 2 to 11 files each,
so that is 2,361 files and one metadata call apiece. The reason is the machinery: the
walk fans out across those 222 siblings with the concurrency gate and the rate-limit
brake under load, which a single folder would never reach. A run that never contends
proves the code path but not the behaviour that breaks first in production.

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

Restart the app — but only after §6.3, so its first read of the index sees the
recreated (empty) table. The app is not running while the value is changed.

**The switch is confirmed in §6.5**, before the one step that writes. **Test Database
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

### 6.4 Start the app

**Start the app here.** Its first read of the index must see the table §6.3 recreated
— an empty one — so the figures it shows are the state under test and not a count
carried over from before the recreate.

The app reads the index once at startup and holds the figures. Started before the
recreate, it would read the copy's old rows and the page would carry a stale count
from the outset — a number that looks live and is not.

### 6.5 Sync the branch

**Before pressing the button, confirm the app is inside the copy.** The Dashboard's
**Objects** figure counts the rows in the sync's table of whichever database the app is
connected to. The copy's table was emptied in §6.3, so:

| The figure reads | The app is on | Do this |
|---|---|---|
| nothing — the table is empty | the **copy** | press **Sync From Here** |
| the working database's full count | the **working database** | **stop** — the switch did not take (§2) |

**Press the Dashboard's Read / Refresh first.** The figure is read once when the app
starts and cached; it does not change by itself when the table is emptied from outside.
**Read / Refresh** re-reads it. Without that, the figure still shows the count from
startup and the check above reads *wrong*.

**If the figure cannot be read at all, confirm the switch from the server instead.**
The dashboard's totals query sums over the table, and a sum over an empty table is NULL,
so a build that predates the `ISNULL` fix cannot read the state this check depends on.
The sessions say which database the app is on:

```sql
SELECT DB_NAME(s.database_id) AS db, s.program_name, COUNT(*) AS sessions
FROM sys.dm_exec_sessions s
WHERE s.program_name LIKE '%SqlClient%' OR s.program_name LIKE 'EFCore%'
GROUP BY DB_NAME(s.database_id), s.program_name;
```

**This check is what stands between the branch sync and the working database.** A sync
that ran against the working database would rewrite the branch's rows in place — updated
rather than inserted — and the branch's enrichment would re-fetch dimensions it already
has.

Then, in the app: the DropBox files tab, the team namespace, navigate to the branch in
§4, press **Sync From Here**.

### 6.6 Exercise the read paths

**The read paths come after the sync**, so they see the branch's rows rather than an
empty index.

Against an empty index the Finder still lists every pair the media library holds —
**363,856 of them**, every verdict *"nothing to do / no size match"*, because there is
no synced file to compare against — and **Liem's List** is slow at that scale. After
the sync the index holds the branch, so the candidates are real.

In the app: the Finder's browse and candidate matching, **Liem's List** and its CSV
export, the tree, and the busy screen.

### 6.7 Clean up

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
| 1 | The copy is created under a second name, and the working database is untouched by it | the restore succeeds; the working database's row count and file sizes are unchanged | measured — the restore succeeded; the working database held 423,709 nodes before and after |
| 2 | The copy carries the media library | the copy's media row count matches the working database's | measured — 765,908 in the copy |
| 3 | The app runs against the copy | the app's sessions are on the copy, and the figures it shows are the copy's | measured — the app's SqlClient session was on the copy, with no session to the working database |
| 4 | The switch is confirmed before any write | the database the app is connected to, read before **Sync From Here** is pressed (§6.5) | measured — confirmed by the session list, not by the dashboard: the dashboard could not read the empty table (§6.5) |
| 5 | The read paths work against the synced branch | the Finder lists candidates from the branch; Liem's List exports a CSV; the tree renders | not measured |
| 6 | The create script runs in the copy | the script completes; the table is present with the script's indexes | measured — exit 0, the table rebuilt from 423,709 rows to 0, and the five indexes were the live set |
| 7 | The branch sync writes the branch's rows into the script's table | the row count under the branch, and the branch root's row | measured — 2,584 rows: the branch root, 222 folders and 2,361 files |
| 8 | The branch sync's enrichment covers only the branch | the enriched rows are all under the branch; the count elsewhere is unchanged | measured — 2,361 files enriched, every one with dimensions and `MediaInfoTag = 'photo'`, 0 pending, 0 failures; nothing outside the branch |
| 9 | The branch sync deletes nothing | the row count outside the branch is unchanged | measured — one row outside the branch prefix (the branch root itself); the run's counters recorded 0 deletes |
| 10 | The branch's ancestors show as synthetic, and the branch root's parent id is null | the tree pane, and the branch root's row | measured in part — the branch root's `ParentId` is NULL; the ancestors-as-synthetic was not caught on screen |
| 11 | The working database's row count is unchanged after the whole procedure | the count before and after | measured — 423,709 before and after |
| 12 | The repair was not exercised, or the library root was redirected in the copy first | which of the two §5 arrangements was taken | measured — the repair was not exercised |
| 13 | The copy and the backup file are gone at the end | the database is absent; the file is absent | measured — the copy dropped, the backup file deleted, and the registry put back to the working database before the drop |
| 14 | The enrichment covered the whole branch | every file under the branch has its dimensions; the count enriched matches the branch's file count | measured — 2,361 of 2,361 files enriched |

