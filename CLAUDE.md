# OrientPyx

Cross-platform desktop application (Avalonia, .NET) for managing orienteering competitions.
It is a working product, not a starter: participant management, groups/courses/chips, chip
read-out, per-discipline result computation, and Word/HTML/thermal-printer protocol export are
all implemented. New work continues on this base — respect the layering and conventions below.

Result computation (`ComputeDayResultsAsync`), thermal-printer split/winner printouts, HTML
splits/monitor export, start draw, online publishing, and the Word `.docx` protocols are all
built and documented in the subsystem notes at the bottom of this file.

## Solution structure

```
OrientPyx.sln
src/
  OrientPyx.Presentation/    Avalonia UI (Views, ViewModels, navigation, app startup, DI composition)
  OrientPyx.BusinessLogic/   Entities, models, enums, discipline strategies, service interfaces + implementations
  OrientPyx.DataAccess/      EF Core + SQLite: app & event DbContexts, stores, events-folder scanner
  OrientPyx.Localization/    ILocalizationService + JSON resource dictionaries (uk-UA default, en-US)
  OrientPyx.Plugins.Sdk/     Plugin contract + shared pure algorithms (toolkit); no dependencies
plugins/
  OrientPyx.Plugins.sln      Separate solution for plugins (built into artifacts/plugins/<Name>/)
  Directory.Build.props      Shared plugin settings: net10.0, SDK referenced with Private=false
  MissedControlPenalty/      «Штраф замість зняття» test plugin
```

## Architecture rules

Three layers plus a shared localization library and the plugin SDK.

- **Presentation** — Avalonia views, ViewModels, navigation, startup, DI wiring.
  No business rules, no file/database logic.
- **BusinessLogic** — domain entities, models, discipline strategies, and service
  abstractions with their implementations. Must NOT reference Avalonia, EF Core, SQLite,
  files, printers, or DOCX/report libraries (infra like that lives in DataAccess and is
  reached through interfaces defined here).
- **DataAccess** — persistence and infrastructure. EF Core and SQLite live here and
  nowhere else. References BusinessLogic.
- **Localization** — pure .NET, no UI dependency, usable from any layer.
- **Plugins.Sdk** — the plugin contract (`IOrientPyxPlugin`, `Discipline`, contexts, columns, settings, fields)
  and the toolkit (`Toolkit/`: course patterns, `CourseMatcher`, `VariantMatcher`, `Punches`, `Ranking`, `Age`,
  `TimeFormat`, `TextKey`, `CourseOrder`) plus the shared simple types (`FinishStatus`, `FinishContext`,
  `FinishStatusResult`, `SplitsView`…, `ControlPointType`, `ChipPunch`, `GeoPoint`/`MapPoint`). **No dependencies,
  no knowledge of EF / files / UI — only the contract and pure algorithms.** Everything in it is public API with
  compatibility obligations: add only what is stable. Its `AssemblyVersion` is set explicitly (`1.0.0.0`) and is
  the plugin API version — never tie it to the app's `Version`. The three app projects import its namespaces
  globally (`<Using>` in their `.csproj`).

### Dependency direction

```
Presentation  -> BusinessLogic, DataAccess, Localization
DataAccess    -> BusinessLogic
BusinessLogic -> Plugins.Sdk
Plugins.Sdk   -> (none)
Localization  -> (none)
plugins       -> Plugins.Sdk (only)
```

Never introduce a dependency that points the other way (e.g. BusinessLogic must not
learn about Avalonia or EF Core).

## Competition context (two databases + session)

The app works in the context of a selected **competition + day**. Until one is selected,
the UI shows only the selection / creation screens (gating in `MainWindowViewModel`).

- **App database** (`./data/app.db`, `AppDbContext`) — shared: configurable paths
  (`AppSettingsRow`) and a last-session pointer (`LastSessionRow`).
- **Event database** (`./events/<identifier>/event.db`, `EventDbContext`) — one per
  competition: `CompetitionInfo`, `EventDay`, and competition data (participants, groups,
  courses, chips). Opened dynamically per path via `EventDbContextFactory` (internal to
  DataAccess); BusinessLogic talks to it only through `IEventStore`.
