# Sync From Here — the two-phase sync started from the DropBox files tab

**STATUS: BUILT.**

The DropBox files tab gains a button, **Sync From Here**, that starts the two-phase
sync — list the structure, then enrich only what changed — from the folder the tab is
currently showing, rather than from the fixed path the DropBox DB page uses.

---

## 1. What the button does

| | |
|---|---|
| Where | the DropBox files tab, in the toolbar's path row, beside **Go** and **Refresh** |
| Label | **Sync From Here** |
| Reads | the folder the tab is navigated to — the path in the path box, and the folder the grid is showing |
| Namespace | the namespace the tab is browsing |
| Runs | `IDropBoxSyncTwoPhaseService.SyncBranch(path, namespaceId)` — the listing phase, then the enrichment |
| Enriches | **only the files under that folder** |
| Shows | the two-phase busy screen over the whole window, exactly as **Sync New** does |
| On finish | the outcome line, and the index's figures re-read |

The path is **not** a second input. The tab already has an address bar, and the button
reads the branch that bar shows. There is no path box on this button, because the tab's
own path box is that.

**The path box FOLLOWS the navigation.** The box and the grid are the same branch, so
every way of moving — **Up**, opening a folder, changing the namespace — moves the box
too. The box is never a stale copy of where the tab was.

**The scope is the branch in the address bar.** If the user has navigated to
`/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Amazon`, the button starts
the walk at `/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Amazon` and
covers everything beneath it.

## 2. The run

### Phase 1 — LIST

The walk from the branch, with no per-file call. Every row is written from the listing.
A file whose content hash is unchanged keeps its dimensions, because the content is the
same and they are therefore still true. A file whose content hash changed has its
dimensions cleared and is marked `pending`.

The walk covers the whole subtree — no depth cap, the same rule the DropBox DB page's
**Sync New** follows.

### Phase 2 — ENRICH

The metadata for the files whose dimensions are not known, **under that branch only**.

This is the one place the button differs from **Sync New**, and the difference is
deliberate. **Sync New** enriches every pending file in the namespace, because the run
it starts is the whole namespace. A run that starts in a branch must not spend the
account's allowance on the rest of the namespace: the work list is the files under the
branch whose dimensions are unknown, plus the rows still tagged `pending`.

The work list is therefore:

```
WHERE IsFolder = 0
  AND NamespaceId = @ns
  AND PathLower LIKE @branch + '/%'
  AND (WidthPixels IS NULL OR HeightPixels IS NULL OR MediaInfoTag = 'pending')
```

The trailing separator is what makes the scope exact. A file under the branch has the
branch's path, then the separator, then its own name — so the branch plus that separator
matches the branch's own files and everything beneath them, and does not catch a SIBLING
whose name merely starts with the branch's name. The bare branch path would catch that
sibling.

`PathLower` cannot be seeked on, so this predicate scans the namespace. It is read once
per run, and the pass is worth it — it is the work list.

### What the run does NOT do

- It does not delete anything. A sync never deletes; the table keeps holding files that
  no longer exist in Dropbox.
- It does not sweep rows outside the branch. Rows the branch no longer holds keep their
  stored values.
- It does not touch the DropBox DB page's own **Sync**, **Sync New**, **Enrich Only** or
  **Continue sync** buttons, and it does not change their behaviour.

## 3. The namespace

The button runs in the namespace the tab is browsing.

**The personal space cannot be synced by path**, and the sync service says so itself:
`ResolveNamespaceAsync` throws when the account has no team namespace, because a path
means nothing without a team root to resolve it against. Team folders are invisible from
the personal space.

So: **the button is enabled only in a team namespace.** In the personal space it is
disabled, and its tooltip says why — the personal space cannot be synced by path.

The namespace id is resolved at runtime, never pinned in source: the team root's id
belongs to the account.

## 4. The busy screen

`SyncNewOverlay` — the two-phase screen, in `src/Utility.UI/Views/MainWindow.xaml`. It
is the same one **Sync New** uses, and nothing about it changes for this button: the run
is the same shape as the one it already shows.

It covers the whole window — title bar, navigation and status bar included — so the app
behind it is not clickable, and a long run cannot be interrupted by a stray click.

It shows the phase, the width, the traffic, the level table and the enrichment's own
figures, and it carries the **Stop** button.

**Stop** cancels the run. The rows already written are kept. A run stopped during phase
1 leaves the folders it reached with their statuses, and the walk is resumable. A run
stopped during phase 2 leaves the un-enriched files marked `pending`, which is the
schema's own word for "ask again later".

### The blur behind the screen

The app behind the screen is blurred, so it reads as inactive rather than as legible
text the user might click.

**The blur follows what is actually up, not a "busy" flag.** It is applied while the
one-phase walk is in flight, while the two-phase run is in flight, and while the
Finder's pass is running.

**A run's result is acknowledged when the outcome is shown.** The one-phase service
keeps a finished run's result so the busy screen can show it until it is dismissed, and
its `IsBusy` covers "a result waiting to be acknowledged". The screen that would dismiss
it is collapsed in the markup, so nothing cleared that flag — and the blur, bound to it,
stayed on for the rest of the session. Every caller that reports an outcome now
acknowledges the result, which is what clears it.

## 5. Running alongside the other syncs

**The two-phase busy screen covers the whole window**, so while **Sync New**, **Enrich
Only** or this button's own run is in flight, the app cannot be navigated at all: the
files tab is unreachable, and no other sync button can be pressed. Two runs cannot
collide, because the second one cannot be started.

