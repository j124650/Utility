# Logs

> **Preliminary.** This spec states what the Logs tab is, what it shows and how. It has
> not been built.

---

## 1. What it is

**A tab in the app that browses the logs the app writes.** The app writes two kinds of
log, and the tab has one sub-tab for each:

| Sub-tab | What it browses |
|---|---|
| **Recovery** | the recovery logs — one file per recovery run, grouped by the date the run started |
| **App** | the app's own logs — one file per app run |

**Both are MASTER-DETAIL forms.** The master is a list; the detail is what the list's
selected entry shows.

**The tab READS and it writes nothing.** It opens no log, deletes none, and starts no
recovery. A reader who opens a log is reading what already happened.

## 2. Where the logs are

**Both kinds live in one folder** — `%LOCALAPPDATA%\Utility\logs\`:

```
%LOCALAPPDATA%\Utility\logs\
├── 2026-10-31_13-00-18.log              ← the APP's own log for that run
├── 2026-10-31_20-48-17.log              ← and this one
├── 2026-10-31\                          ← the RECOVERY logs of that day
│   ├── Recover 2026-10-31_20-32-06.log
│   └── Recover 2026-10-31_20-48-59.log
└── 2026-10-28\
    └── Recover 2026-10-28_15-22-07.log
```

**The app's own logs are FLAT** — one file per app run, in the folder itself, named
`yyyy-MM-dd_HH-mm-ss.log`. **The recovery logs are in a FOLDER per day**, named for the
date the recovery STARTED (§10.5.1 of the recover spec).

**The RECOVERY log's name says which kind it is** — `Recover <stamp>.log` — and the app's
own does not. So the two are told apart by the name, not by where they sit.

**The recovery logs from before the dated folders existed are still flat**, in the folder
itself (§2 above, the `Recover 2026-10-28_*.log` files). **They are not lost**: a
recovery log is found by its NAME wherever it is, and one that is not in a folder is filed
under the date in its own name.

## 3. The tab

**A "Logs" tab in the main nav strip**, after **Finder** and before **Settings** —
Settings stays last, because it is where a reader goes when they are done.

It is a page like any other: `LogsView` and `LogsViewModel`, a singleton in DI, resolved
by `NavigationService` under the key `Logs`, and a `NavLogs` button in the strip.

**Inside the tab there are two sub-tabs — Recovery and App** — and each is a master-detail
form of its own. Switching sub-tab does not lose what the other one had selected.

**The tab reloads its lists when it is opened**, because a log is written while the app
runs and the reader is often looking at a run that is still going. There is a **Refresh**
button as well, for the reader who stays on the tab while a run continues.

## 4. The App tab

### The master

**The app's own log files — the flat ones in the logs folder** — in a list:

| Column | What it says |
|---|---|
| **File** | the file's name |
| **Last written** | when the file was last written — the file's own date |
| **Size** | how big it is |

**The list is ordered by the file's last-written date, MOST RECENT FIRST.** That is the
order a reader wants: the run that just happened is at the top.

**The app's own logs are the files named `yyyy-MM-dd_HH-mm-ss.log`.** The recovery logs
are named differently, so they are not in this list — they are in the other tab, and
neither list shows the other's files.

### The detail

**The file's TEXT**, as it was written, in a read-only monospace box that scrolls and
selects.

**There is no rendering of the app's own log.** It is already text, and it is already
readable — a timestamp, a level and a message per line. The detail adds only the file's
path and its size above the text.

**The file is read when it is selected**, not when the tab is opened. A folder of twenty
logs is twenty files that may be megabytes each, and the reader wants one of them.

## 5. The Recovery tab

### The master — a tree

**A tree with two levels:**

```
2026-10-31  (7)
├── Recover 2026-10-31_20-48-59.log
├── Recover 2026-10-31_20-32-06.log
└── …
2026-10-28  (2)
└── Recover 2026-10-28_15-22-07.log
```

**The top level is the DATE** — a node per day, named for the day the recovery started.
**Under it, the log files** of that day.

**The date nodes are EXPANDED**, so the reader sees the files without a click. The tree is
the master, and a master that hides its contents behind a click is a master that makes
the reader work.

**The order is MOST RECENT FIRST** — the dates descending, and the files within a date
descending by their own time. The newest run is at the top, in both levels.

**A date node says how many files it holds**, in brackets. A reader scanning the tree
wants to know which day was busy.

**The tree's dates are the folders' dates** — and for a recovery log that is not in a
folder, the date in its own name. One rule, no exceptions, and every recovery log is
reachable.

### The detail — the log, in human-readable form

**The detail is a REPORT, not the log's raw text.** The log is JSON Lines — one JSON
object per line (§10.5.3 of the recover spec) — which is for a parser. A reader wants to
read it.

```
RECOVERY RUN  2026-10-31 20:48
  Filtered set      363,856 pair(s)
  Recoverable       292,400
  Recovered             38
  Failed                11
  Downloaded         0.1 GB
  Replaced              76 file(s)

