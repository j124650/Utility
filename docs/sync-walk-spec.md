# The Sync Walk — Worker Pool

> **Preliminary.** This spec states how the walk turns a list of folders into a walk. It
> has not been proven against a live run.

---

## 1. What it is

The walk visits every folder of a Dropbox subtree. It is a breadth-first traversal: it
lists a folder, writes what it found, and then the folders it found are visited in turn.

This spec changes **how the folders are handed to the workers**, not what a folder's visit
does. A visit still lists one folder, enriches its files, writes its rows, and records its
verdict. What changes is that the walk no longer creates one task per folder.

## 2. The problem

The walk was written as a fan-out:

```csharp
await Task.WhenAll(seeds.Select(seed => ExpandFolderAsync(...)))
```

`Task.WhenAll` enumerates the whole sequence, so every folder becomes a task **before any
of them runs**. A continuation over the current frontier starts **5,456 tasks** at once,
and each of them that gets far enough creates one more task per file it holds — a wide
folder holds thousands.

Three things follow from that, and all three are visible in the running app:

| | Now | With a pool |
|---|---|---|
| Tasks alive | ~6,000 | the worker count (24) |
| `TaskCanceledException` on Stop | hundreds to thousands | the worker count (24) |
| Semaphore waiters | 5,456 | none |

The **cancellation noise** is the one a user sees. Stopping a walk cancels every task
that is waiting, and each of them throws as it unwinds. The exceptions are handled
correctly — they are first-chance, they are collected, and the walk reports "Sync
stopped" — but a debugger attached to the app shows thousands of them, and that is noise
that hides a real fault.

The **memory** is the one that will bite. Each of the 5,456 tasks holds a continuation on
the gate it is queued at. A walk that works at 100 folders and fails at 5,000 is a walk
whose shape was never tested at the real size.

## 3. The rule

**The walk holds folders, not tasks.** A folder waiting to be visited is a value in a
list. A task is a worker, and there are only ever as many workers as the width.

```
folders to visit ──> list ──> worker ──> worker ──> worker
                              │         │         │
                              └── pull ──┴── pull ──┴── pull
```

A worker takes a folder from the list, visits it, and takes the next. The list is the
whole backlog; the workers are the only things alive.

## 4. The work list

One work list per level.

The level barrier is **load-bearing** and stays: a folder's children need their parent's
committed `Id`, which only exists after the parent's rows are written. So level N+1 cannot
be visited until level N is finished. The list does not remove that barrier — it is
filled per level, and the next level's list is filled only once the current one has
drained.

The list is **plain data**, not a queue type. The folders of a level are already in
memory — they came out of the previous level's writes — so a `Channel` would add a
producer task and a copy of the list for nothing. The backlog is held as a list of paths,
not as tasks, and the workers take from it by index.

The list holds **the folder, not the work**: its path and its `Id`. Nothing else.

## 5. The workers

A worker is a loop:

1. take the next folder from the list by index — if the index is past the end, stop
2. visit it: list, enrich, write, record
3. the folders it discovered go to the **next** level's list

The number of workers is the **width**, and the width is **24**.

**The width is one number.** It is the request gate's width, the folder gate's width, the
number of workers in the folder pool, and the ceiling of the file pool. They are all the
same number, set in one place (`WalkWidth`), and there is no second number to reconcile
with it — no separate ceiling, no separately-configured starting width.

A worker that is told to stop takes **no new folder**. It finishes the one in its hand
and exits. It does not abandon a half-visited folder, because a folder's rows are written
before its verdict and a folder whose write failed is left for the next continuation to
revisit (§8).

## 6. The width is fixed at 24

The walk was written to climb towards whatever rate Dropbox would accept, and that needed
two numbers — a starting width and a ceiling — plus a folder width and a request width
that had to be kept in step with each other. That machinery is gone.

