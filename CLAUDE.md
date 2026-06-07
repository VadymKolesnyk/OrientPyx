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

## What NOT to do yet

Do not add: test projects, Clean Architecture / CQRS / MediatR, EF migration pipelines,
Docker, authentication, cloud sync, real SportIdent parsing, result calculation, report
(DOCX) generation, or printing. Keep this a lightweight starter.
