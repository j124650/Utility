# Finder — Recover Selected

> **Preliminary.** This spec states what the **Recover Selected** button does and what it
> writes — the behaviour, the paths, the decisions, and the failure rules. Every question
> it raised has been answered (§11). **Nothing here has been built.**

---

## 1. What it is

A button in the Finder's toolbar, next to **Refresh** and **Liem's List**, that recovers
the pairs the Finder's **filters currently show**.

| | |
|---|---|
| **Name** | Recover Selected |
| **Where** | The Finder's toolbar, third button |
| **Acts on** | Every row the filters show **whose verdict is 1, 2 or 3** (§2) |
| **Writes** | Up to two files per pair, in the media library, on local storage |
| **Does not write** | The database, and nothing in Dropbox |

**The name is a label, not the scope.** The unit of work is the **filtered set** — the
rows the table is showing — not the row highlighted in it. A row selection is not the unit
of work. "Selected" means *what the filters selected*.

The filters are the Finder's own: the parent folder, the parent file name, the verdict
ticks, the parent and child states, the candidate count, the child category and variant,
and the best candidate's rank (`finder-spec.md` §14.3). "What is currently filtered" is a
cheap question about the table's rows, not a re-query — the whole set is already loaded
and the filters narrow it in memory.

**The filters are how the reader scopes a repair.** The two the reader reaches for first
are the **folder substring** and the **file name substring** — *recover this product's
folder*, *recover everything named TH101087*. The verdict ticks are a third way in, and
the rest narrow the set further. So the set the repair acts on is chosen by the reader,
by typing into the table's own filters.

**A filter does not have to name a verdict.** Filtering to one folder, or to one file name
substring, brings in the rows of **every** verdict, and the rows that cannot be recovered
come with them. That is expected: the set is what the reader asked to see, and the repair
does what it can within it (§2).

This is the repair that `finder-spec.md` §12.8 deferred: *"the repair — restoring a missing
parent from Dropbox, regenerating a missing child from its parent — is a separate piece of
work with its own spec"*. This is that spec.

## 2. Which rows are recovered

**The selection is in two stages, and they are different things.**

1. **The reader's filters choose the records.** The set is the rows the table shows —
   narrowed by a folder substring, a file name substring, the verdict ticks, the parent
   and child states, the candidate count, the child category and variant, or the best
   candidate's rank. A filter that names no verdict brings in rows of every verdict.
2. **The verdict chooses which of those records take part.** Among the records the
   filters left, only the ones whose verdict is **1, 2 or 3** are recovered.

So a filter by folder or by file name is **not** a filter by recoverability: it selects
the records, and the verdict then decides which of them are acted on. The rows that fall
out are still in the set, and they are reported as not recovered (§8).

**The verdict is the row's own field**, computed by the Finder's pass — the repair does
not re-derive it.

| Verdict | Parent | Child | Original | Recovered? |
|---|---|---|---|---|
| **nothing to do** | ok | ok | — | **no** — nothing is wrong |
| **1 — restore parent** | missing or corrupted | ok | confirmed | **yes** — the parent, from Dropbox |
| **2 — regenerate child** | ok | missing or corrupted | — | **yes** — the child, from the parent |
| **3 — restore parent, then regenerate child** | missing or corrupted | missing or corrupted | confirmed | **yes** — the parent, then the child from it |
| **no original — needs a decision** | missing or corrupted | any | no, but candidates exist | **no** — a human must choose |
| **cannot restore** | missing or corrupted | any | no candidates at all | **no** — nothing to restore from |

**Verdicts 1, 2 and 3 are the recoverable ones.** They are the only verdicts with a
mechanical path, and they are the only ones the repair touches.

**Verdicts 4 and 5 are not recoverable, and the repair does not try.** `finder-spec.md` §9.1 rule 4 of the
Finder spec forbids restoring a parent on anything but an exact size match, so *no original
— needs a decision* is a human's job, by the rule the verdict was decided by. *Cannot
restore* has no candidate at all, so with no parent there is no child to regenerate either.
Both are what the generated Liem's List is made of (`finder-spec.md` §11).

**Verdict 0 needs no action** — both files are fine.

**Corrupted counts as missing.** The verdict says "missing **or corrupted**", and the
repair treats them the same: the file at the path is replaced. The difference is what is
destroyed — a missing parent destroys nothing, a corrupted parent destroys a file that
exists. §9.

**One case inside verdict 2 and 3 needs the settings rather than the row.** The child's
regeneration is normally sized from the child's **recorded dimensions** (§4). A child
whose `ml.media` row records no width or no height has no target of its own, so the
target comes from `ml.Settings` instead — the product widths, chosen by the child's
category (§10.8). Measured: every child in scope has recorded dimensions, so this is the
fallback and no row needs it today.

A parent with no recorded dimensions is **not** a problem: the resize reads the parent's
proportions from the file itself, which is present and opens.

## 3. Restoring the parent

The rules are `finder-spec.md` §9, unchanged. In short:

1. **The candidate** is the row's own confirmed original — the one the Finder's pass
   already selected by `finder-spec.md` §9.1 (name match, exact size match, folder match, with the `final`
   tiebreak). The repair does **not** re-rank and does **not** re-choose.
2. **The bytes** are fetched from Dropbox, from the candidate's own path, in the namespace
   the file is indexed under.
3. **The file is written** to the parent's path, byte for byte.

**Byte for byte** means no conversion, no resize, no re-encode. A parent restored this way
keeps its own color space (`finder-spec.md` §9.2) — which is why the child regenerated from it is converted
to RGB instead (§4).

**The namespace is not optional.** A team file fetched without that scope resolves in the
personal space and comes back as `path/not_found`. The namespace is the candidate's own
field (§5.2), so the repair takes it from there and looks nothing up.

**The file is written to a temporary name and moved into place.** A download that is cut
short leaves a truncated file at the parent's path, and a truncated file is *corrupted* — a
worse state than missing, because the Finder would then report it as corrupted rather than
missing. The temporary name is in the same directory, so the move is on the same volume and
is atomic.

## 4. Regenerating the child

The rules are `finder-spec.md` §8, unchanged:

- **Aspect ratio is preserved. Cropping is prohibited.**
- The parent is scaled so its **longer side** equals
  `max(child.WidthInPixels, child.HeightInPixels)` — the child's **recorded** dimensions,
  from `ml.media`. Only the larger one is used: a target of 1500×1500 means the longer
  side is 1500, not that the output is square.
- **Color space**: if the parent's is not RGB it is converted to RGB. The child is always
  RGB.
- The result is written to the child's path.

### 4.1 How the size is computed

**The box is square and Magick does the fit** — the same shape as the importer's:

```csharp
var size = new MagickGeometry((int)target, (int)target)
{
    IgnoreAspectRatio = false,
    Greater = true          // shrink only - a parent below the target is left alone
};
image.Resize(size);
```

`IgnoreAspectRatio = false` means the image is **fitted inside** the box with its aspect
ratio preserved, so the source's **longer** side lands on the box's side and the other
side follows from the source's own proportions.

`Greater = true` means the resize happens **only if the image is greater than the box** —
so a parent smaller than the target is not scaled up. It is the same rule, expressed on
the geometry, and it is what makes the shrink-only behaviour below a property of the
resize rather than a check around it.

**The box's side is the child's declared dimension on the parent's longer axis.** If the
parent is landscape (width > height), that is the child's **declared width**; if the
parent is portrait, the child's **declared height**. The result is the child's own
declared size, to within the resizer's one-pixel rounding.

**Verified against the database**: this rule holds for **363,856 of 363,856** pairs —
20,719 landscape parents and 343,137 portrait parents, every one within one pixel of the
child's recorded dimensions. In this library the child's orientation always matches the
parent's, so the box's side is also simply `max(child.WidthInPixels, child.HeightInPixels)`.

**A hand-computed width and height is not needed.** The square box with
`IgnoreAspectRatio = false` produces the same result, and it is the importer's own shape.

**The child's recorded dimensions come with the row.** The target is
`max(child.WidthInPixels, child.HeightInPixels)` — the child's own recorded dimensions,
and the repair reads them off the row (§4.1). They are the numbers the importer wrote,
so they are the only surviving statement of what that child was.

**A parent smaller than the target keeps its own size.** `Greater = true` on the
geometry, so the fit is a shrink only: a parent whose longer side is already below the
target is left alone rather than scaled up. A 800×600 parent against a 1500 target stays
800×600.

