# Making the sync fast — the optimisation plan

**Status: planned. Three optimisations are BUILT and MEASURED; the rest are specified
here.** Every measurement was taken against the live account and the live
`ml.DropBoxNode` on 2026-11-01.

The question this answers: the walk's cost is not where it was assumed to be. The
server is idle, the plans are clean, and the per-file API call is gone — what is left
is the **number of database round trips**, and the walk's throughput is bounded by them
because every write to `ml.DropBoxNode` is serialised behind one gate.

---

## 1. The baseline, measured

| | measured |
|---|---|
| the server | ~3% of the wall clock; 2.0 MB/min to the data file, 340 KB/min to the log; the buffer cache holds everything |
| the plans | Index Seek on `UQ_DropBoxNode_Path`, Clustered Index Seek/Update on `PK_DropBoxNode`; no scan anywhere |
| the write amplification | ~1 KB written per row updated — the clustered row plus four nonclustered indexes, three of them carrying `PathDisplay` as an INCLUDE |
| the round trips, full catalogue | ~100,000 — one chunk of four round trips per folder, plus one for the folder's status |
| the API | 19,851 folder listings (~5.7 min); the per-file call, 399,357 of them, was ~97% of the old walk's traffic |
| **after the three changes in §2** | the 109-row Umbro/F26 subtree: **1 row written** (was 109), **23 SQL round trips** |
| **the catalogue walk, after the changes** | **~35 folders a second** — 4,252 folders re-listed in about two minutes, with nothing written but the folders' status. The same walk before the changes managed ~29 rows a second and had not finished in 85 minutes. |
| **what the API did about it** | **two rate-limit holds** — the walk listed faster than the account's limit, and each hold stopped the whole walk for **300 seconds** |

---

## 2. What is already done

| | change | measured effect |
|---|---|---|
| **D1** | EF's batch size is 500, not its default of 42 | the walk's chunk fits one command instead of two |
| **D2** | the flush's chunk is 500 rows, the app's own walk's size | four times fewer tracked queries and transactions |
| **D3** | a row that did not change is not written at all | 109 rows written → 1; the 99 files untouched, dimensions intact |
| **D4** | the statement that writes a folder's status writes its `SyncedUtc` too | every folder's timestamp moves on every walk, so `MAX(SyncedUtc)` still reports the last walk and the dashboard's "last synced" is not stale |

D3 changed one meaning: `SyncedUtc` on a FILE row is now "when this file last
changed", not "when the walk last saw it". D4 keeps the other half honest — a
folder's timestamp still moves on every walk, because the statement that writes the
folder's status carries it. The frontier does not depend on any of this: it is the
folders' statuses, and a folder's status is written on every walk.

---

## 3. The backlog

Ordered by what the measurements support. "Expected" is an expectation, not a
measurement — the acceptance table in §7 says which numbers were taken.

### O1 — the flush's per-chunk write costs four round trips

The flush's chunk is one folder's rows: a tracked query to load them, then EF's
`SaveChanges`, which under the retrying execution strategy opens a transaction —
BEGIN, the batched commands, COMMIT. Four round trips per folder, and every folder
has one chunk.

- **Change:** write the chunk's updates as one set-based statement — `ExecuteUpdate`
  is one command, with no change tracker, no transaction and no `OUTPUT` clause, so
  two round trips per chunk instead of four.
- **The obstacle, stated:** the chunk's rows need different values. A file's
  dimensions depend on whether its content hash changed, and the hash comes from the
  listing, not from the row — so one statement has to carry the old hash and the new
  one side by side. That is a join against the chunk's incoming rows, not a constant.
- **Expected:** ~50,000 round trips saved over the catalogue. The rows written do not
  change; only the round trips carrying them do.
- **Risk:** the deadlock handling depends on the transaction. A batch that fails
  without one leaves its rows partly written — which the flush's own comparison
  repairs on the next run, because an unchanged row is not written and a changed one
  is.

### O2 — the enrichment writes one row per round trip

Phase 2 fetched the metadata for 882 changed files in 19.0 seconds and made **1,766
SQL round trips** — two per file, because each enriched row is saved on its own.

- **Change:** collect the enriched rows and write them the way the flush writes them,
  in chunks of 500, under the same write gate.
- **Expected:** 1,766 → ~25 round trips for the same 882 rows.
- **Risk:** a batch that fails leaves its rows `pending`, which is the schema's own
  word for "ask again later" — the next enrichment picks them up. Nothing is lost.

### O3 — three covering indexes carry `PathDisplay`

`IX_Dimensions`, `IX_Name` and `IX_NameKey` each include `PathDisplay nvarchar(4096)`,
so every row update rewrites about 660 bytes of path across three wide index entries.
They are there so the media library's name queries do not need a key lookup.

