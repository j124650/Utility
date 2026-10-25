# Finder — Specification

**Status:** implemented. The Finder tab gathers and displays; it repairs nothing (§12.8).

Repo: `C:/_Recover/Utility` · DB: `PWR` / `apps_ml_dev` / schema `ml`

---

## 1. What the Finder does

A new **Finder** tab that gathers information about media-library pairs and displays it:

- **displays** the pairs — parent and child — with a **verdict**: whether either image is subject
  to restore, and what the Finder would do about it. The verdict is calculated, not acted on.
- **allows the user to filter** the table by fields;
- reports what it could not repair on **Liem's List**.

The Finder **writes nothing**: it reads the library, reads the Dropbox index, and reports. The
preview fetches a candidate's image and discards it (§18.7). The repair — restoring a missing
parent from Dropbox, regenerating a missing child from its parent — is a separate piece of work
with its own spec (§12.8).

The tab is a **toolbar** (tools filled in later) over a **scrollable, virtualized `DataGrid`** of
pairs.

---

## 2. Concepts

| Term | Meaning |
|---|---|
| **Pair** | One `ml.Media` parent row and one `ml.Media` child row. |
| **Parent** | `ml.Media` row where `OriginalMediaId IS NULL`. |
| **Child** | `ml.Media` row where `OriginalMediaId` = the parent's `Id`. |
| **Missing** | The file is not on disk at its expected path, **or** it is there but will not open as an image. |
| **Verdict** | What the Finder would do about a pair, worked out from the two files' states and the Dropbox match. |
| **Original** | The Dropbox file the parent was most probably imported from — decided by matching (§9.1), not recorded. |
| **Candidate** | An indexed Dropbox file that might be the original. |
| **Liem's List** | The report of pairs that could not be repaired. |

---

## 3. Data the Finder uses

**`ml.Media`** — `Id`, `Size`, `WidthInPixels`, `HeightInPixels`, `FileName`, `Path`,
`OriginalMediaId`, `MediaTypeId`, `MediaCategoryId`, `MediaSubCategoryId`, `ExternalKey`.

**`ml.MediaCategory`** and **`ml.MediaSubCategory`** — the names behind `MediaCategoryId` and
`MediaSubCategoryId`. The card shows the **name**, not the number: a reader is not served by
`5` and `4`.

**`ml.MediaType`** — decides "is this an image". The image set is:

| Id | Name | Id | Name |
|---|---|---|---|
| 1 | JPG | 2 | GIF |
| 15 | PNG | | |

**`ml.Settings`** — `MediaLibraryUncPath` (the media library root) and `MediaLibraryLocalPath`.

**`ml.DropBoxNode`** — the Dropbox index. `Name`, `SizeBytes`, `PathDisplay`, `PathLower`,
`IsFolder`, `ServerModified`, `ClientModified`. Scope: the indexed part of Dropbox —
`/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog`, recursive.

Two facts the rules depend on:

- A child's `FileName` is always blank — the on-disk name comes from the child's own `Id` (§5).
- A child's `Path` is always the parent's `Path`. The pair lives in one folder.

---

## 4. Which rows form a pair

```
parent  = ml.Media  WHERE OriginalMediaId IS NULL
                     AND ExternalKey IS NOT NULL
                     AND MediaTypeId IN (1, 2, 15)
child   = ml.Media  WHERE OriginalMediaId = parent.Id
                     AND ExternalKey IS NOT NULL
                     AND MediaTypeId IN (1, 2, 15)
```

A pair exists only when **both** rows are images. A parent whose child is not an image is not a
pair.

`ExternalKey` is the import batch a row came in with — a value like `sve2533`, shared by the
batch's parents and children alike. The Finder works on the rows that carry a batch, so
**`ExternalKey IS NOT NULL` selects them**.

Parents are identified **only** by `OriginalMediaId IS NULL`. `MediaCategoryId` is not a usable
signal: older parent rows carry `MediaCategoryId = 5` where newer ones carry `6`, so filtering on
it would drop one generation of parents.

Rows whose type is outside the image set are not pairs. The Finder neither checks them nor
reports them.

---

## 5. Where the files are

The media library root is read from `ml.Settings` at run time:

| Setting | Use |
|---|---|
| `ml.Settings.MediaLibraryLocalPath` | the library root, on local storage |
| `ml.Settings.MediaLibraryUncPath` | the same library reached over the network |

The Finder works against local storage (§12.5). Both roots are read from `ml.Settings` at run
time, so the paths are configuration.

**Resolving the local root.** The configured local path is the starting point, not the answer.
A recovered copy of the library holds the media and the PhotoLab trees side by side, so the
library's folders sit **under** the configured path, in a folder named after the network share
(`\\bbcvpn22\media` → `<local>\media`) rather than directly under it.

So the root is **resolved, not assumed**: the candidates are the configured local path and the
configured local path with the UNC path's server dropped (`<local>\media`), and the candidate
whose directories are really there wins. The check counts the candidate's immediate
subdirectories that the media rows name — counted that way round because the library was
destroyed and most of its folders are gone, so probing every known folder name would be tens
of thousands of stat calls to find the few that survived.

The same resolution applies to the UNC root.

### The naming rule

Every file in the media library is named by the **media row's own GUID**:

```
<library root>\<ml.Media.Path>\<Media.Id>          GUID, dashes included, no extension
```

- **Folder** = `ml.Media.Path`, verbatim.
- **File name** = `Media.Id` rendered as a GUID string, dashes included.
- **No extension.** The file's format is not in its name.
- **No source file name.** `ml.Media.FileName` holds the name the file had when it was
  imported (e.g. `HE101246-08.jpg`) — it is the key §9.1 matches on, not the on-disk name. For a
  child that field is always blank; the on-disk name comes from the child's GUID, like any
  other row.

Case is not significant — the share is case-insensitive. What matters is the GUID **with** its
dashes and **without** an extension.

### A pair

Parent and child share a folder, so a pair occupies one folder with two files:

```
<library root>\<parent.Path>\<parent.Id>      the parent file
<library root>\<parent.Path>\<child.Id>       the child file
```

The child is named after its own `Id`, exactly as the parent is — the naming rule above is the
same for both, and a child's file needs no further resolution because it is named by the row
that describes it.

---

## 6. The state of a file

Each of the two files is in one of three states:

| State | Meaning |
|---|---|
| **ok** | the file is at the path above, and opens as an image |
| **missing** | the file is not at the path above |
| **corrupted** | the file is at the path above, but does not open as an image |

