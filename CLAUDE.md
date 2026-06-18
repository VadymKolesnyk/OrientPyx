# OrientDesk

Cross-platform desktop application for managing orienteering competitions.
Currently this is a **starter architecture only**: a working UI shell, localization
structure, placeholder services, and data-access infrastructure. Real competition
features are intentionally not implemented yet.

## Solution structure

```
OrientDesk.sln
src/
  OrientDesk.Presentation/    Avalonia UI (Views, ViewModels, navigation, app startup, DI composition)
  OrientDesk.BusinessLogic/   Entities, models, enums, service interfaces + placeholder implementations
  OrientDesk.DataAccess/      EF Core + SQLite: app & event DbContexts, stores, events-folder scanner
  OrientDesk.Localization/    ILocalizationService + JSON resource dictionaries (uk-UA default, en-US)
```

## Architecture rules

Three layers plus a shared localization library.

- **Presentation** — Avalonia views, ViewModels, navigation, startup, DI wiring.
  No business rules, no file/database logic.
- **BusinessLogic** — domain entities, models, and service abstractions with simple
  placeholder implementations. Must NOT reference Avalonia, EF Core, SQLite, files,
  printers, or DOCX/report libraries.
- **DataAccess** — persistence and infrastructure. EF Core and SQLite live here and
  nowhere else. References BusinessLogic.
- **Localization** — pure .NET, no UI dependency, usable from any layer.

### Dependency direction

```
Presentation  -> BusinessLogic, DataAccess, Localization
DataAccess    -> BusinessLogic
Localization  -> (none)
BusinessLogic -> (none)
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
  the Settings page (`IAppSettingsService`).
- The competition list is built by **scanning** `./events` (`IEventFolderScanner`).

**Session rule (important):** the active competition/day is held **in-memory** by
`ISessionService` for the running instance. The app DB only stores the last selection for
startup restore — read once at launch, written on selection change. Never resolve "current
event/day" by querying the app DB during normal operation; the program may run as multiple
instances over the same `./data`, and runtime state must not be shared through it.

## Localization rules

- **UI text is Ukrainian (uk-UA) by default.**
- **Do not hardcode user-facing strings** in Views or ViewModels.
- New user-facing text → add a key to **both** `src/OrientDesk.Localization/Resources/uk-UA.json`
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

A table can **persist its view** (column order, width, hidden set) per competition: set `LayoutKey`
(a stable id, e.g. `"participants.day"`) and `LayoutStore` (`ITableLayoutStore`, a singleton) on the
`SheetTable`. It saves to `events/<id>/views.json` (one JSON object keyed by table id) on every
hide/reorder/resize and reloads on build. Persistence is opt-in — only set where wanted (currently the
two Participants tables); tables without it stay in-memory only. No-op when no competition is selected.

## How to build

```bash
dotnet restore
dotnet build
```

## How to run

```bash
dotnet run --project src/OrientDesk.Presentation
```

The window opens with a Ukrainian sidebar and Ukrainian placeholder pages; the default
page is Панель (Dashboard). A SQLite file is created under the user's local app-data on
first run.

## Schema management

EF Core **migrations** are the canonical way schema evolves, for both databases:
`AppDbContext` (`./data/app.db`) and `EventDbContext` (per-competition `event.db`). Apply
them at runtime with `Database.Migrate()` — the app DB on startup
(`InitializeOrientDeskDatabase`) and each event DB when it is opened
(`EventStore.EnsureCreatedAsync`, which now calls `MigrateAsync`). Add a new migration when
you change an entity, e.g.:

```bash
dotnet ef migrations add <Name> --context EventDbContext \
  --project src/OrientDesk.DataAccess --startup-project src/OrientDesk.DataAccess \
  --output-dir Persistence/Migrations/Event
```

(Use `--context AppDbContext` + `--output-dir Persistence/Migrations/App` for the app DB.)
Each context has an `IDesignTimeDbContextFactory` so the EF tools can run without the app.
Do not reintroduce `EnsureCreated` — it is incompatible with migrations.

## What NOT to do yet

Do not add: test projects, Clean Architecture / CQRS / MediatR, Docker, authentication, or
cloud sync. Keep this a lightweight starter.

(The result protocol — «Протоколи результатів» — now exports a per-group results protocol to a
Word .docx via `IResultProtocolBuilder` (BusinessLogic) + `DocxResultProtocolWriter` (DataAccess,
Open XML SDK). Settings — orientation, ordered/visible columns, header text — are app-level in
`app.db` (`AppSettingsRow.ResultProtocolJson`). The «Протоколи» top menu hosts it.)
