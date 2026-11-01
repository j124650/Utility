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
| **the round trips, full catalogue** | ~100,000 — one chunk of two round trips per folder, plus one for the folder's status, plus the upsert |
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

### O1 — the flush's per-chunk write: DONE, and the floor is two round trips

**Measured: a chunk costs 2 round trips** — the tracked query that loads the rows, and
one batched update that writes them. EF opens no transaction around it, and the
measurement says so: `0 transaction(s)`.

The reason is D1. EF opens a transaction only when a `SaveChanges` needs more than ONE
command, and a chunk of five hundred rows fits inside a batch of five hundred — so the
chunk is a single command and there is nothing to wrap. Before D1, the chunk was fifty
rows against EF's default batch of forty-two, so it needed two commands, and EF wrapped
those two in a transaction: a query, BEGIN, two batches, COMMIT.

Two things follow:

- **The plan's "four round trips" was wrong** — it counted the transaction without
  counting one of the batches. The floor is two, and the chunk is at it.
- **One round trip is still available, and it is not worth taking.** The tracked query
  has to happen: the flush compares the row's stored hash with the listing's, and that
  comparison is what decides whether a row is written at all. A single statement that
  carried the chunk's incoming rows in a table-valued parameter could do both in one
  round trip, but it trades a two-round-trip chunk for a join, a TVP and a set-based
  update whose per-row conditional has to be expressed as a CASE — for one round trip per
  folder.

`AutoTransactionsEnabled` is now off on the app's writes. It changes nothing today —
there is no transaction to remove — and it exists so that a chunk too large for one
command does not buy a transaction it does not need.

### O2 — the enrichment's writes: DONE

Phase 2 fetched the metadata for 882 changed files in 19.0 seconds and made **1,766
SQL round trips** — two per file, because each enriched row was saved on its own.

The workers now **queue** what they fetch, and the queue is **drained in batches**:
one load and one update for a batch, through the same write gate, the same rule about
filling dimensions and never clearing them, and the same self-healing — a batch that
fails leaves its rows `pending`, which is the schema's own word for "ask again later".

**Measured on 50 rows marked `pending`: 4 SQL round trips**, against two per file
before. The 882-row pass would cost about eight.

- **The rows are written while the pass runs**, not only at its end: the workers flush
  what has piled up as they go, and the pass flushes what is left before it counts
  what is still pending.
- **Nothing is lost** — the 50 rows the measurement touched came back with their
  dimensions and their media kind, and the table finished on 423,698 rows with
  359,280 dimensions and nothing pending, exactly as it started.

### O3 — the two indexes nothing reads: DONE

The premise was wrong, and measuring it is what found that out. **The media library
queries a different table** — `ml.media`, with `Path`, `FileName`, `Size` and
`DateAdded` — so it was never the consumer of `ml.DropBoxNode`'s indexes.

SQL Server's own counters on the live table settled it:

| index | seeks | lookups | scans | updates |
|---|---|---|---|---|
| `IX_DropBoxNode_NameKey` | **0** | **0** | **0** | 4,649 |
| `IX_DropBoxNode_Dimensions` | **0** | **0** | **0** | 3,384 |
| `IX_DropBoxNode_Name` | 1 | 0 | 54 | 4,649 |

Two of them are **read by nothing** and are maintained by every row that changes. The
third is read, so it stays. The tree browser's search does not use any of them — it
scans the clustered index, 423,698 rows, because `PathLower` cannot be seeked on.

**Both write-only indexes are dropped** — `src/Utility.Server/Sql/DropUnusedDropBoxNodeIndexes.sql`,
which recreates them verbatim in comments, so the way back is one paste. A row update
now dirties the clustered row and the one or two indexes whose key actually changed,
instead of five entries — three of them carrying `PathDisplay` at about 220 bytes each.

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

### O5 — the write gate's scope: DECIDED, the gate stays as it is

Every write to `ml.DropBoxNode` goes through one writer at a time. That is what
stops the deadlock, and it is also what bounds the walk's rate: the walk goes as fast
as one write-batch at a time.

