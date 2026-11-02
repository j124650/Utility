# The files tab's listing

**STATUS: BUILT.**

The files tab's grid is a listing, not a picker: the row is what is selected, the cells
are not, and the type is sized to be read at a glance.

---

## 1. What it looks like

**The type is 20% bigger than the app's 12**, so 14.4. The rows are 30 tall, so the
bigger type sits inside the row rather than against its edge.

**The text sits centred in the row**, not against its top edge.

**A cell carries no border of its own.** The grid's own cell template draws a black
outline around the cell that was clicked, even though what is selected is the row. That
outline goes.

**A focused cell carries no focus ring.** WPF draws a focus rectangle around the focused
element unless the element says otherwise, and in a grid that ring lands on a single cell
— which reads as if the cell were selected. It does not.

**The row is still highlighted when it is selected.** The row is the selection: it is
what fills the item-info pane, and the highlight is what says which one that is.

## 2. What does not change

- Clicking a row selects it, and the item-info pane fills with that row's metadata.
- Double-clicking a folder still opens it.
- The columns, their order and their contents are unchanged.

## 3. Implementation shape

| | |
|---|---|
| The type | the grid's font size: 14.4, the app's 12 plus a fifth |
| The row | the grid's row height: 30 |
| The centring | the cell's content presenter, centred |
| The border | a cell template of the view's own, without the cell's focus outline |

## 4. Not in scope

- The Finder's grid, which has its own.
- The item-info pane.

## 5. Acceptance criteria

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The type is 20% bigger than the app's 12 | the app, on screen | measured — 14.4, and it is visibly larger |
| 2 | The text is centred in the row | the app, on screen | measured |
| 3 | No cell carries an outline of its own | the app, on screen | measured — the clicked Size cell has no black frame, and a cell reached with the arrow keys has no focus ring |
| 4 | The selected row is still highlighted across its width | the app, on screen | measured |
| 5 | Clicking a row still fills the item-info pane | the app, on screen | measured — the pane showed the row's name, type, path and id |