Opening means a real decode, not a magic-byte sniff — a truncated or corrupt file is corrupted,
not ok. `File.Exists` alone is not sufficient; it says nothing about whether the file decodes.

**Missing and corrupted are the same for the verdict** — neither can be used as it stands, and
both need the same repair — so §7 treats them as one. The row still shows which of the two it
is, because "not there" and "there but broken" are different problems.

### The size check

Each file's recorded `Size` is the length of the file as it was imported. The Finder compares it
against the length of the file on disk:

| | Reading |
|---|---|
| **match** | the file on disk is byte-identical to what was imported |
| **differs** | it is not — the file was replaced, or regenerated by a different encoder |

Both sides get the check — the parent's and the child's.

A size difference is **not** a state: the file still opens, so it is still ok. It is a separate
observation about the bytes, shown in the row (§14.2) and the info area (§18.5).

A file that is **missing** has no size check — there is nothing on disk to compare. The field is
empty for that side.

The parent's check is the same fact §9.1 rule 2 relies on — `parent.Size` is the length of the
file that was imported, so a parent whose length differs was converted on the way in (§9.2) or
replaced outright.

### One file's data

Each of the two files carries the same five things, and every place that shows a file's data
shows these five:

| Field | Value |
|---|---|
| **State** | ok / missing / corrupted |
| **Size** | the row's recorded `Size` |
| **SizeCheck** | match / differs — empty when the file is missing |
| **Dimensions** | the row's recorded `WidthInPixels × HeightInPixels` |
| **Fault** | why the file would not open — empty unless corrupted |

The pair's two sets of these are what the table's columns (§14.2) and the info area's parent and
child sections (§18.5) show.

---

## 7. The verdict

Each pair gets one **verdict**: what the Finder would do about it. The verdict is calculated,
never carried out.

### The checks

The verdict rests on four **checks**. Each is marked against the pair — **✓** where it is
respected, **✕** where it is not — and the verdict is read off the marks.

| # | Check | Respected when |
|---|---|---|
| 1 | **parent file opens** | the parent is **ok** (§6) |
| 2 | **child file opens** | the child is **ok** (§6) |
| 3 | **candidates found** | the pair has at least one candidate (§10) |
| 4 | **original confirmed by size** | a candidate matches the parent's name **and** its size (§9.1 rules 1–2) |

A fifth mark is recorded when the choice was not the only one:

| # | Check | Recorded when |
|---|---|---|
| 5 | **tiebreak applied** | more than one candidate matched the size, and §9.1 rule 3 picked between them |

Every check is computed for every pair (§19). The info area shows them (§18.6).

### The verdict

From the marks:

| Parent | Child | Original found | Verdict |
|---|---|---|---|
| ok | ok | — | **nothing to do** |
| ok | missing or corrupted | — | **regenerate child** |
| missing or corrupted | ok | yes | **restore parent** |
| missing or corrupted | missing or corrupted | yes | **restore parent, then regenerate child** |
| missing or corrupted | ok | no, but candidates exist | **no original — needs a decision** |
| missing or corrupted | missing or corrupted | no, but candidates exist | **no original — needs a decision** |
| missing or corrupted | any | no candidates at all | **cannot restore** |

**Original found** is check 4. **Candidates exist** is check 3 — so *no original — needs a
decision* is checks 3 and 4 together: candidates were found, but none of them was confirmed by
size. *Cannot restore* is check 3 alone failing.

**No original — needs a decision.** The size rule found no match, so §9.1 rule 4 forbids an
automatic restore. Candidates exist, so the row shows them ranked (§10) for a human to choose
from. The pair is on Liem's List until someone does.

**Cannot restore.** The parent is missing or corrupted and no candidate exists. Nothing can be
done for this pair whatever the child's state: the child is derived from the parent, so with no
parent there is no child to regenerate either.

---

## 8. The child's regeneration rules

The rules a repair follows: read the parent file, resize it, write the result to the child's
path. The Finder does not execute them yet (§12.8), but it needs them — the child's recorded
size is what the verdict is measured against, and the rules below are what the repair spec
will start from.

The parent must be present and valid first. Where the parent is missing too, the parent is
restored (§9) and the child regenerated from what was restored.

### The resize

**Aspect ratio is preserved. Cropping is prohibited.**

The parent is scaled down until its **longer side** reaches the target's longer side; the other
side follows from the parent's own proportions. The target is a pair of dimensions, but only
its larger one is used — a target of 1500×1500 means the output's longer side is 1500, not that
the output is 1500×1500.

Where the parent's proportions differ from the target's, the output keeps the parent's
proportions and one of its sides is shorter than the target says.

### Color space

If the parent's color space is not RGB, it is **converted to RGB**. A parent in CMYK, Lab or
grayscale is converted as part of the resize, and the child is written as RGB.

A parent restored from Dropbox keeps its own color space when it was restored byte for byte
(§9.2); the child regenerated from it is still converted to RGB.

### The target

The target is the child's recorded size — `child.WidthInPixels` / `child.HeightInPixels`. Scale
the parent so its longer side equals `max(child.WidthInPixels, child.HeightInPixels)`.

The recorded size is the only surviving statement of what that child was, and the library's
files are being rebuilt from outside, so it is the target — not a hint.

### The size after a regeneration

Regenerated bytes are not byte-identical to what the importer wrote — encoder version, quality
— so a regenerated child's length will not match `child.Size`. The size check (§6) catches it:
the pair is marked for **Liem's List** (§11) and the row shows it (§14.2). The file is left in
place; it is a usable image of the right size, only its bytes differ.

`ml.media` is read-only to this app, so the recorded `Size` cannot be corrected.

---

## 9. Finding the parent's original

The original is the candidate that rule 2 selects. A repair copies it into the parent's path;
the Finder only identifies it, because the verdict turns on whether it was found (§12.8).

### 9.1 The rules

1. **Candidates** = indexed Dropbox files whose `Name` matches the parent's `FileName`.
2. **Select by size.** Keep only candidates whose `SizeBytes` equals the parent's
   `ml.Media.Size`. The importer stored `file.Length`, so an exact size match is the file most
   probably imported. The same filename sits in several folders as different crops; size tells
   them apart.
3. **Tiebreak.** If more than one size match survives, take the most probable candidate. A
   candidate is identified by its **file name and its folder**. The most probable is the one
   whose path contains **`final`** (case-insensitive); where several do, the **most recently
   modified** one (§10). **To be discussed — §13.1.**
