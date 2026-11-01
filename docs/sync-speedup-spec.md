# Making the sync faster — preliminary spec

**Status: the two-phase service is BUILT and PROVEN against the live database. The UI is
NOT built.** The measurements below were taken against the live account and the live
`ml.DropBoxNode` on 2026-11-01.

The question this answers: the DropBox files tab's **Build tree JSON** lists a folder tree
in seconds. The sync walk takes a very long time. Is the tree browser a *cheaper
operation*, or could it replace the sync's listing?

**The answer: it is a cheaper operation, and the difference is one field — the
dimensions.** This spec says what was measured, and what the two candidates for a faster
sync are.

---

## 1. What was measured

**The subtree:** `/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Umbro/F26`
(namespace `15193064371`, "Images-Catalogs").

**Two runs of the same code:**

| | run | result |
|---|---|---|
| The app's **Build tree JSON** button | DropBox files tab, tree depth 2 | **9 folders, 99 files, 142.82 MB in 1,633 ms** |
| A harness calling the same `IDropBoxDataService.GetFolderTreeAsync` | `tools/tree-compare` | 9 folders, 99 files in 2,426 ms (cold client) |

The two JSON files are **byte-identical apart from `startedUtc` and `durationMs`** — the
harness measures exactly what the button measures.

### The method that produces the JSON

`DropBoxViewModel.BuildTreeAsync` → `IDropBoxDataService.GetFolderTreeAsync(path,
namespaceId, depth: 24, includeMediaInfo: false, ct)`:

- **one client** for the whole walk, scoped to the team namespace (`WithPathRoot`);
- **one `SemaphoreSlim(24)` gate** across the entire recursion;
- a recursive `BuildLevelAsync` that calls **`files/list_folder` with `recursive: false`**
  per folder, following every cursor page. Files become leaves; folders recurse
  concurrently. **At the depth cap a folder is emitted UNEXPANDED**, so depth 2 reaches
  two levels below the root path;
- `includeMediaInfo: false` — the metadata cost is deliberately off.

**API calls for this subtree: 9 folder listings (plus cursor pages), 0 per-file calls.**

---

## 2. What the JSON carries, and what it does not

A file node in the JSON carries: `name`, `pathDisplay`, `isFolder`, `sizeBytes`,
`sizeDisplay`, `clientModified`, `serverModified`, `modifiedDisplay`, `id`, `rev`,
`contentHash`, `isDownloadable`, `childCount`, `totalSizeBytes`, `totalFileCount`,
`children`.

**The one field the JSON does NOT carry is the dimensions** (`WidthPixels`,
`HeightPixels`) and the media kind (`MediaInfoTag`). `files/list_folder` does not deliver
media info.

Everything else the row carries is in the JSON already.

### Verified against the database

`ml.DropBoxNode` holds **109 rows** under the same path (10 folders, 99 files),
149,755,768 bytes.

| check | result |
|---|---|
| files in the tree **and** in the database | **99 / 99** |
| in the tree but **not** in the database | **0** |
| size differs | **0** |
| rev differs | **0** |
| content hash differs | **0** |
| content hash missing in the database | **0** |
| server modified differs | **0** |
| database row HAS dimensions | **99 / 99** |
| folder `SyncStatus` | **all 10 are `Ok`** |

**The database is exactly right for this subtree, and the JSON is missing only the
dimensions.**

---

## 3. Why the sync is slow: the per-file call buys ONE field

`DropBoxSyncService.EnrichFileAsync` calls `dropBox.GetFileInfoAsync(row.PathDisplay, ns,
includeMediaInfo: true, ct)` — a `get_metadata` per file — and sets **only**
`WidthPixels`, `HeightPixels` and the media kind. Everything else in the row came from
the listing.

**Measured: 60 files, one `get_metadata` each, sequential — 25.0 s, i.e. 417 ms per
call.** All 60 returned dimensions.

Scaled to the whole catalog at the walk's width of 24:

| | calls | time at width 24 |
|---|---|---|
| folder listings | 19,851 | **~5.7 min** |
| per-file `get_metadata` | 399,357 | **~1.9 hours** |