Measured against this account, with the same per-file call the walk makes, the API
tolerates far more than 24: latency stays flat at about 370 ms from width 1 to width 256,
and no width produced a single rate limit. Throughput scales with the width — 15.7 calls
per second at six, 84 at thirty-two, 162 at sixty-four, 436 at one hundred and ninety-two.

So 24 is a **choice, not a measurement**. The walk is simply capped, and the cap is the
same number everywhere.

**The width may still narrow.** The controller watches the measured traffic and the
latency, and trims the width if Dropbox rate-limits the walk or the latency rises against
the healthy baseline. A narrowed width is one number, and every layer follows it. The
width never widens again.

The **enrichment pool** takes a share of the request gate rather than a fixed count: with
24 folders enriching, each takes one permit's worth; with one folder alone, it takes the
whole width, because nothing else wants those permits. The floor is one, so a folder
always makes progress on its own files. A higher floor would multiply the live task
count — 24 folders at a floor of four is 96 enrichment workers for 24 permits — and the
permits are what actually limit the calls, so the extra workers would only queue.

## 7. Stopping

Stopping cancels the walk's token, as it does now. The difference is **how much is
cancelled**.

- The **workers** are cancelled. There are as many of them as the width.
- The **work list** is not a task. It is dropped, and its contents go with it.

So a stop produces one cancellation per worker, not one per pending folder.

A folder that was never taken from the list was never visited, and was never written.
That is the same as before: a folder that was not visited has no verdict, and a folder
with no verdict is picked up by the next continuation. **Nothing is lost by stopping.**

The gate wait does **not throw** on cancellation. A worker parked on a full gate would
otherwise raise one `TaskCanceledException` per waiter, which is what filled the debugger
with thousands of them. `TryWaitAsync` takes the permit without throwing, so a cancelled
worker simply stops.

## 8. The failure rules are unchanged

A visit that fails still does not lose a subtree:

| Failure | What happens | Why it is safe |
|---|---|---|
| The folder could not be listed | recorded as `Failed` | the next continuation revisits it |
| The path is gone | recorded as `Skipped` | same |
| The rows could not be written | recorded as `Failed`, the folder is left | same |
| The verdict could not be written | logged, the folder keeps a null verdict | a null verdict is picked up |
| The request was rate limited | waited out, then retried | — |

**A folder with no verdict is a folder the next continuation visits.** That is the
invariant that makes every failure above safe, and the pool does not change it.

## 9. What it does not change

- **What a visit does.** The listing, the per-file metadata calls, the write, the verdict —
  all as they are.
- **The level barrier.** §4.
- **The database rules.** One writer at a time, the path-hash seek, the deadlock retry.
- **The order the walk visits folders in.** The work list is filled in the same sorted
  order the bag was sorted in, so the walk order is unchanged.
- **What is written.** The same rows, the same verdicts, the same reasons.

## 10. Not in scope

- **Writing to Dropbox.** The walk only reads.
- **Any change to the `ml` schema.**
- **A walk that is not breadth-first.** The barrier is a property of the parent-child
  `Id` relationship, not of this design.
- **Parallel levels.** Two levels could overlap if a folder's `Id` were known before its
  rows were written. It is not, and that is not a change worth making.

## 11. Acceptance

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The walk holds no task per folder | count the live tasks during a continuation | **measured for the old shape only** — 5,456 alive at once. The pool's own count is **not measured yet** |
| 2 | A stop raises one exception per worker | cancel a walk with a debugger attached | **not measured yet** |
| 3 | The width is 24 everywhere | one number, `WalkWidth` | **done** |
| 4 | The level barrier still holds | the next level starts only after the current one drains | unchanged by this work |
| 5 | A stopped walk loses nothing | a stopped walk's folders carry no verdict, and the next continuation visits them | unchanged by this work |
| 6 | The whole tree syncs | run a continuation to completion | **not done** |

Criteria 1, 2 and 6 are outstanding. The implementation compiles and the shape is in
place, but the pool has never been run against the live tree, and the numbers it was
written to produce have not been taken.
