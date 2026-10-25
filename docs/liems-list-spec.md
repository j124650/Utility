# Liem's List

> **Preliminary.** This spec states what the report is and where each of its values comes
> from. It has not been built.

---

## 1. What it is

Liem's List is a report of the pairs the verdict leaves unresolved — no original found by
size, or nothing to restore from at all.

It is a **file**, written from the rows the Finder's pass gathered. It is a report on that
scan, not a second scan.

The gathered rows are the output of the Finder's pass over every pair. The list is a
**subset** of them, selected as §2 says. Every value on the report is a field of a gathered
row.

## 2. Membership

The gathered rows, filtered by the verdict:

| Verdict | On the list |
|---|---|
| **no original — needs a decision** | yes |
| **cannot restore** | yes |
| nothing to do | no |
| regenerate child | no |
| restore parent | no |
| restore parent, then regenerate child | no |

Two verdicts leave a pair unresolved, and those two are the list.

**The verdict decides membership alone.** A filter that excludes a row from the Finder's
table does not exclude it from the list — the list is computed from the gathered rows, and a
filter over the table is a filter over the display, not over the data.

## 3. The columns

Seven columns, in this order.

| # | Column | Where it comes from |
|---|---|---|
| 1 | **Style** | the parent's folder — `ml.Media.Path`, verbatim |
| 2 | **File name** | the parent's `FileName` |
| 3 | **Angle or featured type** | the child's category name — 24 Angles, Thumbnail, Top, Bottom, Outsole, Back, Artwork |
| 4 | **Date added** | the parent's `ml.Media.DateAdded` |
| 5 | **Original size** | the parent's recorded `Size` — the length of the file as it was imported |
| 6 | **Reason** | why the original was not confirmed — the three reasons of §4 |
| 7 | **Candidate paths** | every candidate's `PathDisplay`, in rank order |

**Style** is the media library path — the folder the parent sits in, e.g. `dc06460210`. It
is the product's own folder, and it is the folder a candidate has to sit in to be a
candidate at all.

**Angle or featured type** comes from the child's category: the numbered shots are
`24 Angles`, and the featured ones carry their own type (`Top`, `Bottom`, `Thumbnail`,
`Artwork`, `Outsole`, `Back`). Where the category is `24 Angles`, the child's variant names
the particular angle within it.

**Date added** is the parent's. It is the row's date, not the file's — the library records
no date for the file itself. The child's row was always added at or after its parent's.

**Original size** is the size the file was imported at. It is the size a candidate has to
match to confirm the original, so it is what makes the row actionable: a reader compares the
candidate's size against this one.

In the report (§6) the style is the section heading and the other six are the fields of a
pair block.

## 4. The reasons

Three, read off the gathered row's verdict and the gathered row's candidates.

| Reason | Verdict | When |
|---|---|---|
| **not found** | **cannot restore** | the row has no candidates at all — the file name is not in the index |
| **name found but size differs** | **no original — needs a decision** | candidates exist, but none carries the parent's recorded size |
| **ambiguous** | **no original — needs a decision** | candidates exist, and more than one of them carries the parent's recorded size — the choice was not the only one |

The verdict splits the list in two, and the candidates split the second half in two again.
`cannot restore` is **not found**. `no original — needs a decision` is **name found but size
differs** when no candidate carries the recorded size, and **ambiguous** when more than one
does.

The reason is read off the row, never worked out a second time: the row already carries the
verdict and the candidates, and the reason follows from them.

## 5. The order

**By style, then newest first.** Style ascending; within a style, date added descending.

## 6. The report

### 6.1 What it is

A **single self-contained `.html` file** — inline styles, no scripts, no external assets,
nothing fetched when it is opened. It is a **static document**: no filters, no sorting, no
search, no expanding and collapsing. It reads like a report and prints like one.

The reader chooses where the file is written, then reads it in a window of the app (§6.7).
Nothing else is written, and nothing in the media library or the Dropbox index is changed.

### 6.2 The shape

A **header block**, then **one section per style**.

The header carries the title, the pass the report is on — its timestamp and the number of
pairs it scanned — and the breakdown of the unresolved pairs by reason. The report is only
as fresh as that pass, so the pass is named in the document, not in a footnote.

Each style gets a section: the style as its heading, and the unresolved pairs of that style
under it, newest first. The style is the section heading rather than a repeated column,
because the report's order is style ascending — the eye can skip a whole style at once.

### 6.3 The pair

