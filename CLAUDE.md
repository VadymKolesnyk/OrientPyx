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
  `TimeFormat`, `TextKey`, `CourseOrder`, `SiCard`) plus the shared simple types (`FinishStatus`, `FinishContext`,
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
  root** — Windows `%LocalAppData%\OrientPyx-data`, macOS `~/Library/Application Support/OrientPyx/data-root`
  (see `AppDatabasePaths` / `Program.cs`) — so data survives auto-updates AND an uninstall: Velopack's uninstall
  deletes the whole `%LocalAppData%\OrientPyx` install folder, so the data root must never live inside it. Builds up
  to 0.7.16 used `%LocalAppData%\OrientPyx\data-root`; `AppDatabasePaths.MoveLegacyDataRoot` renames it once on
  start (if a file is open it keeps using it in place and retries next launch — it never copies an open SQLite db)
  and `RemapLegacyPath` reads a stored path under it as the same path under the new root. The app-dir default is for
  local dev only.
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

**App-level ids are per app.db.** Points rules (app.db) get their id where they are created — the seeded ones too,
so two installs (or the dev build and the installed one) have different ids for the same rule. A competition
references a rule by id (`CompetitionInfo.DefaultPointsRuleId`, `GroupDaySettings.PointsRuleId`) **and keeps its
own copy** of every referenced rule in `CompetitionInfo.PointsRulesJson` (`Models/PointsRuleCopies`): results take
the rule from the catalogue first, else from the copy, so every instance scores the same. `PointsRuleCopySync`
refreshes the copy on opening (`SessionService`), after a group/default is pointed at a rule, and after a rule is
edited on «Очки»; the copy has its own writer (`IEventStore.SetPointsRulesJsonAsync`) — `SaveCompetitionInfoAsync`
leaves it alone. Rule pickers list `ICompetitionEditorService.GetPointsRuleChoicesAsync` (catalogue through
`IResultsAppData` + copies marked «(зі змагань)»), never `IAppStore` directly.

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
- **SQLite completes synchronously** (no real async I/O), so a store call awaited on the UI thread would run the
  query — and any wait for a lock held by a background writer — on the UI thread. Every reflection-built service
  proxy (`RoutingProxy`, `DecoratorProxy`, and `UiOffloadProxy` over `ICompetitionEditorService` /
  `IAppSettingsService`) routes calls through `UiThreadOffload`: a Task-returning call made from a thread with a
  `SynchronizationContext` (the UI thread) starts on the thread pool; the caller's `await` resumes on the UI thread.
  `BusyService.RunAsync` still adds the overlay. Write UI state only after the `await`, never inside the service.
  `Presentation/Services/UiStallWatchdog` (started in `App.axaml.cs`) logs every UI-thread stall over 1 s to the
  activity log with the role and the running background activities — check `events/logs` after a freeze report.

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
A read-out with no start from anywhere (no chip start, no start-protocol time, no relay leg start) is DNS in
`FinishEvaluator` (both the calculator and the finish log go through it); «Мін. КП» is lowered by the disabled
controls of the group's list. `CompetitionEditorService.ComputeDayResultsAsync` only reads the snapshot and calls it — pass the caller's snapshot
when it has one. `FinishStatusResult.Missed` lists every missed control (maximum match, `MissedControls`); status
and `Detail` still come from the greedy checks. Debug builds have «Файл → (Debug) Знімок результатів…» (JSON of
every day's results) + `scripts/diff-results.ps1` to prove a refactoring left results unchanged.

### Plugins

Author-facing guide (contract, contexts, toolkit, versioning, examples): `website/content/plugins/` (published on the
documentation site, «Власні плагіни»; `docs/plugin-api.md` only points there).

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

## Platforms & packaging

Windows and macOS are shipped; Linux runs from source only. On the `v<version>` tag GitHub Actions runs
`build/publish.ps1` (Windows, `.github/workflows/release-windows.yml`) and `build/publish-mac.sh` (macOS,
`release-macos.yml`); both pack with Velopack into one GitHub Release, each platform on its own channel (`win`,
`osx-arm64`, `osx-x64`; both uploads use `--merge`). The release is created first — by `/new-version` (or the
Windows workflow) with `gh release create`, the macOS workflow waits for it — so neither `vpk upload` ever creates
a draft of its own (two drafts for one tag = the second publish fails with `tag_name already_exists`). A local
`publish.ps1` builds the same package **unsigned** (`-Upload` = emergency fallback only). **Windows code signing**
goes through the SignPath Foundation (free OSS certificate, publisher «SignPath Foundation»), which signs only
artifacts of a GitHub-hosted Actions run: the workflow packs twice (`build/signpath/vpk-sign-hook.ps1`
collects the files `vpk` would sign, request 1; the real pack swaps the signed copies in) and signs `Setup.exe`
after packing (request 2); on for `secrets.SIGNPATH_API_TOKEN` + `vars.SIGNPATH_ORGANIZATION_ID`, unsigned
otherwise. Signed files must say product `OrientPyx` and product version = the release — keep
`src/Directory.Build.props` / `plugins/Directory.Build.props` on every project and list a new own binary in
`build/signpath/SigningRules.ps1` + `artifact-configuration.xml`. How-to and status: `docs/code-signing.md`.
Details and the optional Apple signing secrets: `build/README.md`. Keep the code portable:
anything Windows-only (GDI printing in `DataAccess/Printing`, `FileAssociation`, Explorer) must be guarded by
`OperatingSystem.IsWindows()` and degrade gracefully (the print services expose `IsSupported`); reveal/open
files through `Presentation/Services/FileReveal.cs`, never by launching `explorer.exe` directly. On macOS the
installed layout is the `.app` bundle with `UpdateMac` beside the exe (`Program.IsVelopackInstallLayout`) and
data goes to `~/Library/Application Support/OrientPyx/data-root`. The `.opyx` association on macOS is the
bundle's `build/mac/Info.plist` (copied as-is by `vpk pack --plist`, so keep its standard keys complete) and the
file arrives as an `IActivatableLifetime` file activation handled in `App.axaml.cs`, not in `args`.

Notable formats: **Mixed** (змішаний) judges against a course *pattern* (`<seq>` ordered runs,
`[N …]` any-of blocks, nesting) like a set course. **Scatter** (розсіювання) has several valid
orders (variants) per (day, group) stored in the `GroupCourseVariant` event-DB table (renamed from
`ScatterVariant`); each runner's variant is auto-detected from their read-out (best-matching order,
`Toolkit.VariantMatcher`) and then judged like a set course against it. A variant may also be a mixed
pattern (matched through `CourseMatcher`, never expanded into orders). **Relay** (естафета) legs run the
group's course, or — per `GroupDaySettings.CourseAssignmentMode` (Р-41) — a named course from the same
`GroupCourseVariant` rows: `ByBib` (`LegCourseAssignment` per leg number, typed on «Команди», imported from IOF
`TeamCourseAssignment` / CSV, or brought by the course import), `TeamSet` (a course set per team — its own, else its
leg numbers' `LegCourseAssignment` codes, else the group's — distributed over the read legs by
`Toolkit.TeamCourseAssignment`, least missed controls, Р-42). The former `ByLeg` mode is gone (migration
`DropRelayByLeg` turns it into `ByBib` rows). `LegCourseAssignment.GroupId` (nullable) tells the row's group; the
calculator ignores a row naming another group.

**OCAD variations by number.** `IofXmlParser` keeps every variation in `IofCourse.AllVariants` (nothing merged — a
relay's «1AAAAA» and «4AAAAA» run the same controls) besides the distinct `Variants`, and reads the file's number/leg
assignments into `IofCourse.TeamCombinations` (2.0.3 `<TeamCombination>` by `CourseVariationId`, 3.0
`TeamCourseAssignment` / `PersonCourseAssignment` by course name). «Розділення груп» (`SplitGroupsViewModel`) asks per
course with variations «Розсіювання» / «Естафета» + «варіант за номером етапу» / «набір на команду»
(`IofCourse.VariationUse`, default relay for a multi-leg OCAD relay, a relay day or an existing relay group);
`ImportGroupsAsync` then makes a relay group (`LegCount` from the file, all variations, the assignments via
`IEventStore.ReplaceGroupLegCourseAssignmentsAsync`) or a scatter one (all variations when numbered, else the distinct
orders). «Старт → Варіанти за номерами» (`NumberVariantsViewModel`, `GetNumberVariantsAsync`) lists number → variation
for map handout (plus «за номером етапу» legs without one), exportable like «Кількість карт».

### The day a call works on

`CompetitionEditorService` works on the session's day, except the finish-log methods (`ImportFinishReadoutsAsync`,
`GetFinishReadoutLogAsync`/`RowsAsync`, `GetFinishSplitsAsync`, `GetSplitPrintDocumentAsync`,
`GetFinishReadoutEditAsync`, `UpdateFinishReadoutAsync`) which take an optional `forDayId`: the method sets an
`AsyncLocal` day override (`ResolveDayAsync` + `_dayOverride`, set in the calling method itself — an AsyncLocal set
inside an awaited helper does not flow back) that every nested call sees through `CurrentDay`/`CurrentDayId`. The finish
auto-read, the results monitor and online publishing each remember the day (and competition) they were started on and
keep working for it when a page switches the session's day; only another competition (and, for the auto-read, its own
day closed/deleted) stops them.

