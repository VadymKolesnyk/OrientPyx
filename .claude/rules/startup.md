---
paths:
  - "**/Program.cs"
  - "**/App.axaml.cs"
  - "**/StartupWindow*"
  - "**/StartupTimeline*"
  - "**/DatabaseMigrator*"
  - "**/EventFolderScanner*"
  - "**/EventArchive*"
  - "**/PluginStartup*"
  - "**/MainWindowViewModel*"
  - "**/HeadlessApp*"
  - "src/OrientPyx.Presentation/OrientPyx.Presentation.csproj"
---

# Start-up: loading window, migrations, the competition list

Measured and reworked 2026-11-23 (a double click on an `.opyx` took up to 30 s on the race laptop).

- **Loading window first.** `App.OnFrameworkInitializationCompleted` builds DI, starts `PrepareData` (app.db
  migrations + seeds, plugin assemblies — `PluginStartup.Load`) on the thread pool, and shows `StartupWindow` (code-built,
  localized `Startup.*` status lines). `StartAsync` then registers the plugin strings (UI thread — the localization
  service raises its change there), builds `MainWindowViewModel` + `MainWindow`, shows it and closes the loading window.
  Nothing slow and UI-free belongs on the UI thread before the main window: add it to `PrepareData`.
  `App.MainWindowReady` completes with the shown main window — the headless tests (`HeadlessApp`) wait on it, never on
  `lifetime.MainWindow` (that is the loading window at first).
- **`StartupTimeline`** writes «Startup +N ms: …» lines (ms since the process started) to the activity log — read them
  from `events/logs` (or the competition's `logs`) of a slow machine before guessing. Also logged: «Competition list: N
  competitions read in X ms», «Archive import: preview / unpacked and migrated / opened in …».
- **ReadyToRun**: `OrientPyx.Presentation.csproj` sets `PublishReadyToRun` whenever a runtime identifier is given (every
  release publish). Avalonia, EF Core and our assemblies are IL only; JIT-compiling them was most of the start
  (main window 3.8–4.7 s → ~1.8–2.0 s on the dev machine). It also rewrites third-party IL DLLs (their own signatures
  are gone; ours are signed afterwards as before).
- **Migrations go through `DatabaseMigrator`** (app.db and every event.db — never call `Database.Migrate()` directly):
  an up-to-date database is only read (history table vs the cached migration list, plain SQL). EF's `Migrate` writes a
  lock row in `__EFMigrationsLock`; a process killed while holding it left that database waiting forever on every later
  start. A row older than `StaleAfter` (60 s) is removed; a fresh one is waited for. **A missing database is left to EF
  untouched** (`IRelationalDatabaseCreator.ExistsAsync` first): EF sets WAL journal mode only when it creates the file,
  and opening a connection first creates an empty one — every new competition then stayed in rollback-journal mode,
  where any reader makes a concurrent commit fail with «database is locked» (`DatabaseMigratorTests.NewDatabase_IsCreatedInWalMode`).
- **The competition list** (`EventFolderScanner`) opens each event.db once (migrate check + summary on one context), four
  at a time. It is read on every start that restores a competition (`FindByIdentifierAsync`) and each time the picker
  is shown — keep it cheap; it scales with every competition kept in `events/`.
- **Started with an archive** (`Program.StartupArchivePath`): the last competition is not restored first
  (`InitializeAsync(restoreLastSession: false)`); the import opens the imported competition straight from its summary
  (`EventSelectionViewModel.OpenAsync`, no rescan); a cancelled/failed import then restores as a normal start would
  (`RestoreLastSessionAsync`). The staging folder rename retries (`EventArchiveService.MoveFolder`): Defender holds
  freshly extracted files for a moment and the rename failed with «access denied» in about a third of imports.
- macOS delivers an archive as an activation; `App` subscribes before the main window exists and the import waits
  for it (`WhenStartedAsync`).