- **Change:** drop the include from the indexes the walk does not need, if the
  library's queries can afford the key lookup.
- **This one is NOT measured yet, and it must not be touched until it is.** The
  library's read patterns have not been measured, and the trade is the walk's write
  volume against the library's read cost. Measure the library first.

### O4 — the cursor: an unchanged walk still lists every folder

After D3 an unchanged full walk writes one row. It still makes **19,851 listings**,
because the walk cannot tell "unchanged" from "changed" without looking.

- **Change:** use the cursor. `list_folder/continue` against a stored cursor returns
  the changes since that point, and the delta also reports **deletions** — which
  nothing consumes today, so the table keeps rows for files that no longer exist.
- **Expected:** the listing floor of an unchanged walk drops from 19,851 calls to the
  size of the delta.
- **Risk:** the largest build in this list, and the SDK exposes no usable
  `list_folder/get_latest_cursor` route — the delta comes from the cursor a listing
  already returned.

### O5 — the write gate's scope

Every write to `ml.DropBoxNode` goes through one writer at a time. That is what
stops the deadlock, and it is also what bounds the walk's rate: the walk goes as fast
as one write-batch at a time.

- **Change:** a gate per folder rather than one for the table.
- **This one changes a rule, not a parameter.** The deadlock came from the flush and
  the status write of the SAME folder overlapping, so a per-folder gate may be enough
  — but "one writer at a time" is the rule the table is written under, and changing
  it is the user's decision, not a tuning matter.

---

## 4. The logging the test needs

A change shipped with no measurement is shipped unverified, so the counters come
first: an optimisation that cannot be measured is not an optimisation, it is a hope.

### 4.1 What the test must be able to say

| question | the counter that answers it |
|---|---|
| what did the run cost the database? | SQL round trips, and the split by kind |
| what did the run write? | rows written, rows skipped, rows inserted |
| what did the run cost Dropbox? | requests, by kind — the meter counts them all together today |
| where did the wall clock go? | the listing, the writes, and the time spent waiting for the gate |
| did the change do what it claimed? | the same counters before and after, on the same scenario |

### 4.2 The counters

| counter | where it is counted | how it is reported |
|---|---|---|
| **SQL round trips** | an EF `DbCommandInterceptor` on the app's own context factory — one command is one round trip, because EF batches a `SaveChanges` into one command | the run's summary — **built**: `SqlRoundTripCounter`, and the sync's completion log line carries it |
| **round trips by kind** | the same interceptor, tagged by the statement's shape: the tracked query, the flush's update, the folder's status write, the enrichment's write | the run's summary, and one line per write at Debug |
| **rows written / skipped / inserted** | the flush, which already knows: it counts the chunk, the rows EF marked as modified, and the rows it added | the run's summary — **built**: the completion log line says how many rows changed and how many did not |
| **Dropbox requests** | `DropBoxSyncService.RateSnapshot.TotalRequests` | the run's summary |
| **Dropbox requests by kind** | **a gap today**: the enrichment's calls are not metered, so a phase-2 run reports zero requests | the run's summary — to be counted |
| **gate wait** | the write gate's acquisition time, per write | one line per write at Debug; the total in the summary |
| **the level and the frontier** | the walk's own progress | already reported |

The interceptor's cost is one virtual call per command — small, and it is counted in
the same run it measures, so it is honest.

### 4.3 The log's shape

- **A line per phase boundary** — the walk's start, each level's end, the enrichment's
  start and end.
- **A sample line every 30 seconds** — one line carrying every counter: the level, the
  folders and files done, the rows written and skipped, the round trips, the requests,
  the rate, the mean latency, the gate's queue depth. A run that is 85 minutes long
  has to be readable while it runs.
- **A line per write at Debug** — the kind, the rows, the round trips, the elapsed,
  the gate's wait.
- **A summary at the end** — the whole table, in one block, so two runs can be
  compared by reading two blocks.

Where: the app's own log, which is the Windows Application event log. The harness
writes the same lines to its console.

### 4.4 The protocol

Each optimisation is measured on the same scenarios, before and after, and the
comparison is the summary block:

| | scenario | what it exercises |
|---|---|---|
| **S1** | the 109-row Umbro/F26 subtree, unchanged | the write path's floor — nothing should be written |
| **S2** | the same subtree with one file's content changed | one row written, one enriched |
| **S3** | the whole catalogue | the listing floor, the write path at scale, the gate's behaviour under load |
| **S4** | phase 2 alone over the 882 rows marked `pending` | the enrichment's write path |

A change is accepted when **its own counter falls and no other counter rises**. A
round trip saved at the cost of rows written twice is not a saving.