**~97% of a full sync is the per-file metadata calls.** The skill's independently measured
throughput (60.8 req/s at width 24) agrees: 399,357 / 60.8 ≈ 1.8 hours.

### Does a LISTING give the media info? No — measured

The whole design rests on this, so it was measured rather than read. The same 99 files,
four ways:

| how | elapsed | with dimensions |
|---|---|---|
| `list_folder`, `include_media_info = false` | 4.27 s (9 calls) | **0** |
| `list_folder`, `include_media_info = true` | 4.33 s (9 calls) | **0** |
| `list_folder` **recursive**, `include_media_info = false` | 4.77 s (1 stream) | **0** |
| `list_folder` **recursive**, `include_media_info = true` | 4.11 s (1 stream) | **0** |
| `get_metadata`, one per file | 35.53 s (99 calls) | **99** |

**`include_media_info` IS INERT FOR `files/list_folder`.** It changes neither the data
nor the cost. The API accepts the parameter and the SDK models `MediaInfo` as a
Pending/Metadata union, but a listing never fills it in.

**SO `AlterAddImageDimensions.sql`'s COST WARNING IS WRONG ON THE COST** — asking for the
flag costs nothing — **and right on the outcome**: it does not give the dimensions
either way. Its "an attempt was made and abandoned as too slow" was measuring something
else.

**AND A RECURSIVE LISTING DOES NOT DELIVER THEM EITHER.** So candidate B is not the
answer to the enrichment problem; it only collapses the listing calls, which are ~5% of
the traffic.

**THE PER-FILE `get_metadata` IS THEREFORE UNAVOIDABLE for the dimensions. The only lever
is not making the call when the content has not changed** — the hash skip.

---

## 4. Retries: the tree browser has none

**The tree walk does not retry anything.** `ListLevelAsync` calls `ListFolderAsync` and
`ListFolderContinueAsync` with no `try`/`catch` and no retry. The SDK's
`maxRetriesOnError: 3` covers 500/503 only — **a 429 raises `RateLimitException` and is
not retried.** So one rate limit, or one transient error, **kills the whole tree build**
and everything fetched so far is lost; `BuildTreeAsync` only records the error in the
status line.

The tree walk's listing path does not even call `Translate(ex)`, so the raw SDK exception
surfaces rather than the app's `DropBoxRateLimitedException`. (The download path does
translate it.)

**The sync walk is the opposite:** a shared `DropboxRateLimitBrake` (the limit is per
authorization, not per request), `MaxRateLimitAttempts = 5`, a backoff, and — the part
that matters — an **outcome record**: a 429 is `Failed` (a property of the *moment*), so
the next run retries it. `Skipped` is reserved for a property of the *path* (404, no
access).

**So the tree browser is fast and brittle; the sync is slow and resumable.** That is a
second reason the tree browser cannot simply replace the sync.

---

## 5. THE DESIGN — two phases, and it is verified

**THE SYNC BECOMES TWO PHASES, AND THE RECOVERY LOG'S WORK LIST IS THE HANDOFF
BETWEEN THEM.**

### Phase 1 — LIST

**Walk the tree with the non-recursive `files/list_folder`, and upsert every row.** No
per-file call at all. The rule that decides what happens to a file's dimensions:

```
the listing's contentHash  ==  the stored row's ContentHash
    -> the content is the same, so the dimensions are still true: KEEP them.
the listing's contentHash  !=  the stored row's ContentHash
    -> the content changed, so the dimensions may be stale: CLEAR them and mark the
       row 'pending', for phase 2 to re-fetch.
```

**The comparison happens BEFORE the hash is overwritten.** In one SQL statement the
`SET` expressions see the old row, so `CASE WHEN ContentHash = @ch THEN MediaInfoTag ELSE
'pending' END` compares the OLD hash with the new one. That is the whole design, and it
needs NO new column.

**The marking is SELF-HEALING.** A row marked 'pending' by a run whose enrichment failed
keeps its tag on the next listing (its hash now matches), so phase 2 picks it up again.
A row whose content changes twice is marked again. Nothing is stranded.

### Phase 2 — ENRICH