This is a **decision, not the importer's behaviour** — the importer's own code has no
`Greater`, so it enlarges a small parent. The Finder's spec says the parent is *scaled
down* to the target (§8), so the repair shrinks only. The difference is observable: a
parent of 800×600 becomes 800×600 here, and 1500×1125 under the importer's rule.

### 4.2 The quality and the colour space

Both come from `ml.Settings`, and both follow the importer:

```csharp
image.Quality = settings.VCatQualityPercent == 0 ? 35 : settings.VCatQualityPercent;
image.ColorSpace = ColorSpace.RGB;   // only when the parent is CMYK
```

**Quality** is `ml.Settings.VCatQualityPercent` — **35** — with 35 as the fallback when
the setting is 0. The importer used a second setting for the thumbnail product,
`VCatThumbnailQualityPercent` (also 35), chosen by the child's category; the repair
follows the same split.

`VCatWidthInPixels` is 1500, which is the dominant child size (315,074 children are
1500×1500) and the V-Cat's own width; the thumbnail width is 300, and the 15,723 children
at 300×300 are the thumbnail product.

**Colour space**: the importer converts **CMYK only** — `if (image.ColorSpace ==
ColorSpace.CMYK) image.ColorSpace = ColorSpace.RGB;`. A grayscale or Lab parent is left
in its own space. The child's row records no colour space, so the parent's own is what
carries over.

### 4.3 The format is JPEG

The child's on-disk name is its GUID with **no extension** (§5), so the format has to be
given to Magick explicitly — `WriteAsync(path)` throws
`MagickMissingDelegateErrorException: no encode delegate for this image format 'Unknown'`.

**The child is always written as JPEG.** Not the parent's extension, not PNG for a PNG
parent — JPEG, every time, at quality **35**.

| | |
|---|---|
| **Format** | JPEG |
| **Quality** | 35, or the thumbnail quality for a thumbnail child (§4.2) |

**The child inherits nothing from the parent's extension.** The parent's own row records
its extension, and 340,626 of them are `.jpg` — but the child is a JPEG whatever the
parent was, so the repair reads no extension at all.

**Measured against the recorded child sizes**, regenerating at quality 35 in the parent's
format reproduces `ml.media`'s recorded byte count within 0.66–1.11× on **20 of 20**
surviving parents — the remaining spread is the encoder, not the format. Writing the
child as PNG regardless gave 6–15× the recorded size, and quality 90 gave 1.4–2.9×, so
the quality is pinned by the evidence.

**The child's path** is the child's own `Id` as a GUID, in upper-case (§5.3), in the
parent's folder — the naming rule in `finder-spec.md` §5. The child is named by the row
that describes it, so its path needs no resolving.

**The size after a regeneration will not match** the child's recorded size — the bytes are
not byte-identical to what the importer wrote. The file is left in place; it is a usable
image of the right size. `ml.media` is read-only to this app, so the recorded `Size`
cannot be corrected. The row shows the size check that does not match, which is the
reader's evidence that the bytes are new.

**A recovered pair is not on Liem's List.** The list is generated — its membership is the
verdict, nothing else — and a recovered pair reads back as verdict 0 (§10.2), so it does
not meet the criteria and is not in the list. That is the consequence of the verdict, not
a rule about the list.

**The parent is restored before the child is regenerated.** The child is derived from the
parent, so regenerating from a missing parent produces nothing. Verdict 3 does both, in
that order, and the child is generated from the **restored** parent.

## 5. What it writes, and where

**The repair copies files. It never re-derives a path.** Every path comes from a column
that is already read, and the same construction the Finder's pass uses
(`FinderLibraryService.BuildPath`).

### 5.1 The two destination paths

| File | Written when | Path |
|---|---|---|
| The parent | the verdict is 1 or 3 | `<root>\<parent.Path>\<parent.Id>` |
| The child | the verdict is 2 or 3 | `<root>\<child.Path>\<child.Id>` |

