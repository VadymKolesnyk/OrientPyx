---
description: End-to-end checklist for adding a feature to OrientPyx — correct layer, DI, localization in both JSON files, EF migration, discipline strategy, clean build.
argument-hint: "[short description of the feature]  (optional)"
---

Add a feature to OrientPyx the right way. The feature is: **$ARGUMENTS**
(if empty, infer it from the conversation so far).

This is a checklist, not a script — skip steps that genuinely don't apply, but do not skip
a step just because it's tedious. The recurring bugs in this codebase are a forgotten
en-US key, a missing EF migration, and business logic that leaked into the wrong layer.
Read `CLAUDE.md` first if you haven't this session.

## 1 — Place code in the correct layer

Respect the dependency direction (`Presentation → BusinessLogic/DataAccess/Localization`,
`DataAccess → BusinessLogic`; BusinessLogic and Localization depend on nothing).

- **New page / view / widget** → `OrientPyx.Presentation` (`Views/`, `ViewModels/`;
  navigable pages derive from `PageViewModelBase`, register in
  `PresentationServiceCollectionExtensions` and `NavigationService`).
- **New domain concept** (entity / model / enum) → `OrientPyx.BusinessLogic`.
- **New business operation** → interface in `BusinessLogic/Interfaces/` + service in
  `BusinessLogic/Services/`, registered in `BusinessLogicServiceCollectionExtensions`.
- **Persistence / file / printer / DOCX / SQLite** → `OrientPyx.DataAccess` **only**,
  reached from BusinessLogic through an interface defined in BusinessLogic.

BusinessLogic must never reference Avalonia, EF Core, SQLite, files, printers, or DOCX.

## 2 — Discipline-varying behaviour → strategy, not switch

If the feature behaves differently per `DisciplineType`, put the varying logic behind
`IDisciplineStrategy` (`BusinessLogic/Disciplines/`) and resolve it via
`IDisciplineStrategyProvider`. Add/extend a strategy class + its DI registration. **Do not
write `switch (type)` in shared code.**

## 3 — Localization (every user-facing string)

- No hardcoded UI text in Views or ViewModels.
- Add each new key to **BOTH** `src/OrientPyx.Localization/Resources/uk-UA.json` **and**
  `en-US.json`. Ukrainian is the real text; English is the translation. A key present in
  only one file is a bug (the Stop hook `build/check-localization.py` will block on it).
- Group keys sensibly (`Nav.*`, `Page.*`, `Discipline.Type.*`, …).
- Resolve dynamic keys via a VM property calling `ILocalizationService.Get(key)` that
  re-raises on language change (`PageViewModelBase`); bind literal keys in XAML via
  `{Binding Localization[Some.Key]}`. Keep runtime language switching working.

## 4 — Schema change → EF migration

If you added or changed an entity/DbSet, add a migration (never `EnsureCreated`):

```bash
dotnet ef migrations add <Name> --context EventDbContext \
  --project src/OrientPyx.DataAccess --startup-project src/OrientPyx.DataAccess \
  --output-dir Persistence/Migrations/Event
```

(`--context AppDbContext` + `--output-dir Persistence/Migrations/App` for the app DB.)
Migrations apply at runtime via `Database.Migrate()`. Commit the generated migration +
the updated model snapshot.

## 5 — Editable table? Use SheetTable

Spreadsheet-style screens use the shared `controls:SheetTable` (NOT an Avalonia DataGrid),
columns built in code-behind via `SheetColumnBuilder` → `Sheet.Bands`. Rebuild bands on
language change and when the visible column set changes. The roster uses the two-tier
`RosterColumnBuilder`/`Days`+`Blocks` instead. See `CLAUDE.md` → "Editable tables".

## 6 — Build clean

Run `/build` (or `dotnet build OrientPyx.sln`). It must succeed with **0 warnings and
0 errors**. Fix everything before finishing.

## 7 — Verify + wrap up

- Sanity-check the feature actually runs (`/run`) if it has a runtime surface.
- Confirm localization is in sync (`python build/check-localization.py`).
- Update `CLAUDE.md`'s subsystem notes if the feature is a notable new subsystem.
- Commit only if the user asked; keep the four-layer structure intact.