**Fetch the metadata for the rows marked 'pending', and only those.** The work list is
exactly `WHERE IsFolder = 0 AND MediaInfoTag = 'pending'` — a set the schema already
defines ('pending' already means "the dimensions may be available on a re-read").

**Phase 2 FILLS the dimensions and never clears them.** If the answer comes back empty,
the row keeps whatever it had; only phase 1 may clear a dimension, and only because the
content changed.

### The proof — measured against the live database

`tools/sync-two-phase/` runs both phases against `ml.DropBoxNode`, for the subtree
`/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Umbro/F26` (10 folders, 99
files), and compares every column that means something before and after.

**The unchanged branch:**

```
PHASE 1: LIST      10 list_folder calls, 108 rows written, 99 kept unchanged, 0 marked 'pending'
PHASE 2: ENRICH     0 get_metadata calls
VERIFY:             the rows are IDENTICAL on every column that means something

one-phase walk (today) : 10 listings + 99 get_metadata = 109 calls
two-phase              : 10 listings +  0 get_metadata =  10 calls
saved                  : 99 of 99 get_metadata calls (100.0%)
```

**The changed branch** — one row's stored content hash is corrupted, so the listing says
it changed:

```
PHASE 1: LIST      10 list_folder calls, 99 kept unchanged, 1 marked 'pending'
                   the changed file is now: tag 'pending', dimensions cleared
PHASE 2: ENRICH     1 get_metadata call
VERIFY:             the rows are IDENTICAL on every column that means something

saved                  : 98 of 99 get_metadata calls (99.0%)
```

**So the two phases leave the database in exactly the state the one-phase walk leaves it
in, for a tenth of the API calls.**

### The trap: `include_media_info`

**`get_metadata` WITHOUT `includeMediaInfo: true` RETURNS NO MEDIA INFO AT ALL** — the
dimensions read as NULL, and the media kind reads as NULL. Measured: three sibling files
that the database holds as `4160 × 4160 / 'photo'` return nothing at all without the
flag, and `photo / 4160 × 4160` with it.

**An enrichment built on such a call would silently clear every dimension it touched.**
The flag is not optional in phase 2.

(The listing has the same trap, but it is harmless there: `files/list_folder` ignores
`include_media_info` entirely — see §3.)

### What the listing methods can and cannot do

| method | in the SDK? | what it does |
|---|---|---|
| `ListFolderAsync` | yes | one folder, one page; `recursive: false` |
| `ListFolderContinueAsync` | yes | the next page **or the delta** |
| `ListFolderGetLatestCursor` | **no route method** | the arg/result TYPES exist; the endpoint is not exposed |
| `ListFolderLongpoll` | **no route method** | the same |

**THE CURSOR WORKS WITHOUT THE MISSING ENDPOINTS.** A listing's own cursor, fed back to
`list_folder/continue`, returns **the changes since that point** — measured: 0 entries
right now, because nothing has changed. So a stored cursor per folder is enough to ask
Dropbox "what changed since?" without re-listing, and the delta also reports
**DELETIONS**, which the current walk never learns.

**PHASE 2, ALONE, ON A CHANGED CATALOGUE.** A run of phase 1 over the catalogue root
found 882 files whose content had changed. Enriching those 882 rows and nothing else took
**19.0 seconds** — 882 Dropbox metadata calls at the width of 24, **1,766 SQL round
trips**, 0 failed, 0 left pending, and the table's dimensions back to exactly the count
it started with (359,280). The one-phase walk would have made 399,357 metadata calls for
the same table; phase 2 made 882.

**THE ACCOUNT IS EDITED WHILE THE APP IS NOT LOOKING, and the walk is built for that.**
A full run from the catalogue root found ~1,460 rows that had never been synced, and
882 files whose content hash no longer matched — spread over six brands, mostly in new
seasons whose images had been re-exported. The design handled both without being asked:
the new rows were written, and the 882 changed files had their dimensions cleared and
were marked `'pending'`, so the enrichment phase fetched exactly those 882.

**BUT THE WALK NEVER DELETES.** A file removed in Dropbox keeps its row, so the table
grows to hold files that no longer exist. The delta reports deletions and nothing
consumes them yet — that is the gap the cursor closes, and the strongest argument for
adding it.