**`<root>`** — the library root, resolved by `FinderLibraryRoot.Resolve` from
`ml.Settings.MediaLibraryLocalPath` (`C:\_Recover\`) and `MediaLibraryUncPath`
(`\\bbcvpn22\media`), against the folders the media rows name. On this machine it
resolves to **`C:\_Recover\media`**, because the recovered copy holds `media\` and
`PhotoLab\` side by side under the root. The repair resolves it the same way the pass
does, so it writes where the pass read from.

**`<parent.Path>` / `<child.Path>`** — `ml.media.Path`, **verbatim**. It is the folder
name and nothing else: of the rows in scope (`ExternalKey IS NOT NULL`), **every one of
the 727,877 rows carries a single folder name**, across 27,761 distinct folders. The
1,183 `ml.media` paths that contain a separator (`24//1087//1209`) all have
`ExternalKey IS NULL` and are **out of scope** — no pair references them, so no copy
targets them.

**`<parent.Id>` / `<child.Id>`** — `ml.media.Id`, a `uniqueidentifier`, rendered as a GUID
string with dashes and **no extension**. The same rule for both files, and the same rule
the pass reads with.

**The child shares the parent's folder.** Measured: of 363,856 pairs, **363,856 have
`p.Path = c.Path`** — none differ. So the child is written beside the parent, in one
folder, as two files named by their two GUIDs.

### 5.2 The source path for the parent's copy

The parent's bytes come from the **candidate**, not from anywhere else:

| | Column | Example |
|---|---|---|
| The file to fetch | `ml.DropBoxNode.PathDisplay` | `/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Champion/F26_Champion_Final/CA05434001/CA05434001-Featured-Bottom.jpg` |
| The namespace to fetch it in | `ml.DropBoxNode.NamespaceId` | `15193064371` |
| The name to match | `ml.DropBoxNode.Name` = `ml.media.FileName` | `CA05434001-Featured-Bottom.jpg` |
| The size to match | `ml.DropBoxNode.SizeBytes` = `ml.media.Size` | `2578475` |

**The namespace is not optional.** A team file fetched without that scope resolves in the
personal space and comes back as `path/not_found`. The client's `WithPathRoot(new
PathRoot.Root(namespaceId))` is what scopes it.

**The namespace comes with the candidate.** The candidate is a row of the Dropbox index,
and the namespace is one of that row's fields (§5.2). The repair does not look the
namespace up separately — it takes the one the candidate carries.

**The candidate is found by the join the pass already makes** — `ml.media.FileName` =
`ml.DropBoxNode.Name` **and** `ml.media.Size` = `ml.DropBoxNode.SizeBytes`, plus the
folder rule and the `final` tiebreak. The repair re-uses the row's confirmed candidate
(§3); it does not run that join again.

**The candidate carries the fields the copy needs.** The candidate is a row of the Dropbox
index, and the row carries that row's own fields — the namespace, the index row's `Id`,
Dropbox's `DropboxId` for the file, and the `ContentHash`. The repair reads them from the
candidate it was given and makes no lookup of its own: the query that found the candidate
is the query that read them, and it already ran (§5.2).

### 5.3 The case of the file name

The names on disk are **upper-case** GUIDs (`0E42DFC7-409A-49DB-BEC4-D8981CC08EDC`),
while `ml.media.Id` renders upper-case when cast to text and `Guid.ToString()` renders
lower-case. The filesystem is case-insensitive, so both name the same file — verified:
`ls media/ca05434001/0e42dfc7-409a-49db-bec4-d8981cc08edc` resolves to the file stored
as `0E42DFC7-...`.

**Every file the repair writes is written in upper-case.** The parent and the child
alike — both are written to the same folder, and a folder holding one upper-case name
and one lower-case name is not uniform. The upper-case form of the GUID is what the
folder's other files use, so it is what the repair writes.

### 5.4 What is written, and what is not

**Nothing else is written.** The database is not touched — `ml.media` and `ml.DropBoxNode`
are read-only to this app, and the repair has no row to update. Nothing is written to
Dropbox. No file is written outside the library root.

**A folder that does not exist is created.** The library was destroyed: 18 of 28,944
folders survived. A copy writes into a folder that is usually not there, and the
directory is created first.

**A file is copied, not moved.** The source stays where it is: the parent's bytes come
from Dropbox, and the child's file is generated from the parent's copy.

## 6. What it does not do

- **It does not recover a row whose verdict is 0, 4 or 5.** §2.
- **It does not pick a candidate.** The candidate is the one the Finder's pass confirmed.
- **It does not write the database.** §5.4.
- **It does not write to Dropbox.**
- **It does not re-run the whole pass.** It re-reads the records it attempted, and only
  those (§10.2).
- **It does not re-read a record it recovered on an earlier run.** §10.3.

## 7. The button

In the Finder's toolbar, after **Liem's List**:

```
[ Refresh ]  [ Liem's List ]  [ Recover Selected ]
```

The same look as the other two: a white panel, the app's button style, a 12pt Segoe MDL2
icon and the caption.

**Enabled when** the filtered set contains **at least one** row whose verdict is 1, 2 or 3.
With no such row, the button is disabled — so the reader is never offered an action that
would do nothing.

**Clicking it opens the confirmation window** (§10.1). Nothing is written until the
window is answered.

The button is disabled while a repair is running.

## 8. How it reports

A repair over a filtered set is **not one outcome** — it is a set of them. The report has
to say what was done across the set, and the per-row detail has to be reachable.

**The status line** carries the summary:

| | |
|---|---|
| Recovered | `Recovered 1,234 of 1,500 pair(s).` |
| Nothing to do | `Nothing to recover in the 1,500 pair(s) shown.` |

**The per-row detail is the row's own state, re-read.** A recovered record is visited
again after the recovery, the same way the Finder gathers its data, and its verdict is
recomputed from the files as they now are (§10.2). So a recovered pair's row shows the
verdict it now has, not the one it had.

**Rows that were not recovered are reported**, and this is not a detail. A reader who
filtered to one folder wants to know that 40 of its 100 pairs were not touched, and why.
The counts come from the verdicts, which the rows already carry:

| | |
|---|---|
| Not recovered | `1,500 pair(s) shown: 1,234 recovered, 266 not recovered.` |

and the reasons are the verdicts — *no original — needs a decision* and *cannot restore*
are what the generated Liem's List is made of (§2).

**The run itself is logged** (§10.5) — one log of what happened, per record, whether the
run finished or was stopped. The status line is the summary; the log is the detail.

**The two counts are over the filtered set**, so they say what the reader's filter
produced, not what the repair was asked to do: *"1,500 pair(s) shown"* is the size of the
filtered set, and *"1,234 recovered"* is how many of them were actionable. A reader who
filtered to one folder sees that folder's numbers, and a reader who filtered to nothing
sees the whole library's.

## 9. The failure rules

The rules are **per pair**, and one pair's failure does not stop the others.

| Failure | What happens | Why it is safe |
|---|---|---|
| The parent could not be downloaded | nothing is written for that pair | the parent's path keeps whatever was there |
| The download was cut short | the temporary file is deleted | the parent's path is untouched |
| The parent could not be moved into place | the temporary file is deleted | the parent's path is untouched |
| The parent could not be read for the regeneration | the child is not written | the child keeps whatever was there |
| The child could not be written | the parent stays restored | the parent is the more valuable half |
| The library root is not configured | nothing is written | — |

**A failure is never silent.** It goes to the log (§10.5) and to the row — and every
record the run attempted is re-read, so the record's verdict is recomputed from the files
as they actually are (§10.2). A pair that failed to recover reads back as the verdict its
files deserve.

**A corrupted parent is replaced, not preserved.** The repair overwrites the file at the
parent's path, whatever is there. A corrupted parent is a file that exists and does not
open; it is replaced by the confirmed original, and the corrupted bytes are gone.

**There is no backup of the replaced file.** The file is named by its GUID with no
extension, so nothing identifies it but its contents — and what replaces it *is* the
original, recovered byte for byte. A corrupted file that is replaced with a valid one is
an improvement, and the bytes it loses are the bytes of a file that could not be opened.

**A half-finished pair is not a lost one.** The parent's restore and the child's
regeneration are independent: if the child fails, the parent is still restored, and the
pair is still recoverable by a second run.

## 10. The decisions

These were settled in discussion. Each says what the repair does.

### 10.1 The reader is asked first

**The button opens a confirmation window before it writes anything.** A repair overwrites
files that exist — every corrupted parent and every corrupted child — so the reader is
shown what is about to happen and says yes. **No file is written before the window is
answered.**

**The window's data comes from the rows the table is showing, and from what the recovery
would do to them.** The window reads the same rows the repair will act on — the table's
filtered collection — and it asks two questions of them:

| | |
|---|---|
| **What is displayed** | the rows the table is showing, and the filter that produced them |
| **What is the subject of recovery** | of those, the ones whose verdict is 1, 2 or 3 (§2) |

**The window does not query anything.** It reads the rows the table already holds and the
verdicts they already carry. So what it says is exactly what the table shows and exactly
what the repair will do — there is no second count that could disagree with the first.

Every figure the window shows is a field the row already carries:

| The window shows | Read from the row |
|---|---|
| How many rows are displayed | the count of the table's rows |
| How many are the subject of recovery | the count of those whose verdict is 1, 2 or 3 |
| How many are not, and why | the verdicts of the rest — 0, 4 and 5 |
| How many parents will be copied | the verdict: 1 and 3 |
| How many children will be regenerated | the verdict: 2 and 3 |
| How much will be downloaded | the sum of the confirmed candidate's size — `Candidates[0].Size` |
| How many existing files will be replaced | the rows whose parent or child is `corrupted` |
| How many folders will be written | the distinct `ParentFolder` values of the subject rows |
| Every row of the details table | the same rows: the pair, the candidate, and the path the repair will write (§10.1.4) |

**The download figure is the confirmed candidate's size, not the parent's.** The confirmed
candidate is the one the pass already selected by exact size match (§3), so its size is
the parent's recorded size — and it is the file that will actually be fetched, so it is
the figure to quote. The row carries it as `Candidates[0].Size`.

### 10.1.1 What the window says

The figures are read off the rows, and each is a number the reader can act on. The
example below is over a set of 1,500 pairs:

```
Recover 1,500 pair(s)?

  1,234 pair(s) will be recovered
       412  parent restored from Dropbox
       611  child regenerated from its parent
       211  parent restored, then the child regenerated from it

     494 GB will be downloaded
      27 existing file(s) will be replaced
  14,131 folder(s) will be written to C:\_Recover\media

    266 pair(s) will be left alone - they need a decision, or there is
    nothing to restore them from
```

**The lines, and why each is there:**

| The line says | It answers |
|---|---|
| *how many pairs will be recovered* | the size of the job, in the reader's own terms |
| *the breakdown by verdict* | **what will actually happen** — a parent copy is not a child regeneration, and the reader is about to authorize both |
| *how much will be downloaded* | the cost — 494 GB is a different commitment from 4 GB, and it is the number that decides whether this is a good idea |
| *how many existing files will be replaced* | **what is destroyed.** These files exist and will be overwritten, with no backup (§9) |
| *how many folders will be written* | the library was destroyed; the repair writes into 14,131 folders, and the reader should know that is what is happening |
| *how many will be left alone, and why* | the scope is honest — the recovery does not touch them, and they are not the reader's fault |

**The download figure and the file count are the two that carry a warning.** The rest is
information. "494 GB" and "27 existing files will be replaced" are the sentences that
change a mind.

**The figures say how big the job is; the table below says what the job is** (§10.1.4).
Neither replaces the other: a count is what a reader decides on, and a list is what they
check a count against.

### 10.1.2 A red warning for a long run

**Above a threshold of selected rows, the window says in red that this will take a long
time.** The threshold is **100 rows** — the reader's own figure.

The warning is a line in the window, not a second dialog and not a typed word:

```
              This will take a long time - 1,234 pair(s) and 494 GB.
```

**The threshold is on the pairs that will be recovered.** The line names the recoverable
count and the download volume, so those are what the threshold must agree with — a
warning about a long time that fires over a set with nothing to recover would be a lie,
and one that stays quiet over a set of 90 large downloads would be a silence.

**Why a line and not a typed confirmation.** A typed word is a second gate, and a gate
that is always there stops being read — the reader types it without reading it. A red
line costs nothing to pass and is still read when the number is big. The threshold is a
judgement, and 100 is the judgement: below it the run is minutes, above it the reader
should think about filtering more narrowly first.

**The threshold is a constant, not a setting.** It is a statement about when a run stops
being a quick one, and it does not change with the machine or the connection.

**The duration itself is not quoted.** The width that turns 494 GB into a duration has not
been measured (§10.6), so the window says how much, not how long. A made-up estimate
would be worse than none.

### 10.1.3 The window's shape

**The window is split horizontally: the information above, the details table below.** The
information is the figures — how many pairs, what will happen to them, how much will be
downloaded, how many files will be replaced. The details table is the answer to *which*
pairs and *which* files, with the real paths. The figures say how big the job is; the
table says what it is.

```
┌────────────────────────────────────────────────────────────────────────────┐
│  Recover 1,500 pair(s)?                                                     │
│                                                                            │
│  1,234 pair(s) will be recovered                                            │
│       412  parent restored from Dropbox                                     │
│       611  child regenerated from its parent                                 │
│       211  parent restored, then the child regenerated from it              │
│                                                                            │
│     494 GB will be downloaded                                                │
│      27 existing file(s) will be replaced                                   │
│  14,131 folder(s) will be written to C:\_Recover\media                      │
│                                                                            │
│    266 pair(s) will be left alone - they need a decision, or there is       │
│    nothing to restore them from                                             │
├────────────────────────────────────────────────────────────────────────────┤
│  1,845 action(s), in the order they will be performed                       │
│  showing the first 200                                                     │
│  ┌───┬──────────────────┬──────────────────┬────────────────────┬────────┐ │
│  │ # │ Pair             │ Action           │ From               │ To     │ │
│  ├───┼──────────────────┼──────────────────┼────────────────────┼────────┤ │
│  │ 1 │ CA05434001       │ restore the      │ /Images-Catalogs…  │ C:\_Re…│ │
│  │   │ CA05434001-Fe…   │ parent           │ CA05434001/CA05…   │ media\…│ │
│  │ 2 │ CA05434001       │ regenerate the    │ C:\_Recover\media\… │ C:\_Re…│ │
│  │   │ CA05434001-Fe…   │ child            │ CA05434001\F4D13B… │ media\…│ │
│  │ 3 │ RL00016100       │ restore the      │ /Images-Catalogs…  │ C:\_Re…│ │
│  │   │ RL00016100-05.jpg│ parent           │ RL00016100/RL000…  │ media\…│ │
│  └───┴──────────────────┴──────────────────┴────────────────────┴────────┘ │
│                                                        [ Cancel ] [Recover]│
└────────────────────────────────────────────────────────────────────────────┘
```

**A modal dialog, owned by the main window** — the same way Liem's List's window is
(§14.5), so the Finder cannot be used while it is up and focus returns to the Finder when
it closes.

**Two buttons: `Cancel` and `Recover`.** `Cancel` is the default — the one that is
selected when the window opens, and the one the Escape key closes it with. A window that
destroys files should not open with the destructive action already chosen.

**The title states the scope**, so the window is identifiable in the task bar and in a
screenshot: *Recover Selected — 1,234 pair(s)*.

**The window is wide, because the details table carries paths.** 1,100 pixels wide and
640 tall, resizable, with a floor of 800 × 500. The information block above it keeps its
height whatever the set's size, and the table takes the rest — the table is bounded, so
the window does not grow with the set either.

**The information is above the table, and the table is below it.** The figures are
read first — they are the decision — and the detail is what the decision is about. The
split is horizontal, so the two sit one above the other and both are visible at once.

### 10.1.4 The details table

**The table lists the actions, one per row, in the order they will be performed.** It
is the answer to *what exactly is going to happen*, and it names the real paths — the
file in Dropbox, and the file on disk.

| Column | What it says |
|---|---|
| `#` | the action's position in the execution order |
| `Pair` | the parent's file name and its folder — which pair the action belongs to |
| `Action` | what the action does: *restore the parent* or *regenerate the child* |
| `From` | where the bytes come from — the candidate's Dropbox path, or the parent's path on disk |
| `To` | the real path on disk the action writes to |
| `Detail` | the candidate's size in bytes, or the child's target dimensions |

**One row per action, not one per pair.** A pair whose parent and child both need work
produces two rows: the parent is restored, and then the child is regenerated from it.
The order is the execution order, and the parent comes first because the child is
derived from it.

**The order is the order the repair will use.** The pairs are put in the recovery's own
order — most recent first (§10.11) — and the repair works through the subject rows in
that order. So the table's first row is the first thing that happens, and the last row is
the last.

**A pair that is not recovered has no row.** The table is the list of what will be
**done**. The pairs that are left alone are counted above the table, and why they are
left alone is their verdict.

**The paths are the real ones.** The `To` column is the file's path in the library — the
resolved root, the pair's folder, and the upper-case GUID the file is named by (§5.3).
The `From` column is the candidate's own Dropbox path for a restore, and the parent's own
path on disk for a regeneration — the file the child is made from.

**The table is bounded, and it says what it is bounded to.** It shows the **first 200
actions** of the run — the first 200 things that will happen, in the order they happen —
and the line above it carries the whole count.

This is a confirmation window, not a place to browse a job. The figures above the table
are what the reader decides on; the table is what they check those figures against, and a
sample of two hundred actions with real paths does that. A run over the whole library is
about 400,000 actions, and listing all of them would make the window a data grid with a
dialog wrapped around it.

**The bound is 200, and it is a constant.** Two hundred actions is enough to see the
shape of a folder's work and to spot anything that is not what it should be — a path in
the wrong folder, a size that is out of line with its neighbours. It is also small enough
that the window stays a window.

**When the set is smaller than the bound, every action is shown** and the line says so.
A set of twenty pairs shows all of its actions, and the reader sees the whole job.

**The order is the execution order, so the table is not sortable.** Sorting the sample
would put the actions in an order that is not the order they will happen in, which is
exactly the thing the table exists to tell the truth about.
### 10.2 The recovered records are re-read

**A record whose media was recovered is re-read the same way the Finder gathers its
data** — the file is visited, its state and size check are read, and its verdict is
recomputed from those. So a recovered pair's row shows the verdict it now has, not the
one it had.

The re-read is the pass's own per-file visit (`IFinderLibraryService.GetFileStateAsync`),
so a recovered parent that opens is `ok`, and one that does not is `corrupted` — the
three states of `finder-spec.md` §6 are enough, and **no fourth state is needed** (§10.7).

### 10.3 Recovered records are skipped

**A second run skips the records already recovered.**

**A record is skipped when its files say it is done.** A recovered record is re-read
(§10.2), so the row carries the verdict its files now deserve — and a pair whose parent
and child both open is verdict 0, which is not recovered (§2). So the next run over the
same set finds nothing to do, without anything remembering the last run.

**The log is not how the skip works.** The log is the record of the run for the reader's
own post-analysis (§10.5). It is not consulted to decide what to skip, because it is
named for the app run that wrote it (§10.5.1) — a recovery that was interrupted and a
later one would not be able to read each other's logs.

**This holds across a restart.** A recovered file is a file on disk, and the verdict is
recomputed from the files — so whether the app was closed between the two runs or not
makes no difference. The run does not re-read the log to know what it did last time; the
library does.

### 10.4 The busy screen

**The repair runs on the Finder's busy screen**, the one the pass already uses (`finder-spec.md` §18.8),
showing:

| | |
|---|---|
| How many are selected | the size of the filtered set, and how many of those are recoverable |
| Determinate progress | how many are done, out of how many |
| A stop button | the run can be stopped, and the log says how far it got (§10.5) |

**The recovered records are re-read as the run goes** (§10.2), so the count of done rows
is the count of records actually finished, not a tick per file.

**The screen shows the current progress and nothing else.** The count of done rows out
of the count to do, and the stop button. The busy screen is the Finder's own (§18.8 of
the Finder spec), so it is where the reader already looks for progress — and it is a
modal over the whole window, so it cannot be missed.

