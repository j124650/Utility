# Finder — Groups

> **Preliminary.** This spec states what the **Groups** button does and what it shows — the
> grouping rule, the window, and the figures it carries. **This is built and verified**;
> see §8 for what was checked against the live library. **The button is hidden for now**
> (§9) — the feature is dormant, not gone.
>
> **This is an OPTIONAL TEST FEATURE.** The Groups window is a diagnostic tool, not a
> product feature: it exists to answer a question while the recovery is being planned and
> built, and it may be removed in whole or in part at any time. Nothing else in the Finder
> depends on it, and nothing depends on it depending on it. **§9.**

---

## 1. What it is

A button in the Finder's toolbar, after **Recover Selected**, that opens a window listing the
distinct parent file names, **grouped by the path they sit in**. **The button is hidden for
now** (§9): the command and the window are as they were, and nothing in the toolbar shows
it.

| | |
|---|---|
| **Name** | Groups |
| **Where** | The Finder's toolbar, fourth button — **hidden for now** (§9) |
| **Opens** | A modal window owned by the main window |
| **Shows** | The distinct parent file names, grouped by the parent's path (`ml.Media.Path`) — one row per path, with the number of names in it |
| **Writes** | Nothing |
| **Status** | **Optional test feature** — removable (§9) |

It is a **debugging and monitoring tool**. It reads the rows the pass already produced and
organises them; it changes nothing and asks for nothing.

## 2. The groups

### 2.1 The group key is the path

**A group is one distinct `ml.Media.Path`** — the folder the parent's file lives in. The
Finder already carries it on every row as `ParentFolder` (`finder-spec.md` §19.1), so the
grouping is an in-memory projection of rows the pass produced.

The path is taken **verbatim**, as `ml.Media.Path` stores it. It is not lower-cased, not
trimmed and not rebuilt from the file name.

**The path is the key, not a prefix cut out of the name.** The two agree on almost every
pair, so they look interchangeable — but they are not:

- `ml.Media.Path` is stored **lower-case** (`ca05434001`), while the file name is
  **upper-case** (`CA05434001-01.jpg`). A prefix cut from the name would carry the name's
  case; the path carries its own.
- Some paths hold **more than one hyphen** (`w28ec9l001-0120`, `m189c9l01a-0110`), so a
  first-hyphen cut of the name and the path are different strings for those parents.
- **11 paths hold two different first-hyphen prefixes** among their names — the same
  product code written two ways, or two products sharing a folder. Those folders are
  visible only because the key is the path and not a name prefix.

### 2.2 The count of a group

**A group's count is the number of distinct `ml.Media.FileName` values of the parents in
that path.** The names themselves are not shown — only how many there are.

**Distinct, not counted.** Two pairs in one path can carry the same file name — the same
source file imported twice — and the name is counted once. A name that appears in **two
paths** is counted in both: it belongs to the path it was found in.

The child has no file name of its own (§3 of `finder-spec.md`), so the names counted are
the parents' names alone.

### 2.3 The order

- **Groups** are ordered by the path, **ordinal ascending** — the path as stored.

The order is the reader's, not the table's: the window's rows are grouped, so the table's
own ordering does not apply.

### 2.4 The two-prefix folders are marked

**A group whose names carry more than one first-hyphen prefix is marked.** The prefix is
the part of the name before its first hyphen (`CA05434001-01.jpg` → `CA05434001`).

**Twelve paths hold two such prefixes** — `dc01789310`, `he01106100`, `q2007354p`,
`q2200190`, `q2200191`, `q2200192`, `q2200195`, `q2200207`, `q2200212`, `q2200213`,
`q2200214`, `sockclife`. In those folders one product was written two ways, or two
products share a folder. The mark saves a monitor from spotting them by eye.

**The prefixes are compared exactly, trailing spaces included.** `dc01789310` holds a
name whose prefix is `DC01789310` and one whose prefix is `DC01789310 ` — a trailing
space — and it is marked for it. A trailing space is a difference in the data, and the
mark is there to point at the difference.

The mark is a **column of the row**, not a colour: the window reads in rows, and a
monitor scans them.

