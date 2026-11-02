# The DropBox files tab's toolbar

**STATUS: BUILT.**

The DropBox files tab's panel is reworked into a single toolbar row, and the tab reads
like a browser: up, the address, and the actions that work on what the address shows.

---

## 1. What changes

The tab's top panel currently holds three rows of controls under a heading: the namespace
row, the path row, and the tree row. It becomes **one toolbar row**:

```
┌───────────────────────────────────────────────────────────────────────────────────────────────────┐
│ [↑ Up] [scope ▾] │ /Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog │ │ [→ Go] [⟳ Refresh] [⭳ Sync From Here] │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- `↑ Up` — one level.
- `scope ▾` — the namespace drop-down, with no label in front of it.
- the text — the path input: the path the grid is showing.
- `→ Go` — to the path in the input.
- `⟳ Refresh` — re-read this folder.
- `⭳ Sync From Here` — the branch-scoped two-phase sync, unchanged in behaviour.

The panel's heading and its description line go with it: the navigation strip already
names the tab, and a toolbar is not a title card. The tree row's controls are hidden.

## 2. The toolbar

**One row, and it does not wrap.** The row holds, left to right: **Up**; the namespace
drop-down; the path input; then the actions, set apart from the input by a gap.

**The path input takes the width the row has left over.** Every other control in the row
sizes to its content. The input is the only one that grows.

**The input sits directly against the drop-down**, with no gap between them, so the two
read together as one field: the drop-down says which namespace the path belongs to, and the
text says where in it.

**The input's text is monospaced.** A path is read character by character, and the
proportions of a monospaced face make the separators line up.

**The strip wears the app's toolbar look** — a white panel, a light border, rounded
corners — the same as the Finder's toolbar and the dashboard's, so the tabs read as one
app. The toolbar inside it paints nothing, so the border around it is the only one.

**Everything below the row is unchanged**: the listing on the left, the item-info pane on
the right, the status line at the bottom.

## 3. The icons

Every glyph is from **Segoe MDL2 Assets**, the icon font the app's other tabs already use.
Two of these four already render in this app; the other two are the same font, so the set
stays consistent.

| Control | Glyph | Name in the font | Word beside the icon | Tooltip |
|---|---|---|---|---|
| Up | `U+E74A` | Up | Up | Up one level |
| Namespace | — | — | — | Personal space, or a team root. Team folders are not visible in Personal. |
| Path | — | — | — | Type a path and press Enter, e.g. /Images-Catalogs Team Folder/LightBoxPics |
| Go | `U+E8AD` | Go | Go | Go to this path |
| Refresh | `U+E72C` | Refresh | Refresh | Re-read this folder |
| Sync From Here | `U+E896` | Download | Sync From Here | the button's own hint |

**Every button is an icon beside its word**, the shape the app's other tabs use.

**The download glyph is the brief's choice** for **Sync From Here**, and nothing else in
this tab carries a download glyph once the tree row is hidden — the tab's own **Download
file…** is one of the hidden controls.

If a glyph that names the action is wanted instead, the same font carries **Sync**
(`U+E895`).

## 4. What is hidden

**Hidden means collapsed, not removed.** The controls, the commands behind them and the
view model's properties stay where they are, so the row can come back by changing one
flag.

| Hidden | |
|---|---|
| The tree row, whole | the **Tree depth** label, its input, **Build tree JSON**, **Save JSON…**, **Download file…**, and the tree's summary text |
| The panel's heading | the word "Dropbox" |
| The panel's description line | the sentence under the heading |

The brief's "comment" is read as the panel's description line — the sentence that says
what the tab is for. The comment block in the markup above the **Sync From Here** button
goes too: a comment cannot be hidden, and the rewrite replaces that block anyway.

**The tree row goes whole.** Its summary text has nowhere to sit once the controls around
it are hidden, so it is hidden with them.

**The item-info pane is not touched.** Its tree-JSON preview is bound to whether a tree
has been built, and with **Build tree JSON** hidden no tree can be built, so the preview
never appears. Its heading stays, so the pane shows a heading with nothing under it.

## 5. What does not change

- The path input drives the grid, and Enter still goes.
- The path input still follows the navigation: **Up**, opening a folder and changing the
  namespace all move it.
- **Up** still goes to the parent folder.
- The namespace drop-down still switches namespace and reloads.
- **Sync From Here** still behaves as `docs/sync-from-here-spec.md` specifies, and keeps
  its disabled states.
- The grid, the item-info pane and the status line are unchanged.
- The hidden commands still do what they did, if they are ever shown again.

## 6. Implementation shape

**The change is in the view.** `DropBoxViewModel` is untouched: the same commands, the
same properties, the same bindings.

| | |
|---|---|
| The strip | a white panel with a light border and rounded corners, carrying the app's `FlatToolBar` toolbar — the same look as the Finder's toolbar and the dashboard's |
| The buttons | the app's `ToolbarButton` style, whose width floor is zero, so a button is as wide as its icon and its word |
| An icon | a text run in `Segoe MDL2 Assets` carrying the glyph from §3, beside the control's word — the pattern the other tabs already use |
| The drop-down's name | the **Namespace** label is removed; the drop-down keeps its own tooltip, which is what names it now |
| The hidden row | the row is collapsed; its commands and properties stay |
| The comment block | the markup comment above the **Sync From Here** button is removed with the rewrite |

## 7. Not in scope

- The Finder's and the Logs' toolbars.
- The item-info pane, and the tree-JSON preview inside it.
- The status line.
- What the hidden commands do.
- The **Sync From Here** feature itself, which is specified in `docs/sync-from-here-spec.md`.
- Deleting the hidden controls for good. They are hidden.

## 8. Decisions

**The namespace control.** The drop-down stays, unlabeled, immediately left of the path
input — the way a browser puts its site control at the left of the address bar. The tab
must still be able to move between the personal space and the team roots, and the
drop-down is what does that.

**The words on the buttons.** Every button is an icon beside its word, the shape the
Finder's toolbar uses. The three navigation actions are named on the face of the button,
not only in the tooltip.

Both were open when this document was written. These are what was built, and each is a
small change if the other reading is preferred. The alternatives not taken: the drop-down
at the far left of the row, or the namespace folded into the typed path; and the buttons
icon-only, with the name in the tooltip.

## 9. Acceptance criteria

| # | Criterion | How it is checked | State |
|---|---|---|---|
| 1 | The tab shows one toolbar row | the app, on screen | measured |
| 2 | The row holds Up, the namespace drop-down, the path input, Go, Refresh and Sync From Here | the app, on screen | measured |
| 3 | The heading and the description line are gone | the app, on screen | measured |
| 4 | The drop-down carries no "Namespace" label | the app, on screen | measured |
| 5 | The path input takes the width left over in the row | the app, on screen | measured |
| 6 | Up shows `U+E74A`, Go `U+E8AD`, Refresh `U+E72C`, Sync From Here `U+E896` | the app, on screen | measured — all four glyphs render, and they are distinct |
| 7 | The tree row is not on screen — tree depth, its input, the JSON buttons, Download file…, and the summary | the app, on screen | measured |
| 8 | The tree row's commands and properties are still in the view model, and showing the row brings them back | the code | measured — the bindings are in the view, and the row is collapsed rather than removed |
| 9 | The path input still navigates on Enter | the app, on screen | measured — a typed path loaded the folder it names |
| 10 | The path input still follows the navigation | press **Up** from a branch | measured — opening a folder moved the field, and **Up** moved it back |
| 11 | Up still goes to the parent folder | the app, on screen | measured |
| 12 | The namespace drop-down still switches namespace | the app, on screen | not measured — the drop-down was never opened |
| 13 | Sync From Here still starts the branch-scoped sync, and keeps its disabled states | the app, on screen | measured in part — the button is on the row and its hint names the branch the tab is showing; the run behind it was measured before this change |
| 14 | The grid, the item-info pane and the status line are unchanged | the app, on screen | measured |
| 15 | `DropBoxViewModel` is unchanged | the diff | measured — the change is one view |
