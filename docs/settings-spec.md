# The Settings tab

**Status:** SPEC — nothing has been built. Every row of §11 is outstanding.

## Glossary

| Term | What it is |
|---|---|
| **the Settings tab** | the app's configuration page — the SQL connection string, the Dropbox credentials, and the actions that act on them. |
| **the sync's table** | `ml.DropBoxNode` — the one table the app writes. It holds the Dropbox folder and file hierarchy. |
| **the create script** | `src/Utility.Server/Sql/CreateDropBoxNode.sql` — the script that builds the sync's table. |
| **the confirmation phrase** | the exact sentence the user must type before the sync's table may be created. |
| **create-if-absent** | the rule that the sync's table is created only when it is absent; a table that exists is never dropped or altered. |
| **the target database** | the database the app's session is on, as the server reports it. |
| **the `ml` schema** | the schema the sync's table lives in. It is shared with tables the app does not own, and the app never creates it. |

## §1 What this is

The Settings tab, and one new action on it: **creating the sync's table from the app**.

The create script ships with the app. A button in the Settings tab runs it, behind a
protective dialog that opens only the way when the user types an exact phrase.

**The app creates the table only when it is absent** (§4). It never drops an existing
table and never alters one.

## §2 The tab today

| Control | What it does | Where it goes |
|---|---|---|
| Connection string | the SQL connection the app uses | `HKCU\Software\BbcUtility\Sql` → `ConnectionString` |
| App key / App secret / Refresh token / Retry budget | the Dropbox account's credentials and the SDK's retry budget | `HKCU\Software\BbcUtility\DropBox` |
| Test connection | opens a connection and reports the outcome | — |
| Save all | writes both sets of values back to their registries | — |
| Reload | re-reads both registries into the page | — |

**The registry is the source of truth**, and the registry replaces the configuration file:
a connection string in `appsettings.json` is not read at all. The page writes the
registry; the app reads the registry at startup.

**Test connection reports that a connection succeeded; it does not name the database.**
"Connected." is the same answer for every database on the server, so it cannot answer
which database the app is on. The new action's dialog therefore names the target
database itself (§6).

**Save all does not take effect until the app restarts**, and the page says so. The new
action acts on the connection string **in force** — the one the app already uses — not
on the text in the box.

## §3 The script ships with the app

The create script becomes an embedded resource of Utility.Server, taken from the same
file in the repository — so the deployed script and the reviewed script are the same
bytes, and the script cannot drift from the code that runs it.

The script is written for `sqlcmd`, and the app runs it differently:

| The script's construct | What the app does |
|---|---|
| `GO` | not T-SQL — the app splits the script on it and runs the batches in order, on one connection |
| `SET QUOTED_IDENTIFIER ON;` | harmless: SqlClient sets it ON for every connection |
| `SET NOCOUNT ON;` | harmless: the app reads no row counts from the script |

**One connection, one transaction.** All the batches run on the same connection inside
one transaction, so a failure anywhere leaves no table behind (§7).

The script's header says how to run it by hand. The header must also say that the app
runs it only when the table is absent (§4) — the script still drops the table if it
finds one, and that is deliberate: it is the reset path the sandbox procedure uses
(`docs/sandbox-database-testing-spec.md`).

## §4 Create-if-absent

The app creates the sync's table only when it does not exist.

- The table exists → no DDL runs. The app reports that `ml.DropBoxNode` already exists
  in the target database, and stops. Nothing is dropped, altered or emptied.
- The table does not exist → the app runs the script (§3).

**The check is enough, because nothing can be lost.** The table can hold synced rows
only if it existed before the check, and nothing can write to a table that does not
exist. A table that appeared between the check and the create holds no rows, so the
script's drop finds nothing to lose.

The script is not modified: it keeps the drop-and-recreate behaviour the hand-run reset
path depends on. The app's rule is what keeps the button safe.

## §5 The button

The Settings tab gains one button in the **SQL Server** card, beside **Test
connection**: **Create DropBox table**.

Pressing it opens the confirmation dialog (§6). The button is always enabled — a table
that already exists is reported, not hidden.

## §6 The confirmation dialog

A modal window, owned by the Settings tab, showing:

- what will happen: the sync's table will be created in the target database, and no
  existing data is touched;