FAILURES (11 of 292,400)
  1  Y42408OD-Featured-Bottom.jpg   (y42408od)
     reason   path/not_found/
     from     /Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/New Balance/F27/PROCESSED/Y42408OD/Y42408OD-Featured-Bottom.jpg
     into     C:\_Recover\media\y42408od\78FDF6AC-7976-48BA-A3EB-90B956FF6C44
     20:49:20 → 20:49:21   (0.6 s)

RECOVERED (38 of 292,400 — showing the first 200)
  1  M80608F0-Featured-Top.jpg   (m80608f0)
     restored the parent, then regenerated the child
     from     /Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/New Balance/F27/M80608F0/M80608F0-Featured-Top.jpg
     into     C:\_Recover\media\m80608f0\33540F62-0C09-4746-AB7B-52A2F0CB1114
     parent   2,023,419 bytes   ·   child  52,324 bytes, 1500 × 1500
     20:48:59 → 20:49:05   (5.7 s)
```

**THE FAILURES COME FIRST, because that is why a reader opens a recovery log.** A run
that went well is a run nobody needs to read. The failures are the ones worth the reader's
time, so they are at the top, and the successes follow.

**The failures are bounded at 500**, and the bound is stated in the heading — the same
rule as the successes and as the confirmation window's actions (§10.1.4 of the recover
spec). A reader with more than 500 failures wants the count more than they want the
501st entry.

**Each failure is shown in full**: the pair's name and folder, the reason, the Dropbox
path the bytes were to come from, the path they were to be written to, and how long the
attempt took.

**The successes are bounded at 200**, and the bound is stated in the heading — the same
rule as the confirmation window's actions (§10.1.4 of the recover spec). A run that
recovered 292,400 pairs has nothing to show the reader beyond the first 200.

**An operation that was INTERRUPTED — a `begin` with no `end` — is shown with the
failures, and says so.** It is an operation that did not finish, and a reader looking at
a run that ended badly needs to see it.

**The figures at the top are the log's OWN** — the `header` line's and the `summary`
line's. Nothing is recomputed: the log says what the run did, and the browser shows it.

### The older format

**A recovery log in the OLD text format — `key=value` pairs on a line — is shown as its
own text**, with a line above saying it is in the older format. Those logs are readable
already, they are a handful, and the browser does not grow a second renderer for them.

### The render does not hold the log in memory

**A full run's log is ~340 MB.** The reader makes **two passes** over the file:

1. **find which operations failed** — reading the lines and keeping the failing ones'
   numbers, and the first 200 successes';
2. **collect the details of the ones it will show** — the lines of those operations only.

**Neither pass holds the whole log.** The first pass keeps a few numbers; the second keeps
the details of the operations being shown.

**The render runs off the UI thread**, and the detail says it is reading while it does.
A run of 292,400 operations is 1.5 million lines, and reading them takes seconds.

## 6. What the detail does NOT do

- **It does not search.** There is no find box. A reader who wants one pair reads the
  log file in an editor, which is what the log is FOR.
- **It does not follow a path.** A path in the detail is text; clicking it does not open
  Explorer.
- **It does not write.** Nothing here creates, edits or deletes a log.
- **It does not run a recovery.** The tab is a reader.

## 7. Implementation shape

| Piece | Where |
|---|---|
| the view | `src/Utility.UI/Views/LogsView.xaml` |
| the view model | `src/Utility.UI/ViewModels/LogsViewModel.cs` |
| the page's key | `NavigationKeys.Logs` — `src/Utility.UI/Services/NavigationService.cs` |
| the nav button | `NavLogs` in `src/Utility.UI/Views/MainWindow.xaml` |
| the log folder | `%LOCALAPPDATA%\Utility\logs\` — the same folder the log provider writes into |

**The logs folder is read, not written.** The browser knows the folder; it does not own
it.

**The tree and the list are the view model's**, and the detail is read on selection —
so switching between sub-tabs, or coming back to the tab, does not re-read a file.

**The recovery report is rendered by a separate class** — it is a translation of the log,
not a view concern, and it is worth being able to check on its own.

## 8. Acceptance

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The tab is in the nav strip, named Logs | open the app | **not built** |
| 2 | The tab has two sub-tabs — Recovery and App | open the tab | **not built** |
| 3 | The App tab's list is the app's own logs, newest first | open the tab | **not built** |
| 4 | Clicking an app log shows its text | click a row | **not built** |
| 5 | The Recovery tab's tree is the dates, expanded, with the log files under each | open the tab | **not built** |
| 6 | Clicking a recovery log shows it in human-readable form | click a file | **not built** |
| 7 | The failures are shown first, in full, with their reason and their paths | open a log with failures | **not built** |
| 8 | The failures and the successes are bounded, and the bounds are stated | open a log with many of each | **not built** |
| 9 | An interrupted operation is shown with the failures | interrupt a run, open the log | **not built** |
| 10 | The figures are the log's own, not recomputed | compare with the log file | **not built** |
| 11 | The render holds neither the whole log nor the whole file in memory | open a full run's log | **not built** |
| 12 | The browser writes nothing | check the folder before and after | **not built** |
