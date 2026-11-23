---
paths:
  - "**/Undo/**"
  - "**/*Undo*"
  - "**/*EditHistory*"
---

# Undo / redo — internals

(The «Schema changes» rules stay in CLAUDE.md «Undo / redo».)

Data-level undo (plan + rules У-1…У-19: `docs/undo-redo-plan.md`; user page `website/content/start/undo.md`). A step is
a `Guid` flowing with the async call (`BusinessLogic/Services/Undo/UndoScope` — AsyncLocal step + owner, leased: a task
that outlives its `Enter` no longer writes into the step); the station sends it as `X-OrientPyx-Undo-Step` and the
host's `RpcDispatcher` enters it with the station's **token** as owner.
- **Journal** (`DataAccess/Persistence/Undo/`): `UndoRecorder` (SaveChanges / transaction / command interceptors added
  in `EventDbContextFactory.Create`) records every changed row (type, key, old/new values) into the in-memory
  `UndoJournal.Shared` of the process that owns `event.db`, on commit only; a bulk statement (`ExecuteUpdate/Delete`,
  raw DML) under a step makes it `NotUndoable`. `UndoStoreGuard` (inside the router, over the local `EventStore`) runs
  reads under `UndoScope.Suppress()` (a read that seeds — the FSOU discount — never joins a step) and `Touch`es the step
  for writes (empty ≠ unknown). Limits: 200 steps per (competition, owner), 200 000 changes, 20 000 per step.
  `ReleaseAsync` forgets a competition's steps.
- **Revert**: `IEventStore.RevertUndoStepAsync` (`UndoReverter`): net effect per row → checked against the DB (third
  value / gone = conflict; value already back = skipped, `AlreadyReverted` when everything is) → the state AFTER the
  revert checked for `UndoUniqueRules` (bib, chip per day, names — `TextKey`), `UndoReferenceRules` (every Guid
  relation by convention) and closed days (`DayLocked`, step stays) → the saves replayed backwards, one `SaveChanges`
  each (unique indexes see the original intermediate states). All or nothing, one transaction, journalled under the
  revert's own step (redo = revert of the revert). `IEditHistoryService` wraps it and recomputes the points-rule copy
  (`CompetitionInfo.PointsRulesJson` is never journalled — `UndoRecorder.ExcludedColumns`).
- **UI** (`Presentation/Services/Undo/`): `IUndoHistory` — undo/redo stacks per (page `UndoKey`, page `UndoDayId`),
  100 each, cleared on another competition / role / `INetworkClient.HostRestarted`; `UndoGesture` decides the step
  synchronously at the moment a value changes: an open `Begin(label)` gesture (paste / fill-down in `SheetTable`, page
  multi-row actions), else the live cell's edit session (`LazyEditCell` opens it with «column — row»), else a new step;
  `UndoGesture.Programmatic()` for program-made row changes (no step, redo kept). A page opts in with `UseUndo`,
  captures with `CaptureUndo(label)` and runs each write through `RunUndoAsync(step, …)` (`PageViewModelBase`);
  debounced pages override `FlushPendingWritesAsync` and flush before switching the day. Undo waits for those writes,
  reverts under the busy overlay and awaits the page's `ReloadAsync` (whole-row DTO saves would otherwise write a stale
  row back). Keys in `MainWindow.TryUndoKey` (not in a `TextBox`), the «Файл → Правка» submenu and the cell-menu items
  (`SheetTable.UndoMenu`) all go through `EditMenuViewModel`. Not undoable (§10 of the plan): imports, draws, mass
  start, copy between days, delete-all / delete day, the finish page, forms outside tables, app.db pages, system writes.
  Entries hold only a Guid and a label — never a row VM. Tests: `Core/Undo/*`, `Ui/Undo*`.
