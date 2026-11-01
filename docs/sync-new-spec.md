# Sync New — the two-phase sync in the app

**STATUS: BUILT.**

The DropBox DB page gains a second sync button, **Sync New**, beside the existing
**Sync**. It runs the two-phase algorithm — list the structure, then enrich only what
changed — and it shows its work on a busy screen of its own.

The existing **Sync** button, the one-phase walk that fetches every file's metadata as
it goes, is untouched.

---

## 1. What the button does

| | |
|---|---|
| Where | the DropBox DB page, in the sync strip under the toolbar |
| Label | **Sync New** |
| Reads | the sync strip's path box — the same box the existing **Sync** reads |
| Runs | `IDropBoxSyncTwoPhaseService.SyncFrom(path)` |
| Shows | the busy screen of §3, over the whole window, until the run ends |
| On finish | the figures are read again, and the page's status line carries the outcome |

The path is **not** a second input. Both buttons read the one box, so the folder a
user types is the folder either button acts on. The box's default is the catalogue
root.

**The button is disabled while a run is in flight**, and while the existing one-phase
sync is running — two walks over the same table would fight each other.

## 2. The run

### Two phases, in order

**Phase 1 — LIST.** The walk, with no per-file call. Every row is written from the
listing. A file whose content hash is unchanged keeps its dimensions, because the
content is the same and they are therefore still true. A file whose content hash
changed has its dimensions cleared and is marked `pending`.

**PHASE 2 — ENRICH.** The metadata for the files whose dimensions are not known, and
only those. The work list is `WHERE IsFolder = 0 AND (WidthPixels IS NULL OR
HeightPixels IS NULL OR MediaInfoTag = 'pending')`. The schema's own word for a file
still asking is `pending`, and a file left `pending` by a failed enrichment keeps its
tag, so the next run picks it up again — but a file the one-phase walk never reached
carries **no tag at all**, so a work list of `pending` rows alone is empty while tens
of thousands of files wait for their dimensions. A row with unknown dimensions is
still enriched: a hash match does not make an incomplete row complete.

### The rules that belong to the algorithm

The width, the ceiling, the refusal and the hold are the algorithm's, and they live
in `docs/sync-optimisation-plan.md` §8 and `docs/sync-speedup-spec.md`. The screen
reports them; it does not decide them.

What the screen must show, because it is what the operator is watching for:

| | |
|---|---|
| the width | the requests in flight, and the width they may reach |
| the refusals | how many the account has refused this run |
| the latency | it rises before the first refusal — it is the early warning |
| the hold | the walk pauses for the account's `Retry-After` and resumes by itself |

### Stopping

**Stop** cancels the run. The rows already written are kept — a sync never deletes
anything. A run stopped during phase 1 leaves the folders it reached with their
statuses, and the walk is resumable. A run stopped during phase 2 leaves the
un-enriched files marked `pending`, which is the schema's own word for "ask again
later".

## 3. The busy screen

It covers the **whole window** — title bar, navigation and status bar included — for
as long as the run lasts, exactly as the existing sync's busy screen does. The app
behind it is not clickable, so a long run cannot be interrupted by a stray click on a
page that is about to be re-read anyway.

**Its panel is 90% of the window's width**, not a fixed one: the level table grows
with the catalogue's depth, and a capped panel would clip it.

```
┌──────────────────────────────────────────────────────────────────┐
│                       Syncing Dropbox                            │
│   /Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog       │
│                                                                  │
│        PHASE: Listing the structure                              │
│                                                                  │
│        Dropbox requests in flight: 6 of 7 at a time              │
│        Measured: 15.2/s, 481 ms mean, 1 refused                 │
│                                                                  │
│   Level 1        Level 2        Level 3        Level 4           │
│   ─────────      ─────────      ─────────      ─────────         │
│   48 of 48       1,204 of       8,930 of       2,411 of          │
│   1,240 rows     1,240 rows     9,102 rows     2,600 rows        │
│                                                                  │
│   ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░   │
│                                                                  │
│   Phase 2 of 2 — enriching the changed files                     │
│   882 of 882 enriched, 0 failed                                  │
│                                                                  │
│                    [  Stop  ]                                    │
└──────────────────────────────────────────────────────────────────┘
```

### The regions

**The headline and the path.** The path the run started from, in the monospaced face
the path box uses.

**The phase line.** Which of the two phases is running, in words: *Listing the
structure*, then *Enriching the changed files*. This is the one thing that is not
obvious from the rest of the screen — everything else looks the same in both phases.

**The traffic line.** The requests in flight against the width they may reach, and the
measured traffic beside them. These are the service's own live properties, not copies.