4. **No size match → do not restore.** The pair goes on Liem's List.

**Assumption.** The Dropbox index is taken to be complete. If a filename is not found and its
folder turns out never to have been listed, the index is re-synced and the Finder re-run —
nothing is repaired from an incomplete index.

### 9.2 Color space

A restored parent is written **byte for byte** when the candidate's size and dimensions match the
parent's recorded values — `parent.Size` and `parent.WidthInPixels × parent.HeightInPixels`. That
identifies the file as the one that was imported, and converting it would change the bytes away
from what `ml.Media.Size` records.

Otherwise the downloaded file is **converted to RGB** if it is in any other color space. A parent
in CMYK, Lab or grayscale is converted on the way in. A candidate whose dimensions are not
recorded cannot be confirmed this way, and is converted.

Conversion happens **after** the candidate is selected, so the size rule in §9.1 still matches
against the source file's `SizeBytes`.

A parent restored byte for byte keeps the color space it was imported with, so the library is not
uniformly RGB. The child regenerated from it is always RGB (§8).

Where a conversion took place, the written file's byte size differs from `ml.Media.Size`.
`ml.media` is read-only to this app, so that cannot be corrected here.

### 9.3 Candidates when the name does not match

A pair whose parent is missing may have no candidate by name alone. The Finder widens the
selection set with the similarity rules of §10, and the pair's **display set** (§10) is what the
grid and the info area show (§14.4, §18.5).

A parent is **not** restored on the strength of a ranked candidate — only rule 2's exact size
match restores a file. Everything else is presented for a human to decide.

---

## 10. Ranking candidates

Every pair carries its candidates (§19.1). Two sets sit inside that list:

| Set | What it is | Used for |
|---|---|---|
| **the selection set** | candidates whose name matches the parent's `FileName` (§9.1 rule 1) | what the size rule and the tiebreak choose from (§9.1) |
| **the display set** | the selection set, plus the ones found by similarity where the pair needs it (§9.3) — ranked | what the reader sees (§14.4, §18.5) |

The display set is what the row carries. The selection set is what the verdict is computed from.

The selection set is gathered for every pair, in one set-based join with the pair query
(§19.4). The similarity widening is gathered only for the pairs that need it — a pair whose
parent is ok needs no repair, so similarity has nothing to add to it (§19.2).

### The ranks

The display set is ranked, best first:

| Rank | Condition | Reading |
|---|---|---|
| **0 — exact** | name matches (case-insensitive) **and** size matches | most probably the original |
| **1 — size only** | size matches, name differs | same bytes, renamed |
| **2 — name only** | name matches, size differs | a different crop of the same name |
| **3 — fuzzy** | neither, but names are similar | best guess |

Within a rank, order by: `final` in path first → **most recently modified** → path depth →
`PathLower` ascending.

### The date order

Candidates are ordered by **most recent modification first**, within the tiebreak order of §9.1
rule 3.

- The date is the file's **`ServerModified`** — when Dropbox last changed the file.
- `ClientModified` is when the uploading client last changed it. Where the two differ, the
  server's date is the one used: it is the moment the file became what it is now.
- A candidate with no date sorts last, among the undated ones.

**Fuzzy comparison**, in order:

1. normalized exact — upper-case, extension stripped, non-alphanumerics removed;
2. equal after stripping a trailing `-<digits>` token from either side;
3. otherwise a Levenshtein ratio on the normalized stem.

The trailing number is the parent's `MediaSubCategoryId`, and the child inherits the same value.
A candidate whose trailing number matches is the same variant; a differing one is not, and is a
weaker match.

### One candidate's data

Each candidate in the display set carries:

| Field | Value |
|---|---|
| **Rank** | 0–3, from the table above |
| **WhatMatched** | the condition that produced the rank — e.g. `name and size`, `size only` |
| **Name** | `Name` |
| **Size** | `SizeBytes` |
| **Dimensions** | `WidthPixels × HeightPixels` |
| **Path** | `PathDisplay` |
| **Modified** | `ServerModified` |

