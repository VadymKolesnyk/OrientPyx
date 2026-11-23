---
paths:
  - "**/*Protocol*"
  - "**/*Monitor*"
  - "**/*Online*"
  - "**/*Publisher*"
  - "**/*Docx*"
  - "docs/online-snapshot.md"
---

# Result outputs: monitor, Word protocols, online results

(Plugin and additional-field columns in these outputs: `.claude/rules/plugins.md`, «Extra result columns».)

(**Results monitor** — «Результати на монітор» (`MonitorResultsViewModel`): the generation loop ticks every second,
builds the day's documents only when a file is due (its «Оновлення», default 5 s) and writes a file only when its
content changed: the page `rezNN.html` and beside it `rezNN.data.js` (`MonitorFile.DataFileName`,
`IMonitorHtmlWriter.WriteData` — one `orientPyxMonitor({...})` call, ASCII JSON). The open page (file://, so no
`fetch`) loads that script every 2 s and swaps its header / groups in place when their stamps changed, keeping the
group at the top of the screen at the same offset; a data file with another layout (timing, `PageVersion`) or none
at all ⇒ the old reload at the top of a scroll cycle. Browser-checked in `Core/MonitorLiveDataTests`
(`[BrowserFact]`: headless Edge/Chrome, skipped without one).)

(The result protocol — «Протоколи результатів» — now exports a per-group results protocol to a
Word .docx via `IResultProtocolBuilder` (BusinessLogic) + `DocxResultProtocolWriter` (DataAccess,
Open XML SDK). The template — orientation, ordered/visible columns, header text — is stored **per
competition day** in `event.db` (`ResultProtocolSettingsRow`, JSON keyed on `EventDayId`, accessed via
`ICompetitionEditorService.Get/SaveResultProtocolSettingsAsync(dayId)`); a day with no row is seeded from
the app-level default (`AppSettingsRow.ResultProtocolJson` via `IAppSettingsService`). The page shows a
**live document preview** (`ProtocolPreviewViewModel`) — the actual mock-up built from the same builder the
export uses, filled with real participants of the selected day — whose column headers are drag-reorderable
(code-built `Grid` via the shared `ProtocolPreviewTable` + `IProtocolPreviewHost`). The «Протоколи» top menu hosts it.

The «Старт» menu also hosts two **start protocols** built on the same pattern (per-day template + live
preview + .docx export, reusing `ResultProtocolDocument` + `DocxResultProtocolWriter`): «Стартовий протокол»
(regular — one section per group, members ordered by start time within the group) and «Стартовий суддівський
протокол» (judges — one section per start minute, members of that minute across all groups; undrawn runners
in a trailing "no start time" section). Both run off one `StartProtocolsViewModel` (singleton, `Kind` set by
the open command) → `IStartProtocolBuilder`/`StartProtocolBuilder` (BusinessLogic, produces a
`ResultProtocolDocument`). Template stored per day **and per kind** in `event.db`
(`StartProtocolSettingsRow`, JSON keyed on `(EventDayId, Kind)`) via
`ICompetitionEditorService.Get/SaveStartProtocolSettingsAsync(dayId, kind)`; a missing row seeds from
`StartProtocolSettings.Default(kind)`. Start columns are their own enum (`StartProtocolColumn`: Start/№/ПІБ/
рік/клуб/регіон/ДЮСШ/тренер/кваліф./чіп/група). Source data: `GetStartProtocolDataAsync(dayId)` reads
`ParticipantDay.StartTime`/`Chip`.)

(**Online live results** — «Онлайн-результати» (in the «Протоколи» top menu) publishes the active session day's
already-computed results to a Supabase project that a separate, already-deployed React spectator frontend reads
(repo `VadymKolesnyk/o-results`: `web/` + `online-db/schema.sql`; self-hosting how-to for users —
`website/content/race-day/online-site.md`).
OrientPyx only does the publisher side: a background process (`OnlineResultsViewModel`, surfaced in the top-bar
activity block via `OnlineResultsActivity`) builds a snapshot each tick with `ICompetitionEditorService.
GetOnlineResultsSnapshotAsync(dayId)` — which reuses `GetResultProtocolDataAsync`, so it's the same computed data
the protocols show — and upserts it via `IResultPublisher`/`SupabaseResultPublisher` (DataAccess, PostgREST). The
**connection keys** (Supabase URL, secret service-role key, public frontend URL, interval) are app-level: columns on
`AppSettingsRow`, edited on the Settings page (`IAppSettingsService.Get/SaveOnlineApiSettingsAsync`,
`OnlineApiSettings`). The **per-competition publish options** (slug, title, subtitle, standings/points flags) live
per competition in `event.db` (`OnlinePublishSettingsRow`, JSON) via
`ICompetitionEditorService.Get/SaveOnlinePublishSettingsAsync` (`OnlinePublishSettings`), seeded from the
competition metadata. The service-role key never leaves the local app DB. Start numbers are optional: a runner
without one (or the second holder of a shared one) goes under a stable negative `bib` (`OnlineResultRow.UnnumberedKey`,
from the participant id — the same on every day; the site shows only a positive bib), and the publisher deletes that
stand-in row once the runner gets a number (`PruneUnnumberedAsync`). Relay groups publish to a separate
`relay_teams` table, never to `results`; a project without it just logs a warning — contract in
`docs/online-snapshot.md`.)