### 10.4.1 The file the run is on

**The busy screen also names the file the run is working on right now** — "Downloading
X" or "Resizing X". It is there because "1,200 of 160,000 pair(s)" says how far along
the run is and not what it is doing; the file says the second thing.

| The line says | |
|---|---|
| **Downloading** | the parent being restored, by the file name in Dropbox |
| **Resizing** | the child being regenerated, as `{category} of {parent file name}` — the child is written under a GUID with no extension, so it is named by the product it belongs to and what is being produced of it |

**The line appears only when the run is at WIDTH 1** — one file at a time. At a wider
width several files are in flight, and naming one of them would say something untrue: the
reader would think the run is on that file and nothing else. So the line is there at
width 1 and gone above it, and it comes and goes with the work.

**The line belongs to the RECOVERY and to no other phase.** It is cleared when the
recovery ends — finished, cancelled or failed — and before the re-read begins, because
the re-read is a different phase and "Downloading X" left on screen while the table is
being re-read would be a lie. The line is also HIDDEN when there is no file, so the
panel closes up rather than carrying an empty row.

### 10.4.2 How many are recovered

**A second line says how many pairs are RECOVERED so far, out of how many the run is
recovering** — "12,340 of 160,000 recovered".

**It is a different question from the count above it.** The count says how far the run
has GOT; this says how many of those are WHOLE. They differ whenever a pair fails, and
the reader watching a long run wants the second one — a run that has attempted 12,340
pairs and recovered 12,100 is a different fact from one that has recovered 12,340.

