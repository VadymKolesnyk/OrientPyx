Explain and enforce the OrientPyx architecture when adding new code.

## Layers

```
Presentation  -> BusinessLogic, DataAccess, Localization   (Avalonia UI)
DataAccess    -> BusinessLogic                              (EF Core + SQLite)
Localization  -> (none)                                     (shared, pure .NET)
BusinessLogic -> (none)                                     (domain + abstractions)
```

## Where new code goes

- **New page / screen / widget** → `OrientPyx.Presentation` (View under `Views/`,
  ViewModel under `ViewModels/`; navigable pages go in `ViewModels/Pages/` and derive
  from `PageViewModelBase`, then register in
  `DependencyInjection/PresentationServiceCollectionExtensions.cs` and in
  `Services/NavigationService.cs`).
- **New domain concept** (entity, model, enum) → `OrientPyx.BusinessLogic`
  (`Entities/`, `Models/`, `Enums/`).
- **New business operation** → an interface in `BusinessLogic/Interfaces/` + a service
  in `BusinessLogic/Services/`, registered in `BusinessLogicServiceCollectionExtensions`.
- **New persistence / file / infrastructure** → `OrientPyx.DataAccess`
  (`Persistence/`, `Repositories/`, `FileSystem/`). EF Core / SQLite stay here only.

## Hard rules

- **UI text must be Ukrainian by default.**
- **Do not hardcode user-facing UI strings** in Views or ViewModels.
- **Put new localization keys into the localization resources**
  (`src/OrientPyx.Localization/Resources/uk-UA.json` and `en-US.json`), then resolve
  through `ILocalizationService`.
- **Keep future multilingual support possible** — every user-facing string goes through
  a key; never inline a translated literal.
- BusinessLogic must not reference Avalonia, EF Core, SQLite, files, printers, or DOCX.
- Respect the dependency direction above; never reverse it.