**They belong to whichever phase is running.** The enrichment has its own request gate
and does not share the walk's, so the walk's counters stand still while it works — and
an enrichment-only run has no walk at all. The enrichment reports its own in-flight
count and width, and the line shows those during the enrichment.

**The level table.** One column per level of the walk, carrying how many of that
level's folders have been listed and how many records it has written. It belongs to
phase 1 only; during phase 2 it stays as it was, so the operator can see what phase 1
did.

**The progress bar.** Indeterminate during phase 1 — a level's folder count is only
known once the level above it has finished, and the number of levels is unknown until
the walk ends. During phase 2 it is determinate: the work list's size is known before
the pass starts.

**The enrichment line.** How many files have been enriched, of how many the work list
held, and how many failed. Phase 2 only.

**The outcome line.** What the run did, in one line, when it ends.

**The error line.** The failure, when there was one.

**Stop.** Cancels the run.

### One example, threaded through

The screen's worked example is a catalogue sync that found **882 files changed**:

- Phase 1 lists the structure and marks those 882 files `pending`.
- Phase 2 enriches them: *882 of 882 enriched, 0 failed*.
- The level table shows the four levels of the catalogue, each with its folders
  listed and its records written.
- The traffic line reads *6 of 7 at a time* and *15.2/s, 481 ms mean, 1 refused* — one
  refusal, which held the walk for the account's `Retry-After` and was then resumed.

## 4. Implementation shape

| | |
|---|---|
| The run's view model | `src/Utility.UI/ViewModels/SyncNewViewModel.cs` — a singleton, holding the cancellation token, the phase, the enrichment's counts and the outcome |
| The command | `DashboardViewModel.RunSyncNewCommand` — calls the view model's start, and reads the figures again when it ends |
| The busy screen | a second overlay in `src/Utility.UI/Views/MainWindow.xaml`, above the existing sync overlay |
| The algorithm | `IDropBoxSyncTwoPhaseService` — already registered, already a singleton |

**The view model owns the run**, so the busy screen's state survives the page being
navigated away from — the same reason the existing overlay binds to the sync service
rather than to a page.

**The listing phase's live numbers are the sync service's own.** The two-phase service
runs phase 1 through `IDropBoxSyncService`, so the width, the traffic and the level
table are the service's properties, read live and not copied.

**The enrichment's numbers are the view model's own**, fed by the progress callback
the two-phase service already publishes.

## 5. Not in scope

- **Replacing the existing Sync button.** It stays, and it keeps its own busy screen.
- **Changing the algorithm.** The width rule, the ceiling and the refusal handling are
  the algorithm's, and they are specified elsewhere.
- **A depth limit, or a per-file enrichment toggle**, on the new button. The run
  covers the whole subtree and enriches what changed.
- **Resuming a stopped run from the new button.** The frontier is the existing
  **Continue sync** button's job.
- **A schedule, or a run at startup.**

## 6. Acceptance criteria

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | **Sync New** sits in the DropBox DB page's sync strip, beside **Sync** | the page, on screen | measured |
| 2 | It reads the path box the existing **Sync** reads | one box, one value | measured |
| 3 | It runs the two-phase algorithm | phase 1 marks the changed files `pending`, phase 2 enriches exactly those | measured |
| 4 | The busy screen covers the whole window and carries the Stop button | the app, on screen | measured |
| 5 | The screen shows the phase, and it changes between the phases | the app, on screen | measured |
| 6 | The screen shows the width, the traffic, the refusals and the latency | the app, on screen | measured |
| 7 | The level table is the walk's own levels | the app, on screen | measured |
| 8 | Stop ends the run, and the rows written are kept | the app, on screen, and the row count afterwards | measured |
| 9 | The button is disabled while a run is in flight | the app, on screen | measured |
| 10 | The figures are read again when the run ends | the page's figures change after a run | measured |
| 11 | **Enrich Only** runs the second phase on its own | the app, on screen | not measured yet |
| 12 | An enrichment-only run needs no path, and shows no level table | the app, on screen | not measured yet |

## 7. Enrichment only

A third button, **Enrich Only**, beside **Sync New** in the sync strip. It runs the
**second phase on its own**: the files a previous sync marked as changed, and nothing
else.

**It needs no path.** The work list is the set of rows tagged `pending`, so there is
nothing to scope it to — the path box does not apply to this run, and the button's
tooltip says so.

**The busy screen shows the enrichment phase from the first moment**, with the phase
line reading *Enriching the changed files*, and the determinate bar and the
enrichment's own figures. **The level table is hidden**, because there is no walk in
this run: the table belongs to the walk, and showing the previous walk's levels would
be worse than showing none.

The button is disabled while either sync is running, exactly as **Sync New** is.
