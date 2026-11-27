---
paths:
  - "src/**/Mcp/**"
  - "src/**/*Mcp*"
  - "tests/**/Mcp/**"
  - "docs/mcp-*"
---

# Assistant (local MCP server)

Plan and decisions: `docs/mcp-server-plan.md`; implementation and deviations: `docs/mcp-server-implementation.md`;
user page: `website/content/start/assistant.md`.

- **Layers.** The tools are plain classes in `BusinessLogic/Services/Mcp/Tools/` (`IMcpToolHandler`, one per tool group)
  over `McpContext` (the services, resolved lazily — the tool layer is a composition root). Their metadata (names,
  English descriptions with Ukrainian terms, JSON schemas, `McpToolClass`) is ONE static list, `McpToolCatalog` — the
  bridge lists it while no window serves. `McpToolDispatcher` runs a call (policy, errors → text, log). The SDK
  (`ModelContextProtocol.AspNetCore` 2.4) lives only in `DataAccess/Mcp/`: `McpHttpServer` (Kestrel on 127.0.0.1, its own
  `WebApplication.CreateEmptyBuilder`, shared `ToolCollection` set in `ConfigureSessionOptions` so a mode change sends
  `tools/list_changed`), `McpBridge` (`--mcp-bridge`, stdio ↔ HTTP, first lines of `Program.Main`, before Velopack, stdout
  only JSON-RPC), `AnalyticsSandbox` (in-memory SQLite: authorizer + `sqlite3_prepare_v2` tail / `stmt_readonly` + limits +
  interrupt, `median` / `percentile`), `McpEndpointFile` (`<data root>/mcp-endpoint.json`), `McpFileAccess`,
  `McpClientSetup` (.mcpb / Claude Desktop config). The window's side is `IMcpUi` (`Presentation/Services/Mcp/McpUi`:
  confirmation bar `McpConfirmBarViewModel`, journal, «MCP» activity, labels from the pages' `CreateLabels` factories).
- **A new tool**: an entry in `McpToolCatalog.All` + its case in a handler (`PolicyTests` fails otherwise). Reads use
  `ctx.ReadCompetition` + `McpContext.Pin(competition, day)`; writes build a `McpWrite` (class after `McpToolPolicy.BySize`,
  a `ChangeSummary` with a `Mcp.Change.*` key in both JSON files) and run through `ctx.Writer.RunAsync` — never write
  outside it. Not undoable / bulk → a preview through `ctx.Plans` (`McpPlanDraft` with a fingerprint) applied by
  `apply_plan`. Only the editor's methods — never `IEventStore` — and no file I/O in BusinessLogic (`IMcpFileAccess`).
- **The writer** (`McpWriter`): policy → confirmation (`McpConfirmations`: waits ≤ `Wait` = 40 s, then `pending` +
  `await_confirmation`; the change runs when allowed) → one at a time → `EditorScope.Enter(competition, day)` (the
  editor reads it before the session) → `UndoScope.Enter(step)` when undoable → `DataChangeOrigin = "<SelfOrigin>·mcp"`
  (pages skip only their own machine's origin; a station tags the RPC `X-OrientPyx-Origin-Tag: mcp`, the host stamps
  `«<station>·mcp»`) → the journal.
- **Journal, undo, redo** (`McpWriter`): every write but undo/redo is an entry (`McpChange`: the answer's step id,
  `ActiveStep` to revert for undo, `RedoStep` = the revert's own step while undone — redo is the revert of the revert,
  and a redo moves `ActiveStep` to its new step so the original step id still undoes it). `list_changes` reads it; the
  undo / redo summary carries the change's own `ChangeSummary` as `Inner` (`ChangeSummaryText` shows it as {0}).
- **Other handlers**: `ReadoutTools` (`edit_readout` — the finish modal's `UpdateFinishReadoutAsync` only when the
  read-out itself changes, else the chip / group / bonus writers, so a holder's status set elsewhere survives),
  `DuplicateTools` (`find_duplicates` in the tool; `merge_participants` → `ICompetitionEditorService.MergeParticipantsAsync`:
  delete the merged one, re-add its day links on the kept one, fill blanks — tracked, one step), `FileTools`
  (`inspect_file`), `GroupRuleTools` (`assign_groups`, an undoable plan). `get_background_status` reads the window's
  activities through `IMcpUi.ActivitiesAsync`. `export kind=plugin_report` builds `BuildPluginReportAsync` and names the
  file by the report's name key (`IOutputLabels.FileName("key:<NameKey>")`).
- **Analytics tables** (`Analytics/AnalyticsSchema` + `AnalyticsDatasetBuilder`): built from
  `ICompetitionEditorService.GetAnalyticsDayDataAsync` (one snapshot → the protocols' results and the split export's
  passages) and the roster; columns snake_case, times seconds (`*_s`), `*_text` = `ShownTime`. A new column → the schema
  (with its meaning) and the builder; `McpAnalyticsTests` compares with the protocol.
- **Modes** (`McpMode`, stored as an int in `AppSettingsRow.McpMode`): 0 «Лише читання» (write tools not listed),
  1 «Зміни з підтвердженням» (every write waits), 2 «Лише небезпечні зміни з підтвердженням» (`Write` direct, `Risky` waits),
  3 «Без підтвердження» (nothing waits — the settings page asks once before switching to it; NeverViaMcp still refused).
  `get_context` returns `mode` + `confirmation`; tool descriptions say "confirmed … (except in mode no_confirmation)".
  Taking someone off their only day deletes them (`RemoveParticipantFromDayAsync`): the tools count it as a deletion.
- **Never via MCP** (`McpToolPolicy.NeverViaMcp`): deleting competitions / days, unlocking a day, printing, online
  publishing, network roles, program settings, «Очки»/«Розряди», plugins.
- Tests: `tests/OrientPyx.Tests/Core/Mcp/` (`TestMcpUi` answers confirmations); the core test environment registers
  `AddOrientPyxMcp()`.