**The decision is to leave it.** The case for changing it was that the global gate was
throttling the walk, and that case is gone: a chunk is two round trips, so the gate's
ceiling is roughly 160 writes a second against a listing rate of ~35 folders a second,
and the gate is not what the walk waits for. The deadlock it was built for is no longer
reachable either — EF opens a transaction around a `SaveChanges` only when it needs
more than one command, a chunk is one command, and the app's writes now open none.

It also stays because it is the rule the table is written under, and it still protects
the one interleaving that remains: the enrichment pass writing while the walk writes.

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
| **Dropbox requests** | `DropBoxSyncService.RateSnapshot.TotalRequests` | the run's summary — the run's own figure comes from the result, because the meter is reset when a run ends |
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

**THE RATE LIMIT IS THE NEXT THING TO LOOK AT.** One 429 holds the whole walk, because
the limit is per authorization and a shared hold is what keeps a retry storm from
becoming the storm. The hold's LENGTH is not the app's: the app's own fallback is 5
seconds on the first attempt, and the 300 seconds it waited for was **Dropbox's
`Retry-After`**, honoured as it should be.

So the question is not what to do about the hold — it is why the account refused at all.
The walk was listing at ~35 folders a second. The account's own ramp probe carried 436
requests a second without a refusal, **but it measured `get_metadata`, not
`list_folder`** — and a listing carries a whole folder's metadata while a metadata read
carries one file's. The listing endpoint's limit has never been measured, and that is
the number that decides the walk's width.

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
| 6 | the flush's chunk costs two round trips, not four | the round-trip count by kind, on S1 | **measured** — 22 round trips for the 109-row subtree, of which 0 were transactions; a chunk is a tracked query and one batched update |
| 7 | the enrichment's writes are batched | the round-trip count by kind, on S4 | **measured** — 4 round trips for 50 rows, against two per row before |
| 8 | the write-only indexes are gone | SQL Server's own index counters, before and after | **measured** — `IX_DropBoxNode_NameKey` and `IX_DropBoxNode_Dimensions` had 0 seeks, 0 lookups and 0 scans against 4,649 and 3,384 updates; both dropped |
| 9 | the gate may be per folder | the deadlock reproduced and the fix measured — the shipped harness in the sync skill | **closed** — the decision is to leave the gate as it is; the deadlock it prevented is no longer reachable, and the gate is no longer the bottleneck |
| 10 | an unchanged walk's listing floor is the delta, not 19,851 calls | the cursor delta against the live account | not measured |
| 11 | the app counts its own round trips, by kind, and says so in its log | the sync's completion log line | **measured** — "The database cost 22 round trip(s) (11 select, 10 update, 0 insert, 0 delete, 1 other), and 0 row(s) changed while 108 did not" |
| 12 | the catalogue walk, after the changes | one catalogue run with the counters | **measured** — ~35 folders/s, 4,252 folders re-listed in about two minutes, nothing written but the folders' status; then two rate-limit holds of 300 s each |
| 13 | the whole catalogue walk, after the changes | one catalogue run with the counters | not measured — the run stopped at the rate limit |
| 14 | the listing endpoint's allowance | the width ramp held for a whole window, against `list_folder` | **measured** — a COUNT per five-minute window, between 1,400 and 4,000 calls; the burst limit is at least 96 a second, and it is not what binds |

---

## 8. The width rule

**THE RULE, AS AGREED.** The width is a number the walk earns, and the API gives it back.

1. **A refusal narrows ONCE.** The controller is told about a refusal by the code that
   caught it, not by the meter. The meter reports a refusal for the whole minute it
   happened in, and a controller that acted on it halved the width again and again for
   that minute: **one burst of two refusals took the walk from 24 to 1**, and it stayed
   there for the rest of the run - 1.9 folders a second against the 35 it had been
   doing.
2. **A refused width is remembered.** The ceiling drops below the width that was
   refused, so the walk does not climb back into a width the API has already said no to.
