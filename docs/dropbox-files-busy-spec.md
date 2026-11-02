# The files tab's busy screen over the listing

**STATUS: BUILT.**

While the DropBox files tab fetches a folder, the listing keeps showing the folder it
was showing. A click on a row that is about to be replaced would navigate somewhere the
user did not mean, so the listing is blurred and covered while the fetch is in flight, and
the cover carries the words **Please wait..**

---

## 1. What it does

**The listing is blurred while it is being loaded.** The blur sits on the listing itself,
not on the window: the toolbar, the item-info pane and the status line stay sharp,
because they are still true.

**The cover above the blur is translucent.** WPF has no backdrop filter: an element's
Effect blurs THAT element, never what sits behind it. So the listing carries the blur
and the cover above it is left translucent — that is what lets the blurred shapes show
through.

**The cover says "Please wait.."** It is the only thing on the cover.

**The listing cannot be clicked while it is covered.** The cover is a hit-testable sheet
over the grid, so a click during the fetch lands on the cover and not on a row.

**Only the listing is covered.** The toolbar, the item-info pane and the status line are
untouched and stay usable.

## 2. When it is up

**While the tab is fetching a folder.** That is: the first load, and every navigation —
opening a folder, **Up**, **Go**, **Refresh**.

The cover follows the tab's own busy flag, which is the same flag the status line's
progress bar follows. One flag, one meaning: the tab is busy or it is not.

## 3. Implementation shape

| | |
|---|---|
| The blur | a blur effect on the listing's border, set by a data trigger on the tab's busy flag |
| The cover | a grid over the listing, holding a translucent white sheet and the words |
| The cover's text | "Please wait.." |
| The cover's sheet | hit-testable, so the listing underneath cannot be clicked |

Nothing in the view model changes: the tab already had a busy flag, and the status line
already used it.

## 4. Not in scope

- The window-wide busy screens: the one-phase sync's, the two-phase run's, and the
  Finder's pass.
- The status line, which keeps its own progress bar.
- The item-info pane.

## 5. Acceptance criteria

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | "Please wait.." is over the listing while it loads | the app, on screen | measured — caught on screen during the first load |
| 2 | The listing is blurred while it loads | the app, on screen | measured — the listing behind the cover was soft; on that load the listing was empty, so only the column headers showed it |
| 3 | The listing cannot be clicked while it is covered | the app, on screen | not measured — the cover is a hit-testable sheet over the grid by construction, but no click was made during a load |
| 4 | The cover goes away when the load ends | the app, on screen | measured — the listing was populated and uncovered on the next capture |
| 5 | The toolbar, the item-info pane and the status line are untouched | the app, on screen | measured |
