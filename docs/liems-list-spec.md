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

## 6. The file

CSV, UTF-8 with a BOM so Excel opens it correctly, with a header row and one line per pair.
The candidate paths sit in the last column, in rank order.

The reader chooses where the file is written.

## 7. Implementation shape

- **The source** — the rows the Finder's pass produced. The report reads them; it does not
  run a pass of its own. The filter of §2 is the one the Finder service already applies when
  it produces the list.
- **The projection** — the report's rows are projected from the gathered row. No value is
  re-derived in the report except the reason (§4), which is read off the row's verdict and
  candidates.
- **The file** — written where the reader chooses it, in the CSV form of §6.
- **The button** — the Finder's toolbar carries a button named **Liem's List**. The nav
  entry that used to carry the list is hidden; the view and its view model stay registered.

## 8. Not in scope

- **The repair** — restoring a missing parent from Dropbox, regenerating a missing child
  from its parent. That is a separate spec, and it is not written yet.
- **Writing anything back.** The report reads the media library and the Dropbox index and
  writes nothing to either. It writes one file, and that file is the report.
- **Any change to the `ml` schema.** The only schema change this spec adds is on the table
  this app owns.