**It rides on the FILE report**, which arrives at every file. The count reports come
once a hundredth of the run, which for a run of 160,000 pairs is every 1,600 pairs —
far too coarse to watch. The file report comes at every file, so the recovered count
moves with the file name.

**It appears only at width 1**, for the same reason the file line does: the count
rides on that report, and a wider run names no file.

### 10.4.3 How long the run will take

**The same line also says how long the run is expected to take still** — "about 2h 40m
left" — so the reader can decide whether to let it run or come back tomorrow.

**The estimate is the LAST pair's time, multiplied by the pairs still to go.** The last
pair is the best measure of the run's pace there is: it is the most recent thing the run
actually did, at the run's current pace. A run's average would lag — it would still be
carrying the slow pairs from an hour ago, when the network was busy and the files were
large.

**The estimate is made from the pair BEFORE the one in flight.** A pair's own duration
is not known until it is done, so the estimate at any moment is built on the last
completed one.

**There is no estimate until one pair has finished.** Before that there is nothing to
base one on, and a guess of "0s" would be worse than nothing.


**It is width 1 only**, for the same reason the count is: it rides on the file report.
At a wider width the pairs overlap, so no single pair's duration describes the run's
pace — a number there would be a guess dressed as a measurement.

### 10.5 One log of what happened

**There is one log**, and it answers both "what did the recovery do" (§10.3) and "what
happened when the run was stopped".

**It is written through a provider, so the log's home can change without the recovery
knowing.** The recovery writes log records; where they go is the provider's business.
Today the provider writes JSON Lines; later it can write a table, and the recovery does
not change.

| | |
|---|---|
| The provider | writes the records — JSON Lines now, a table later |
| The record | one per pair, saying what was done to it |
| The reader | the user, for their own post-analysis |

**A file is enough** for a log that is only ever read by the user. The database is
read-only to this app, so a table would have to be created by the system that owns `ml`
— the provider is what makes that a later decision rather than a blocker.

### 10.5.1 Where the log lives

**Beside the app's own log**, in `%LOCALAPPDATA%\Utility\logs\` — the folder the app's
own log file is written to (`App.xaml.cs`, `FileLog.TryOpen`). **Not the temporary
folder**: a log in `%TEMP%` is a log that gets cleaned up, and this is the record of what
happened to the media library.

**Each recovery's log goes in a FOLDER named for the date the recovery started** — and the
file sits inside it:

```
%LOCALAPPDATA%\Utility\logs\2026-10-28\Recover 2026-10-28_18-20-01.log
```

