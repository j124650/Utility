# Liem's List as a CSV

**Status:** BUILT — the code is in the tree. Nothing has been measured yet: every row of
§10 is still outstanding.

## §1 What this is

The Finder's **Liam's List** button writes the list as a **CSV file** and opens it.

The list itself does not change: it is still the report of the pairs the verdict leaves
unresolved (`docs/liems-list-spec.md` §2), built from the rows the last pass gathered.

The HTML report the button used to write is **not deleted** — it stays in the tree, unwired
(§7).

## §2 The rows

One row per pair, and the pairs are the report's own — the sections of `LiemListReport`, in
the report's order.

- **Membership** — the two verdicts that leave a pair unresolved. The verdict decides it
  alone; a filter over the Finder's table does not narrow the file.
- **Order** — by style (the parent's folder, verbatim), then by the parent's date added,
  **newest first**; a tie breaks by the file name, case-insensitively.
- **One row per pair** — the pair's candidates share the row (§4).

## §3 The columns

| # | Column | Value | From |
|---|---|---|---|
| 1 | Path (style) | the parent's folder, verbatim | `LiemListPair.Style` |
| 2 | Original name | the original's file name, verbatim | `LiemListPair.FileName` |
| 3 | Angle | `24 Angles`, or `Angle N` (e.g. `Angle 22`) — empty for a featured shot | `LiemListPair.Angle` |
| 4 | Featured type | `Featured` / `Top` / `Bottom` / `Thumbnail` / `Outsole` / `Back` / `Artwork` — empty for an angle | `LiemListPair.FeaturedType` |
| 5 | Reason | `not found` / `name found but size differs` / `ambiguous` | `LiemListPair.Reason.Text` |
| 6 | Candidates | the candidates, one per line (§4) | `LiemListPair.Candidates` |

**Columns 3 and 4 are exclusive** — the child's category decides which one carries a value:

- the child's category is `24 Angles` → an angle: **Angle** takes the child's variant
  (`Angle 22`) where the row carries one, and `24 Angles` where it does not; **Featured
  type** is empty;
- any other category → a featured shot: **Featured type** takes the category name as it
  stands; **Angle** is empty.

The original's file name is **copied, never parsed** — it does not decide columns 3 and 4.

## §4 The candidate cell

One candidate per line inside the one quoted cell, and the fields are separated by ` | `:

```
<rank> | <path> | <size> | <dimensions> | <what matched>
```

- the **rank** is the candidate's own rank — `0` is the strongest (name, size and folder all
  match);
- the **path** is the full path, as the index holds it;
- the **size** is the candidate's size as the report shows it, thousands-separated;
- the **dimensions** are `width × height`, empty when the index records none;
- **what matched** is the candidate's own phrase.

A pair with no candidates carries an **empty cell** — the reason says why.

## §5 The file format

The CSV is written the way a spreadsheet reads it — the same as the recovery failures' CSV
(`src/Utility.Server/Finder/Repair/RecoveryFailureCsv.cs`):

- every field is **always quoted**, and an embedded quote is **doubled**;
- a line ends with **CRLF**;
- the file is **UTF-8 with a BOM** — Excel needs the BOM to read the paths as UTF-8;
- the **header row is row 1**, naming the columns of §3 in that order.

## §6 Saving the file

The button asks where the file goes.

- A **save dialog** opens, with the filter `CSV file (*.csv)|*.csv|All files (*.*)|*.*`.
- The dialog opens in the **user's Documents folder**
  (`Environment.SpecialFolder.MyDocuments`), and the default name is
  **`Liem's List <the pass's date and time>.csv`** (`yyyy-MM-dd HHmm`) — the name the HTML
  report used.
- The file is written where the reader chose, and **opened** in the default app for it.
- The status line says where it went. A cancelled dialog writes nothing and says nothing.

Nothing else is written: the media library, the Dropbox index and the database are read, and
the file is the only thing written.

## §7 The HTML report is kept, and not used

`LiemListHtmlWriter`, `LiemListWindow`, `LiemListView` and `ILiemListReportPresenter` stay in
the tree. The button does not call them: the button writes the CSV (§6). Nothing is deleted,
and the HTML report can be wired up again by changing what the command calls.

## §8 Where the code goes

| Piece | Where |
|---|---|
| the CSV writer | `src/Utility.Server/Finder/LiemListCsv.cs` — beside the report model, like `RecoveryFailureCsv` |
| the quoting rule | one shared helper, used by both CSV writers |
| the angle/featured split | `LiemListReportService.Project` — the projection decides it once, and the pair carries it |
| the pair's two fields | `LiemListPair.Angle` and `LiemListPair.FeaturedType` |
| the export | `src/Utility.UI/Services/LiemListCsvExporter.cs` — asks for the location, writes the file, opens it |
| the command | `FinderViewModel.ShowLiemListAsync` — calls the exporter |
| the registration | `App.xaml.cs` — the exporter is registered; the presenter is not |

## §9 Not in scope

- **The HTML report.** It stays as it is (§7).
- **A date column.** The file carries the columns of §3 and no others; the newest-first
  order is the record of the dates.
- **The original's size.** The candidate cell carries the candidates' sizes; the original's
  own size is not in the file.
- **The pass's figures** — how many pairs the pass scanned, and the breakdown by reason. The
  breakdown is a pivot over the file; the pass's timestamp is in the file name.

## §10 Acceptance

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The button writes a CSV and opens it | click **Liam's List**, choose a location — a file appears at the chosen path and opens | not measured yet |
| 2 | The dialog opens in the user's Documents folder | open the dialog and look at where it starts | not measured yet |
| 3 | The default file name is `Liem's List <date> <HHmm>.csv` | open the dialog and read the name | not measured yet |
| 4 | The file is UTF-8 with a BOM | the first three bytes are `EF BB BF` | not measured yet |
| 5 | The header row is row 1 and names the six columns of §3 in that order | read the first line | not measured yet |
| 6 | Every field is quoted, and an embedded quote is doubled | read a line with a candidate path in it | not measured yet |
| 7 | Lines end with CRLF | read the file's bytes | not measured yet |
| 8 | One row per pair — the row count equals the report's pair count | count the rows and compare with the report | not measured yet |
| 9 | The order is by style, then newest first | read the rows | not measured yet |
| 10 | An angle pair fills the Angle column and leaves Featured type empty; a featured pair the other way round | read a row of each kind | not measured yet |
| 11 | A pair with candidates carries them one per line; a pair with none carries an empty cell | read a row of each kind | not measured yet |
| 12 | The status line says where the file was written | read the Finder's status line | not measured yet |
| 13 | The HTML writer, the window and the view are still in the tree, and the button does not open the window | the tree holds them; the button opens no window | not measured yet |
