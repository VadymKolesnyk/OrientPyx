---
paths:
  - "**/*MassStart*"
  - "**/*MapCount*"
  - "**/*Draw*"
---

## Mass start («Масовий старт»)

«Старт → Масовий старт» (`MassStartDrawViewModel` / `MassStartDrawView`, beside the two draws): one start per group,
no minutes drawn. A row per group of the day (checkbox, «Поточний старт», editable «Старт»); with a relay on the day
also «Етапів» and one «Перезапуск етапу N» column per leg 2…(longest relay) — `Restarts[i]` cells, disabled where the
group has no such leg. Nothing is written until «Призначити старт» → `ICompetitionEditorService.ApplyMassStartAsync`
(`CompetitionEditorService.MassStart.cs`, existing store writers only — no RPC change): every runner's
`ParticipantDay.StartTime`; in a relay the leg-1 runners (and runners on no leg) get it, and the group's
`MassStartTime` + `RestartTimesJson` are replaced (blank = no restart; legs beyond `LegCount` dropped). The typed
values are page memory per day (`DrawSettingsKind.MassStart`, `DrawGroupRowSettings.Restarts`); a group with nothing
remembered seeds from the competition (relay settings, else the runners' common start). Tests: `Core/MassStartTests`.
This page is the only editor of a relay's mass start and restarts: the «Групи» relay strip edits legs, restart ranking
and leg courses only, and its save re-reads the group's relay settings first so it never writes back stale ones.

## Map counts («Кількість карт»)

«Старт → Кількість карт» (`MapCountsViewModel` / `MapCountsView`): one read-only `SheetTable` over the whole
competition — a row per group, a column per day with the group's headcount on that day (`null` = the group is
not on the day, shown blank), a trailing «(без групи)» row (amber) for day members with no group, per-day totals in
the table's status bar (`SheetColumnBuilder.Summary(path)` opts a column in), and an optional reserve (percent,
rounded up, but at least N maps) that adds a «День N + запас» twin per day. Data: `ICompetitionEditorService.
GetMapCountsAsync` → `MapCountsData` (BusinessLogic/Models/MapCounts.cs), joined to existing participants like the
dashboard. Export reuses the participants' CSV/Excel flow (`IParticipantExportFlow.RunAsync(view, day, nameKey)`)
over `SheetTable.ExportView()`. The reserve is page state only (not persisted).