- Paths `./data` and `./events` default to the application directory and are editable on
  the Settings page (`IAppSettingsService`). **Installed (Velopack) builds redirect the data
  root** to `%LocalAppData%\OrientDesk\data-root` (see `AppDatabasePaths` / `Program.cs`) so
  data survives auto-updates — the app-dir default is for local dev only.
- The competition list is built by **scanning** `./events` (`IEventFolderScanner`).
- The identifier can be changed later on the Information page (`IEventCatalogService.RenameIdentifierAsync`):
  it renames the folder *and* rewrites `CompetitionInfo.Identifier` (the scanner reads it from the DB, so both
  must move together). The event DB is released first (`IEventStore.ReleaseAsync` — WAL checkpoint +
  `SqliteConnection.ClearAllPools()`), otherwise Windows refuses to move a folder holding an open file.

**Session rule (important):** the active competition/day is held **in-memory** by
`ISessionService` for the running instance. The app DB only stores the last selection for
startup restore — read once at launch, written on selection change. Never resolve "current
event/day" by querying the app DB during normal operation; the program may run as multiple
instances over the same `./data`, and runtime state must not be shared through it.

## Localization rules

- **UI text is Ukrainian (uk-UA) by default.**
- **Do not hardcode user-facing strings** in Views or ViewModels.
- New user-facing text → add a key to **both** `src/OrientPyx.Localization/Resources/uk-UA.json`
  and `en-US.json`, then bind/resolve it through `ILocalizationService`.
- Keep keys clear and grouped, e.g. `Nav.Participants`, `Page.Results.Title`.
- Resolve dynamic-key text via a VM property that calls `ILocalizationService.Get(key)`
  and re-raises on language change (see `PageViewModelBase`). Bind literal keys in XAML
  via the indexer: `{Binding Localization[App.Title]}`.
- Language can be switched at runtime (see Settings page) — keep that working.

## Coding style

- File-scoped namespaces, nullable reference types enabled, `ImplicitUsings` on.
- `async` methods where appropriate; pass `CancellationToken` through service methods.
- Small classes, clear names, simple code. No over-engineering.
- MVVM via CommunityToolkit.Mvvm source generators: `ObservableObject`,
  `[ObservableProperty]`, `[RelayCommand]`.
- DI via plain `Microsoft.Extensions.DependencyInjection` (no Generic Host).

## Disciplines (strategy, not switch)

Everything that varies by competition type lives behind `IDisciplineStrategy`
(`BusinessLogic/Disciplines/`), one implementation per discipline id, resolved through
`IDisciplineStrategyProvider`. A discipline's identity is the string struct `DisciplineId` (built-ins
`SetCourse`, `ScoreByCount`, `ScoreByTime`, `Rogaine`, `Mixed`, `Scatter` — equal to the former enum names, so
stored values are unchanged; plugin ids are `"<pluginId>.<name>"`). An id with no registered strategy resolves
to `UnknownDisciplineStrategy` (judged like a set course, no places/points/ranks, `IsUnknown` for the banner) —
never an exception. Shared code asks the strategy — grid columns, control count, `Competitors`
(`CompetitorModel.Individual` / `Team` = rogaine / `TeamWithLegs` = relay), `UsesCourseVariants` (scatter), `UsesCoursePattern` (mixed only),
`AwardsRanks`, `EvaluateFinish`, `BuildSplits`, `ResolveStart`, `AdjustTime`, `Rank` — instead of comparing against
a specific id. **Adding a format = one new strategy class + one DI registration**
(`BusinessLogicServiceCollectionExtensions`); do not reintroduce `switch`/`==` on a discipline in shared code.