## Network mode («Спільна робота»: host + stations)

Several computers work on one competition over the LAN: one is the **host** (its `events/<id>/event.db`, the only
process that opens it), the others are **stations** that edit the same data live. Plan and design notes:
`docs/network-mode-plan.md`, `docs/network-mode-implementation.md`; the wire contract: `docs/network-protocol.md`.

- **The network boundary is a set of interfaces**, nothing above them knows the role: `IEventStore`,
  `IEventFolderScanner`, `IEventCatalogService`, `IEventFiles` (the competition folder's files + this machine's local
  paths), `IEventArchiveService`, `IResultsAppData` (points rules / ranks / qualification — on a station the HOST's
  tables, so both machines compute identical results). DI registers each as a **router**
  (`DataAccess/Network/Routing/RoutingProxy<T>`, a `DispatchProxy`) over the local class and the remote proxy
  (`IRemoteServices`), switched by `IWorkspaceMode.Role` (`Local | Host | Station`) on every call — no restart.
  `IEventStore` is additionally wrapped in `NotifyingEventStore` (outermost) and `IAppStore` in `NotifyingAppStore`.
- **Adding a method to one of those interfaces**: it must be JSON-serialisable with `RpcJson.Options` (no delegates /
  streams; `IProgress<>` only via the job list), take `eventFolderPath` (or be listed as competition-free in
  `RpcContractTests`), and — for `IEventStore` — be classified in `EventStoreMethods` (read, or write → `DataArea`s).
  `tests/Core/Network/RpcContractTests.cs` fails otherwise. Bump `NetworkProtocol.Version` on any signature change.
