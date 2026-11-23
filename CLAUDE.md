# OrientPyx

Cross-platform desktop application (Avalonia, .NET) for managing orienteering competitions.
It is a working product, not a starter: participant management, groups/courses/chips, chip
read-out, per-discipline result computation, and Word/HTML/thermal-printer protocol export are
all implemented. New work continues on this base — respect the layering and conventions below.

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
  and the toolkit (`Toolkit/`) plus the shared simple types. **No dependencies,
  no knowledge of EF / files / UI — only the contract and pure algorithms.** Everything in it is public API with
  compatibility obligations: add only what is stable. Its `AssemblyVersion` is set explicitly (`1.0.0.0`) — the
  binary-compat version, bumped only on a breaking change; never tie it to the app's `Version`. `ApiVersion.Current`
  counts additive feature levels (2 = imported fields + reports, 3 = `ReportGroup.Settings`, 4 = `ResultColumnDef.Online`
— a plugin column kept out of the online results, e.g. weighted relay's «T·K» = the leg's «Результат»; 5 = `FinishContext.MaxLate`
— the group's «Макс. запізнення», past which rogaine / score-by-time give OVT); a plugin using them sets
  `MinApiVersion` to that level. New `IPluginBuilder` members get a default implementation. The three app projects import its namespaces globally
  (`<Using>` in their `.csproj`).

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

**Plugins** (internals: `.claude/rules/plugins.md`; author guide: `website/content/plugins/`): a plugin is a DLL with one
`IOrientPyxPlugin` that registers `Discipline` subclasses (each `BasedOn` a built-in), participant fields, strings
and reports; it stays off until switched on («Плагіни → Керування плагінами…»); every call into plugin code is guarded
(on an exception the built-in result is used), and the id of a missing/disabled plugin resolves to
`UnknownDisciplineStrategy`.

## Platforms & packaging

Windows and macOS are shipped; Linux runs from source only. Keep the code portable: anything Windows-only (GDI
printing in `DataAccess/Printing`, `FileAssociation`, Explorer) must be guarded by `OperatingSystem.IsWindows()`
and degrade gracefully (the print services expose `IsSupported`); reveal/open files through
`Presentation/Services/FileReveal.cs`, never by launching `explorer.exe` directly. Signed files must say product
`OrientPyx` and product version = the release — keep `src/Directory.Build.props` / `plugins/Directory.Build.props`
on every project and list a new own binary in `build/signpath/SigningRules.ps1` + `artifact-configuration.xml`.
Release workflows, Velopack channels, SignPath and the macOS bundle: `.claude/rules/packaging.md` (and `/new-version`).

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
  `IsActive`, deferred while `IsEditingCell`, own-origin events skipped; `DataArea.Any` for one competition — an undo —
  concerns only that one, and a hidden page's pending reload for competitions no longer open is dropped when shown).
  `SessionService` listens too and refreshes `CurrentDay` / `CurrentEvent` after a remote change.
- **Over the wire goes the identifier, never a host path**: the host's `RpcDispatcher` maps `eventFolderPath` /
  `EventSummary.FolderPath` ↔ `events/<identifier>` (validated with `EventIdentifier.IsValid`). On a station
  `EventSummary.FolderPath` **is** the identifier — treat it as an opaque key; never build a local path from it.
  Local per-machine files (read-out files, monitor HTML, `views.json`, logs, `machine.json`) come from `IEventFiles`
  / `TableLayoutStore` / `FileActivityLog`, which know the role.
- **No file or app-DB access from BusinessLogic past these interfaces**: `CompetitionEditorService` has no
  `Directory.`/`File.` (day folders and imported XML go through `IEventFiles`; results app-data through
  `IResultsAppData`).
- Host / station internals, the network UI, flaky-network handling and last-session restore:
  `.claude/rules/network-mode.md`.

## Undo / redo (Ctrl+Z / Ctrl+Y)

Data-level undo journal (plan + rules: `docs/undo-redo-plan.md`; journal, revert and UI internals:
`.claude/rules/undo.md`). When you change the schema or a store write:

- **Schema changes**: a new Guid relation column → `UndoReferenceRules.All` (or `NotReferences`), a test keeps the list
  complete; a new service-level uniqueness → `UndoUniqueRules`; a new derived column → `ExcludedColumns`; a new bulk
  write method gives `NotUndoable` by itself. Keep store writes tracked (load → change → `SaveChanges`) on everything a
  table edit can reach; `MergeGroupAsync`, `DeleteParticipantAsync`, the relay team deletes, `ClearParticipants*` and
  the custom-field / discount deletes were converted for this.

## Editable tables (SheetTable)

Every spreadsheet-style screen uses the one shared `controls:SheetTable` (`Controls/Sheet/`) — **not** an Avalonia
DataGrid. Columns are built in the View's code-behind with `SheetColumnBuilder` (`Sheet.Bands = builder.Bands`), the
shell hosts pages **without an outer ScrollViewer** (it would make the table render every row), and every table sets
`LayoutKey` + `LayoutStore`. **Cell controls must not be held by anything that outlives the row** (≈1 MB leaked per
row otherwise). Full conventions — roster banded header, selection and row actions, view persistence, the leak rule:
`.claude/rules/sheet-table.md`.

## Build, test, run

- Build: `dotnet build`. Plugins are a separate solution: `dotnet build plugins/OrientPyx.Plugins.sln`. Run
  `scripts/locked-build.ps1` when several terminals/agents build the same checkout.
- Run: `dotnet run --project src/OrientPyx.Presentation`.
- Test: `dotnet test tests/OrientPyx.Tests.sln` — a separate solution (xunit; guide in `tests/README.md`) so the app
  solution stays test-free. The snapshot format equals the Debug «Знімок результатів»
  (`scripts/diff-results.ps1`). Run the suite before a release and after any change to results, protocols or
  plugin loading; when a results change is intended, replace the golden with the written `.actual.json`.

## Documentation site (`website/`)

User documentation, **Ukrainian only**, for the people running competitions (MkDocs Material, published to GitHub
Pages on every `v*` tag). **Screenshots and demo competitions are generated, never committed**
(`website/content/shots|demo/`); never put a real path or machine name on a shot. Every «?» help button needs its
`Help.*` text in both uk-UA and en-US (`LocalizationTests`), and every public SDK type must be listed in
`website/content/plugins/api.md` (`PluginDocsTests`). Writing rules, generated pages, DocShots, keeping it current:
`.claude/rules/docs-site.md`.

## Schema management

EF Core **migrations** are the canonical way schema evolves, for both databases:
`AppDbContext` (`./data/app.db`) and `EventDbContext` (per-competition `event.db`). They are
applied at runtime through `DataAccess/Persistence/DatabaseMigrator` — the app DB on startup
(`InitializeOrientPyxDatabase`) and each event DB when it is opened (`EventStore.EnsureCreatedAsync`,
the competition list) — never `Database.Migrate()` directly: the migrator only reads an up-to-date
database and clears a stale `__EFMigrationsLock` row (`.claude/rules/startup.md`). Add a new migration when
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
publisher (`.claude/rules/outputs.md`) is the one exception to "no cloud sync" — it is publish-only and stays confined to
DataAccess.)