Day results are computed by `Services/Results/DayResultsCalculator` (internal, no DB access) from one
`EventDaySnapshot` (now carrying the day's read-outs and scatter variants, read in the same transaction) plus
`DayResultsAppData` (points rules, rank tables). Stages: competitors → start + finish status (`FinishEvaluator`) →
`AdjustTime` (changed times rounded to a second; built-ins unchanged) → `Rank` per group (rogaine team ranking lives
in `RogaineStrategy.Rank`) → points (formula uses `AdjustedTime`) → ranks (actual time, only if `AwardsRanks`).
`CompetitionEditorService.ComputeDayResultsAsync` only reads the snapshot and calls it — pass the caller's snapshot
when it has one. `FinishStatusResult.Missed` lists every missed control (maximum match, `MissedControls`); status
and `Detail` still come from the greedy checks. Debug builds have «Файл → (Debug) Знімок результатів…» (JSON of
every day's results) + `scripts/diff-results.ps1` to prove a refactoring left results unchanged.

### Plugins

Author-facing guide (contract, contexts, toolkit, versioning, examples): `docs/plugin-api.md`.

A plugin is a DLL with one `IOrientPyxPlugin`; it registers `Discipline` subclasses (each `BasedOn` a built-in —
the format's structure comes from it, the plugin overrides rules: `EvaluateFinish`, `BuildSplits`, `ResolveStart`,
`AdjustTime`, `Rank`, `AwardsRanks`, `Columns`, `Settings`), participant fields and translated strings.
- **Loading** (`DataAccess/Plugins/`): `PluginCatalog : IPluginCatalog` scans one folder, `<data root>/plugins`; before
  loading it copies the standard plugins in from `<app dir>/plugins` (and, Debug, `<repo>/artifacts/plugins`) — a file
  missing or older than the bundled one is replaced, a locked one skipped. Each plugin gets its own
  `PluginLoadContext`; the SDK always resolves from the default context. Loading is lazy and happens right after
  the app DB is migrated (`PluginStartup.RegisterStrings` in `App.axaml.cs`), because the switched-on list lives in
  `AppSettingsRow.EnabledPluginsJson`. Every found plugin registers, but **stays off until switched on** on the
  «Плагіни → Керування плагінами…» page (`PluginsViewModel`); `IPluginCatalog.IsEnabled`/`SetEnabledAsync`/`EnabledChanged`
  switch it live (no restart: the provider, the type dropdowns, the banner and the menu follow). Any failure →
  `PluginLoadError` (shown on that page); the app always starts. No hot reload of DLLs (a new file needs a restart).
- **Adapter** (`BusinessLogic/Disciplines/Plugins/`): `DisciplineStrategyProvider` wraps each plugin
  discipline in a `PluginDisciplineStrategy` (`All`/`For` skip the switched-off plugins' ones — `For` gives
  `UnknownDisciplineStrategy`) over its `BasedOn` built-in (`base.*` runs the built-in through
  `IDisciplineHost` with the contexts the core built). Every call into plugin code is guarded: on an exception the
  built-in result is used, logged, and `PluginRuntimeErrors.FirstFailure` shows one banner per plugin per session.
- **Strings**: a plugin's keys resolve as `"<pluginId>.<key>"` via `ILocalizationService.AddOverlay`; a key with no
  translation falls back to en-US, then to the bare key (so plain text works).
- **Settings** (Р-11): `PluginSettingsRow(EventDayId?, DisciplineId, Json)` in `event.db` — a day row for
  `SettingScope.Day` values, a no-day row for `SettingScope.Competition`; `DisciplineSettings.Resolve` merges them
  with the defaults. Edited on the plugin's own page: the top menu «Плагіни» lists one item per switched-on plugin
  (built in `MainWindow.axaml.cs` from `MainWindowViewModel.PluginMenu`) → `PluginSettingsViewModel` (one singleton,
  `PluginId` set before load): an auto-form card per discipline, day-scoped values for the day picked on the page;
  saving re-points the session so pages recompute. The snapshot carries the rows
  (`EventDaySnapshot.DisciplineSettings`) so the calculator and the read-out log see the same values.
- **Table settings**: `Setting.Table(key, label, columns, TableValue default)` — rows of text cells keyed by column
  key, stored as a JSON array; edited in `SettingTableView` shown over the settings dialog
  (`IDialogService.ShowSettingTableAsync` restores the previous dialog). `Toolkit.RangeTable` reads a «range × key →
  number» table (nearest row outside the ranges, last row open — Р-47).
- **Results**: a plugin's `ResultRow.Set` values land in `ParticipantDayResult.Extra` and show as extra day-grid
  columns (`ResultExtras[key]`, red tint when `ResultColumnDef.Problem` says so); `MarkUnranked` →
  `ParticipantDayResult.UnrankedReason` qualified as `"<pluginId>.<reason>"` (no place, no points; on a relay leg the
  whole team; the protocol prints the translated reason after the name).
- **Extra result columns** (§10.2): `ExtraResultColumn` (BusinessLogic/Models) — plugin columns keyed
  `"<pluginId>.<key>"` and additional fields keyed `"field.<key>"`, listed per day in
  `ResultProtocolData.ExtraColumns`. The Word protocol stores them as `ProtocolColumn.Extra` + `ProtocolColumnSetting.Key`
  (built-ins keep the enum, so old templates read unchanged; `ColumnKey` = `builtin.<Name>` | extra key); the monitor
  as `ResultColumn.Extra` keys in `ResultColumnSelection`. A stored extra key the day lacks is skipped on build and
  kept on save. The online `results` table keeps the fixed columns only (the frontend knows no others); relay legs
  carry plugin columns in `relay_teams.legs[].extra` (`docs/online-snapshot.md`).
- **Unknown discipline** (plugin missing/disabled): `UnknownDisciplineStrategy` + the «Потрібен плагін» banner
  above the page (`PluginNoticeViewModel`). `.opyx` export writes `plugins.json` (required plugins); import warns
  about missing ones but proceeds.
- **Release**: `build/publish.ps1` builds `plugins/OrientPyx.Plugins.sln` and copies `artifacts/plugins/*` into the
  package's `plugins\` folder. Build plugins locally with `dotnet build plugins/OrientPyx.Plugins.sln`.

Notable formats: **Mixed** (змішаний) judges against a course *pattern* (`<seq>` ordered runs,
`[N …]` any-of blocks, nesting) like a set course. **Scatter** (розсіювання) has several valid
orders (variants) per (day, group) stored in the `GroupCourseVariant` event-DB table (renamed from
`ScatterVariant`); each runner's variant is auto-detected from their read-out (best-matching order,
`Toolkit.VariantMatcher`) and then judged like a set course against it. A variant may also be a mixed
pattern (matched through `CourseMatcher`, never expanded into orders). **Relay** (естафета) legs run the
group's course, or — per `GroupDaySettings.CourseAssignmentMode` (Р-41) — a named course from the same
`GroupCourseVariant` rows: `ByLeg` (leg → code, `LegCoursesJson`), `ByBib` (`LegCourseAssignment` per leg
number, typed on «Команди» or imported from IOF `TeamCourseAssignment` / CSV), `TeamSet` (a course set per
team/group distributed over the read legs by `Toolkit.TeamCourseAssignment`, least missed controls, Р-42).

## Additional participant fields

A competition can define its own participant fields (Р-13…Р-19 in `docs/plugins-plan.md`): `CustomFieldDef`
(key PK, label, kind Text|Number|Bool|Choice, choice options + synonyms as JSON, source `user`/`plugin:<id>`) and
`ParticipantFieldValue(ParticipantId, Key, Value)` in `event.db`. Values are participant-level, deleted with the
participant (`DeleteParticipantAsync`, delete-all, import clear-first) and with the field. The domain model is
`CustomField` (BusinessLogic/Models) — `Normalize` is the single place a raw value becomes the stored one (choice
synonym → value via `TextKey`, number comma/dot), `IsInvalid` drives the red cell. Service API on
`ICompetitionEditorService`: `Get/Add/Update/DeleteCustomFieldAsync`, `SetParticipantFieldAsync`,
`EnsurePluginFieldsAsync` (a plugin's `AddParticipantField` fields are created when its discipline is first used).
Both participant tables add one `field.<key>` column per field (`Controls/Roster/CustomFieldColumns.cs`, binding the
row's `Fields[key]` cell VM); export and the statement pick them up. CSV/Excel import maps onto `ImportTarget`
(string-keyed: built-ins by their old enum names, `field.<key>`, `relay.teamNumber|teamName|leg`), remembers the last mapping per
header set (`ImportMappingRow` in `event.db`, app default in `AppSettingsRow.ImportMappingsJson`) and can create a new
field from a file column («+ Нове додаткове поле…»).

## Relay (естафета)

Built-in discipline `DisciplineId.Relay` (`RelayStrategy`, `CompetitorModel.TeamWithLegs`; rules Р-20…Р-44 in
`docs/plugins-plan.md`, worked examples in `docs/relay-scenarios.md`). `RelayTeam` (day + group, number unique per
day) holds the team; a runner is placed on a leg via `ParticipantDay.RelayTeamId` + `Leg`; the leg number «100.2»
is computed (`Toolkit.RelayBib`), never written to `Participant.Number`. Group settings: `LegCount`,
`MassStartTime`, restarts (`RestartTimesJson`, `RestartRanking`), `CourseAssignmentMode`. The calculator's relay
pass (`DayResultsCalculator.Relay.cs`) judges legs in sequence (leg N starts at leg N−1's finish or the restart),
sums adjusted leg times, ranks teams and copies place/points to every member; `TeamDayResult` /
`RelayLegResult` carry the team and leg results, `ParticipantDayResult.Team/Leg/LegBib` link a runner to them.
Pages/outputs: «Команди» (teams + legs), day-grid team/leg columns, read-out log, result/start protocols
(`ResultProtocolGroup.RelayTeams`, sub-rows), import (CSV `relay.*` targets, IOF `EntryList` `TeamEntry`), draw
(a team is the unit, leg 1 gets the minute), monitor (`MonitorRowKind.Team/Leg`), HTML splits (leg start + leg
course), summary («за очками» counts relay points; «за часом» skips relay days, Р-39), online (`relay_teams`).

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

## How to build

```bash
dotnet restore
dotnet build
```

## How to run

```bash
dotnet run --project src/OrientPyx.Presentation
```

The window opens with a Ukrainian sidebar; the default page is Панель (Dashboard). Until a
competition + day is selected the UI shows only the selection/creation screens. SQLite files
are created on first run (`app.db` in the data root; per-competition `event.db` under `events/`).

## Schema management

EF Core **migrations** are the canonical way schema evolves, for both databases:
`AppDbContext` (`./data/app.db`) and `EventDbContext` (per-competition `event.db`). Apply
them at runtime with `Database.Migrate()` — the app DB on startup
(`InitializeOrientPyxDatabase`) and each event DB when it is opened
(`EventStore.EnsureCreatedAsync`, which now calls `MigrateAsync`). Add a new migration when
you change an entity, e.g.:

```bash
dotnet ef migrations add <Name> --context EventDbContext \
  --project src/OrientPyx.DataAccess --startup-project src/OrientPyx.DataAccess \
  --output-dir Persistence/Migrations/Event
```

(Use `--context AppDbContext` + `--output-dir Persistence/Migrations/App` for the app DB.)
Each context has an `IDesignTimeDbContextFactory` so the EF tools can run without the app.
Do not reintroduce `EnsureCreated` — it is incompatible with migrations.

## What NOT to do

Do not add: test projects, Clean Architecture / CQRS / MediatR, Docker, or authentication.
Keep the app lightweight and the four layers + plugin SDK structure above intact (no new projects besides
plugins under `plugins/`; plugins reference only the SDK). (The Supabase online-results
publisher below is the one exception to "no cloud sync" — it is publish-only and stays confined to
DataAccess.)

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
already-computed results to a Supabase project that a separate, already-deployed React spectator frontend reads.
OrientPyx only does the publisher side: a background process (`OnlineResultsViewModel`, surfaced in the top-bar
activity block via `OnlineResultsActivity`) builds a snapshot each tick with `ICompetitionEditorService.
GetOnlineResultsSnapshotAsync(dayId)` — which reuses `GetResultProtocolDataAsync`, so it's the same computed data
the protocols show — and upserts it via `IResultPublisher`/`SupabaseResultPublisher` (DataAccess, PostgREST). The
**connection keys** (Supabase URL, secret service-role key, public frontend URL, interval) are app-level: columns on
`AppSettingsRow`, edited on the Settings page (`IAppSettingsService.Get/SaveOnlineApiSettingsAsync`,
`OnlineApiSettings`). The **per-competition publish options** (slug, title, subtitle, standings/points flags) live
per competition in `event.db` (`OnlinePublishSettingsRow`, JSON) via
`ICompetitionEditorService.Get/SaveOnlinePublishSettingsAsync` (`OnlinePublishSettings`), seeded from the
competition metadata. The service-role key never leaves the local app DB. Relay groups publish to a separate
`relay_teams` table, never to `results`; a project without it just logs a warning — contract in
`docs/online-snapshot.md`.)