- the target database, as the server reports it;
- the confirmation phrase, shown so that it can be copied;
- one text box;
- two buttons: **Create** and **Cancel**.

**The Create button is enabled only while the text box holds the confirmation phrase.**
The comparison trims the text box's surrounding whitespace and then compares the rest
exactly — case included. The phrase is:

    weaponize demons for a brighter tomorrow

Pressing **Create** closes the dialog and runs the create (§7); the outcome appears on
the Settings page's status line. **Cancel** closes the dialog and does nothing.

## §7 What the create does

1. The app opens a SQL connection with the connection string in force.
2. It asks the server which database the session is on, and that name is what the
   dialog shows.
3. If the sync's table exists in that database → no DDL runs; the app reports that it
   already exists and stops.
4. If it does not exist → the app runs the script's batches in order, on one connection,
   inside one transaction.
5. On success the table exists with the script's shape, and the app reports it.
6. On failure the transaction rolls back: no table, no half-built table, and the app
   reports the error.
7. The `ml` schema must already exist — the script needs it and does not create it,
   because the schema is shared with tables the app does not own.
8. The app does not create a database. If the target database does not exist, the
   connection fails and the app reports that.

Nothing else in `ml` is touched: the script creates one table and three indexes.

## §8 The phrase is a speed bump, not a control

The phrase is a constant in the app's code and is shown in the dialog. It is there so
that a click is deliberate — anyone who has the source can read it. It is not an
authorisation.

## §9 Not in scope

- No update path: the app does not alter a table that already exists. The `Alter*.sql`
  scripts stay hand-applied.
- No drop or reset from the app.
- No automatic run at startup, and nothing runs on a schedule.
- No creating a database or the `ml` schema.
- No change to the sync, the Finder, the Dashboard, or any table the app does not own.
- How to exercise the button against a copy of the working database is the sandbox
  procedure's job (`docs/sandbox-database-testing-spec.md`); nothing is duplicated here.

## §10 Implementation shape

| File | What it holds |
|---|---|
| `src/Utility.Server/Sql/CreateDropBoxNode.sql` | unchanged in behaviour; embedded as a resource of Utility.Server |
| `src/Utility.Server/DropBoxSync/DropBoxSchemaService.cs` (new) | the create: reads the script from the resource, splits it on `GO`, runs the batches in one transaction, and returns the outcome — created, already exists, or failed with the reason |
| `src/Utility.UI/Views/SettingsView.xaml` | the **Create DropBox table** button |
| `src/Utility.UI/ViewModels/SettingsViewModel.cs` | the command that opens the dialog and runs the create; the status line |
| `src/Utility.UI/Views/CreateTableWindow.xaml` (new) | the confirmation dialog; it takes an `IServiceProvider` and gets its DataContext from DI, as the views do |

The new service is registered where the SQL services are registered.

## §11 Acceptance criteria

| # | What must hold | How it is checked | State |
|---|---|---|---|
| 1 | The script the app runs is the file in the repository | the embedded resource and `Sql/CreateDropBoxNode.sql` are byte-identical | not measured yet |
| 2 | The Settings tab carries the button | the SQL Server card shows **Create DropBox table** beside **Test connection** | not measured yet |
| 3 | The dialog does not open the way without the phrase | **Create** stays disabled until the text box holds the phrase | not measured yet |
| 4 | The dialog names the database the create will act on | the name in the dialog is the one the server reports | not measured yet |
| 5 | With the table absent, the button creates it | the table exists afterwards with the script's shape — the same columns, computed definitions, constraints and indexes, ignoring physical column order | not measured yet |
| 6 | With the table present, nothing is touched | the table's columns and rows are unchanged, and the app says it already exists | not measured yet |
| 7 | A failing script leaves nothing behind | the create fails, and no table exists afterwards | not measured yet |
| 8 | No other table in `ml` is touched | the other `ml` tables' row counts are unchanged | not measured yet |
| 9 | Nothing runs by itself | starting the app creates no table | not measured yet |
| 10 | The outcome is reported and logged | the status line names the outcome, and the log carries it | not measured yet |

## §12 Open question

- Should the same button later offer the upgrade scripts — the `Alter*.sql` files — for
  a table that exists but is older than the app? This spec covers the create only.