**A RECURSIVE LISTING IS NOT THE ANSWER.** It does not deliver the dimensions either
(§3), it asks Dropbox to do all the per-file work in one call, and a recursive walk is
exactly what runs into the throttle. The non-recursive walk spreads that work over
19,851 small calls, and the throttle is rare there.

### The old candidate, kept for the record

**A FILE'S DIMENSIONS ARE A PROPERTY OF ITS CONTENT.** If the listing's `contentHash`
equals the stored row's `ContentHash`, the content is the same, so the dimensions cannot
have changed, so **the per-file `get_metadata` call is unnecessary.**

**Measured on the subtree: 99 of 99 files' content hashes match the stored rows, and 99 of
99 stored rows already have the dimensions. So 99 of 99 files need no `get_metadata` call
at all.**

The rule the sync would apply, per file, after the listing:

```
if the stored row exists
   and its ContentHash equals the listing's ContentHash
   and its dimensions are already known (or the file is known not to be an image)
then the file's metadata is already correct: do not call get_metadata.
```

**The rule's exception, which must be honoured:** a stored row whose dimensions were never
fetched (`MediaInfoTag` = `pending`, or the row predates enrichment) is INCOMPLETE, and a
hash match does not make it complete. Those files still need the call.

### What it would save

| | calls | time at width 24 |
|---|---|---|
| today, a run that re-lists every folder | ~419,000 | **~1.9 hours** |
| with the hash skip | ~19,851 | **~5.7 min** |

**But the honest caveat:** the walk today does NOT re-list folders marked `Ok` — it only
lists the frontier. So this optimisation pays only when a run re-lists folders it has
listed before: **the frontier after an interruption**, or **a new change-detecting
re-sync**. The first build of a namespace cannot benefit — there are no stored rows to
compare against.

---

## 6. Candidate B — a recursive listing (not measured; a design change)

`files/list_folder` with `recursive: true` returns a whole subtree through one cursor
stream, instead of one call per folder. For the catalogue that would replace 19,851
listing calls with a few hundred cursor pages.

**It is not a drop-in.** The sync's frontier model is PER FOLDER: a folder's `SyncStatus`
is `Ok` only when it was listed completely, and a folder with a NULL status is a resume
point. A single recursive stream has no per-folder boundary to record, so a failure
part-way through leaves "everything from here on" undiscussed rather than "these folders
need another look".

**It needs measuring and a redesign of the frontier before it can be considered.** It is
listed here so the option is not lost, not because it is ready.

---

## 7. What must NOT change

The speed-up must not cost any of these, because they are what makes the sync *correct*:

- **the status semantics** — `Ok` / `Skipped` / `Failed` / `Partial` / NULL;
- **the frontier** — the NULL-status folders are the resume points, and "re-sync the rows
  that failed" is the wrong model;
- **one writer at a time** — parallel writers to `ml.DropBoxNode` deadlock (error 1205),
  and the victim's folder loses its whole subtree permanently;
- **the `PathHash` seek** — `PathLower` cannot be seeked on (1700-byte index key limit),
  and a scan of the namespace costs 273 ms against 3 ms for the seek;
- **one cached client per namespace** — a client per request sends half the walk's
  traffic to the token endpoint.

---

## 7. What a walk writes

**A ROW THAT DID NOT CHANGE IS NOT WRITTEN.** The flush used to set `SyncedUtc` and
`SyncRunId` on every row it touched, so every walk rewrote every row in the table to
say it had seen it: one write per row, for a run whose real work is the handful of
files that actually changed. The timestamps now go on only when something about the row
moved, and EF - which already compares every property with the value the row had - is
what decides that.

What this means, plainly:

- `SyncedUtc` on a FILE row now means "when this file last changed", not "when the walk
  last saw it".
- A walk that found nothing writes nothing. Measured on the 109-row Umbro/F26 subtree
  after the change: **1 row written** (the walk's own start folder, which carries the
  run id) against **109 before**, and **23 SQL round trips** for the whole subtree.