- **Change bus**: every write publishes `DataChange(eventPath, area, version, origin)` on `IDataChangeBus`; the host
  streams it to stations (SSE `/changes`); a station publishes nothing itself (only `Replay`s the host's stream). Pages
  call `ObserveData(bus, session, mode, areas…)` and override `PageViewModelBase.ReloadAsync` (coalesced 1/s, only while
  `IsActive`, deferred while `IsEditingCell`, own-origin events skipped). `SessionService` listens too and refreshes
  `CurrentDay` / `CurrentEvent` after a remote change.
- **Over the wire goes the identifier, never a host path**: the host's `RpcDispatcher` maps `eventFolderPath` /
  `EventSummary.FolderPath` ↔ `events/<identifier>` (validated with `EventIdentifier.IsValid`). On a station
  `EventSummary.FolderPath` **is** the identifier — treat it as an opaque key; never build a local path from it.
  Local per-machine files (read-out files, monitor HTML, `views.json`, logs, `machine.json`) come from `IEventFiles`
  / `TableLayoutStore` / `FileActivityLog`, which know the role.
- **No file or app-DB access from BusinessLogic past these interfaces**: `CompetitionEditorService` has no
  `Directory.`/`File.` (day folders and imported XML go through `IEventFiles`; results app-data through
  `IResultsAppData`).
- **Host** (`DataAccess/Network/Host`, Kestrel via `FrameworkReference Microsoft.AspNetCore.App`, minimal API, no
  URL ACL): `/pair` (4-digit PIN → bearer token stored in `app.db` `StationTokens`), `/rpc/{iface}/{method}` (JSON
  array of args; `Idempotency-Key` on writes; `/jobs` for the import), `/changes`, `/version`, `/files/*`, `/log`,
  `/ping`; UDP discovery on 47831 (`DataAccess/Network/Discovery`), spoken `ConnectionCode` fallback. **Station**
  (`DataAccess/Network/Station`): `RpcChannel` (timeouts/retries), `RpcProxy` (`DispatchProxy`), `RemoteChangeListener`,
  `NetworkStation : INetworkClient`. Self-checks with one-click fixes: `IStationDiagnostics`
  (`DataAccess/Network/Diagnostics`; nothing changes the OS silently, every fix is a button and is logged).
- **UI**: «Файл → Спільна робота…» is a global overlay (reachable before a competition is selected, because a station
  must connect first), the role indicator in the top bar, the `NetworkActivity` in the activities block and the
  «немає зв'язку» banner (`NetworkNoticeViewModel`) that disables editing while reconnecting. «Очки» / «Розряди» are
  read-only on a station.
- **Flaky networks** (a mobile router silently drops long-lived connections; field test 2026-11-07): the station's
  offline state comes only from `Station/ConnectionHealth` — offline = a failure **and** no answer of any kind for 15 s;
  any answer clears it. A silent `/changes` (3 s heartbeat, ~10 s silence) is reopened quietly and resumed from
  `Last-Event-ID` + stream epoch — never a banner by itself. `RpcChannel` retries within a window (`RpcRetryPolicy`:
  reads 20 s, writes 60 s with one idempotency key; short first attempt; idle pooled connections closed after 4 s),
  sends writes one at a time in call order, shows «Зберігається…» (`INetworkClient.DelayedWrites`) and a toast when a
  write is abandoned (`WriteFailed`). The host runs keyed calls with `CancellationToken.None`. Details:
  `docs/network-protocol.md`.
- **Last session**: `LastSessionRow.Mode/HostId` — a station pointer is restored only after reconnecting to the same
  host, never from the local folder. Builds: run `scripts/locked-build.ps1` when several terminals/agents build the
  same checkout.

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
- **A team may have no number** (`RelayTeam.Number` is `int?`): a UOF import makes its teams without numbers, the user
  types them later (in step with OCAD). Show/sort it through `Models/RelayTeamNumbers` (`Text`/`Bib`/`SortKey`/`Label`
  — blank number, blank «100.2», sorted last); such a team is skipped online (`relay_teams.num` is the key) and gets
  no «за номером етапу» course; plugins see `TeamCompetitor.Number` = 0.
- **A runner may be on a team without a leg** (`RelayTeamId` set, `Leg` null — «без етапу»): an import with more
  runners than legs leaves the extras so; evaluated alone, never placed; listed in `RelayTeamRow.Unplaced` and in red
  on «Команди»; `IEventStore.ReleaseRelayRunnerAsync` takes a runner off the team.
- **UOF relay** (`UofXmlParser`): «3Е» in `ProgEvent` = the runner's relay day (`UofParticipant.RelayDayNumbers`),
  `GrupRel` = the relay group there (`GroupOn(day)`), `Prim` = team name, `PsRel` = leg (`<RelayLeg>` is a region-team
  number, not read). The import matches a number-less team by its exact name in the group, sets a missing `LegCount`
  from the file, marks the relay group-days (`RelayGroupDay`) or makes a new all-relay day a relay day, never skips a
  runner, and reports teams created / without a leg / without a team (`ParticipantImportResult`, `RelayImportReport`).

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

## Chip capacity (SportIdent card model)

`Toolkit.SiCard.Identify(number)` maps a chip number onto its SportIdent card model and punch capacity by the
SPORTident number ranges (Card 5 = 30, Card 6 = 64 (192 station-dependent), Card 8 = 30, Card 9 = 50, pCard 20,
tCard 25, Card 10/11/SIAC = 128, fCard 20); an unknown number is `null` and never judged. `Models/ChipCapacity.
NeededPunches(groupDayRow, strategy)` = max(course control count, longest named course / scatter variant) and is
carried on `GroupOption.ControlCount`. Surfaces: «Чіпи» shows read-only «Тип картки» / «Відміток» columns; the
day grid and the roster amber-tint a chip cell whose card holds fewer punches than the group's course + `SiCard.Reserve` (2 spare)
(`ChipTooSmall` / `ChipCapacityTooltip` on both row VMs and `RosterDayCellViewModel`); «Сканування чіпів»
holds such a chip back (`ChipScanCandidate.NeededPunches`) with «Пропустити» / «Присвоїти все одно», like a
rental-note mismatch. **All of it is gated on the app-level «Тип відмітки» = SportIdent** (another system's numbers
say nothing about the card): `ParticipantsViewModel.UsesSportIdentCardsAsync` feeds `ControlCount` = 0 otherwise,
`ChipsViewModel.ShowCardColumns` / `ChipScanViewModel.ShowCardColumn` hide the columns; a Settings change applies on
the next page load.

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

## How to build

```bash
dotnet restore
dotnet build
```

## How to test

```bash
dotnet test tests/OrientPyx.Tests.sln
```

A separate solution (`tests/OrientPyx.Tests.sln`, xunit; guide in `tests/README.md`) so the app solution stays
test-free. `Core/` tests wire the three layers through the real DI extensions over a temp data root and check:
computed results against approved snapshots (`Golden/autotest.results.json` for the synthetic competition built
by `Support/SyntheticCompetition.cs`; `tests/fixtures/<id>.results.json` next to git-ignored real `.opyx`
fixtures), every document/export of every day, a sweep of day 1 through every discipline strategy, the plugin
catalog, an `.opyx` round trip, plus rule-level tests of every discipline (set course, score, rogaine, mixed,
scatter, relay with restarts and course modes, the two standard plugins), the read-out parsers and import, the
CSV/IOF/UOF imports, every document builder, the draw, entry fees, custom fields and the editing service — each on a
competition built by `Support/CompetitionBuilder.cs`. `Ui/` tests run the real `App` + `MainWindow` on Avalonia's headless
platform with Skia (`Support/HeadlessApp.cs`), open every page through the root view model's commands and save
screenshots to `tests/TestResults/<timestamp>/`. The snapshot format equals the Debug «Знімок результатів»
(`scripts/diff-results.ps1`). Run the suite before a release and after any change to results, protocols or
plugin loading; when a results change is intended, replace the golden with the written `.actual.json`.

## Documentation site (`website/`)

User documentation, **Ukrainian only**, for the people running competitions — not for developers: task-first pages,
on-screen names and menu paths («Старт → Жеребкування»), no class names. Writing rules: `website/README.md`. The one
developer section is «Власні плагіни» (`website/content/plugins/`, the plugin API guide; authors get the SDK by cloning
this repo and adding a project under `plugins/`). MkDocs Material, versions pinned in `website/requirements.txt` (keep
`mkdocs` 1.x). `.github/workflows/docs.yml` publishes to GitHub Pages (`https://vadymkolesnyk.github.io/OrientPyx/`)
on every `v*` tag (texts + screenshots of the release); a manual run republishes the branch's texts with the latest
release's screenshots.
- **Screenshots and demo competitions are generated, never committed** (`website/content/shots|demo/`, git-ignored):
  the `DocShots` tests (`tests/OrientPyx.Tests/DocShots/`, `[DocShotFact]`, skipped unless `ORIENTPYX_DOCSHOTS` is set)
  drive the headless app over the demo competitions of `DemoCompetitions.cs` (fictional people, seeded, one per format
  + a two-day cup) and save `shots/<name>.png` + `manifest.json`; `DocShooter` crops to a control, frames controls,
  draws open popups/flyouts in place. Never put a real path or machine name on a shot (mask it like the finish page's
  read-out file). `scripts/docs.ps1` renders them when missing (`-Shots` = again) then previews; `-Build` = the strict
  CI build.
- **Generated pages** (`website/hooks.py`): «Довідник екранів» — one page per `Help.<Screen>` (sections per
  `Help.<Screen>.<Block>`) straight from `uk-UA.json`; «Що нового» from the GitHub releases. The «?» dialog's
  «Детальніше в документації» and «Довідка → Документація» open them through `Presentation/Services/DocsLinks.cs` —
  its kebab rule must stay the hook's (`DocsLinksTests`).
- **Keeping it current**: `/new-version` Step 2b — `python website/tools/affected.py` lists the pages whose code changed
  since the last tag (`website/docs-map.yml`), UI names renamed while still quoted in the docs, new strings and changed
  help; update the pages, add shots, `scripts/docs.ps1 -Shots -Build`. A new page → `mkdocs.yml` nav + `docs-map.yml`.
- The in-app «?» texts (`Help.*`) are the short form of the same content: `LocalizationTests.EveryHelpButtonHasItsText`
  / `EveryHelpTextHasAButton` keep every «?» backed by uk + en text and every help text reachable (a page whose
  view-model name differs from its help prefix overrides `HelpKeyPrefix`). `PluginDocsTests` keeps every public SDK
  type in `website/content/plugins/api.md`.

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

Do not add: Clean Architecture / CQRS / MediatR, Docker, or authentication.
Keep the app lightweight and the four layers + plugin SDK structure above intact (no new projects in
`OrientPyx.sln`; plugins live under `plugins/` and reference only the SDK; tests live under `tests/`). (The Supabase online-results
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
competition metadata. The service-role key never leaves the local app DB. Relay groups publish to a separate
`relay_teams` table, never to `results`; a project without it just logs a warning — contract in
`docs/online-snapshot.md`.)