---

## 5. The order

1. **§4's counters**, so every later change is measurable. **Done**: the interceptor
   lives in the app (`SqlRoundTripCounter`), the sync's completion log line carries the
   round trips by kind and the rows that changed against the rows that did not, and
   the harness reads the app's own counter rather than a second one.
2. **O1**, the flush's per-chunk write — the largest measured cost that is not a rule
   change.
3. **O2**, the enrichment's per-row writes — small, self-contained, and it is the
   phase that runs on every sync.
4. **O3**, the indexes — only after the library's read patterns are measured.
5. **O5**, the write gate's scope — the user's decision.
6. **O4**, the cursor — the largest build, and the one that changes what a sync
   *means* rather than how long it takes.

**O4 MOVES UP.** The write path stopped being the bottleneck, and the API became it: the
walk now lists at ~35 folders a second and the account rate-limited it. The cursor is
what cuts the LISTING calls, and the listing is what is now slow — so O4 is worth more
than O1 and O2 together. O1 and O2 still stand, but they are now worth less than they
were, because the round trips they save are not what the walk is waiting for.

**THE RATE LIMIT IS THE NEXT THING TO LOOK AT.** One 429 holds the whole walk for 300
seconds. The limit is per authorization, so a shared hold is right — but the hold's
LENGTH is what turns one refused call into five lost minutes, and the walk refused the
call at only ~35 requests a second, which the account's own ramp probe once carried at
436 a second without a single refusal. Either another consumer shares this
authorization, or the endpoint's limit is lower than the probe's. That is an open
question, and it decides whether the answer is a shorter hold or a narrower walk.

---

## 6. Not in scope

- The sync UI. The service and the measurements come first.
- Changing the walk's width. It is 24, it was measured, and throughput at the width
  is not what is slow.
- A sweep. The walk never deletes, and the cursor's delta is where deletions come
  from.

---

## 7. Acceptance

| # | criterion | how it is checked | state |
|---|---|---|---|
| 1 | EF's batch size is 500 and the flush's chunk is 500 rows | the source, and one command carrying the chunk | **measured** |
| 2 | a row that did not change is not written | the 109-row subtree: 1 row written against 109 | **measured** |
| 3 | the two phases leave the database as the one-phase walk leaves it | `tools/two-phase-service-checks`, 0 differences | **measured** |
| 4 | phase 2 enriches 882 changed rows in one pass | 882 enriched, 0 failed, 0 left pending, 19.0 s | **measured** |
| 5 | the run's cost is reported on both sides | the harness prints SQL round trips and Dropbox requests | **measured** (the enrichment's requests are not counted — see §4.2) |
| 6 | the flush's chunk costs two round trips, not four | the round-trip count by kind, on S1 | not measured |
| 7 | the enrichment's writes are batched | the round-trip count by kind, on S4 | not measured |
| 8 | the library's name queries survive without the `PathDisplay` include | the library's read patterns measured, then the plan compared | not measured |
| 9 | the gate may be per folder | the deadlock reproduced and the fix measured — the shipped harness in the sync skill | not measured |
| 10 | an unchanged walk's listing floor is the delta, not 19,851 calls | the cursor delta against the live account | not measured |
| 11 | the app counts its own round trips, by kind, and says so in its log | the sync's completion log line | **measured** — "The database cost 22 round trip(s) (11 select, 10 update, 0 insert, 0 delete, 1 other), and 0 row(s) changed while 108 did not" |
| 12 | the catalogue walk, after the changes | one catalogue run with the counters | **measured** — ~35 folders/s, 4,252 folders re-listed in about two minutes, nothing written but the folders' status; then two rate-limit holds of 300 s each |
| 13 | the whole catalogue walk, after the changes | one catalogue run with the counters | not measured — the run stopped at the rate limit |

---

## 8. Open questions

1. **May the write gate become a gate per folder?** The deadlock it prevents was
   between the flush and the status write of the same folder, so a per-folder gate may
   be enough — but "one writer at a time" is the rule the table is written under.
2. **What does the UI's "last synced" figure mean now?** `MAX(SyncedUtc)` still
   reports the last walk, because the folders carry it. A per-file "last seen" is
   gone; nothing shows it today, and the tree browser's per-node freshness is worth a
   look before the next walk changes what it says.
3. **Is the media library's read cost worth measuring now?** O3 cannot be decided
   without it, and it is the only item on the list whose answer might be "do nothing".
4. **Is something else using this team's Dropbox authorization?** The walk was refused
   at ~35 requests a second, and the account once carried 436 a second without a
   refusal. If another consumer shares the token, the walk's width has to be set for
   both of them; if not, the endpoint's limit is the one that counts.