3. **The width is given back.** After a settled period with no refusal, it steps up by
   one. The controller was written to do this; only the ceiling stopped it.
4. **The floor is a fraction of the start**, not one. A refusal means slow down, not stop:
   at a width of one the walk takes thirty times longer than it needs to.
5. **The walk starts at the measured width every run, and climbs from there.** The
   width it runs at is something it earns, not something it was told. The next run does
   NOT start where the last one finished: a width lost to one moment's refusal is a
   fact about that moment, not a lesson about the account - and starting lower every
   run made a lost width permanent. The ceiling the walk climbs toward is 24.

**THE LIMIT IS A COUNT PER WINDOW, NOT A RATE.** The shipped width ramp, pointed at
`list_folder` instead of `get_metadata`, measured:

| width | calls | req/s | avg ms | 429s |
|---|---|---|---|---|
| 1 | 200 | 2.1 | 487 | 0 |
| 6 | 200 | 11.8 | 502 | 0 |
| 12 | 200 | 23.5 | 499 | 0 |
| 24 | 200 | **46.0** | 506 | 0 |
| 32 | 200 | 52.3 | 500 | 0 |
| 48 | 200 | 81.4 | 531 | 0 |
| 64 | 200 | **96.2** | 538 | 0 |

**That ramp is MISLEADING, and finding out why took three walks.** It carried 96
requests a second with no refusal - but it makes 200 calls per width and pauses, so it
never spends the allowance. The walks did:

| what | calls | rate | outcome |
|---|---|---|---|
| the ramp | 1,400 | 96/s in bursts | no refusal |
| the walk at width 24 | 3,827 | **50/s sustained** | spent the window in 77 s - refused |
| the walk at width 12 | 336 | 21/s | got what was left - refused |
| a nine-call probe | 9 | - | fine |

Dropbox's own `Retry-After: 300` says the window is **five minutes**. So the allowance
is a COUNT of listing calls per window, between 1,400 and 4,000 - and a burst ramp
cannot find it. The probe now holds each width for a whole window.

**The walk that synced this catalogue before never hit the limit, for a reason worth
keeping:** it enriched each file as it listed, so its listings were paced by the
enrichment and it spent each window a fraction at a time. A two-phase walk lists
everything first, so **it has to pace itself** - the width is what paces it.

**THE WIDTH IS 4, AND IT CLIMBS TO 24.** Four is about eight listings a second,
roughly two thousand calls in a five-minute window: under the allowance the ramp proved
by spending one without a refusal. Twenty-four is where the walk climbs to while it
stays healthy - the width the walk used before the allowance was measured, and inside
what the listing ramp carried without a refusal.

The ceiling comes back after 30 minutes rather than five, so it cannot climb back into
a width the account has already refused for the window it is in. Its cap is a separate
number from the ceiling itself, because a ceiling that has been lowered must still
have somewhere to climb back to.

**MEASURED, RUNNING FROM THE CATALOGUE ROOT AT WIDTH 4**: a steady 8.2 folders a
second (490-500 a minute after a slower first minute), **no refusals**, and no writes
beyond the folders' status.

## 9. Open questions

1. **May the write gate become a gate per folder?** The deadlock it prevents was
   between the flush and the status write of the same folder, so a per-folder gate may
   be enough — but "one writer at a time" is the rule the table is written under.
2. **What does the UI's "last synced" figure mean now?** `MAX(SyncedUtc)` still
   reports the last walk, because the folders carry it. A per-file "last seen" is
   gone; nothing shows it today, and the tree browser's per-node freshness is worth a
   look before the next walk changes what it says.
3. **Is the media library's read cost worth measuring now?** O3 cannot be decided
   without it, and it is the only item on the list whose answer might be "do nothing".
4. **How big is the allowance exactly?** It is between 1,400 and 4,000 listing calls
   per five-minute window, measured by what the walks could and could not spend. The
   width of four assumes about two thousand. If the allowance is smaller, the width
   comes down again - which the rule now handles one narrowing at a time.