**The one-phase Sync is the exception.** Its busy screen is hardcoded collapsed in
`src/Utility.UI/Views/MainWindow.xaml` and nothing in the code-behind ever shows it, so
that run blurs the app but leaves it clickable, and the navigation stays live. The files
tab is reachable during it.

**So the button is disabled whenever a walk is in flight** — and the one-phase Sync is
the case that actually needs it. Two walks over the same table would fight each other,
and they would write the same rows.

## 6. Implementation shape

| | |
|---|---|
| The command | `DropBoxViewModel.SyncFromHereCommand` — reads the tab's current path and namespace, and starts the run |
| The run | `SyncNewViewModel.StartBranchAsync(path, namespaceId)` — the existing view model, with a branch-scoped entry point beside the existing `StartAsync(path)` |
| The service | `IDropBoxSyncTwoPhaseService.SyncBranch` — a branch-scoped entry point beside `SyncFrom`; already registered, already a singleton |
| The busy screen | `SyncNewOverlay` — see §4. No new screen. |

**The run lives in `SyncNewViewModel`, not in `DropBoxViewModel`.** The busy screen
covers the whole window, so the run's state has to survive the page being navigated away
from — the same reason the existing overlay binds to the sync service rather than to a
page.

**`SyncBranch` is `SyncFrom` with the enrichment scoped to the branch.** Both share one
private method that runs the two phases in order; the difference is the prefix the
enrichment is limited to. `SyncFrom` passes none, so it enriches the whole namespace —
which is right for a run that starts at the catalogue root, and is what the DropBox DB
page's **Sync New** still does. `SyncBranch` passes the branch's own path **plus a
trailing separator**, so the prefix matches the branch's own files and everything beneath
them, and does not catch a sibling whose name merely starts with the branch's name.

**Phase 1 is the existing walk.** `IDropBoxSyncService.SyncAsync` is what the two-phase
service already calls, with `enrichFileMetadata: false` and `batchSize: 500`. The button
changes neither.

**`enrichFileMetadata: true` is not used.** That flag spends one `get_metadata` per
file in the branch — 97% of the run's traffic — even when the file's dimensions are
already stored. The two-phase path spends one call per folder plus one per file whose
dimensions are unknown, and the branch's files mostly have their dimensions already.

**The view model is released with the page.** It listens to the sync services, which are
singletons, so it unsubscribes when the page it belongs to is navigated away from —
otherwise its handler would outlive the page and keep it alive.

## 7. Not in scope

- **A depth cap, or a per-file enrichment toggle**, on the new button.
- **A confirmation dialog.** The button starts the run; the busy screen's **Stop** is
  what ends it.
- **Resuming a stopped run from the new button.** The frontier is the DropBox DB page's
  **Continue sync**.
- **A delete, a sweep, or any change to rows outside the branch.**
- **Changing the DropBox DB page's buttons.** They keep their own paths and their own
  behaviour.
- **A schedule, or a run at startup.**

## 8. Acceptance criteria

Measured against the live account and the live `ml.DropBoxNode`, on the branch
`/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Amazon`.

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | **Sync From Here** sits in the DropBox files tab's path row | the app, on screen | measured |
| 2 | It reads the branch the address bar shows | navigated to the branch, clicked the button, the run's path was that branch | measured |
| 3 | It runs the two-phase algorithm | the log shows the listing phase, then the enrichment of what it marked | measured |
| 4 | The enrichment covers only the branch | 5 files enriched, against 44,321 pending namespace-wide | measured |
| 5 | The busy screen covers the whole window and carries **Stop** | the app, on screen | not measured — the run finished in ~3 s, so the screen was never caught on screen |
| 6 | The screen shows the phase, and it changes between the phases | the app, on screen | not measured — same reason |
| 7 | Stop ends the run, and the rows written are kept | the app, on screen, and the row count afterwards | not measured — no run was stopped |
| 8 | The button is disabled while the one-phase Sync is running | start that sync on the DropBox DB page, navigate to the files tab, look at the button | not measured |
| 9 | The button is disabled in the personal space | switch the namespace to Personal, look at the button | not measured — the click needed an approval that did not arrive; the rule is in `CanSyncFromHere` |
| 10 | A run in the personal space is refused with a reason | the status line carries the reason | not measured — same reason |
| 11 | The index's figures are re-read when the run ends | the figures change after a run | measured — the stats service re-read in the log |
| 12 | The run leaves rows outside the branch untouched | the row count outside the branch is unchanged | measured — 9 updates, 0 inserts, 0 deletes, and no row outside the branch changed |
| 13 | The app is not left blurred after the run | the app, on screen | measured — the app is sharp after the run, and the blur releases |
| 14 | The branch prefix does not catch a sibling | the prefix is the branch plus a separator | measured — the bare prefix would have caught 1 sibling row that the prefixed one does not |
| 15 | The path box follows the navigation | press **Up** from a branch | measured — the box becomes the parent branch, and the grid shows the parent's contents |

**The one thing this run did not prove is the busy screen itself.** The branch is small,
so the run finished in about three seconds and the screen was never on screen during a
capture. The screen is the existing one, unchanged, and **Sync New** uses it the same
way — but this feature's use of it is not observed.

**The five files that stayed without dimensions are PNGs.** The enrichment fetched them
(5 of 5, 0 failed) and Dropbox returned no media info for them, so there were no
dimensions to write. The 30 JPGs under the branch already had theirs and kept them.