## 3. Where the data comes from

**From the rows the pass produced — no query, no new pass.**

`IFinderScanState.Rows` holds every row the last pass produced, and each row carries the
parent's path and file name. The window's content is therefore a projection of data that
is already in memory.

| Figure | Where it comes from |
|---|---|
| the groups | the scan state's rows, grouped by `ParentFolder` |
| a group's count | the distinct `ParentFileName` values in that group |
| the group count | the number of distinct `ParentFolder` values |
| the name count | the sum of the groups' counts |

**The filters do not narrow the window.** The window shows the **whole gathered set** — the
rows the pass produced — not the rows the table is currently showing. The filters narrow
what the *table* shows; the window is a view of the library, and its figures are the
library's.

**The paths are compared case-insensitively.** The paths are directories on disk, and
the filesystem does not distinguish case — so a path and the same path in another case are
the same folder. The path a group shows is the one the rows carry.

## 4. The window

A modal window of the app, owned by the main window, shown with `ShowDialog()` — the same
way **Recover Selected**'s confirmation window and **Liem's List**'s window are. The Finder
cannot be used while it is up, and focus returns to it when the window closes.

The window carries a **list** and a **counter**:

```
┌────────────────────────────────────────────────────────────┐
│  Groups                                                       │
├────────────────────────────────────────────────────────────┤
│  27,736 groups · 357,156 names                              │   ← the counter
├────────────────────────────────────────────────────────────┤
│  Path                  │ Names │                             │   ← one row per group
│  ca05434001            │    11 │                             │
│  ca02570001            │    11 │                             │
│  cp102207m             │    28 │                             │
│  q2200190              │     9 │ ⚠                          │   ← two prefixes
│  …                                                         │
└────────────────────────────────────────────────────────────┘
```

- **The counter** is a single line at the top: the number of **groups** and the number of
  **names** across them. Both are stated, so the reader knows which set the list holds.
- **The list** is one row per group: the **path**, the **number of names** in it, and the
  **mark** (§2.4). The names themselves are not shown.
- The two columns are **Path** and **Names**, and **both sort**. Clicking a heading sorts
  by that column; clicking it again reverses the order. The mark has no heading and does
  not sort.
- The list **scrolls vertically** and is **virtualized**, so a window holding tens of
  thousands of groups opens at once and scrolls without lag.
- The window is **resizable** and opens at a size that shows the counter, the column
  headings and a screenful of rows.
- The path is **selectable and copyable**, so a reader can take a path out of the window
  and paste it into the Finder's folder filter.

**The window has one button: Close.** It changes nothing, so it has no OK and no Cancel.

## 5. What it does not do

- It does **not write** anything — not the database, not the library, not Dropbox.
- It does **not run the pass** and does **not re-read** it. It shows what the last pass
  produced. If the pass has never run, the button is **disabled** — there is nothing to
  group.
- It does **not** restore, regenerate or repair anything. That is **Recover Selected**.
- It does **not** apply the Finder's filters (§3).
- It does **not** reach Dropbox. The names it counts are the parents' own recorded names
  (`ml.Media.FileName`); the candidates are not involved.

## 6. Implementation shape

- **`src/Utility.Server/Finder/NameGroup.cs`** — the record: the path, the count, the mark.
- **`src/Utility.Server/Finder/Services/FinderService.cs`** — `GetNameGroups()`, on
  `IFinderService` beside the pass and Liem's List. It groups the scan state's rows.
- **`src/Utility.UI/Views/GroupsWindow.xaml`** — the window: the counter, the list.
- **`src/Utility.UI/ViewModels/FinderViewModel.cs`** — a `ShowGroupsCommand`, beside
  `ShowLiemListCommand` and `RecoverSelectedCommand`. The command calls the service and
  shows the window.
- The button sits in `src/Utility.UI/Views/FinderView.xaml` beside the other three, in the
  toolbar's own style.
- **`tools/groups-checks/`** — runs the real pass and the real method against the live
  library and checks the rules above.

