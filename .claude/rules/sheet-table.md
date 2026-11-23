---
paths:
  - "src/OrientPyx.Presentation/Controls/**"
  - "src/OrientPyx.Presentation/Views/**"
  - "tests/OrientPyx.Tests/Ui/**"
---

## Editable tables (SheetTable)

Every spreadsheet-style screen (control points, groups, days, chips, and the participant roster)
uses the one shared `controls:SheetTable` (`Controls/Sheet/`), wrapped in a
`Border Classes="card" ClipToBounds="True"`. It is a purpose-built control — **not** an Avalonia
DataGrid — with a virtualized `ListBox` body and a code-built header; it owns its own scrollbars.
The shell hosts pages **without an outer ScrollViewer** (see `ShellView.axaml`) so the table keeps
a bounded height and scrolls internally; an outer ScrollViewer would hand it infinite height and
make it render every row. (The old `SheetDataGrid` + `Avalonia.Controls.DataGrid` package were
removed in favour of this.)

Columns are built **in the View's code-behind**, not in XAML. Use the fluent `SheetColumnBuilder`
(`Controls/Sheet/`) to declare columns — `Text(...)` (optionally a numeric `mask`, `enabledPath`,
`opacityPath`, or rental-chip highlight), `Combo(...)`, `Date(...)`, `Custom(...)` for an arbitrary
cell, and `DeleteAction(...)` for the trailing delete column — then assign `Sheet.Bands = builder.Bands`.
Rebuild the bands on a language change and whenever the visible column set changes (headers are baked
into the band model at build time). Wire keyboard delete via the table's `DeleteRequested` event and
per-row delete buttons via the `DeleteAction` callback (capture Ctrl in a tunnel `PointerPressed` to
support Ctrl+Click = skip confirm). `ChipsView.axaml.cs` is the simplest template; `GroupsView` shows
conditional columns + per-cell enabled/opacity; `CompetitionDaysView` shows a multi-button action cell.

The participant roster ("Мандатка") additionally needs a true two-tier (banded) header — per-day
columns under a spanning band label — which it builds with the roster-specific `RosterColumnBuilder`
/ `RosterCellFactory` (`Controls/Roster/`) and the table's `Days`/`Blocks` properties instead of `Bands`.

**Selection and row actions.** A press-drag / Shift+click / Shift+arrow selects an Excel-style cell range; **Ctrl+A**
selects the whole filtered, sorted view (outside a text edit). `SheetTable.SelectedRowItems` lists the rows the
selection covers whatever columns it spans. A page may give a table one **row action** — `RowActionLabel`
(rows → localized label, null = not offered) + `RowActionRequested` — shown in the cell context menu with the
«F8» hint and run by F8 over the selected rows (right-click outside the selection = the clicked row alone). The
roster uses it for **«Мандатку пройдено»** (`Participant.CheckedIn`, competition-level): the «Номер» cell turns
green (`DayColumnBuilder.CheckedInBrush`), the toggle marks all selected rows when any is unmarked, else clears
them, written by `SetParticipantsCheckedInAsync` (its own writer — `UpdateParticipantAsync` leaves the flag
alone, like `PaysRaisedFee`); the roster's `StatusInfo` adds «Мандатка: пройшли X з Y». No column of its own; the «Номер» column's filter has a «За станом» mode (`CheckedInStatus`).

**Cell controls must not be held by anything that outlives the row.** The body `ListBox` clears a container's content
when it recycles it, so every scroll (and every arrow key that scrolls) builds the rows it reaches anew; a row that
a long-lived object still references (the table, the page, a shared registry or options list) is never collected —
≈1 MB a row, ~6 GB on a host after an hour of «Мандатка» (2026-11-07). So no `table.GetObservable(...)` /
`registry.Changed +=` from a cell: push table-wide state onto the realised cells (`SheetLock.Apply`, called on build,
on `ContainerPrepared` and on `IsLocked` change), or subscribe only while the control is in the visual tree
(`ChipHighlight.Subscription`). Plain `Binding`s (DataContext or `Source =` an INPC/Avalonia object) are weak and fine.
`tests/Ui/SheetSelectionMemoryTests` guards it (arrow keys + scrolling, roster and day grid).

Column widths are content-sized then user-resizable; explicit fixed widths are set via the builder's
`width:` argument. There is no star/`*` sizing — the table left-aligns content-width columns and
scrolls horizontally.

A table **persists its view** (column order, width, hidden set) in two layers, wired by setting
`LayoutKey` (a stable id, e.g. `"participants.day"`) and `LayoutStore` (`ITableLayoutStore`, a
singleton) on the `SheetTable` — every table in the app sets both.

- **Per competition** — `events/<id>/views.json`, one JSON object keyed by table id. Written
  automatically on every hide/reorder/resize, reloaded on build. No-op when no competition is selected.
- **Application default** — `views-default.json` in the data root (`AppDatabasePaths.BaseDirectory`,
  so it follows the installed build's redirected root). Written only when the user picks «Зберегти
  вигляд для наступних змагань» in the table's columns menu, and only for that one table. A
  competition with no view of its own seeds from it.

`Load` returns the competition's own entry, else the app default. The columns menu also offers
«Скинути вигляд таблиці» (`SheetTable.ResetLayout`), which drops the table's entry in *both* layers and
restores the widths the columns were built with. Both files are sanitised on read (blank/duplicate
keys, implausible widths dropped; an entry left with nothing reads as "no layout"), so a hand-edited or
stale file always falls back to the build default rather than breaking the table.