## Subsystem notes (`.claude/rules/`)

These load automatically when you work with matching files; read the one that fits before planning a change there:

- `plugins.md` — plugin loading, adapter, strings, settings, imported fields, reports, table settings, plugin result
  columns, extra result columns, unknown discipline, plugin release
- `course-formats-relay.md` — Mixed / Scatter, relay course modes, OCAD variations by number, relay teams, UOF relay
- `results-calculation.md` — `DayResultsCalculator` stages, the finish-log day override (`forDayId`)
- `network-mode.md` — host & station internals, network UI, flaky networks, last-session restore
- `custom-fields.md` — additional participant fields, CSV/Excel import mapping
- `start-pages.md` — «Масовий старт», «Кількість карт»
- `chips.md` — SportIdent card capacity
- `finish-alerts.md` — finish sounds, Windows toasts, taskbar flash
- `sheet-table.md` — SheetTable conventions, roster header, selection/row actions, view persistence, cell-leak rule
- `undo.md` — undo journal, revert, UI histories
- `docs-site.md` — the `website/` docs, DocShots, generated pages
- `outputs.md` — results monitor, Word result/start protocols, online results publishing
- `packaging.md` — release workflows, Velopack channels, SignPath signing, macOS bundle
- `competition-folders.md` — renaming and deleting a competition folder
- `startup.md` — loading window, `StartupTimeline` marks, ReadyToRun, `DatabaseMigrator` (stale migration lock), the
  competition list's cost, starting with an archive

A new subsystem gets its own `paths`-scoped file there plus one line in this list — not a new section in this file.