**Path** is what the preview fetches from (§18.7). **Modified** is what the date order uses
(§10's tiebreak).

---

## 11. Liem's List

A report of every pair the verdict leaves unresolved. Membership is decided by the verdict, and
the two verdicts that leave a pair unresolved are **no original — needs a decision** and
**cannot restore** (§7) — a parent that is missing or corrupted with nothing to restore from.

Two more kinds of row will join this list once the repair runs (§12.8): a pair that was acted
on but did not complete — the parent was restored but the child could not be regenerated
(unsupported format, encoder failure) — and a pair whose child was regenerated but whose length
differs from `child.Size` (§6), which is a usable image whose recorded size does not describe it.

Each row carries the parent Id, `FileName`, `Path`, expected `Size`, the verdict, the reason it
failed, and the ranked candidates that were rejected — so the list is actionable, not just a
count.

**The list is a report, built by the Finder.** It is computed on the fly from the same scan that
produced the Finder rows — no schema change, and it is only as fresh as the last scan. It can be
exported in whatever format is convenient at the time.

---

## 12. Decisions

### 12.1 The child's target size
**The child's recorded size** — `child.WidthInPixels` / `child.HeightInPixels`, larger side only.
The library's files are being rebuilt from outside, and the recorded size is the only surviving
statement of what each child was.

### 12.2 The Dropbox index
**Assumed complete.** If a filename is not found and its folder turns out never to have been
listed, the index is re-synced and the Finder re-run.

### 12.3 The child's on-disk file name
**The child's own `Id`**, the same rule as the parent (§5). Nothing further to resolve.

### 12.4 Liem's List
**A report, computed on the fly** from the same scan that produced the Finder rows, and
exportable in whatever format is convenient (§11).

### 12.5 Where the Finder runs
**Local storage.** The library root is read from `ml.Settings` at run time, so the paths are
configuration and can be pointed at whatever is mounted.
### 12.6 Scope of a full pass
**Every pair.** The pass touches every media row, selects every parent and child file, and
decodes every file that exists. The decode is the expensive part, but the storage is
local SSD and survives the load.

**Memory.** Every pair's record is held, so the whole set is in memory at once (§14.3). The
candidate lists are the part that grows: a record carries the candidates that matched by name
(§19.4), and a pair that needs a decision carries the ones found by similarity as well.

### 12.7 What a filter covers
**The whole set.** Every pair is loaded and its verdict calculated up front; filtering then
happens in memory over all of it (§14.1, §14.3).

### 12.8 What triggers the repair
**Nothing, yet.** The Finder gathers and displays. The repair — restoring a missing parent from
Dropbox, regenerating a missing child from its parent — is a separate piece of work with its own
spec, to be written before it is built.

Until then the Finder **writes nothing**: it reads the library, reads the Dropbox index, and
reports. The verdicts are the input to that future spec — each one states what would have to be
done, and the counts of each verdict are the size of the job.

The rules for that future repair are already stated here — §8 for regenerating a child, §9 for
restoring a parent — so the later spec has a starting point rather than a blank page.

### 12.9 Concurrency and rate limits
**Supervised.** A full or partial pass runs under supervision, so the concurrency and the rate
limits are settled when the repair spec is written, against the real run rather than in the
abstract.

---

## 13. Open questions

### 13.1 The `final` tiebreak
§9.1 rule 3 picks the most probable candidate when more than one size match survives: a
candidate is identified by its **file name and its folder**, and the most probable is the one
whose path contains **`final`**. Where several do, the **most recently modified** one wins
(§10).

`final` is the primary preference. Open points, for further clarification:

- **What counts as `final`?** A path segment exactly equal to `final` (a folder named
  `…_Final`), or `final` anywhere in the path — which also catches `F25_Champion_Final` and
  `Final Artwork`? The indexed paths carry it in many forms.
- **Which part of the path is searched** — the folder only, or the file name too?
- **Does `final` outrank the date, or the date outrank `final`?** As written, `final` decides
  first and the date only separates candidates that share it. The two can disagree: the same
  name often appears in a `… FINAL` folder with an older copy and in an approved folder with a
  newer one. Which is more likely to be the file that was imported?
- **Does `final` outrank the size match?** As written it does not — rule 2's size match has
  already narrowed the field, and `final` only ranks what is left.

---

## 14. UI

### 14.1 Finder tab

- A nav entry in `MainWindow.xaml` beside Dashboard / DropBox files / Media library / Settings.
- A toolbar strip — present, empty; tools come later.
- Below the toolbar, the work area split horizontally: the **table** on the left, the **info
  area** on the right, separated by a **splitter**. The info area takes 25% of the app window's
  width by default; the table takes the rest. Selecting a row fills the info area with that
  record's detail. The design is in §18.
- A `DataGrid` filling the table side, with **row virtualization on** and `CanContentScroll`.
  Every pair is loaded and its verdict calculated up front, so the table filters over the whole
  set rather than over a page (§14.3). The Dashboard tree keeps virtualization off because of
  TreeView expansion state; that constraint does not apply to a flat grid.

**The pass runs by itself the first time the tab is shown.** The tab is where the app opens,
and a pass is the only way to have anything to show, so the reader should not have to ask for
it. The **Run the pass** button stays, for a re-run.

**The tab keeps what it loaded.** Switching to another tab and back does not throw the rows
away: the rows, the filter and the selected row are all still there. A pass is long and a
re-run is not free, so the tab is a thing that is loaded once and then looked at.

The load is a full pass over the library on local storage (§12.6), so it runs in the background
with progress and can be cancelled. The grid populates as verdicts arrive.

### 14.2 Columns

One row per pair. Every value is a field of the row record (§19.1) — nothing here is
re-derived.

The record is **four columns**, and each one holds a whole side of the pair rather than a
single field:

| # | Column | What it holds |
|---|---|---|
| 1 | Icon | the verdict icon (§7, §18.4) |
| 2 | Parent | everything about the parent file |
| 3 | Child | everything about the child file |
| 4 | Verdict | the outcome and its note |

Columns 2 and 3 are **parallel**: each side's identity comes first, then the same four state
lines in the same order — **state**, **size check**, **size**, **dimensions**. Those four line
up across the row, so the two sides can be read against each other without counting.

**Parent**

| Line | Row field |
|---|---|
| File name | `ParentFileName` |
| Folder | `ParentFolder` |
| Id | `ParentId` |
| State | `Parent.State` |
| Size check | `Parent.SizeCheck` |
| Size | `Parent.Size` |
| Dimensions | `Parent.Dimensions` |

**Child**

| Line | Row field |
|---|---|
| Id | `ChildId` |
| Category | `ChildCategoryName` |
| Variant | `ChildVariantName` |
| State | `Child.State` |
| Size check | `Child.SizeCheck` |
| Size | `Child.Size` |
| Dimensions | `Child.Dimensions` |

The child's identity is shorter because the child has no file name of its own — it is a
rendition of the parent's file, written under its own GUID (§5) — and it carries the category
and variant that say which rendition it is.

**Category and variant are shown by NAME.** `ChildCategory` and `ChildVariant` keep the raw
ids from `ml.Media`; `ChildCategoryName` and `ChildVariantName` carry the words from
`ml.MediaCategory` and `ml.MediaSubCategory` (§3). A bare id says nothing to a reader.

The child's **variant is the parent's variant** — in every pair, without exception. It is
carried on the child's side because that is where the reader reads it, next to the category
that names the rendition.

**Verdict**

| Line | Row field |
|---|---|
| **Verdict** | `Verdict` |
| Note | `VerdictNote` |

**The candidates are not in the table.** They are in the info area (§18.5), which lists every
one of them with its rank, name, what matched, size, dimensions, date and path — more than the
table ever carried, and for every candidate rather than only the best. The table says what the
pair needs; the info area is where a reader goes to act on it.

**Why these fields.** The row is a card, not a spreadsheet: the reader wants the whole parent
in front of them, and the whole child beside it, so the two sides can be compared line by
line.

- **State and size check on both sides** — the two questions that decide the verdict (§7):
  does the file open, and does its length match what was recorded. A missing file and a
  corrupted one are different problems, and the row says which.
- **Size and dimensions on both sides** — the reference the candidates are judged against, and
  the numbers a regenerated child will be written to.
- **Parent id and child id** — the on-disk file names are these GUIDs (§5), so they are what a
  reader needs in order to look at or copy the file by hand.
- **The candidate's own fields** live in the info area, where each candidate's dimensions sit
  beside the parent's — pixel size is the fastest check that a candidate is the same picture
  and not a different crop of the same name.

The **Note** line carries why a verdict came out the way it did, so a reader can tell a
"nothing to do" from a "nothing to do because the file is corrupted".

### 14.3 Filtering

The whole set is loaded (§14.1), so filtering happens in memory over every row. Filters narrow
the rows on screen; they never change a verdict.

**Free text.** One substring, matched against any part of:

- the parent's file name;
- the parent's folder;
- the candidate's path — any part of the Dropbox path;
- the candidate's name — the original file name.

A row matches when the substring is found in any one of these.

**Verdict.** One or more of the §7 verdicts.

**Other fields.** Anything the verdict's required action depends on: parent state, child state,
candidate count, child category, child variant, best candidate rank.

The state filters offer all three values of §6 — **ok**, **missing**, **corrupted** — so
corrupted files can be isolated from files that are simply gone.

Filtering by **verdict** is the one that does the real work — it is how a user isolates "the
parents that need restoring" without reading every row.

A filter that excludes a row does not exclude it from Liem's List; that list is decided by the
verdict alone (§11).

### 14.4 The candidate list

The candidates are **not in the table** (§14.2). They are the info area's candidates section,
and it carries the row's full display set for the selected pair: every candidate, in rank
order, each showing the fields of §10's "One candidate's data" — name, size, dimensions, path,
date — plus the rank and what matched. The candidate list is a detail of the selected pair, not
another set of rows in the main grid.

This is where a reader goes when the verdict is *no original — needs a decision*: the
candidates are there to choose from (§18.6).

### 14.5 Liem's List tab

The same grid, filtered to the pairs the verdict leaves unresolved — no original found, or
nothing to restore from at all (§11) — with the failure reason and the rejected candidates.

---

## 15. Image manipulation library

**Magick.NET** — `magick.net-q8-x64`, Apache-2.0, version 15.0.0.

The package must:

- decode every format in the image set well enough to tell a good file from a corrupt one;
- resize without cropping, preserving aspect ratio (§8);
- **convert to RGB** when the source is in another color space — on the child's resize (§8), and on
  the parent's restore unless it was restored byte for byte (§9.2);
- re-encode the regenerated child.

The Finder uses the package to **decode** — telling a good file from a corrupt one (§6). Resize,
convert and re-encode are what the repair will need (§12.8), not what the Finder does now.

Magick.NET covers all of these and covers the image set's formats — JPG, PNG and GIF — outright.
No external dependency is needed: the image set contains no format that ImageMagick reaches
through a delegate.

`Directory.Packages.props` uses central package management, so the version goes there as a
`PackageVersion` entry.

---

## 16. Implementation shape

- **`Utility.Server`** — a Finder service, service-based access with intent-named methods, in
  the style of `IMlDataService`: `GetMediaPairsAsync(...)`, `GetDropBoxCandidatesAsync(...)`,
  `GetLiemListAsync(...)`. Reads go through `IMlDataService` for the `ml` schema's own surface
  (the settings, the media folders), and one raw ADO.NET connection for the set-based queries —
  they need a CTE, a temp table and a single round trip, which the query builder does not give.
  `ml.DropBoxNode` is read through the same connection.
- **One set-based query** for the pairs and their name-matched candidates (§19.3, §19.4). The
  lookup is a join over the Dropbox index, and the index carries
  `IX_DropBoxNode_Name` on `Name`, so both the join and the per-parent seeks have a path to it.
  `Name` and `ml.Media.FileName` share a case-insensitive collation, so the join is
  case-insensitive as the rules require (§9.1).
- **A row record** — the fields of §19.1, one per pair. It is what the UI binds to, so the
  server returns it whole rather than the UI assembling it from parts.
- **A library service** for the media library root, and for each file's five fields (§6) —
  **ok**, **missing** or **corrupted**, the recorded size, the size check, the dimensions, and
  the reason a file would not open. A file that is there but does not open is reported as
  corrupted, with the decoder's complaint, so the row and the info area can show why.
- **A verdict service** that marks the checks (§7) and reads the verdict off them. Kept apart
  from the pair query, because a verdict is computed from facts the query does not return.
- **`Utility.UI`** — `FinderView` / `FinderViewModel` and `LiemListView` / `LiemListViewModel`
  (transient, like the other pages), nav keys, the virtualized grid, the filter controls
  (§14.3), and the candidate list (§14.4). The grid holds every pair, so the view model holds
  them all — and each holds its display set of candidates (§19.1).
- **Reads** — the media library on local storage, and the Dropbox index. The Finder writes
  nothing: no file to the library, nothing to `ml`.

---

## 17. Not in scope

- The tool bar's actual tools.
- **The repair itself** — restoring a missing parent from Dropbox, regenerating a missing child
  from its parent. Separate spec (§12.8).
- Writing findings back to `ml.media` — read-only to this app. Nothing that depends on
  `ml.media` needs to follow the Finder.
- Re-running the Dropbox sync.
- Any change to the `ml` schema — the only changes this spec adds are on the table this app
  owns (§16): `IX_DropBoxNode_Name` on `Name`, and `IX_DropBoxNode_NameKey` on the
  `NameKey` computed column, which the similarity widening joins on.

---

## 18. Tab UI Design

How the Finder tab is laid out and what each part looks like. The behaviour behind these parts
is in §14; this section is the shape of them.

### 18.1 General design of the tab

The tab is a vertical stack of three bands:

```
┌───────────────────────────────────────────────────────────────────────┐
│  TOOLBAR                                                             │
├──────────────────────────────────────────┬────────────────────────────┤
│                                          │                            │
│  TABLE                                    │  INFO AREA                 │
│                                          │                            │
│  one row per pair                        │  detail of the             │
│  virtualized, scrollable                 │  selected row              │
│                                          │                            │
│                                          │                            │
└──────────────────────────────────────────┴────────────────────────────┘
                                           ↑
                        splitter — info area takes 25% of window width
```

- The toolbar spans the full width at the top.
- The table and the info area share the band below it, split left to right.
- The **splitter** separates them and can be dragged. By default the **info area takes 25% of
  the app window's width** and the table takes the rest.
- The info area shows the selected row's detail. With no row selected it shows an empty state.

The proportions are a default, not a constraint — the splitter moves.

### 18.2 Toolbar

A single strip across the top, holding the tab's tools. It is **empty for now** — the tools come
later (§17), and the strip exists so they have a place.

```
┌───────────────────────────────────────────────────────────────────────┐
│  [Scan]  │  [Filter…]  │                              │  (n) pairs    │
└───────────────────────────────────────────────────────────────────────┘
```

- Buttons are grouped left, separated by dividers.
- The right side carries **status text** — how many pairs are loaded.
- The **filter controls** (§14.3) live here, so filtering is reachable without leaving the
  table.
- During a pass the **busy screen** (§18.8) takes over the tab's work area: it carries the
  progress and the cancel.

### 18.3 Table

The table side of the split. One row per pair, the four columns of §14.2.

```
┌────┬────────────────────────┬────────────────────────┬──────────────────────────┐
│    │ Parent                 │ Child                  │ Verdict                  │
├────┼────────────────────────┼────────────────────────┼──────────────────────────┤
│  ? │ File name  HE100890H…  │ Id       A91C22F0…     │ no original — needs a    │
│    │ Folder     2025/Export │ Category 24 Angles     │ decision                 │
│    │ Id         D17343ED…   │ Variant  Angle 4       │ Note  no size match      │
│    │ State      missing     │ State    ok           │                          │
│    │ Size check —           │ Size check match       │                          │
│    │ Size       4,182,943   │ Size     97,882        │                          │
│    │ Dimensions 4176 × 4176 │ Dimensions 1500 × 1500 │                          │
└────┴────────────────────────┴────────────────────────┴──────────────────────────┘
```

- **Virtualized**, so the whole set loads without the cost of rendering it (§14.1).
- The four columns **share the pane**. Each carries a minimum width, so when the pane is
  narrower than the minimums the table scrolls sideways; otherwise it fits and scrolls only
  vertically.
- The **header** sorts by the clicked column.
- **One row selected at a time.** Selecting a row is what fills the info area.
- The columns are the icon, the parent, the child and the verdict (§14.2).

### 18.4 Table record

One row = one pair. The record is four columns: the icon, the parent, the child, the verdict.

```
│ ? │ File name    HE100890H-11.jpg │ Id          A91C22F0… │ no original — needs a │
│   │ Folder       2025/Export       │ Category    24 Angles │ decision               │
│   │ Id           D17343ED…         │ Variant     Angle 4   │ Note  no size match    │
│   │ State        missing           │ State       ok         │                        │
│   │ Size check   —                 │ Size check  match      │                        │
│   │ Size         4,182,943         │ Size        97,882     │                        │
│   │ Dimensions   4176 × 4176       │ Dimensions  1500 × 1500 │                        │
```

- **Icon first.** Every row opens with an icon that carries the verdict (§7), before any text.
- **The parent, then the child.** Each is a **stack of label/value lines** in its own column,
  because the columns no longer carry a header for each field.
- **Identity first, then the four state lines** — **state**, **size check**, **size**,
  **dimensions** — in that order, in both columns. The four line up across the row, so the two
  sides are read against each other.
- **State** is **ok**, **missing** or **corrupted** (§6) — a file that is not there and a file
  that is there but will not open are different problems, and the row says which.
- **Size check** — whether that file's length matches its recorded `Size` (§6). A file that is
  missing has no size check, because there is nothing to compare.
- **Verdict** — the outcome (§7) and its note.
- **The candidates are not in the table.** They are in the info area (§18.5), where every
  candidate is listed with its rank, name, what matched, size, dimensions, date and path.
- A value longer than its line is **truncated with an ellipsis**, the way the record is drawn
  above (`D17343ED…`). The whole value is in the info area, and the column shares the pane, so
  nothing is lost.

**The icon column.** One icon per row, in front of all the fields, showing what the pair needs:

| Icon | Meaning |
|---|---|
| ✓ | both files ok — nothing to do |
| ↓ | the parent needs restoring from Dropbox |
| ↻ | the child needs regenerating from its parent |
| ↓ ↻ | both need work |
| ? | no original matched — a human must choose a candidate |
| ✕ | nothing to restore from |

The icon says what the row needs, so a reader can scan the column and see the shape of the
damage without reading every cell. The text beside it says the same thing in words.

### 18.5 Info area

The detail of the selected row — the same record the table shows (§19.1), read a field at a
time. It is the place where the reader decides, so it carries more than the table does.

```
┌────────────────────────────┐
│ ▾ PARENT                   │
│ State        missing       │
│ Size         4,182,943     │
│ Size check   match         │
│ Dimensions   4176 × 4176   │
├────────────────────────────┤
│ ▾ CHILD                    │
│ State        ok            │
│ Dimensions   1500 × 1500   │
│ Size check   match         │
├────────────────────────────┤
│ ▾ VERDICT                  │
│      no original — needs   │
│      a decision            │
│ Note         no size match │
│  ✕ parent file opens       │
│  ✓ child file opens        │
│  ✓ candidates found        │
│  ✕ original confirmed size │
├────────────────────────────┤
│ ▾ CANDIDATES (3)           │
│ ● HE100890H-11.jpg  4,182… │
│   …/2025/Export/HE1008…    │
├────────────────────────────┤
│ ▾ PREVIEW                  │
│  ┌──────────────────────┐  │
│  │                      │  │
│  │      the image       │  │
│  │                      │  │
│  └──────────────────────┘  │
│  HE100890H-11.jpg  4176×…  │
└────────────────────────────┘
```

- **Sections**, in the order the reader needs them: parent, child, verdict, candidates,
  preview. Each section is a set of label/value rows.
- The **parent** and **child** sections show the five fields of §6's "One file's data", and the
  **fault** — why a file would not open — appears under a corrupted file, because "there but
  broken" is worth a reader's attention in a way "not there" is not, and the reason decides
  whether the file is a truncated download or something else.
- The **child** section also shows the child's **category** and **variant** by name — the same
  two lines the table's child card carries (§14.2), so the two views agree.
- The **verdict** section shows the verdict, its note, and the **checks** (§18.6).
- The **candidate list** (§14.4) is the candidates section — the row's display set in rank
  order (§10), each candidate showing the fields of §10's "One candidate's data": its **rank**,
  its **name**, **what matched**, its **size**, its **dimensions**, its **date** and its
  **path**. This is where the candidates live: they are **not in the table** (§14.2), so this
  list is the only place a reader sees them, and it shows every one of them rather than only
  the best.
- The **preview** (§18.7) is the last section.
- **Every section header carries ONE collapse/expand button**, at its left. The button collapses
  that section's body and leaves the header standing, so a reader can fold away what they do
  not need and keep what they are working on. Each section collapses on its own; the sections
  are independent of each other. The button points down while the section is open and right
  while it is closed.
- **Scrolls vertically**, so a pair with many candidates does not push the verdict out of view.
- **Empty state** — with no row selected, a short prompt naming what to select.

### 18.6 The verdict's rules

Under the verdict, the **checks the verdict rests on** (§7), one line each, so the reader can
see *why* the verdict came out the way it did rather than only what it is. Each check carries
the mark the row record holds (§19.1): **✓** where the rule is respected, **✕** where it is not.

```
✕ parent file opens
✓ child file opens
✓ candidates found
✕ original confirmed by size
```

The four lines are the checks of §7, in their order. The row's `CheckParentOpens`,
`CheckChildOpens`, `CheckCandidatesFound` and `CheckOriginalConfirmed` are what is shown — the
info area does not recompute them.

**The two candidate lines tell *no original* apart from *cannot restore*.** Candidates found but
none confirmed by size is *no original — needs a decision* (§7): the candidates are listed for a
human to choose from. No candidate at all is *cannot restore* (§7).

The candidate lines are listed only when the parent is missing or corrupted, because that is
the only case where the verdict rests on them.

Where more than one candidate matched the size, a fifth line says so:

```
✓ tiebreak applied
```

§9.1 rule 3 picked between them, so the reader knows the choice was a rule's and not the only
option.

The marks are the **rules**, checked against this pair. A rule that did not apply — the
candidate rules on a pair whose parent is ok, say — is not marked ✕; it is simply not listed,
because it was never in play.

**A pair whose parent and child are both ok** is the shortest list there is:

```
✓ parent file opens
✓ child file opens
```

Both files are present and open, so the verdict is *nothing to do* (§7) and the icon is ✓
(§18.4). The candidate checks never entered the verdict, so they are not listed — but the row
still carries them, because the candidates were selected in the same pass as the pair (§19.4).
The size checks (§6) are shown on the parent and child, not as rules, because they are
observations about the bytes rather than conditions the verdict rests on.

The list is what makes the verdict checkable by hand: a reader who doubts the verdict reads the
lines and sees exactly which one failed.

### 18.7 Preview

Under the candidate list, the **image of the selected candidate**, fetched from Dropbox. It is
what lets a reader judge a candidate by looking at it rather than by reading its dimensions.

- **One candidate at a time.** The candidate list and the preview are one selection: picking a
  candidate shows its image, and the preview follows the selection.
- **The image comes from Dropbox**, not from the media library — the candidate is a file in
  Dropbox, and the library may hold nothing at all. It is fetched from the candidate's `Path`
  (§10's "One candidate's data").
- **It appears only when there are candidates.** A pair with none shows no preview section.
- **Scaled to fit** the area, keeping the aspect ratio, with the image's own name and
  dimensions beneath it.
- **Nothing is written.** The image is fetched, shown and discarded — never written to the
  media library, never to `ml`.

### 18.8 The busy screen

A pass opens every parent and child file in the library, so it takes a while (§19.2). While it
runs, the window is covered by a **busy screen**.

- It covers the **whole window** — title bar, navigation and status bar included — for as
  long as the pass runs, exactly like the app's other busy screen (the sync overlay). A pass
  that opens every file in the library is not something to do beside other work, and a scrim
  over part of a window reads as half-finished rather than as modal.
- It is a **translucent dark scrim** with a **panel** in the middle: the stage, a progress bar,
  the counts and the cancel. The shape follows the app's existing busy screen (the sync
  overlay), so the two look like the same thing.
- It carries **the stage** — what the pass is doing right now: *Reading pairs*, *Checking
  files*, *Widening candidates*, *Building rows*.
- It carries **progress**, and the progress is **determinate for three of the four stages**:
  - *Reading pairs* — **indeterminate**. The row count is not known until the query returns.
  - *Checking files* — **determinate**: the pair count is known, so the bar is `done of total`.
  - *Widening candidates* — **determinate**: the pairs that need widening are known.
  - *Building rows* — **determinate**: one row per pair.
- It carries a **Cancel**. A pass that cannot be stopped is not something to put in front of a
  reader. Cancelling leaves the tab as it was — whatever was already loaded stays loaded.
- While it is up, the app behind it cannot be clicked, so a long pass cannot be interrupted by
  a stray click on a page that is about to be re-read anyway.

---

## 19. How the table's data is gathered

How one row of the table is assembled, from the database and the disk, into the values the grid
shows. The rules it applies are stated above; this section is the order they run in.

### 19.1 The row record

One record per pair. Every value the UI shows is a field of this record — the table's columns
(§14.2), the info area's sections (§18.5), the rules list (§18.6) and the preview (§18.7) all
read from it. Nothing in the UI is re-derived.

**Identity** — from `ml.Media`:

| Field | Value |
|---|---|
| ParentFileName | `parent.FileName` |
| ParentFolder | `parent.Path` |
| ParentId | `parent.Id` |
| ChildId | `child.Id` |
| ChildCategory | `child.MediaCategoryId` |
| ChildVariant | `child.MediaSubCategoryId` |
| ChildCategoryName | the category's name, from `ml.MediaCategory` |
| ChildVariantName | the variant's name, from `ml.MediaSubCategory` |

**Each side** — one set of the five file fields per side (§6):

| Field | Value |
|---|---|
| Parent.State / .Size / .SizeCheck / .Dimensions / .Fault | the parent file |
| Child.State / .Size / .SizeCheck / .Dimensions / .Fault | the child file |
| Parent.SizeDisplay / Child.SizeDisplay | the size in bytes, as the reader sees it |

**The verdict and its checks**:

| Field | Value |
|---|---|
| Verdict | §7 |
| VerdictNote | why the verdict came out that way |
| VerdictIcon | the icon for the verdict (§18.4) |
| CheckParentOpens | ✓ / ✕ (§7) |
| CheckChildOpens | ✓ / ✕ (§7) |
| CheckCandidatesFound | ✓ / ✕ (§7) |
| CheckOriginalConfirmed | ✓ / ✕ (§7) |
| CheckTiebreakApplied | ✓ / ✕ — empty when no tiebreak was needed (§7) |

**The candidates** (§10):

| Field | Value |
|---|---|
| CandidateCount | how many the pair has |
| Candidates | the display set, ranked — each carrying the fields of §10's "One candidate's data" |

The **best candidate** is `Candidates[0]`. It is not a column in the table — the candidates live
in the info area (§14.4), which lists every one of them.

### 19.2 The order of work

**Phase 1 — the database pass.** One pass over the data: the pairs (§19.3) and, for every
parent, the candidates whose name matches (§19.4). Both come out of the same query.

**Phase 2 — the file pass.** Every record is then visited on disk — existence, decode, length
(§19.6). This is the long part: it touches every parent and child file in the library.

**Phase 3 — widening the candidates that need it.** For a pair whose parent is missing or
corrupted and whose name matched nothing, the candidate set is widened by similarity (§9.3,
§19.4). Only these pairs need it.

**Phase 4 — the verdict and the row.** The candidates are ranked (§19.7), the checks are marked
(§19.8), the verdict is read off them (§7), and the row is assembled (§19.9).

Phase 2 is where the time goes. Phase 1 is one set-based pass over the database.

**Measured on the live library**, the pass takes 52 seconds: the pairs query 1.5 s, the file
check 35.6 s, the widening 13.8 s, the row building 1.4 s. Of the file check, about 12 s is
asking whether each of the 727,712 files is there — and almost all of them are not — and about
17 s is decoding the 215 that are.

**The file pass is PARALLEL.** The database pass finishes before the first file is opened, and
then the pairs are visited by a pool of workers — one pair each, with the parent and the child
of each pair read in turn.

The pool's size is the machine's **logical processor count**. The app asks **one service** for
that number and keeps the answer: it cannot change while the app runs, and it is asked for on
every parallel loop. The same number is the degree for the existence checks and for the
decodes — one rule for the whole file pass, rather than a tuned constant for each half.

The stages report progress as they go (§18.8): the file pass knows how many pairs it has and
how many it has done; the widening pass knows how many pairs it is widening; the row pass
knows how many rows it is building. Only the database pass has no number, because the row
count is not known until the query returns.

### 19.3 The pair query

Both rows come from `ml.Media`, joined on `OriginalMediaId`:

```
parents = rows WHERE OriginalMediaId IS NULL
                   AND ExternalKey IS NOT NULL
                   AND MediaTypeId IN (1, 2, 15)
children of a parent = rows WHERE OriginalMediaId = parent.Id
                             AND ExternalKey IS NOT NULL
                             AND MediaTypeId IN (1, 2, 15)
```

`ExternalKey IS NOT NULL` on both sides (§4) — a row that carries an import batch.

A parent whose child is not an image is not a pair and is not in the table. Rows outside the
image set are never touched.

The whole set is loaded, because the table filters over all of it rather than over a page
(§14.3), so this is one pass over the media rows, not a query per pair.

### 19.4 Selecting the candidates

The candidates come from the Dropbox index (`ml.DropBoxNode`) — the index is local, so this is a
database read, not a call to Dropbox.

**By name**, in the same query as the pairs:

```
by name = indexed files WHERE Name matches parent.FileName  (case-insensitive)
```

That is the **selection set** (§10) — what the size rule and the tiebreak choose from (§9.1).

The lookup is a **set-based join**, not a query per parent: the index carries
`IX_DropBoxNode_Name` on `Name`, so both the join and a per-parent seek have a path to it.

**By similarity**, for the pairs that need it:

```
by similarity = indexed files whose name is similar to parent.FileName   (§10's fuzzy rules)
```

The widening is **one set-based join**, not a query per pair. The pairs' names go into a temp
table, each reduced to a **join key** — the name up to its last dot, upper-cased, first eight
characters — and joined to `ml.DropBoxNode.NameKey`, which is the same key computed by the
database as a PERSISTED column.

The join key is a **pooling** key, not the decision: it says which candidates are worth
comparing. §10's fuzzy rules are applied to each one afterwards, and a candidate that is not
similar enough is dropped. Without the key the join would scan the whole index; with it, it
seeks.

The key's rule is stated in two places — the computed column's definition and
`FinderService.Prefix` — and the two must agree exactly, or the join finds nothing.

The **display set** (§10) is the two together, and it is what the row carries (§19.1). A pair
whose parent is ok carries only the name matches — similarity has nothing to add to a parent
that needs no repair.

### 19.5 Placing the files

From the pair's rows and the library root, the two paths follow the naming rule (§5):

```
parent file = <library root>\<parent.Path>\<parent.Id>
child  file = <library root>\<child.Path>\<child.Id>
```

Both paths are built the same way. The child's folder is its own `Path`, which is the parent's
folder (§3), so the pair's two files sit in one folder.

### 19.6 Checking a file

Every record is visited in the file pass. For each of the two paths, in order:

```
if not exists(path)                     -> missing
else if not opens_as_an_image(path)      -> corrupted   (keep the reason)
else                                     -> ok
```

The file is **opened, not sniffed** — a truncated file is corrupted, not ok (§6). The reason a
file failed to open is kept with the state, so the info area can show it (§18.5).

The file's length is read at the same time, and compared against its recorded `Size` for the
size check (§6). Every file that exists is decoded, because **ok** and **corrupted** are told
apart by the decode, and the row shows which.

A corrupted file is not repaired and not deleted here. It is reported, and the verdict treats it
as missing or corrupted (§6).

### 19.7 Ranking

Every candidate is given a rank and an order by §10. The ranked list is what the row's candidate
fields hold (§19.1) and what the info area lists (§14.4).

### 19.8 Marking the checks

The five checks of §7 are marked against the pair:

| Check | Marked ✓ when |
|---|---|
| CheckParentOpens | the parent is ok (§6) |
| CheckChildOpens | the child is ok (§6) |
| CheckCandidatesFound | the display set is not empty (§10) |
| CheckOriginalConfirmed | a candidate in the selection set matches the parent's size (§9.1 rules 1–2) |
| CheckTiebreakApplied | more than one candidate matched the size, and the tiebreak picked one (§9.1 rule 3) |

The verdict is read off the marks (§7).

### 19.9 Assembling the row

The record of §19.1 is filled from the three phases:

- the identity fields come from `ml.Media` in the database pass (§19.3);
- the candidate fields come from the same pass (§19.4), ranked (§19.7);
- the file fields come from the file pass (§19.6);
- the checks and the verdict come from marking them (§19.8);
- the icon is the verdict's icon (§18.4).

The **Note** carries why the verdict came out the way it did — `no size match`, `final tiebreak
used`, the reason a file is corrupted — so the row explains itself without the reader having to
recompute anything.

### 19.10 The pass as a whole

- The pass runs over every pair (§12.6), in the background, with progress and a way to stop it
  (§14.1).
- Every pair is computed and held, so the table filters over the whole set (§14.3). The grid
  populates as verdicts arrive.
- Nothing is written — no file to the library, nothing to `ml` (§17). The preview fetches a
  candidate's image and discards it (§18.7).
