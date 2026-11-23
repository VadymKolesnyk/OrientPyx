---
paths:
  - "src/OrientPyx.Plugins.Sdk/**"
  - "plugins/**"
  - "**/Plugins/**"
  - "**/*Plugin*"
---

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
- **Imported fields** (API 2, Р-59…Р-62): `AddParticipantField(key, label, kind, FieldImport)` names the field in
  files (`FieldImport.From("Pol", "sex")`) and translates values (`.Value("0", "Ж")`). The XML readers keep every
  simple value of a record in `UofParticipant.Raw` (`[JsonIgnore]`); `ICompetitionEditorService.GetImportFileFieldsAsync`
  lists what a file can fill (a checkbox each in the UOF/IOF import window → `UofParticipantData.FileFieldKeys`),
  `GetImportFieldsAsync` the CSV mapping targets (switched-on plugins' fields not created yet included, matched by
  label/key/import names). `PrepareImportFieldsAsync` (preview + import) moves the chosen raw values into `Fields` and
  applies the translations; the import creates a missing plugin field that got a value. User fields match by name too.
- **Reports** (API 2, Р-63…Р-66): `AddReport<T>()` — `Report.Build(ReportContext)` → `ReportDocument` (columns,
  sections, `ReportRow` normal/caption/sub). `PluginReports.Enabled/Find` lists them (guarded); a plugin with reports
  is a submenu in «Плагіни» (settings + reports). `CompetitionEditorService.BuildPluginReportAsync` builds the context
  from `GetResultProtocolDataAsync` per day (+ `ParticipantViews`), calls the plugin (exception → `Error`), and
  `PluginReportDocumentBuilder` maps it onto a `ResultProtocolDocument` (protocol header, officials, footer) — shown by
  `PluginReportViewModel` (shared `ProtocolPreviewTable`, no drag: `IProtocolPreviewHost.CanReorderColumns`) and
  written by `IResultProtocolWriter`. Report settings: `PluginSettingsRow` under the report id. Reports only read.
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
  kept on save. A plugin column shows only in groups of its discipline (`ResultProtocolGroup.PluginColumnKeys` /
  `Shows`): the protocol section and the monitor group carry `HiddenColumns` — the .docx and the HTML leave them out
  (the .docx sizes each column set on its own), the code-built previews give their slot to the column before
  (`PreviewColumnSlots`). Online, extras are `OnlineDisplayConfig` entries (`Resolve(extras)`: a new plugin column shown on the
  large screen, a field hidden; `Unresolved` keeps another day's) published in `display_config.columns` with a
  `label`; values go in `results.extra` (jsonb — a project without the column → `IResultPublisher.ExtraUnsupported`,
  rows sent without it) and `relay_teams.legs[].extra`; the spectator site lays relays out by the same config
  (`docs/online-snapshot.md`). Everywhere «Час» is the clean time and «Результат» the counted one (a relay leg's
  `AdjustedTime ?? LegTime`); a relay leg's running time is its own column `ResultColumn.Cumulative`
  («Наростаючий», monitor/online — never the gap column).
- **Unknown discipline** (plugin missing/disabled): `UnknownDisciplineStrategy` + the «Потрібен плагін» banner
  above the page (`PluginNoticeViewModel`). `.opyx` export writes `plugins.json` (required plugins); import warns
  about missing ones but proceeds.
- **Release**: `build/publish.ps1` builds `plugins/OrientPyx.Plugins.sln` and copies `artifacts/plugins/*` into the
  package's `plugins\` folder. Build plugins locally with `dotnet build plugins/OrientPyx.Plugins.sln`.