**The grouping is done once, on the rows already in memory**, and it is fast enough to do
in the click: the rows are already loaded, and the grouping is a partition of them.

## 7. The decisions

1. **The window does not follow the table's filters.** It shows the whole gathered set, so
   its figures are the library's and do not change as the reader types in the filters.
2. **The list shows the group and its count.** The names themselves are not shown — the
   list is a monitor's view of the library's shape, and a count per folder is what it is
   read for.
3. **The two-prefix folders are marked.** Eleven paths hold two different first-hyphen
   prefixes among their names, and the mark saves a monitor from spotting them by eye.
4. **No filter box in the window.** The list is read by scrolling.

## 8. Acceptance

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The button is in the Finder's toolbar, named Groups | open the Finder | **hidden** — collapsed for now (§9); the command and the window are unchanged |
| 2 | It opens a modal window owned by the main window | click it | **built** |
| 3 | The list holds one row per distinct `ml.Media.Path` | `tools/groups-checks` | **verified** — 27,736 |
| 4 | A row's count is the number of distinct parent file names in that path | `tools/groups-checks` | **verified** — 357,156 across them |
| 5 | A name in two paths is counted in both | `tools/groups-checks` | **verified** — 28 names are in two |
| 6 | The counter states the number of groups and the number of names | read the counter | **built** |
| 7 | The groups are ordered ordinally, ascending | `tools/groups-checks` | **verified** |
| 8 | The window shows the whole gathered set, whatever the filters say | filter, then open the window | **built** |
| 9 | The window queries nothing and runs no pass | `tools/groups-checks` — the method reads the scan state | **verified** |
| 10 | The button is disabled when no pass has produced rows | open the Finder on a fresh run | **built** |
| 11 | The list scrolls, virtualized, over the whole set | scroll it | **built** |
| 12 | The path can be selected and copied | select a path | **built** |
| 13 | The twelve two-prefix folders are marked, and no other row is | `tools/groups-checks` | **verified** — 12 marked |
| 14 | Nothing is written — database, library, Dropbox | run it and check | **built** |

**Verified** means `tools/groups-checks` ran the real pass and the real `GetNameGroups()`
against the live library and checked it: the counts, the order, the distinctness, the
mark, and the names' sum. **Built** means the code is written and compiles, but nobody has
looked at the window on screen.

Two numbers the spec carries are now measured, not estimated: **27,736 groups** and
**357,156 names**. The marked groups are **twelve**, not eleven — `dc01789310` holds a name
whose prefix carries a trailing space, and SQL Server's `DISTINCT` had hidden it.

## 9. This feature is optional and removable

**The Groups window is a TEST FEATURE.** It was built to answer a question while the
recovery was being planned — what the library's paths look like, and where a product was
written two ways. It is not part of the product the Finder is becoming.

**It may be removed at any time, in whole or in part.** Removing it is a normal change,
not a repair of a mistake.

**The button is hidden for now.** The feature is dormant, not gone: the command, the
window and the checks are as they were, and the button comes back by removing one
attribute from the view.

**Nothing depends on it.** The Finder's pass, the table, the filters, the verdicts, the
recovery and Liem's List are all unaffected by the button's absence. The window reads the
scan state's rows and writes nothing, so there is nothing to unwind when it goes.

**What removing it takes:**

| Piece | |
|---|---|
| `src/Utility.Server/Finder/NameGroup.cs` | the record — delete |
| `IFinderService.GetNameGroups()` and its implementation | delete |
| `src/Utility.UI/Views/GroupsWindow.xaml` and `.xaml.cs` | delete |
| `ShowGroupsCommand` and `CanShowGroups` in `FinderViewModel` | delete |
| the toolbar button in `FinderView.xaml` | delete |
| `tools/groups-checks/` | delete, or keep the grouping checks by moving them into another harness |
| this document | delete |

**What a removal does NOT touch:** the pass, the rows, the filters, the verdicts, the
recovery, the recovery's log, and Liem's List.

**If it is kept,** it stays a read-only diagnostic: it writes nothing, it runs no pass of
its own, and it is offered only when a pass has produced rows.
