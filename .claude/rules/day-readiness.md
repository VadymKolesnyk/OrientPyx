---
paths:
  - "**/*Readiness*"
  - "**/*Dashboard*"
  - "**/GroupCourseChecks*"
  - "**/Services/Relay/*"
---

## Day readiness check («Перевірка готовності дня»)

«Старт → Перевірка готовності дня» (first item of «Старт»; `DayReadinessViewModel` / `DayReadinessView`) lists every
problem of the session day's data, errors first, each row with «Перейти» (+ F8) to the page it is fixed on; the
dashboard's «Проблеми дня» tile counts it (`DashboardInfo.ReadinessErrors/Warnings`, red / amber / «Усе готово»). Plan
and decisions: `docs/day-readiness-check-plan.md`.

- **Logic**: `BusinessLogic/Services/Readiness/DayReadinessChecker` (pure, public, built by `CompetitionEditorService`
  like `_calculator`) over one `EventDaySnapshot` + the custom-field defs + the local «Тип відмітки»
  (`CompetitionEditorService.Readiness.cs`, `GetDayReadinessAsync(dayId)`; `GetDashboardAsync` reads one snapshot for
  its tiles, the results and the check — `WithoutGroup` = the check's `NoGroup` count). Model `Models/DayReadiness.cs`:
  `ReadinessIssue(Kind, Target, MessageKey, Args)` + ids; BL never localizes — the row formats `MessageKey` with
  `Args`, reusing the cells' tooltip keys where they exist. Level per kind in `ReadinessRules.SeverityOf` only.
- **The cells and the list share their checks** — keep it so: `GroupCourseChecks` (unknown codes / day codes / several
  courses — «Групи» cells), `Group.CheckAgeWindow` (birth-date cell), `ChipCapacity.NeededPunches` over
  `GroupDayRows.Create` (chip cell; `GetGroupsForDayAsync` builds its rows the same way — the asked day's discipline
  and named courses), `Services/Relay/LegCourseCheck` + `RelayPlacements.Build` (the red «Група» of a leg number
  «за номером етапу»), `Services/Relay/NumberVariantRows.Build` («Варіанти за номерами» page). A new red/amber cell
  rule → move it here and add its kind. Tests: `Core/DayReadinessTests` (parity with the participant rows and the
  number-variants page), `Ui/DayReadinessUiTests`.
- **Deliberately not checked** (user decisions 2026-11-24): disabled controls, finish read-outs, payment / check-in.
  «Без чіпа» is a warning. Start times: nobody on the day has one → one `DayWithoutStarts` (start station or no draw
  yet); else per group (`GroupWithoutStarts` / relay `RelayNoMassStart`) and per runner (`NoStartTime`); a format whose
  `ResolveStart` gives a start of its own is skipped; relay legs > 1 never. Only groups with members on the day are
  checked.
- **«Перейти»**: `GoToRequested(issue)` → `MainWindowViewModel.GoToReadinessIssueAsync` switches on `Target` (never on a
  discipline): `PageViewModelBase.RequestReveal(id, columnKey)` before the page loads, `NotifyPendingReveal()` after;
  the view takes it on attach or on `RevealRequested` through `SheetReveal.Apply` → `SheetTable.RevealRow(row, key)`
  (clears filters + search at once if they hide the row, selects, scrolls, focuses the cell; the key is a column key or
  a header localization key — `SheetColumn.HeaderKey`). Wired on Participants (forces the day grid, like a dashboard
  drill-in), Groups, RelayTeams, CompetitionDays; the column intents are in `ReadinessColumns`.
- **Live**: the page observes Days, ControlPoints, Groups, GroupDaySettings, CourseVariants, Participants,
  ParticipantDays, CustomFields, Relay (+ plugin on/off) and reloads on every open (own writes elsewhere are not on the
  bus for it). Read-only: no day lock, no undo.