| | |
|---|---|
| **Folder** | `%LOCALAPPDATA%\Utility\logs\<the date the recovery started>\` — `yyyy-MM-dd`, the month a **number** (`2026-10-28`, not `2026-Oct-28`) |
| **File** | `Recover 2026-10-28_18-20-01.log` — the same stamp as the folder, so the file names the moment inside the day that holds it |
| **One folder per day** | the day's logs sit together, and the day's folder says what it holds |
| **One file per run** | two recoveries on the same day share the folder and still get separate files |

**The date is the recovery's START, not its end.** A recovery that starts at 23:59 and
ends at 00:05 the next day belongs to the day it began — the procedure's start is what
names it. The moment is captured when the procedure starts, before the settings and the
library root are read, so it cannot slip across midnight while the run gets under way.

**The month is a number, so the folders sort as dates.** `2026-10-28` comes before
`2026-10-29` comes before `2026-11-01`. A folder named for a month would break that, and
the folders are read in date order.

**The app's own log stays flat**, one file per run, named for the moment the app started
(`2026-10-26_18-20-01.log`). It is the app's own record of its own run, and it does not
move into a dated folder — only the recovery's log does.

**No colon in the name** — a colon is not a legal character in a Windows file name, so
the time carries a dash between the hours and the minutes. The same rule as the app's own
log file, and as Liem's List's report.

### 10.5.2 What a record carries

One record per pair the run touched. The fields are **the workflow's own steps** — the
ids each step used, what was copied and where it went, what was resized on behalf of
which id, and what the file became.

**The workflow, and the ids each step uses:**

| Step | What it does | The ids it uses |
|---|---|---|
| **1. the pair** | the row the recovery acts on | `ml.media` — the parent's `Id` and the child's `Id` |
| **2. the candidate** | the file in Dropbox the parent's bytes come from | `ml.DropBoxNode` — the node's `Id` and its `DropboxId` |
| **3. the copy** | the bytes fetched, and the file written | the parent's `Id` — the file is **named** by it |
| **4. the resize** | the parent read, the child written | the parent's `Id` in, the child's `Id` out |
| **5. the result** | what the file is now | the file's own length and dimensions |
| **6. why not** | for a pair that was not recovered | the reason |

**The record:**

| Field | The step | What it says |
|---|---|---|
| `parent` | 1 | the parent's `ml.media.Id` — the GUID, which is also the file's name on disk |
| `child` | 1 | the child's `ml.media.Id` — the GUID the child's file is named by |
| `parent_name` | 1 | `ml.media.FileName` — the name the candidate was matched on |
| `folder` | 1 | `ml.media.Path` — the folder both files sit in |
| `node` | 2 | the candidate's `ml.DropBoxNode.Id` — the index row the bytes came from |
| `dropbox_id` | 2 | the candidate's own `ml.DropBoxNode.DropboxId` — Dropbox's object id for the file |
| `namespace` | 2 | the candidate's `ml.DropBoxNode.NamespaceId` — the scope the file was fetched in |
| `source` | 2, 3 | the candidate's `ml.DropBoxNode.PathDisplay` — the full Dropbox path |
| `content_hash` | 2 | the candidate's `ml.DropBoxNode.ContentHash` — Dropbox's own hash of the bytes |
| `copied_to` | 3 | the parent's full path on disk, where the bytes were written |
| `copied_bytes` | 3 | how many bytes were written |
| `resized_for` | 4 | the child's `ml.media.Id` — the id the resize was done on behalf of |
| `resized_from` | 4 | the parent's full path on disk — the file the resize read |
| `resized_to` | 4 | the child's full path on disk, where the result was written |
| `target` | 4 | the size the resize aimed at — the child's recorded dimensions, or the settings' width (§10.8) |
| `result` | 5 | what the file is now — its length, and its dimensions |
| `reason` | 6 | for a pair that was not recovered, why |
| `plan` | — | what the run set out to do to the pair |
| `outcome` | — | how it turned out |
| `ts` | — | the moment the line was written |
| `n` | — | the operation's number within the run — what ties an operation's lines together |

**Every id is the one the step actually used.** The `parent=` and `child=` are the media
row's own GUIDs; the `node=` and `dropbox_id=` are the index row's own ids and Dropbox's
object id. So a post-mortem can name every object involved — the media row, the index
row, the Dropbox file, and the two files on disk — and nothing is inferred.

**`content_hash` is the check that the copy was faithful.** It is Dropbox's own hash of
the bytes, and it is the one value that says *the file in Dropbox and the file in the
library are the same file* — not merely the same length. It is what makes "100%
identify what went right" answerable rather than merely plausible.

**`copied_to` and `resized_to` are the paths, not the ids.** The ids name the rows; the
paths say **where** the file ended up. A post-mortem that says "the parent F4D13B32… was
restored" is not complete without "to C:\_Recover\media\ca05434001\F4D13B32…".

**`target` is what the resize aimed at**, so a regenerated child can be checked against
what the record says it should be — and against the child's recorded dimensions, which is
where the target came from.

**The result is what the file is now**, measured after the write: the parent's length
and the child's dimensions. A record that says only what was *attempted* cannot be
checked against the library; one that says what was *written* can.

**A FAILED DOWNLOAD NAMES THE FILE IT COULD NOT FETCH.**
`path/not_found/` has to leave a path behind, or the reader has nothing to search on:
the error names a condition, not a file. **The path is on the record's own `candidate`
line**, beside the namespace and the content hash, and **it is on the app's own log
line** for the failure — the one that reaches the Windows event log.

**The reason is a FIELD of its own, not part of the outcome's text.** In the format this
log used to have — `key=value` pairs on a line — the reason sat mid-line, and a reason
carrying spaces (and several do: *"the parent is not there to regenerate the child
from"*) could not be told from the tokens after it. **JSON Lines is what settles it**:
every value is a JSON value, so a reason is a string of any shape, and a path with spaces
in it is a path with spaces in it.

**A record is written for every pair the run touched, including the ones that failed.**
A log that only holds the successes cannot answer "what happened to the ones that did
not work".

**Every field the record needs is a field the candidate carries.** The candidate is a row
of the Dropbox index, so it holds the path, the size, the modification date, the
namespace, the index row's `Id`, Dropbox's `DropboxId` and the `ContentHash` — all of
them, because they are all columns of the same row, and the query that found the
candidate is the query that read them.

**And the repair looks nothing up for itself.** It reads the row's candidate for the path,
the size and the scope to fetch in, and the index row's own fields come with them. Every
field in the record is a field the workflow already had in its hands.

### 10.5.3 The format: JSON Lines, for post-mortem parsing

**The log is JSON Lines — one JSON object per line — and it is meant to be parsed.** Each
line stands on its own and can be read with any JSON reader; a reader asks a question of
the file afterwards.

**An operation is a GROUP of lines, and the `n` field says which operation a line belongs
to.** The log is written as the work happens, so an operation's lines are written as its
values become known:

```
{"type":"header","format":1,"started":"2026-10-26T18:24:07.412+04:00","shown":1500,"recoverable":1234,"folders":14950}
{"type":"begin","n":1,"ts":"2026-10-26T18:24:07.412+04:00","parent":"f4d13b32-fab2-451f-83f9-0961c38b6286","child":"8fdd66c2-c1c5-4fbe-b64b-35302c7f96a4","parent_name":"CA05434001-Featured-Bottom.jpg","folder":"ca05434001","plan":"restore the parent, then regenerate the child"}
{"type":"candidate","n":1,"ts":"2026-10-26T18:24:07.412+04:00","node":123984,"dropbox_id":"id:sMa5MD0C7uMAAAAAAAfj2Q","namespace":"15193064371","content_hash":"3eeb5f91c234eb6bb3fe0a5f469b42fc5a24a82f5a76bf3a7c0458f9623b11b6","source":"/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Champion/F26_Champion_Final/CA05434001/CA05434001-Featured-Bottom.jpg"}
{"type":"parent_restored","n":1,"ts":"2026-10-26T18:24:12.006+04:00","copied_to":"C:\\_Recover\\media\\ca05434001\\F4D13B32-FAB2-451F-83F9-0961C38B6286","copied_bytes":2578475}
{"type":"child_regenerated","n":1,"ts":"2026-10-26T18:24:12.418+04:00","resized_from":"C:\\_Recover\\media\\ca05434001\\F4D13B32-FAB2-451F-83F9-0961C38B6286","resized_for":"8fdd66c2-c1c5-4fbe-b64b-35302c7f96a4","resized_to":"C:\\_Recover\\media\\ca05434001\\8FDD66C2-C1C5-4FBE-B64B-35302C7F96A4","target":1500,"result":"size 2578475, 1500 × 1500"}
{"type":"end","n":1,"ts":"2026-10-26T18:24:12.420+04:00","outcome":"recovered"}
```

**The types are the CONTRACT, and the header names their version.** `format` in the
header is the version of the format the file is written in. A parser reads it and knows
which contract it is reading — and when the format changes, the version changes with it,
so a parser written against version 1 still reads every version 1 file.

| `type` | When it is written | What it carries |
|---|---|---|
| `header` | once, when the log opens | the format's version and the run's figures |
| `begin` | when the operation starts | the pair's ids and the **plan** |
| `candidate` | when the operation starts | the Dropbox file the bytes come from |
| `parent_restored` | after the download | where the bytes went, and how many |
| `child_regenerated` | after the resize | what was read, what was written, and what it became |
| `end` | when the operation ends | the **outcome**, and the **reason** when it failed |
| `summary` | once, when the run ends | the run's outcome |

**The plan is at the start and the outcome is at the end, because only one of them is
known when the line is written.** The plan comes from the verdict, so it is known before
any work happens. The outcome is not known until the pair is done.

**A pair that failed is an operation too, and it ends like any other** — with the reason
in a field of its own, not inside the outcome's text:

```
{"type":"end","n":2,"ts":"2026-10-26T18:24:09.880+04:00","outcome":"not recovered","reason":"path/not_found/"}
```

**An operation interrupted by a crash leaves its `begin` and no `end`** — and that is the
evidence it was attempted and did not finish. This is why the log is written as the work
happens.

**The fields are the workflow's own steps** (§10.5.2), and the names are the same ones
the table above already used — so the vocabulary carries over and nothing is renamed:

| Field | The step | What it identifies |
|---|---|---|
| `parent` | 1 | the parent's `ml.media.Id` — the GUID, and the file's name on disk |
| `child` | 1 | the child's `ml.media.Id` |
| `parent_name` | 1 | `ml.media.FileName` — the name the candidate was matched on |
| `folder` | 1 | `ml.media.Path` |
| `node` | 2 | the candidate's `ml.DropBoxNode.Id` |
| `dropbox_id` | 2 | the candidate's `ml.DropBoxNode.DropboxId` |
| `namespace` | 2 | the candidate's `ml.DropBoxNode.NamespaceId` |
| `content_hash` | 2 | the candidate's `ml.DropBoxNode.ContentHash` |
| `source` | 2, 3 | the candidate's full Dropbox path |
| `copied_to` | 3 | where the parent's bytes were written |
| `copied_bytes` | 3 | how many |
| `resized_from` | 4 | the file the resize read |
| `resized_for` | 4 | the child's id the resize was done on behalf of |
| `resized_to` | 4 | where the result was written |
| `target` | 4 | the size the resize aimed at |
| `result` | 5 | what the file is now |
| `reason` | 6 | why a pair was not recovered |
| `plan` | — | what the run set out to do to the pair |
| `outcome` | — | how it turned out |

**Every record names every object involved**, so a post-mortem can say exactly what
happened and where — and none of it is inferred:

| | |
|---|---|
| **Which pair** | the parent's and the child's `ml.media.Id` — the GUIDs, which are the files' names on disk |
| **Which file in Dropbox** | the index row's `Id`, Dropbox's own `DropboxId`, the namespace, the path, and the content hash |
| **What was copied, and where** | the source path and the destination path, and the byte count |
| **What was resized, for whom** | the parent's path in, the child's id, the child's path out, and the target size |
| **What the file became** | its length and its dimensions, measured after the write |

**The `parent` is the anchor.** It is the GUID, so the record and the file on disk are
the same thing — a reader can take the `parent` from a log line and find the file, and
take a file and find its log line. Nothing in the record depends on a path that could
have changed.

**The `content_hash` is what makes "the copy was faithful" answerable.** It is Dropbox's
own hash of the bytes, so the file in Dropbox and the file in the library can be
compared, not merely the lengths. A length match says the copy was the right length; the
hash says it was the right file.

**The file is UTF-8 without a byte order mark, and the lines are flushed as they are
written.** A run worth reading is the one that ended badly, and it ends without a clean
shutdown, so a buffer would lose exactly the lines that explain why.

**A WRITE THAT FAILS IS LOGGED AND SWALLOWED.** A log that cannot be written must never
stop the recovery — the same rule the open already follows. The log is written while the
work happens, so this is where a full disk would otherwise end the run.

### 10.5.4 The run's summary

**The log opens with the run's figures** — the same ones the confirmation window showed,
so the two agree — as the `header` line, and it closes with the run's outcome as a
`summary` line:

```
{"type":"header","format":1,"started":"2026-10-26T18:24:07.412+04:00","shown":1500,"recoverable":1234,"folders":14950}
{"type":"summary","phase":"end","shown":1500,"recovered":1234,"parent_restored":412,"child_regenerated":611,"both":211,"not_recovered":266,"failed":0,"downloaded_bytes":530000000000,"replaced":27,"log":"C:\Users\…\Recover 2026-10-26_18-24-07.log"}
```

and between them, one operation per pair the run touched.

**The run's outcome is also in the status line** (§8) — the summary is what the reader
sees without opening anything, and the log is what they open when they want the detail.

**The log's path is in the status line when the run ends** — and when it is stopped, which
is also an ending. So the reader can find it without hunting. **The log is not opened in a
window of the app's own** — unlike Liem's List's report. Liem's List is a report on a few
hundred pairs; the recovery log is one record per pair, and on a full run that is a
quarter of a million records. A window showing that would be unusable, and a file the
reader opens when they want it is what they will use.

### 10.6 The recoverer is sequential, and its width is configured

**The recoverer starts sequential** — one record at a time.

**The width is a configuration value**, so it can be raised without touching the code.
The repair is a different shape from the Finder's pass — it is **bandwidth-bound**, not
request-rate-bound — so the pass's width of 24 was not a guide to it, and the width that
turns 494 GB into a duration has not been measured.

**"The width" is how many records are recovered at the same time.** A width of 1 is the
sequential recoverer; a width of 8 recovers eight pairs at once, each with its own
download. It is the same idea as the walk's width, applied to the repair instead of the
sync.

**It is a setting, in the app's own configuration** — under the `Server` section, beside
the timeouts that are already there:

```json
"Server": {
  "ConnectionString": "...",
  "CommandTimeoutSeconds": 1200,
  "RecoverWidth": 1
}
```

| | |
|---|---|
| **Name** | `Server:RecoverWidth` |
| **Default** | **1** — the sequential recoverer, which is what runs now |
| **Range** | 1 upward |
| **What it does** | how many records the recoverer works on at once |

**The width is 1 now**, and the setting is what will let it be raised later **without
touching the code**. That matters because the number that turns 494 GB into a sensible
duration has not been measured yet, and it will differ with the connection.

**The width is bounded by the request gate, not only by the setting.** A width of 8 with
a gate of 24 is eight records in flight; a width of 100 with a gate of 24 is still 24
requests at once, and 76 records waiting their turn. So the setting cannot outrun the
gate — it can only decide how many records are *in progress*, not how many requests are
in flight.

**The width does not change what is written.** Two records recovered at once are two
records recovered; the order and the result are the same either way. Only the duration
changes.

### 10.7 There is no fourth state

**A record the repair could not finish is re-read, and its verdict is whatever the
re-read says** (§10.2). A parent that downloaded but does not open reads back as
`corrupted`, and the record's verdict is computed from that.

So a *restored but corrupted* parent is not a new state and needs no new verdict: it is
`corrupted`, and the verdict that follows from `corrupted` is the one it already had.

### 10.8 A child with no recorded dimensions is sized from the settings

**The target comes from `ml.Settings` when the child's row records no dimensions.**

`ml.Settings` carries the product widths: `VCatWidthInPixels` (1500) and
`VCatThumbnailWidthInPixels` (300), with the matching qualities. The importer chose
between them by the child's category — `MLMediaCategory.Thumbnail` takes the thumbnail
settings, everything else takes the V-Cat's.

**Measured: every child in scope has recorded dimensions** — 363,856 of 363,856, none
null. So this is the fallback for a row that has no dimensions of its own, and today no
row needs it. It is specified because a child with no dimensions has no other target.

**The quality follows the same split** (§4.2), so a child sized from the thumbnail
settings is written at the thumbnail quality.

### 10.9 The filtered set is re-read after the recovery

**When the run finishes, the set the filters left is re-read.** The files are visited
again, the verdict is decided from them again, and the row shows the verdict its files now
deserve — not the one it had.

**The re-read is the pass, narrowed to the filtered set.** The parent ids of the rows the
filters left go into the pair query, so only those pairs are loaded, only those files are
visited, and only those rows are rebuilt.

**The steps:**

1. The filtered set is the subject — the same set the repair was given.
2. Its parent ids go into the pair query (`p.Id IN (...)`) **and** into the candidate
   query, so both are narrowed by the same set.
3. The pass runs as it does: the files are visited, the candidates are ranked, the verdict
   is decided from the checks.
4. The rows that come back are merged into the table by the parent's id, replacing the
   ones the run left behind.

**The filtered records are updated in place, and they stay in the table.** The re-read
replaces each row's state and verdict with what the re-read found. **The filters are not
applied again** — a row whose new verdict no longer matches the active verdict filter
stays visible, showing the verdict its files now deserve.

**THE MERGE REPORTS AS IT GOES, because it is not instant.** The table is bound to
`Rows`, so every write raises a change notification the grid handles — about half a
second for 160,000 rows, with nothing on screen saying so. The merge therefore yields
to the dispatcher as it goes, and the busy screen's bar moves while the table is updated.

**The yield is at `DispatcherPriority.Background`, NOT `await Task.Yield()`.** A yield
posts at Normal priority, which runs BEFORE the render pass, so the loop carries on and
the screen never repaints — measured: 0 frames rendered with a Normal yield, 20 with a
Background one. **The merge reports every 10,000 rows**: a report every 1,000 rows
costs as much as the merge itself (456 ms against 172 ms every 50,000), so the interval
is chosen by what the reporting costs, not by how often the reader could watch it move.

**The bar's total is the number of rows that will ACTUALLY be replaced** — not the size
of the re-read set. The two can differ, and a bar that promises a total it never reaches
is worse than no bar.

**The narrowed run does not publish to the scan state.** The scan state holds the whole
library; publishing a narrowed set into it would make a later read of the state return only
the filtered set. The rows it holds stay as they are.

**The row's size check is the evidence that the bytes are new.** A regenerated child's
length will not match the recorded size — that is what the check is for. The verdict is
decided on the file's state, not on its size.

## 10.10 A rate limit is a signal to slow down, not a verdict on the file

**Dropbox refuses a request that comes too fast (HTTP 429), and the repair must ride it
out rather than lose the pair.**

The rule this section states: **a refused download does not fail its pair.** The run
waits, backs off and tries again; only a download refused fifty times in a row gives up,
and then it says so.

**Why it matters here, and not in passing.** The repair is a MASS operation — about
160,000 downloads — and **a pair it loses to a rate limit is lost for good.** Nothing
recovers it later: the skip mechanism is the verdict, and the verdict of a pair whose
parent was never restored stays "restore parent". So a run that treated every 429 as a
failure would quietly shed pairs, and the reader would have to find them by hand.

**The SDK does not retry a rate limit.** Its retry budget
(`DropBox:MaxRetriesOnError`, default 3) covers **server errors only** — a 500 is
retried, a 429 is raised straight through. So the handling is the app's own job.

**The refusal arrives as the app's own exception.** The download path translates the
SDK's `RateLimitException` into `DropBoxRateLimitedException`, which carries the wait
Dropbox asked for in seconds. The listing and the metadata already did this; the download
did not, which is why a refusal used to arrive as an opaque SDK failure and fall through
the recovery's catch-all as an ordinary failed pair.

**The wait is SHARED, because the limit is per AUTHORIZATION.** Dropbox's limit is per
user or per team, not per request: every request the repair makes counts against the same
budget. So a 429 is not one unlucky request — it is the API saying *"stop, all of you"*.
One **brake** holds the run, and every worker waits at it before its next request, so the
fleet pauses together and resumes together. A per-worker backoff would let the others keep
hammering while one waits, and Dropbox counts rate-limited requests against the limit
too — the retry storm would keep the limit in place.

**The wait is the one Dropbox asked for.** When it named none, the backoff grows: five
seconds, then ten, twenty, forty, eighty, and then it stays at the cap — a limit that has
not cleared after a minute and a half is not going to clear in the next second either.
The sync walk backs off the same way.

**A partial download is deleted before the retry.** A file that was cut short must not be
mistaken for a good one on the next attempt.

**The give-up is loud.** A download refused fifty times in a row fails its pair with the
reason *"Dropbox rate limited the download after 50 attempts."* — in the log, in the
summary, and where the reader can see it. A pair that failed for this reason was not
judged on its merits; it was refused.

**What the repair does NOT do:** it does not narrow its own width in response to a 429
(the sync walk's adaptive width is for the walk's request pattern, and the repair's width
is 1 anyway), and it does not give up on the run — only on the pair.

## 10.11 The recovery starts with the pairs closest to now

**The recovery works through the pairs whose files are MOST RECENT first.** The reader
clicks **Recover Selected**, and the run begins with the pairs whose date and time are
closest to now, and works backwards through time from there.

### 10.11.1 Why

**A recovery is a mass operation that gets interrupted, and what is done first is what
survives.** The run is about 160,000 restores and the same again in regenerations — it
will be stopped, and it will be run again tomorrow over whatever is left. So the order
is not a detail: it decides which pairs are whole when the run ends.

**The pairs closest to now are the ones the business cares about now.** The library is
catalogued by date, and the recent end of it is what the catalogue is for today. Starting
there means the run delivers the current catalogue first, and the older tail is what a
later run picks up.

### 10.11.2 Which date

**Each pair is dated by the MORE RECENT of the two dates it carries:**

| The date | What it is |
|---|---|
| `ParentDateAdded` | when the parent's **media row** was added to the library (`ml.Media.DateAdded`) |
| `BestCandidate.Modified` | when the **Dropbox file** was last changed (`ml.DropBoxNode.ServerModified`) |

**The more recent of the two wins.** A pair whose media row was added in 2024 but whose
Dropbox file was changed last week is dated last week — the file's own date is the truth
about the file, and the row's date is only the truth about the row. The two are different
things, and the later of them is the pair's date.

**A pair with neither date is dated LAST.** It has no claim on being recent, so it goes
to the end of the run rather than to the front of it. A pair whose candidate exists but
carries no date falls back to its media row's date; if that is missing too, it is undated.

### 10.11.3 The order

**Most recent first.** The pairs are sorted by their date, descending, and the run works
through them in that order.

**Ties are broken by the pair's file name**, case-insensitively, so the order is
**deterministic** — the same set always recovers in the same order, and the confirmation
window's table can promise an order that the run keeps.

**The order is the RECOVERY's order, and it is applied where the run begins** — at
**Recover Selected**, on the set the filters left. The table's own row order is not
changed: the reader sorts the table as they please, and the recovery order is the
run's own.

**The confirmation window shows this order, because it shows the execution order.** Its
details table lists the actions in the order they will be performed (§10.1.4), so its
first row is the first thing the run does. That means **the window's rows and the run's
rows are one list** — the same sorted set, projected two ways — and not two lists that
agree by accident.

### 10.11.4 What it does not change

- **It does not change WHAT is recovered.** Every pair the filters left, and the verdict
  chose, is still recovered — the same set, the same counts.
- **It does not change the table's order.** The table shows the pass's order; the run
  uses the date order.
- **It does not change the width.** The width is still how many records are in flight.
  At a width above one the pairs start in the date order, but they overlap, so the
  finished order is not the date order.
- **It does not survive a re-read.** The re-read replaces the rows in the table; the
  rows carry no order of their own, so nothing about this is stored.

## 11. What is still open

**Nothing.** Every question the spec raised has been answered, and the answers are in
§1–§10.

**The log's record fields** are as specified in §10.5.2 and §10.5.3, and are **good enough
for now** — the log's shape is settled and its fields can be extended later if a
post-mortem turns out to want more. The record is written by the provider, so adding a
field is a change to the provider, not to the recovery.

## 12. Acceptance

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The button is in the toolbar, named Recover Selected | open the Finder | **not built** |
| 2 | It is enabled only when the filtered set holds a verdict of 1, 2 or 3 | filter to each verdict and watch the button | **not built** |
| 3 | Clicking it asks first, and writes nothing until answered | click it, cancel, check the library is untouched | **not built** |
| 4 | A verdict-1 pair gets its parent, byte for byte | restore a row, compare the bytes with the candidate | **not built** |
| 5 | A verdict-2 pair gets its child at the recorded size, aspect ratio kept | regenerate a row, measure the file | **not built** |
| 6 | A verdict-3 pair gets the parent and then the child from it | restore a row, check the child's provenance | **not built** |
| 7 | A child is written as RGB | regenerate a row, read the color space | **not built** |
| 8 | A child is scaled so its **longer** side is the recorded target | regenerate a non-square parent, measure | **not built** |
| 9 | A small parent keeps its own size | regenerate a parent below the target, measure | **not built** |
| 10 | A failed download leaves the library untouched | interrupt a download, check the path | **not built** |
| 11 | A corrupted parent is replaced, and the pair becomes recoverable | restore a corrupted pair, re-read it | **not built** |
| 12 | The database is not written | run a repair, check `ml.media` and `ml.DropBoxNode` | **not built** |
| 13 | A verdict-0, 4 or 5 row is not touched | run a repair over a mixed set, check the files | **not built** |
| 14 | Filtering by a folder or file name substring scopes the repair to that set | filter to one folder, repair, check no file outside it changed | **not built** |
| 15 | A filter that names no verdict still recovers only the 1, 2 and 3 rows in it | filter to a folder holding every verdict, repair, check the others | **not built** |
| 16 | A recovered record is re-read, and its verdict is the one its files now deserve | recover a row, read its state back | **not built** |
| 17 | A pair that could not be recovered reads back as the verdict its files deserve | fail a download, re-read the row | **not built** |
| 18 | The outcome reaches the status line, with the not-recovered count | run a repair over a mixed set | **not built** |
| 19 | One pair's failure does not stop the others | fail one download in a set of ten | **not built** |
| 20 | The busy screen shows how many were selected and how many are done | watch a run | **not built** |
| 21 | The run can be stopped, and says what it did | stop a run part-way | **not built** |
| 22 | A second run skips the records already recovered — across an app restart too | recover a set, close the app, run again, check the second run skips them | **not built** |
| 23 | The log records what was done, per record | run a repair, read the log | **not built** |
| 24 | The recoverer is sequential, and its width is configured | read the configuration, run with a width of 1 and of 2 | **not built** |
| 25 | A child with no recorded dimensions is sized from the settings, by its category | size a child from `ml.Settings` and check the target | **not built** |
| 26 | The confirmation window shows the download volume and the replaced-file count in red | open the window on a large set | **not built** |
| 27 | Every file the repair writes is named in upper-case | recover a pair, list the folder | **not built** |
| 28 | The repair makes no lookup of its own — the namespace comes with the candidate | recover a pair, check the namespace the fetch used | **not built** |
| 29 | A child is written as JPEG, whatever the parent's extension is | regenerate a child of a PNG parent, read the file's format | **not built** |
| 30 | The child's recorded dimensions come with the row, and are the resize target | regenerate a child, measure it against `ml.media`'s recorded size | **not built** |
| 31 | The confirmation window is split: the information above, the details table below | open the window | **not built** |
| 32 | The details table lists the actions, one per row, in the execution order | open the window over a set, compare the rows with the set | **not built** |
| 33 | The table names the real paths — the file in Dropbox and the file on disk | open the window, check a `To` path against the disk | **not built** |
| 34 | A pair that is not recovered has no row in the table | open the window over a mixed set | **not built** |
| 35 | The table shows at most 200 actions, and says how many there are in all | open the window over a set larger than 200 | **not built** |
| 36 | The recovery's log goes in a folder named for the date the recovery started, with the month a number | run a recovery, list the log folder | **built — `tools/repair-log-checks` passes 12/12** |
| 37 | A refused download does not fail its pair — the run waits, backs off and retries | refuse a download, check the pair is still recovered | **built — `tools/repair-throttle-checks` passes 15/15** |
| 38 | The re-read's merge reports as it goes, and the bar moves while it does | run a recovery and watch the merge | **built** |
| 39 | The busy screen names the file the run is on, and the line is gone when the recovery ends | run a recovery, cancel it mid-download | **built** |
| 40 | The recovery works through the pairs closest to now first | filter to a set, recover, check the order | **built — `tools/recovery-order-checks` passes 16/16** |
| 41 | The busy screen says how many pairs are recovered, out of how many will be | run a recovery at width 1 and watch | **built** |
| 42 | The busy screen says how long the run will take still, from the last pair's time | run a recovery at width 1 and watch | **built** |
| 43 | A failed download names the file it could not fetch, in the log and in the app's log | fail a download, read both | **built — `tools/repair-log-checks` passes 25/25** |
| 44 | The log is JSON Lines — every line one JSON object, and the header names the format's version | open a log, parse every line | **built — `tools/repair-log-checks` passes 25/25** |
| 45 | An operation's lines are tied together by its number, and an interrupted one leaves a start with no end | interrupt a run, read the log | **built** |

Nothing in this list has been checked, except rows 36, 37, 38, 39, 40, 41, 42, 43, 44 and
45 — the spec is written; the button is not.