- The FRONTIER does not depend on it. The frontier is the FOLDERS' statuses, and a
  folder is always written - the folder that starts the walk is written by the upsert,
  and every other folder's row is written when its status or its content changes.
- `MAX(SyncedUtc)` still reports the last walk, because the folders carry it.

**EF'S BATCH SIZE IS 500, NOT ITS DEFAULT OF 42.** The walk's chunk is five hundred
rows, and the common update - the two columns that say the walk saw the row - costs
three parameters each, so five hundred of them fit inside SQL Server's 2,100-parameter
limit in one command. EF splits the batch by itself when a wider update needs more
parameters than that.

## 8. What is built

**`DropBoxSyncTwoPhaseService`** (`src/Utility.Server/DropBoxSync/`), behind
`IDropBoxSyncTwoPhaseService`:

| method | what it does |
|---|---|
| `SyncFrom` | **the whole thing** from a path: phase 1, then phase 2 |
| `SyncStructureFrom` | phase 1 — the walk with `enrichFileMetadata: false`, with a depth limit |
| `ContinueSyncStructureFrom` | phase 1, continued from the frontier |
| `EnrichAsync` | phase 2 — the rows marked `'pending'`, and only those |

**It REUSES the walk rather than duplicating it.** One width (`WalkWidth = 24`, now
`internal`, so both layers read the same number), the same `AdaptiveConcurrencyGate`, the
same shared `DropboxRateLimitBrake` with its 429 retry, the same `WorkerPool`.

**`DropBoxWriteGate` is a new shared service.** "One writer at a time" has to mean one
writer for the TABLE, not one per operation: the walk and the enrichment pass both write
to `ml.DropBoxNode`, and two gates would let them deadlock against each other. The
deadlock-retry logic moved into it.

**The flush's dimension rule changed**, in `DropBoxSyncService.FlushAsync`:

- the content has NOT changed → the stored dimensions are still true → **kept**, even when
  the read carried no media info;
- the content HAS changed → they describe the old content → **cleared**, and the row
  marked `'pending'`.

The hash is read BEFORE the update, so the comparison is against the old value. This also
closes a latent hazard in the existing sync: a `get_metadata` that came back empty used
to wipe a known dimension.

**`tools/two-phase-service-checks/`** runs the real service, resolved from the app's own
DI, against the live account and the live table: 15/15 checks passed.

## 9. Acceptance

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The listing carries every field the row carries except the dimensions | compare the JSON against the row, field by field | **measured** (99/99) |
| 2 | A file whose content hash is unchanged needs no `get_metadata` call | compare the listing's hash with the stored hash | **measured** (99/99 skippable) |
| 3 | The per-file call costs 417 ms and buys only the dimensions | time 60 `get_metadata` calls | **measured** |
| 4 | The tree browser has no retry and loses the whole build on a 429 | read the listing path | **measured** (by reading) |
| 5 | A run with the two phases makes no `get_metadata` call for an unchanged file | `tools/sync-two-phase --write` | **measured** (0 of 99) |
| 6 | A file whose content changed is marked 'pending' and re-enriched | `tools/sync-two-phase --write --simulate-change` | **measured** (1 of 99) |
| 7 | The two phases leave the database in the state the one-phase walk leaves it in | the same harness, comparing every column | **measured** (identical) |
| 8 | The marking is self-healing: a row left 'pending' by a failed run is picked up again | the same harness, run twice | **measured** |
| 9 | `get_metadata` is called WITH `includeMediaInfo: true` | probe the flag on and off | **measured** |
| 10 | The status semantics, the frontier, the single writer and the PathHash seek are unchanged | the existing harness plus a diff of the walk | **not built** |
| 11 | A change-detecting re-sync notices a changed file and updates it | a harness that changes a file and re-syncs | **not built** |
| 12 | A stored cursor per folder reports the changes since, deletions included | the cursor probe | **not built** (the mechanism measured) |
| 13 | The two-phase service runs both phases, and leaves the database as the one-phase walk leaves it | `tools/two-phase-service-checks` | **built** (15/15) |
| 14 | The walk and the enrichment pass share ONE write gate | the shared `DropBoxWriteGate` | **built** |
| 15 | The UI for the two phases | — | **not built** |
