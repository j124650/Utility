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
700 tall, resizable, with a floor of 800 × 500. The information block above it keeps its
height whatever the set's size, and the table takes the rest.

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

**The order is the order the repair will use.** The pairs come in the order the filters
left them, which is the order the pass produced them, and the repair works through the
subject rows in that order. So the table's first row is the first thing that happens,
and the last row is the last.

**A pair that is not recovered has no row.** The table is the list of what will be
**done**. The pairs that are left alone are counted above the table, and why they are
left alone is their verdict.

**The paths are the real ones.** The `To` column is the file's path in the library — the
resolved root, the pair's folder, and the upper-case GUID the file is named by (§5.3).
The `From` column is the candidate's own Dropbox path for a restore, and the parent's own
path on disk for a regeneration — the file the child is made from.

**The table is virtualized, and its rows are built as they are shown.** A run over the
whole library is 271,758 pairs and about 400,000 actions. The rows are computed from the
pair and the action's position, so the window's memory does not grow with the size of
the set — a question asked of the set, not a document to be read to the end.

**The table is not sortable.** Its order is the execution order, and a sorted table
would be a lie about what will happen.

**The action's count is stated above the table**, so the reader knows what they are
looking at before they look at it.
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

### 10.5 One log of what happened

**There is one log**, and it answers both "what did the recovery do" (§10.3) and "what
happened when the run was stopped".

**It is written through a provider, so the log's home can change without the recovery
knowing.** The recovery writes log records; where they go is the provider's business.
Today the provider writes a text file; later it can write a table, and the recovery does
not change.

| | |
|---|---|
| The provider | writes the records — a text file now, a table later |
| The record | one per pair, saying what was done to it |
| The reader | the user, for their own post-analysis |

**A text file is enough** for a log that is only ever read by the user. The database is
read-only to this app, so a table would have to be created by the system that owns `ml`
— the provider is what makes that a later decision rather than a blocker.

### 10.5.1 Where the log lives

**Beside the app's own log**, in `%LOCALAPPDATA%\Utility\logs\` — the folder the app's
own log file is written to (`App.xaml.cs`, `FileLog.TryOpen`). **Not the temporary
folder**: a log in `%TEMP%` is a log that gets cleaned up, and this is the record of what
happened to the media library.

The app's own log is one file per run, named for the moment the app started
(`2026-10-26_18-20-01.log`). The recovery log follows the same rule and the same
naming, so the two sit together and sort together:

| | |
|---|---|
| **Folder** | `%LOCALAPPDATA%\Utility\logs\` |
| **Name** | `Recover 2026-10-26_18-20-01.log` — the same stamp as the app's log, so a recovery is tied to the run it happened in |
| **One file per run** | a second run writes its own file and leaves the first exactly as it was |

**The stamp is the app run's start time, not the recovery's.** A recovery happens inside
a run, and the run's stamp is what the app's own log carries — so the two files name the
same moment and can be read together. If the app is restarted, the new run's recovery
writes to the new run's file.

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
| `when` | — | the moment the record was written |

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

### 10.5.3 The format, for post-mortem parsing

**The log is a line-oriented text file, one record per line, and it is meant to be
parsed.** It is not prose and not a table for a human to read — it is a record that a
question can be asked of afterwards.

**Every line carries a timestamp, a level and a message, in the same shape as the app's
own log** — so the two files read the same way and a tool that reads one reads the other:

```
2026-10-26 18:24:07.412 [Information] Recovery: parent restored. parent=F4D13B32-FAB2-451F-83F9-0961C38B6286 child=8FDD66C2-C1C5-4FBE-B64B-35302C7F96A4 parent_name=CA05434001-Featured-Bottom.jpg folder=ca05434001
2026-10-26 18:24:07.412 [Information] Recovery:   node=123984 dropbox_id=id:sMa5MD0C7uMAAAAAAAfj2Q namespace=15193064371 content_hash=3eeb5f91c234eb6bb3fe0a5f469b42fc5a24a82f5a76bf3a7c0458f9623b11b6
2026-10-26 18:24:07.412 [Information] Recovery:   source=/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog/Champion/F26_Champion_Final/CA05434001/CA05434001-Featured-Bottom.jpg
2026-10-26 18:24:07.414 [Information] Recovery:   copied_to=C:\_Recover\media\ca05434001\F4D13B32-FAB2-451F-83F9-0961C38B6286 copied_bytes=2578475
2026-10-26 18:24:07.418 [Information] Recovery:   result=size 2578475
2026-10-26 18:24:09.880 [Error] Recovery: parent failed. parent=8B9E53C2-C151-4771-B998-000001414B child=1EEC33E8-479F-424F-974E-199B9DCBF1D7 parent_name=HE05682681-02.jpg folder=ca100419p reason=download failed: path/not_found
```

**One pair's record is a group of lines, and the first line names the pair.** The lines
that follow carry the detail, in the order of the workflow (§10.5.2). So a parser reads a
record by its first line, and the tokens are what a question is asked of:

| Token | What it identifies |
|---|---|
| `parent` | the parent's `ml.media.Id` — the GUID, and the file's name on disk |
| `child` | the child's `ml.media.Id` |
| `parent_name` | `ml.media.FileName` — the name the candidate was matched on |
| `folder` | `ml.media.Path` |
| `node` | the candidate's `ml.DropBoxNode.Id` |
| `dropbox_id` | the candidate's `ml.DropBoxNode.DropboxId` |
| `namespace` | the candidate's `ml.DropBoxNode.NamespaceId` |
| `content_hash` | the candidate's `ml.DropBoxNode.ContentHash` |
| `source` | the candidate's full Dropbox path |
| `copied_to` | where the parent's bytes were written |
| `copied_bytes` | how many |
| `resized_from` | the file the resize read |
| `resized_for` | the child's id the resize was done on behalf of |
| `resized_to` | where the result was written |
| `target` | the size the resize aimed at |
| `result` | what the file is now |
| `reason` | why a pair was not recovered |

**Every record names every object involved**, so a post-mortem can say exactly what
happened and where — and none of it is inferred:

| | |
|---|---|
| **Which pair** | the parent's and the child's `ml.media.Id` — the GUIDs, which are the files' names on disk |
| **Which file in Dropbox** | the index row's `Id`, Dropbox's own `DropboxId`, the namespace, the path, and the content hash |
| **What was copied, and where** | the source path and the destination path, and the byte count |
| **What was resized, for whom** | the parent's path in, the child's id, the child's path out, and the target size |
| **What the file became** | its length and its dimensions, measured after the write |

**The `parent=` is the anchor.** It is the GUID, so the record and the file on disk are
the same thing — a reader can take the `parent=` from a log line and find the file, and
take a file and find its log line. Nothing in the record depends on a path that could
have changed.

**The `content_hash` is what makes "the copy was faithful" answerable.** It is Dropbox's
own hash of the bytes, so the file in Dropbox and the file in the library can be
compared, not merely the lengths. A length match says the copy was the right length; the
hash says it was the right file.

**The file is UTF-8 without a byte order mark, and the lines are flushed as they are
written** — the same as the app's own log. A run worth reading is the one that ended
badly, and it ends without a clean shutdown, so a buffer would lose exactly the lines
that explain why.

### 10.5.4 The run's summary

**The log opens with a summary of the run** — the same figures the confirmation window
showed, so the two agree:

```
Recovery run - 2026-10-26 18:24
  Filtered set      1,500 pair(s)
  Recovered           1,234
    parent restored     412
    child regenerated   611
    both                211
  Not recovered         266
  Failed                  0
  Downloaded         494 GB
  Replaced               27 existing file(s)
```

and then one record per pair.

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
| 35 | The table is virtualized, and its memory does not grow with the set | open the window over the whole library | **not built** |

Nothing in this list has been checked. The spec is written; the button is not.