A pair is a **block**, not a line in a grid: the reason and the candidate paths both need
vertical room, so the pairs stack.

| Field | Where it comes from |
|---|---|
| **File name** | the parent's `FileName` |
| **Angle or featured type** | the child's category; for `24 Angles` the variant names the angle — two lines, `24 Angles` / `Angle 22` |
| **Date added** | the parent's `ml.Media.DateAdded` — `yyyy-MM-dd HH:mm` |
| **Original size** | the parent's recorded `Size`, thousands-separated |
| **Reason** | the reason of §4, spelled out in words |
| **Candidates** | every candidate's path, in rank order |

Every field is a field of the gathered row. Nothing is re-derived.

### 6.4 The candidates

Under the pair, the candidates in rank order, one per line: the rank, the path in full, the
candidate's size, its dimensions, and what matched.

A candidate whose size equals the original size is marked in words — **size matches** —
because that comparison is the decision the reader is making. The paths are shown in full
and wrapped, not truncated: a truncated path is useless to someone about to act on it.

### 6.5 The look

The reason is spelled out in words **and** carried as the block's coloured edge — red for
*not found*, amber for *name found but size differs*, indigo for *ambiguous*. The colour is
the fast read; the word is what carries it in print, where the colour reads as a grey
value.

Paths, sizes and dates are set in a monospace face, so the columns align without a grid and
the sizes can be compared down the column. The body is set in the system sans.

### 6.6 Print

Each style section starts on a fresh page and its heading is never orphaned from its first
pair. A pair block never splits across a page. The footer carries the page number and the
report's title.

### 6.7 The window

The report is shown in a **modal window** of the app — not in the reader's browser, and not
in a tab.

The window is **modal**: it is shown with `ShowDialog()` and owned by the main window, so
the Finder cannot be used while the report is up, and focus returns to the Finder when it
closes. The report is something the reader reads and acts on; it is not a second task.

The window carries the **standard chrome** — a title bar, a close button, and the
maximize/restore buttons. It opens **maximized**, because the report is long and the reader
scrolls it, and it can be restored down. It is resizable.

Its content is a **WebView2** control that fills the window and navigates to the file that
was written. The window carries no toolbar of its own: the report's own header holds
everything the reader needs, and the engine's own print command is reached from the
keyboard (§6.8).

### 6.8 Printing

The report prints from the window with **Ctrl+P**, which the engine handles with its own
print dialog. The reader picks the printer there, or **Microsoft Print to PDF** to keep a
copy. There is no print button on the window and no print code of the app's own — the
engine's print dialog is the print path, and the `@media print` rules of §6.6 are what make
the printed page right.

A missing WebView2 runtime is reported in the window rather than shown as a blank page.

## 7. Implementation shape

- **The source** — the rows the Finder's pass produced. The report reads them; it does not
  run a pass of its own. The filter of §2 is the one the Finder service already applies when
  it produces the list.
- **The projection** — the report's rows are projected from the gathered row. No value is
  re-derived in the report except the reason (§4), which is read off the row's verdict and
  candidates.
- **The file** — written where the reader chooses it, in the HTML form of §6.
- **The window** — a `Window` in the UI project, shown with `ShowDialog()` and owned by the
  main window; it opens maximized, carries the standard chrome, and holds a `WebView2` that
  navigates to the written file.
- **WebView2** — the `Microsoft.Web.WebView2` package, referenced by the UI project and
  versioned in `Directory.Packages.props` like every other package. The control is
  initialised with `EnsureCoreWebView2Async()` before its source is set.
- **The runtime** — the Evergreen WebView2 runtime, a shared system component. If it is
  missing, the window reports it instead of showing a blank page.
- **The user data folder** — WebView2 keeps its browser profile beside the executable unless
  told otherwise, which fails when the app is installed under `Program Files`. The folder is
  set to `%LOCALAPPDATA%\Utility\WebView2`.
- **The button** — the Finder's toolbar carries a button named **Liem's List**. It asks the
  reader where the file goes, writes it, and opens the window on it. The nav entry that
  used to carry the list is hidden; the view and its view model stay registered.

## 8. Not in scope

- **The repair** — restoring a missing parent from Dropbox, regenerating a missing child
  from its parent. That is a separate spec, and it is not written yet.
- **Writing anything back.** The report reads the media library and the Dropbox index and
  writes nothing to either. It writes one file, and that file is the report.
- **Any change to the `ml` schema.** The only schema change this spec adds is on the table
  this app owns.
