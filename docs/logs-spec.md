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

**The tab READS, and it writes ONE thing: the CSV the reader asks for** (§ "Export
Failed"). It opens no log, deletes none, and starts no
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

The detail is a **list of the operations**, one entry each, virtualized so the count can
be large. The figures sit above it; a filter narrows it to the failures.

**The detail is every operation, in human-readable form.** The log is JSON Lines — one
JSON object per line (§10.5.3 of the recover spec) — which is for a parser. A reader wants
to read it.

**NO RECORD IS LEFT OUT.** A reader who opens a log sees every operation in it, however
many there are. There is no "showing the first 200" and no "and N more": a log that holds
49 operations shows 49, and a log that holds 292,400 shows 292,400.

**Because the count can be large, the detail is a LIST of the operations, not one block of
text.** A single text box holding a full run's report would be over a hundred megabytes
of text, and no text box survives that. A list does: the list is VIRTUALIZED, so only the
entries on screen exist at once, and the rest are read as the reader scrolls.

**Each operation is one entry, in the report's shape:**

```
1  Y42408OD-Featured-Bottom.jpg   (y42408od)
   reason   path/not_found/
   from     /Images-Catalogs Team Folder/…/Y42408OD-Featured-Bottom.jpg
   into     C:\_Recover\media\y42408od\78FDF6AC-…
   20:49:20 → 20:49:21   (0.6 s)
```

**The figures are at the top**, above the list — the log's own `header` and `summary`,
verbatim. Nothing is recomputed.

```
RECOVERY RUN   2026-10-31 20:48
  Filtered set       363,856 pair(s)
  Recoverable        292,400
  Recovered                38
  Failed                   11
  Downloaded             0.1 GB
  Replaced                 76 file(s)

  1  Y42408OD-Featured-Bottom.jpg   (y42408od)
     reason   path/not_found/
     from     /Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/New Balance/F27/PROCESSED/Y42408OD/Y42408OD-Featured-Bottom.jpg
     into     C:\_Recover\media\y42408od\78FDF6AC-7976-48BA-A3EB-90B956FF6C44
     20:49:20 → 20:49:21   (0.6 s)

  2  M80608F0-Featured-Top.jpg   (m80608f0)
     restored the parent, then regenerated the child
     from     /Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/New Balance/F27/M80608F0/M80608F0-Featured-Top.jpg
     into     C:\_Recover\media\m80608f0\33540F62-0C09-4746-AB7B-52A2F0CB1114
     parent   2,023,419 bytes   ·   child  52,324 bytes, 1500 × 1500
     20:48:59 → 20:49:05   (5.7 s)

  …
```

**THE OPERATIONS ARE IN THE LOG'S ORDER.** The browser does not reorder them: the log is
the record of what happened, and a reader who scrolls it is reading the run.

**A FAILED OPERATION IS MARKED, not moved.** It carries the reason, and it is marked so
the reader can find it while scrolling.

**THE READER CAN NARROW THE LIST TO THE FAILURES.** One filter, above the list: **all**
or **failures only**. The filter narrows the VIEW, never the set — every operation is in
the log, and "all" shows every one of them.

**An operation that was INTERRUPTED — a `begin` with no `end` — is marked, and says so.**
It is an operation that did not finish, and a reader looking at a run that ended badly
needs to see it.

### What the reader can see that the raw log does not say

**The browser PARSES the log, and it is the first thing that can see a broken one.** Three
defects are detected and shown, because they are the ones a reader would otherwise have
to find by eye:

| The defect | What the reader sees |
|---|---|
| **An operation that never ended** — a `begin` with no `end` | the operation, marked **interrupted**, in the list and among the failures |
| **A line that is not valid JSON** | a count of the malformed lines, above the list — never a silent skip |
| **An operation whose events do not fit** — an `end` with no `begin`, two `end`s for one operation | the operation, marked **malformed** |

**A malformed line is never dropped silently.** A log that cannot be trusted is worse
than a log that says it is damaged.

### The reason, in high-signal words

**THE RAW REASON IS NOT SHOWN. A SHORT PHRASE IS.** The raw reason is what an exception
or an API said — `path/not_found/`, or "the parent is not there to regenerate the child
from" — and neither is a thing a reader scans, nor groups well in a spreadsheet.

| The raw reason | What the reader sees |
|---|---|
| `path/not_found/` | Path not found in Dropbox |
| `path/conflict/` | Path conflict in Dropbox |
| `path/malformed_path/` | Malformed path in Dropbox |
| `path/restricted_content/` | Restricted content in Dropbox |
| `path/disallowed_name/` | Disallowed name in Dropbox |
| `path/too_long/` | Path too long in Dropbox |
| `from_lookup/not_found/` | File not found in Dropbox |
| `from_lookup/not_file/` | Source is not a file in Dropbox |
| `from_lookup/folder/` | Source is a folder in Dropbox |
| `from_lookup/malformed_path/` | Malformed source path in Dropbox |
| `insufficient_space/` | Not enough space |
| `too_many_write_operations/` | Too many write operations |
| `too_many_concurrent_write_operations/` | Too many concurrent write operations |
| `internal_error/` | Dropbox internal error |
| `other/` | Dropbox error |
| no candidate to restore the parent from | No candidate to restore from |
| the candidate carries no namespace | Candidate carries no namespace |
| the parent is not there to regenerate the child from | Parent not there to regenerate from |

**THE RAW REASON IS NEVER LOST.** The log keeps it verbatim, and the reader's tooltip on
the short phrase shows it.

**A REASON THAT IS NOT IN THE TABLE KEEPS ITS OWN WORDS.** The table is the app's and
Dropbox's own vocabulary; an unknown reason is not shortened by guesswork. A Dropbox code
nobody has seen yet is named — "Dropbox: some new code" — so it is still short and still
recognisable as Dropbox's.

### Export Failed

**THE DETAILS PANE HAS AN "Export Failed" BUTTON, and it writes the run's failures as a
CSV.** A reader who wants to triage in a spreadsheet gets one.

**THE BUTTON IS OFF when the log has no failures.** There is nothing to export, and a
button that would write an empty file is a lie.

**THE READER CHOOSES WHERE THE FILE GOES: a Save File dialog opens**, with the file name
already filled in — "Recover 2026-10-31_20-48-59 failures.csv" — the logs folder as the
starting folder, and an overwrite prompt. The browser does not choose a folder and does
not write a file nobody asked for.

**THE CSV IS ONE ROW PER FAILED OPERATION**, with the columns:

```
Number, Pair, Folder, Plan, Outcome, Reason, Source, Into, Started, Ended, Duration (s)
```

**The reason in the CSV is the SHORT reason** — the high-signal phrase above, not the
raw exception message. The raw reason stays in the log, which the reader can always open.

**THE CSV IS WRITTEN THE WAY A SPREADSHEET READS IT**: every field is quoted, an embedded
quote is doubled, a line ends with CRLF, and the file is UTF-8 **with** a BOM, because
Excel needs the BOM to read a UTF-8 file's paths correctly.

**THE CSV IS STREAMED TO THE FILE, not built in memory.** A run with 292,400 failures
writes 292,400 rows without holding them.

### The older format

**A recovery log in the OLD text format — `key=value` pairs on a line — is shown as its
own text**, with a line above saying it is in the older format. Those logs are readable
already, they are a handful, and the browser does not grow a second renderer for them.

### How the render works

**A full run's log is ~340 MB, and it is read ONCE.**

The reader streams the file line by line and builds an entry for each operation as its
lines arrive — the `begin` gives the pair and the plan, the `candidate` the source path,
the `parent_restored` and `child_regenerated` the results, and the `end` the outcome and
the times.

**The log's TEXT is never held in memory** — only the entries are, and an entry is what
the reader sees. A line at a time is read, parsed and dropped.

**The render runs off the UI thread**, and the detail says it is reading while it does.
A run of 292,400 operations is 1.5 million lines, and reading them takes seconds.

**THE LIST IS VIRTUALIZED**, so the entries on screen are the only ones the UI holds.
The app already shows a grid of 363,856 rows (the Finder's table), so a list of 292,400
entries is within what it does.

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
| 1 | The tab is in the nav strip, named Logs | open the app | **built** |
| 2 | The tab has two sub-tabs — Recovery and App | open the tab | **built** |
| 3 | The App tab's list is the app's own logs, newest first | open the tab | **built** |
| 4 | Clicking an app log shows its text | click a row | **built** |
| 5 | The Recovery tab's tree is the dates, expanded, with the log files under each | open the tab | **built** |
| 6 | Clicking a recovery log shows it in human-readable form | click a file | **built** |
| 7 | EVERY operation in the log is shown - nothing is dropped or truncated | open a log, count the entries | **built** (`tools/logs-checks`) |
| 8 | The list copes with a log of any size, because it is virtualized | open a full run's log | **built** |
| 9 | The reader can narrow the list to the failures | open a log with failures | **built** |
| 10 | An operation that never ended is marked as interrupted | interrupt a run, open the log | **built** (`tools/logs-checks`) |
| 11 | A malformed line is counted and shown, never dropped silently | damage a log, open it | **built** (`tools/logs-checks`) |
| 12 | The figures are the log's own, not recomputed | compare with the log file | **built** |
| 13 | The render holds the log's text in memory at no point | open a full run's log | **built** |
| 14 | The browser writes nothing except the CSV the reader asks for | check the folder before and after | **built** |
| 15 | The reason is shown in high-signal words, and the raw reason is in the tooltip | open a log with failures | **built** (`tools/logs-checks`) |
| 16 | "Export Failed" is off when the log has no failures | open a log with no failures | **built** |
| 17 | "Export Failed" writes a CSV of the failures, where the reader chooses | click it | **built** |
| 18 | The CSV's fields are quoted the way a spreadsheet reads them | export a failure with a comma in its path | **built** (`tools/logs-checks`) |

### A log in the OLDER format

The logs written before the format changed are `key=value` pairs on a line, and the
browser shows those as their own TEXT, because they are readable already. The reader
recognises them by the first line that says anything not beginning with a brace.

### Where the recovery logs are found

A recovery log is found by its NAME wherever it is: in the dated folder the run wrote
it into, or flat in the logs folder when it was written before the dated folders
existed. A flat recovery log is filed under the date in its own name, so every recovery
log stays reachable and there is one rule, not two.
