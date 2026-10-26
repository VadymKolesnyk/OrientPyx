# OrientPyx — implementation plan: плагіни, додаткові поля, естафета

> Спека: [`plugins-plan.md`](plugins-plan.md) (що і навіщо). Цей документ — **як і в якому порядку**: кроки розміром
> з один коміт/PR, конкретні файли, перевірка. Статус: **фаза 0–1 виконана** (2026-10-26). Оновлено: 2026-10-26 (звірено з кодом).

## 0. Правила виконання

- **Кожен крок збирається і запускається** (`dotnet build` без нових попереджень). Між кроками програма робоча —
  можна зупинитися й випустити реліз після будь-якої фази.
- **Фаза 1 і крок 2.1 не змінюють поведінки.** Перевірка — порівняння зі знімком результатів (крок 0.1), а не «на око».
- Один крок = один коміт (за проханням — PR). Повідомлення коміту українською, як у історії.
- Нові UI-рядки — одразу в `uk-UA.json` **і** `en-US.json` (Stop-хук `check-localization.py` це ловить).
- Зміна сутності → міграція EF у тому ж кроці (команди — CLAUDE.md «Schema management»).
- Розмір кроку: **S** ≤ пів дня, **M** ≈ день, **L** — кілька днів.

### Граф залежностей фаз

```
0 знімок ─► 1 ядро ─► 2 SDK+завантажувач+«Штраф» ──────────────┐
                 └──► 3 додаткові поля/імпорт ──┐                ├─► 6 «Зважена естафета» ─► 7 онлайн/монітор/підсумок
                 └──► 4 естафета ─► 5 дистанції етапів ──────────┘
                                                                       8 docs / .cs-джерело — будь-коли після 2
```

Фази 2, 3 і 4 після фази 1 здебільшого незалежні; рекомендований порядок 2 → 3 → 4 (плагін штрафів —
найдешевша перевірка API, поля потрібні естафетному імпорту). Точкові залежності між ними:

- 3.1 (створення полів плагіна) ← 2.3 (`AddParticipantField`, `FieldKind`) — якщо фазу 3 робити раніше за 2,
  поля плагінів відкласти до 2.3;
- 4.6 (імпорт естафети) ← 3.4 (`ImportTarget`);
- 5.3 (TeamSet) ← 1.4 (`Missed`) і 2.2 (`CourseMatcher`).

---

## Фаза 0 — страховка

### 0.1. Знімок результатів для порівняння (S)

Тестових проєктів немає, тому регресії ловимо diff-ом.

- Presentation, лише `#if DEBUG`: пункт меню «Файл → (Debug) Знімок результатів…» → для кожного дня поточного
  змагання викликає наявний `GetResultProtocolDataAsync(dayId)` і пише відсортований JSON
  (група → учасник → статус, час, місце, бали, очки, розряд, П/К) у
  `events/<id>/_debug/results-<yyyyMMdd-HHmmss>.json`.
- Скрипт `scripts/diff-results.ps1 <a.json> <b.json>` — `git diff --no-index` двох файлів.
- Зняти знімки на копіях **3 реальних змагань**: рогейн (команди, бонус, П/К), розсіювання, заданий/змішаний.
  Зберегти поза репозиторієм (особисті дані).

**Готово, коли:** два знімки одного змагання підряд дають порожній diff.

---

## Фаза 1 — підготовка ядра (поведінка не змінюється)

### 1.1. `DisciplineId` замість enum `DisciplineType` (M)

| Файл | Зміна |
|---|---|
| `BusinessLogic/Enums/DisciplineType.cs` | видалити; новий `BusinessLogic/Disciplines/DisciplineId.cs`: `readonly record struct DisciplineId(string Value)` + `static readonly` `SetCourse`, `ScoreByCount`, `ScoreByTime`, `Rogaine`, `Mixed`, `Scatter` (значення = нинішні імена enum) |
| `Entities/EventDay.cs`, `Entities/GroupDaySettings.cs` | тип властивостей → `DisciplineId` / `DisciplineId?` |
| `DataAccess/Persistence/EventDbContext.cs` | `HasConversion<string>()` → `HasConversion(id => id.Value, s => new DisciplineId(s))` (і для nullable) |
| `Disciplines/IDisciplineStrategy.cs`, `DisciplineStrategyBase.cs`, 6 стратегій | `Type` → `Id` |
| `Disciplines/DisciplineStrategyProvider.cs` | словник за `DisciplineId`; невідомий id → `UnknownDisciplineStrategy` (новий файл) замість винятку |
| `Disciplines/UnknownDisciplineStrategy.cs` | новий (Р-2): перевірка як `SetCourse`; `Rank` нічого не робить (без місць, отже без очок); `AwardsRanks = false` (з 1.3); `IsUnknown = true` для банера |
| `Models/GroupDayRow.cs`, `ParticipantDayRow.cs`, `DashboardInfo.cs`, `FinishEvaluation.cs` (cref) | тип поля |
| Presentation: `DisciplineTypeOption.cs`, `DisciplineOverrideOption.cs`, `DayRowViewModel.cs`, `GroupDayRowViewModel.cs`, `ParticipantDayRowViewModel.cs`, `GroupsViewModel.cs`, `CompetitionDaysView.axaml.cs` | опції будувати з `IDisciplineStrategyProvider.All` (назва — `NameKey`), порівняння — через `DisciplineId` |

**Перевірка:** `dotnet ef migrations add _Check --context EventDbContext …` дає порожні `Up/Down` → видалити міграцію.
Diff знімка порожній. Старе `event.db` відкривається; день/група з вигаданим значенням у БД (вписати вручну
через `sqlite3`) → програма не падає.

### 1.2. Прибрати порівняння з конкретними дисциплінами зі спільного коду (M)

Нові члени `IDisciplineStrategy` (дефолти в `DisciplineStrategyBase`):

```csharp
CompetitorModel Competitors { get; }      // Individual | Team (Rogaine) | TeamWithLegs (з'явиться у фазі 4)
bool UsesCourseVariants { get; }          // Scatter
bool UsesCoursePattern { get; }           // лише Mixed — прев'ю/валідація шаблону в «Групах» (як зараз; не Scatter!)
```

Плюс статичний `CourseOrder.CountControls(string)` (витяг із `DisciplineStrategyBase.ControlCount`, яким базовий
метод і користується) — для лічильника КП варіантів розсіювання.

Місця, які треба переписати (стан на 2026-10-26, рядки `CompetitionEditorService.cs`):

| Рядок | Зараз | Стане |
|---|---|---|
| 47 | `?? DisciplineType.SetCourse` | `?? DisciplineId.SetCourse` (константа, не гілка — ок) |
| 565, `GroupsViewModel:314` | `_strategies.For(Scatter)` як лічильник КП варіантів | `CourseOrder.CountControls` — плоский підрахунок, який зараз і дає `ScatterStrategy` (не `CoursePatternVariants` — інакше зміниться кількість КП) |
| 836, 858–859, 3580, 4387, 4395 | `== Scatter` | `strategy.UsesCourseVariants` |
| 2114, 2657, 2915, 4012 | `== Rogaine` | `strategy.Competitors == CompetitorModel.Team` |
| 2121 | `discipline == Mixed` | `strategy.ControlCount(...)` (вже є в стратегії) |
| 2702 | `isScatter` | `UsesCourseVariants` |
| 3310 | `Discipline: DisciplineType.Rogaine` | `DisciplineId.Rogaine` |
| 4063 | `For(SetCourse)` для учасника без дня | лишити (дефолт, не гілка) |
| `GroupDayRowViewModel:213`, `GroupsViewModel:72, 229` | `== Scatter` | `UsesCourseVariants` (через стратегію з провайдера) |
| `GroupsViewModel:338` | `!= Mixed` | `!UsesCoursePattern` |

**Перевірка:** `grep -rn "DisciplineId\.\(Rogaine\|Scatter\|Mixed\)" src --include=*.cs` поза `Disciplines/` — лише
константи-дефолти. Diff знімка порожній. Ручний прохід: «Групи» на днях Scatter і Mixed (редактор варіантів,
валідація шаблону; на Scatter прев'ю шаблону **не** з'являється, кількість КП групи-розсіювання та сама), зчитка
рогейну.

### 1.3. Винести обчислення результатів дня в окремий клас (L)

`ComputeDayResultsAsync` (~2860–3040) + `RankRogaineTeams` (~3320) + `AwardPointsAsync`/`AwardRanksAsync`
(~3043, ~3133) перенести в `BusinessLogic/Services/Results/DayResultsCalculator.cs` (internal, **без доступу до
БД**), вихід — словник результатів. `CompetitionEditorService` лише читає дані й викликає калькулятор. Це зменшує
5600-рядковий сервіс і дає естафеті місце для власного проходу.

**Вхідні дані — зараз їх немає в одному місці.** `ComputeDayResultsAsync` знімок не використовує: окремо читає
`GetParticipantDaysAsync`, `GetFinishReadoutsAsync`, `GetGroupDaySettingsAsync`, `GetControlPointsAsync`,
`GetParticipantsAsync`, `LoadScatterVariantsAsync`, а `AwardPointsAsync`/`AwardRanksAsync` — ще `CompetitionInfo`
і app DB (`GetPointsRulesAsync`, `GetRankQualificationAsync`, `GetRankConditionsAsync`, `GetRanksAsync`). Тому:

- `EventDaySnapshot` + `EventStore.GetDaySnapshotAsync`: додати `FinishReadouts` (дня) і `ScatterVariants` (дня) —
  читаються в тій самій транзакції (правило «один знімок», memory «Duplicate rows»);
- новий `DayResultsAppData` (правила очок, таблиця кваліфікації, умови, розряди) — читається з `IAppStore` один раз
  на виклик (для підсумкового — один раз на всі дні);
- калькулятор: `Compute(EventDaySnapshot day, DayResultsAppData app, GroupRankCalculation? rankCalcs)`;
- 9 викликів `ComputeDayResultsAsync` (рядки 197, 1460, 1511, 2029, 2080, 2384, 2636, 3686, 3803): там, де знімок
  уже прочитано (напр. `GetResultProtocolDataAsync`), передавати **той самий** знімок — це заодно прибирає
  розбіжність між рядками протоколу і результатами.

Конвеєр усередині калькулятора — явні етапи:

```
a) Competitors   — згрупувати ParticipantDay за strategy.Competitors (Individual / Team; TeamWithLegs — фаза 4)
b) Evaluate      — для кожної людини: strategy.ResolveStart → EvaluateFinish → ResultTime
c) Adjust        — strategy.AdjustTime(...) → AdjustedTime (дефолт: без змін)
d) Rank          — strategy.Rank(RankingContext) по групах
e) Points/Ranks  — як зараз (очки від місця, формула — від AdjustedTime (Р-9; для вбудованих = ResultTime);
                   розряди від фактичного часу, якщо strategy.AwardsRanks)
```

Нові члени стратегії: `ResolveStart(StartContext)` (нинішня логіка `resolvedStart`), `AdjustTime(AdjustContext)`,
`Rank(RankingContext)` (нинішнє «бали ↓, час ↑, рівні ділять місце, П/К поза рангом»; `RogaineStrategy.Rank` —
код `RankRogaineTeams`), `bool AwardsRanks` (дефолт `true`).

Порядок роботи: (0) розширити знімок і `DayResultsAppData`, перевести `ComputeDayResultsAsync` на них, diff;
(1) перенести код як є в калькулятор, diff; (2) розбити на етапи a–e, diff; (3) винести `ResolveStart`/`Rank` у
стратегії, diff. Чотири коміти.

**Перевірка:** diff порожній після кожного з трьох комітів; час перерахунку на змаганні ~600 учасників не гірший
(заміряти `Stopwatch` у Debug-лог).

### 1.4. Розширюваний результат (M)

- `ParticipantDayResult`: `AdjustedTime` (`TimeSpan?`), `Extra` (`IReadOnlyDictionary<string, object?>`, порожній
  за замовчуванням). `AdjustedTime` = `ResultTime`, доки дисципліна не змінює час (без округлення, Р-6).
- `FinishStatusResult`: додати `IReadOnlyList<string> Missed` (порожній за замовчуванням). **Це новий алгоритм, а
  не «заповнити»:** нинішній жадібний прохід (`SetCourseStrategy.EvaluateFinish`, `CoursePattern.Match`,
  `ScatterStrategy.FirstMissing`) після першого пропуску пробігає решту відміток до кінця й знає лише перший
  пропущений КП. Реалізувати за Р-52a:
  - заданий порядок / варіант розсіювання — найдовша спільна підпослідовність (DP, O(n·m), n,m ≤ ~60);
    `Missed` = обов'язкові КП поза нею;
  - змішаний тип — максимальне задоволення шаблону (рекурсивно по `PatternItems`: ряд — як LCS по елементах,
    `[N: …]` — найкращі N варіантів; невзяті з блоку — у `Missed` за порядком запису);
  - статус і `Detail` **не чіпати** — їх дає нинішній жадібний прохід (впорядкований збіг існує ⇔ LCS = довжині
    дистанції, тож OK/MP збігаються); `Missed` рахується лише для MP. Для змішаного типу жадібний `Match` теоретично
    може дати MP там, де максимальний алгоритм знаходить повний збіг (`Missed` порожній при MP) — такий випадок
    писати в Debug-лог: це баг жадібного проходу, виправляється окремим кроком зі зміною поведінки.
  Перевірка: diff знімка порожній; для кількох реальних MP-зчиток `Missed` вручну звірено зі сплітами
  (`BuildOrderedSplits` уже показує пропущені КП з ресинхронізацією — розбіжність = помилка).
- `TeamDayResult` (порожній клас-заготовка з `Time`, `Status`, `Place`, `Legs`) — з'явиться в ужитку у фазі 4;
  можна відкласти до 4.2.

**Готово (фаза 1):** немає `DisciplineType`; немає `== <дисципліна>` у спільному коді; diff знімків порожній на всіх
3 змаганнях. Оновити розділ «Disciplines» у CLAUDE.md (id-рядки, `CompetitorModel`, калькулятор). Можна релізити.

---

## Фаза 2 — SDK, завантажувач, плагін «Штраф замість зняття»

### 2.1. Проєкт `OrientPyx.Plugins.Sdk` і перенесення спільних типів (M)

- `src/OrientPyx.Plugins.Sdk/OrientPyx.Plugins.Sdk.csproj` — `net10.0`, без `PackageReference`;
  **явні** `<AssemblyVersion>1.0.0.0</AssemblyVersion>` і `<FileVersion>1.0.0.0</FileVersion>` (не `<Version>`):
  `build/publish.ps1` передає `-p:Version=<версія програми>` глобальною властивістю, яка перепише `Version` усіх
  проєктів, а явний `AssemblyVersion` — ні. Інакше SDK у релізі матиме версію `0.x` і плагіни, зібрані проти 1.0.0,
  не завантажаться (`FileLoadException`). Додати в `OrientPyx.sln`; `BusinessLogic` → `ProjectReference` на SDK.
- Перенести (простір імен `OrientPyx.Plugins.Sdk` / `.Toolkit`):
  - `FinishStatus`, `FinishStatusResult`, `FinishDetailKind`, `SplitsView`/`SplitsLayout` (і їхні рядкові типи),
    `ControlPointType`;
  - `Disciplines/CoursePattern/*` → `Toolkit/Patterns/` (простір імен `OrientPyx.Plugins.Sdk.Toolkit.Patterns`:
    зараз простір імен і клас обидва `CoursePattern`, звідси виклики `CoursePattern.CoursePattern.Parse`);
  - `Models/TextKey.cs` → `Toolkit/TextKey.cs`.
- Щоб не правити сотню `using`: у `BusinessLogic.csproj`, `DataAccess.csproj`, `Presentation.csproj` додати
  `<Using Include="OrientPyx.Plugins.Sdk" />` і `<Using Include="OrientPyx.Plugins.Sdk.Toolkit" />`.
- EF: перенесені enum-и зберігаються рядком — конвертери не змінюються; перевірити порожньою міграцією, як в 1.1.
- CLAUDE.md: структура рішення, правила шарів, напрям залежностей, «What NOT to do» (§4.1 спеки).

**Перевірка:** порожня міграція для обох контекстів; diff знімка порожній; `powershell -File build/publish.ps1
-Version 0.0.1` (без `-Upload`) → у publish-папці `OrientPyx.Plugins.Sdk.dll` має AssemblyVersion `1.0.0.0`.

### 2.2. Toolkit: вирізати чисті алгоритми з ядра (M)

| Новий тип (`Sdk/Toolkit/`) | Звідки береться | Ядро після кроку |
|---|---|---|
| `Punches` (`Split`, `WithoutStartFinish`, `WithoutDisabled`) | `SplitCodes`, `DisabledCodesOf` (private у сервісі) | викликає `Punches` |
| `CourseOrder` (`CountControls`) | крок 1.2 | — |
| `CourseMatcher` (`Match(pattern, punches) → MatchResult {Ok, FirstMissing, Missed, Passage}`) | жадібний прохід `SetCourseStrategy`/`CoursePattern.Match` + алгоритм `Missed` з 1.4 | стратегії — тонкі обгортки |
| `VariantMatcher` (`Best(variants, punches) → {Variant, Depth, Consumed, FirstMissing, Missed}`) | `ScatterStrategy.SelectBestVariant`/`ScoreVariant` — **скоринг без змін** (повний збіг > `Consumed` > порядок) | те саме |
| `Ranking` (`AssignPlaces(items, comparer, isOutOfCompetition)` + `ByTime`, `ByScoreThenTime`) | етап d з 1.3 | `DisciplineStrategyBase.Rank` викликає `Ranking` |
| `Age.InYear(int birthYear, DateOnly date)` | новий | використати в перевірці вікового вікна групи; дату дня (з запасним варіантом Р-46) обчислює ядро, не toolkit |
| `TimeFormat` | форматування в протокольних білдерах | білдери викликають його |

`RangeTable` і `TeamCourseAssignment` — пізніше (6.3, 5.3), коли з'являться споживачі.

**Перевірка:** diff знімка порожній; спліти на зчитці (заданка, змішаний, розсіювання) — як були.

### 2.3. Контракт плагіна в SDK (M)

Файли `Sdk/`: `IOrientPyxPlugin.cs`, `PluginInfo.cs`, `IPluginBuilder.cs`, `Discipline.cs`, `BuiltinDiscipline.cs`,
`CompetitorModel.cs`, `Contexts/{FinishContext, StartContext, AdjustContext, RankingContext, ResultRow,
ParticipantView}.cs`, `Columns/{ResultColumnDef, Column}.cs`, `Settings/{SettingDef, Setting, SettingValues}.cs`,
`Fields/FieldKind.cs`, `IDisciplineHost.cs`, `ApiVersion.cs` (`Current = 1`).

- `Discipline` має internal-ish точку `Attach(IDisciplineHost host)` (публічний метод з `[EditorBrowsable(Never)]`);
  `base.EvaluateFinish(ctx)` тощо делегують у `host`.
- Контексти — **незмінні** обгортки над даними ядра; `ResultRow.Set/Get` пишуть у словник, що стане
  `ParticipantDayResult.Extra`. `FinishContext` має `TimeLimit` і `IsOverTime` (потрібно плагіну штрафів, Р-57);
  `AdjustContext.Date : DateOnly` — дата дня за Р-46 (`EventDay.Date` буває null).
- `Fields/FieldKind.cs` — record з фабриками (`Text`, `Number`, `Bool`, `Choice(params ChoiceOption[])`), не enum;
  `ChoiceOption(string Value, params string[] Aliases)` з неявним перетворенням з `string` (Р-18, Р-51).
- Сигнатури — як у §4.2 спеки, плюс `bool AwardsRanks => host.AwardsRanks` (дефолт від `BasedOn`).

### 2.4. Адаптер у BusinessLogic (M)

- `Disciplines/Plugins/PluginDisciplineStrategy.cs : IDisciplineStrategy` — загортає `Discipline`; колонки групи,
  `Competitors`, `UsesCourseVariants`… бере у вбудованої стратегії `BasedOn`; `EvaluateFinish/BuildSplits/
  ResolveStart/AdjustTime/Rank` — у плагіна.
- `Disciplines/Plugins/DisciplineHost.cs : IDisciplineHost` — виконує `base.*` вбудованою стратегією з **тими самими**
  контекстами (проблемні КП, варіанти), що їх побудувало ядро.
- Ізоляція: кожен виклик у `try/catch` → результат `BasedOn` + запис в `IActivityLog` + один банер на сесію
  «Плагін `<id>` дав помилку, результати пораховано за правилами «<BasedOn>»».
- `Interfaces/IPluginCatalog.cs`: `IReadOnlyList<LoadedPlugin> Plugins`, `IReadOnlyList<PluginLoadError> Errors`,
  дисципліни й поля плагінів.
- `DisciplineStrategyProvider` приймає й плагінні стратегії (`IEnumerable<IDisciplineStrategy>` з каталогу).

### 2.5. Завантажувач у DataAccess (M)

`DataAccess/Plugins/`:

- `IPluginSource` / `DllPluginSource` — перелік кандидатів у папці (`<dir>/<Name>/<Name>.dll`).
- `PluginLoadContext : AssemblyLoadContext` (isCollectible: false) з `AssemblyDependencyResolver`; збірку
  `OrientPyx.Plugins.Sdk` **не** вантажить сам — повертає `null`, щоб узялася зі спільного контексту.
- `PluginLoader` — сканує по черзі (у Debug) `<repo>/artifacts/plugins`, `<app dir>/plugins`, `<data root>/plugins`;
  той самий `Info.Id` пізніше перекриває раніший, тобто data root > папка програми > `artifacts` (Р-12); перевіряє `MinApiVersion ≤
  ApiVersion.Current`; вимкнені id (налаштування додатка) пропускає; будь-який виняток → `PluginLoadError`.
- `PluginCatalog : IPluginCatalog`.
- `DependencyInjection`: `services.AddOrientPyxPlugins(PluginPaths)` — завантажує **синхронно до** `BuildServiceProvider`
  (`PresentationServiceCollectionExtensions.cs:80`), реєструє каталог і стратегії. Помилки завантаження записуються в
  `IActivityLog` уже після побудови контейнера (лог до DI ще не існує).
- App DB: `AppSettingsRow.DisabledPluginsJson` (міграція `AppDbContext`, `PluginsDisabled`).

**Перевірка (з тимчасовим плагіном-заглушкою):** DLL у кожній із трьох папок підхоплюється; дубль id — перемагає
data root; `MinApiVersion = 99` → пропущено з повідомленням; виняток у `Register` і битий файл → програма стартує.

### 2.6. Налаштування дисципліни (прості типи) (M)

- Сутність `PluginSettingsRow(Id, EventDayId?, DisciplineId, Json)`, унікальний індекс `(EventDayId, DisciplineId)`;
  `EventDayId = null` — налаштування з областю `Competition` (Р-11). Міграція `EventDbContext` `PluginSettings`.
- `SettingDef.Scope` = `Day` (за замовчуванням) | `Competition`; форма показує обидві групи, позначаючи
  «для всього змагання».
- `ICompetitionEditorService.Get/SaveDisciplineSettingsAsync(dayId, disciplineId)` → `SettingValues` (зливає рядок
  дня й рядок змагання); збереження змагальних значень перераховує всі дні з цією дисципліною.
- Калькулятор (1.3) читає налаштування разом зі знімком дня; `ctx.Setting<T>(key)` — збережене або дефолт `SettingDef`.
- Збереження → перерахунок результатів (як після зміни дисципліни, memory «Discipline change live propagation»).

### 2.7. UI плагінів (M)

- Випадайки «Тип змагань» (дні, групи): плагінні дисципліни після вбудованих, з позначкою «(плагін)».
- Кнопка «Налаштування дисципліни…» біля випадайки, видима, коли `Settings` не порожні → модалка
  `DisciplineSettingsDialog` з автоформою (Int/Decimal — власний степер, не `NumericUpDown`, memory
  «NumericUpDown automation crash»; Bool — CheckBox; Text — TextBox; Choice — ComboBox).
- «Налаштування → Плагіни»: таблиця (назва, версія, автор, джерело-папка, стан / помилка), «Вимкнути», «Відкрити
  папку плагінів», примітка «Зміни — після перезапуску».
- Банер на сторінках дня, коли дисципліна дня/групи — `UnknownDisciplineStrategy`: «Потрібен плагін `<id>`».
- Рядки плагіна: `ILocalizationService.AddOverlay(culture, prefix, dict)` у `OrientPyx.Localization` (нового методу
  ще немає — зараз лише `Get`/`SetLanguage`); ключі плагіна резолвляться як `<pluginId>.<key>`, відсутній переклад →
  ключ англійської, далі сам ключ. Викликає **Presentation** одразу після `BuildServiceProvider`, беручи рядки з
  `IPluginCatalog` — DataAccess на Localization не посилається, тож завантажувач лише збирає рядки.
- Нові ключі ядра: `Page.Settings.Plugins.*`, `Dialog.DisciplineSettings.*`, `Banner.MissingPlugin`,
  `Discipline.PluginSuffix` (uk + en).

### 2.8. `.opyx` і реліз (S)

- `EventArchiveService` (`DataAccess/FileSystem/`): у маніфест архіву (поряд з `app-shared.json`) —
  `requiredPlugins: [{id, version}]` (дисципліни днів/груп, які не вбудовані); пошкоджений/відсутній маніфест, як і
  зараз, імпорт не блокує. `EventArchiveFlow` (Presentation) при імпорті: відсутні → попередження в модалці
  конфлікту, імпорт дозволено.
- `plugins/OrientPyx.Plugins.sln`, `plugins/Directory.Build.props` (§4.4 спеки); `artifacts/` уже в `.gitignore`.
- `build/publish.ps1` (пакування робить він, `/new-version` лише викликає): після `dotnet publish` і перед
  `vpk pack` — `dotnet build plugins/OrientPyx.Plugins.sln -c Release` + копія `artifacts/plugins/*` у
  `$publishDir/plugins/`. В `.claude/commands/new-version.md` — одне речення, що плагіни входять у пакет.

### 2.9. Плагін «Штраф замість зняття» (S)

`plugins/MissedControlPenalty/{MissedControlPenalty.csproj, MissedControlPenaltyPlugin.cs}` — дві дисципліни
(`missed-penalty.set-course`, `missed-penalty.mixed`, спільний базовий клас), правила Р-52…Р-58: налаштування
`minutes`/`maxMissed`, колонки «Пропущено КП»/«Штраф», MP → OK лише за наявності фінішу, `AdjustTime` = час + штраф,
`AwardsRanks => false`. **OVT плагін перевіряє сам** при перетворенні MP → OK (`ctx.IsOverTime` → OVT): вбудована
перевірка повертає MP раніше, ніж дивиться на контрольний час (Р-57, код — §6 спеки).

**Готово (фаза 2):** критерій §6 спеки (1–3 пропущені → OK і час + штраф, 4+ → MP, 2 пропущені + перевищення
контрольного часу → OVT; видалений DLL → банер; битий
DLL → старт); diff знімків вбудованих форматів порожній; інсталятор містить плагін. Реліз.

---

## Фаза 3 — додаткові поля учасника та гнучкий імпорт

### 3.1. Модель (S)

- `Entities/CustomFieldDef.cs` (`Key` PK, `Label`, `Kind` (рядок), `OptionsJson` (варіанти Choice + синоніми),
  `Source`, `Order`),
  `Entities/ParticipantFieldValue.cs` (`ParticipantId`, `Key`, `Value`; PK `(ParticipantId, Key)`).
- Міграція `ParticipantCustomFields`.
- `DeleteParticipantAsync` — видаляти значення разом з учасником (memory «Orphaned ParticipantDay links»).
- Поля плагіна (`AddParticipantField`) при першому використанні дисципліни в змаганні створюють `CustomFieldDef`
  з `Source = plugin:<id>` (якщо ключа ще немає).

### 3.2. Сервіс і рядки таблиць (M)

- `ICompetitionEditorService`: `Get/Add/Rename/DeleteCustomFieldAsync`, `SetParticipantFieldAsync`.
- `ParticipantDayRow` / рядок мандатки: `IReadOnlyDictionary<string, string?> Fields`; у VM рядка — індексатор для
  біндингу колонки.

### 3.3. UI полів (M)

- Модалка «Додаткові поля» (з меню колонок мандатки + сторінка «Інформація»): SheetTable полів, тип, варіанти Choice,
  видалення з підтвердженням (видаляє й значення).
- Мандатка (`RosterColumnBuilder`) і day-grid (`SheetColumnBuilder`): колонка на поле, `LayoutKey`-ключ колонки
  `field.<key>`; перебудова бендів при зміні набору полів. Пошук/фільтр F3 працюють.

### 3.4. Імпорт CSV/Excel (M)

- `Models/CsvParticipantData.cs`: `CsvParticipantField` → `ImportTarget` (record з рядковим `Key`); вбудовані цілі —
  статичні екземпляри з тими самими назвами; `field.<key>` — додаткові.
- `CsvMappingViewModel`: опції будуються з вбудованих + полів змагання; пункт «+ Нове додаткове поле…» (створює поле
  з назвою заголовка й одразу зіставляє); автозіставлення за назвою враховує поля.
- Шаблон зіставлення: `ImportMappingRow(HeaderSetHash, Json)` у `event.db` (міграція), засів з app-default у app DB.
- `CsvImportFlow` → `ImportParticipantsBatchAsync`: значення полів пишуться в тій самій транзакції. Choice — синонім
  зводиться до `Value` варіанта (`TextKey`, без регістру), невідоме значення лишається як є (Р-18); Number приймає кому
  й крапку (Р-19).

### 3.5. Експорт і «Відомість» (S)

Поля — серед доступних колонок експорту (`ExportView()` уже бере видимі колонки) і «Відомості».

**Готово (фаза 3):** поле «Стать» створено вручну, заповнено імпортом з Excel (через «+ Нове додаткове поле»),
видно й редагується в мандатці, експортується; видалення учасника не лишає значень. Реліз.

---

## Фаза 4 — естафета (вбудована дисципліна)

### 4.1. Модель і міграція `Relay` (M)

- `Entities/RelayTeam.cs` (§8.1 спеки), унікальний `(EventDayId, Number)`.
- `ParticipantDay`: `RelayTeamId?`, `Leg int?`, унікальний `(RelayTeamId, Leg)`.
- `GroupDaySettings`: `LegCount`, `MassStartTime?`, `RestartTimesJson`, `RestartRanking`.
- `DisciplineId.Relay`; `CompetitorModel.TeamWithLegs`.
- Номер етапу: `RelayBib.Format(teamNumber, leg)` / `TryParse("100.2")` у toolkit.

### 4.2. `RelayStrategy` і прохід калькулятора (L)

- Етап a) калькулятора: для `TeamWithLegs` будує `TeamCompetitor` з етапами за `Leg`.
- Етап b): етапи **послідовно** — старт етапу 1 = стартова відмітка ?? `StartTime` ?? `MassStartTime` (Р-27);
  етап N = `min(фініш N−1, RestartTime N)` (Р-28, Р-29); стартова відмітка етапів N ≥ 2 ігнорується; немає фінішу
  N−1 і перезапуску → «очікує» (Р-30).
- Дистанція етапу на цьому кроці — дистанція групи (усі етапи однакові); режими — фаза 5.
- c) `AdjustTime` на кожен OK-етап, округлення до секунди (Р-6, Р-49); час команди = Σ (Р-32); статус команди —
  Р-33/Р-34 (DSQ > MP > OVT > DNF > DNS > «очікує»), контрольний час — до фактичного часу команди (Р-31);
  неповна → П/К (Р-26); `MarkUnranked` будь-якого етапу → команда без місця (Р-48).
- d) `RelayStrategy.Rank` — за часом команди, з урахуванням `RestartRanking` (Р-35).
- e) очки від місця команди — кожному члену однакові (Р-37); `AwardsRanks = false` (Р-38).
- `TeamDayResult` у результатах дня; `ParticipantDayResult` етапу отримує час етапу, наростаючий час, позначку
  перезапуску в `Extra`.

**Перевірка (ручний сценарій, записати в `docs/relay-scenarios.md`):** 3 команди × 3 етапи, вигадані зчитки:
нормальна передача; MP на етапі 2; незчитаний етап 2 при зчитаному 3; перезапуск 3-го етапу; неповна команда;
рівний час. Очікувані результати порахувати заздалегідь. Diff знімків не-естафетних змагань порожній.

### 4.3. Сторінка «Команди» (M)

Day-scoped сторінка (видима, коли на дні є естафетна група): SheetTable команд + панель етапів обраної команди
(пошук учасника за ПІБ, layout-tolerant); «Масове створення команд» (N від номера X); видалення команди звільняє етапи.

### 4.4. Day-grid і зчитка (M)

- Day-grid: колонки «Команда №», «Етап», «Номер етапу» — коли `Competitors == TeamWithLegs` у будь-якій групі дня;
  група учасника на дні-естафеті — група команди, лише для читання (Р-22).
- Номер `100.2` приймається в пошуку, фільтрі, швидкому знятті, модалці невідомого чипа; «100» — уся команда (Р-24).
- Чип унікальний у межах дня; призначення іде через наявне підтвердження перепризначення (Р-25).
- Зчитка: команда/етап у журналі, спліти етапу, позначка «перезапуск»; модалка невідомого чипа — вибір команди й етапу.

### 4.5. Протоколи (L)

- `ResultProtocolDocument`: рядки другого рівня (команда → етапи); `DocxResultProtocolWriter` і
  `ProtocolPreviewTable` їх малюють (відступ, менший шрифт).
- Стартовий протокол (естафета): команди з етапами, старт 1-го етапу.

### 4.6. Імпорт (M)

- CSV/Excel: цілі `relay.teamNumber`, `relay.teamName`, `relay.leg`; номер у форматі `100.2` розбирається
  автоматично; команди створюються/зіставляються за номером.
- IOF XML v3 `EntryList` → `TeamEntry`/`TeamEntryPerson`.
- «Широкий» формат (рядок на команду) — окремим кроком пізніше.

### 4.7. Жеребкування команд (M)

`DrawViewModel` / класичне жеребкування: на дні-естафеті одиниця — команда (розведення за клубом/регіоном команди),
хвилина старту пишеться в `ParticipantDay.StartTime` учасника 1-го етапу (Р-36).

**Готово (фаза 4):** сценарії з 4.2 проходять від імпорту до Word-протоколу; жеребкування розставляє команди. Реліз.

Підсумковий протокол (Р-39) — у фазі 7, крок 7.3.

---

## Фаза 5 — призначення дистанцій етапам

### 5.1. `GroupCourseVariant` (M)

`ScatterVariant` → `GroupCourseVariant(EventDayId, GroupId, Code, CourseOrder, Order)` — ті самі поля, нова назва
(назва `CourseVariant` уже зайнята record-ом розгорнутого порядку в toolkit `Patterns`); міграція з перенесенням
даних (SQL `INSERT … SELECT` у `Up`, зворотне в `Down`); `ScatterVariantData` → `CourseVariantData`; редактор
варіантів у «Групах» — спільний для розсіювання й естафети.
**Перевірка:** diff знімка змагання з розсіюванням порожній.

### 5.1a. Варіанти-шаблони (M)

Зараз варіант розсіювання — плоский порядок; для естафети дистанція — будь-який шаблон (Р-40). Голий ряд — валідний
шаблон, тож старі дані сумісні. `VariantMatcher` і `ScatterStrategy` переходять на зіставлення з **шаблоном** через
`CourseMatcher` (не розгортати через `CoursePatternVariants` — обмежено `Limit`, росте факторіально); валідація
шаблону в редакторі варіантів. Для плоских варіантів результат той самий — diff знімка розсіювання порожній.

### 5.2. Режими ByLeg / ByBib (M)

`GroupDaySettings.CourseAssignmentMode` + таблиця `LegCourseAssignment(DayId, TeamNumber, Leg, CourseKey)` для ByBib;
імпорт призначень з IOF `TeamCourseAssignment` і CSV («Номер етапу; Дистанція»).

### 5.3. Режим TeamSet + `Toolkit/TeamCourseAssignment` (M)

Штраф етапу на дистанції — 0 для OK, інакше `Missed.Count` з `CourseMatcher` (Р-42; **не** скоринг автовизначення
розсіювання). Перебір перестановок для `LegCount ≤ 8`, угорський алгоритм інакше; рівні варіанти — за Р-42; незчитані етапи —
«не визначено» (Р-43). Набір команди — `RelayTeam.CourseSetJson` (або спільний на групу, Р-41). Призначена
дистанція — у зчитці, сплітах, протоколі (Р-44).

**Готово:** команда {заданка, вибір} у будь-якому порядку — OK; обидва бігли заданку — MP команди.

---

## Фаза 6 — «Зважена естафета»

### 6.1. Табличні налаштування (M)
`Setting.Table` → модалка з SheetTable (рядки/колонки з `SettingDef`), JSON у `PluginSettingsRow`; `TableValue` у SDK.

### 6.2. Динамічні колонки результатів (L)
`ProtocolColumn`/`ResultColumn`/`SummaryColumn` → рядкові ключі (`builtin.*`, `field.<key>`, `<pluginId>.<key>`) з
сумісним читанням старих JSON-шаблонів (enum-ім'я → `builtin.<ім'я>`). Застосувати: Word-протокол + прев'ю, монітор,
day-grid, рядки етапів. Невідомий ключ — ігнорується.

### 6.3. Плагін `plugins/WeightedRelay` (S)
+ `Toolkit/RangeTable` (поза діапазоном — найближчий рядок, Р-47). `AdjustContext.Row.MarkUnranked(reason)` у SDK
(Р-48: немає статі/року → команда без місця). Таблиця K — налаштування з областю `Competition` (Р-50). Округлення
кожного етапу до секунди — ядро (Р-49). Поле `gender` — `FieldKind.Choice` із синонімами (Р-51). Вік — від дати дня з
запасним варіантом (Р-46). Код — §10.3 спеки, дефолтна таблиця — §14.

**Готово:** критерій §10 спеки (Ч 40 р. + Ж 12 р. → `T1·0.95 + T2·0.82`; правка таблиці K одразу перераховує).

---

## Фаза 7 — онлайн, монітор, підсумковий

7.1. `docs/online-snapshot.md` — контракт; `GetOnlineResultsSnapshotAsync` додає `teams[]` лише для днів-естафет
(особисті — без змін, сумісність із фронтендом). (S)
7.2. Фронтенд (окремий репозиторій). (—)
7.3. Підсумковий: день-естафета дає очки кожному члену; у заліку «за часом» не враховується. (S)
7.4. Монітор і HTML-спліти — естафетний режим. (M)

## Фаза 8 — для автора плагінів

`docs/plugin-api.md` (контракт, toolkit, обидва плагіни як приклади, версіонування); за потреби
`dotnet new orientpyx-plugin`; опційно `SourcePluginSource` (Roslyn) — лише якщо з'явиться реальна потреба.

---

## Підсумкова таблиця кроків

| Крок | Розмір | Міграція | Змінює поведінку | Реліз після |
|---|---|---|---|---|
| 0.1 знімок | S | — | ні (Debug) | |
| 1.1 DisciplineId | M | перевірка порожньої | ні | |
| 1.2 прапорці стратегій | M | — | ні | |
| 1.3 знімок + калькулятор + Rank/ResolveStart | L | — | ні | |
| 1.4 Extra/AdjustedTime/Missed (новий алгоритм) | M | — | ні | ✅ |
| 2.1 SDK-проєкт | M | перевірка порожньої | ні | |
| 2.2 toolkit | M | — | ні | |
| 2.3–2.5 контракт, адаптер, завантажувач | M×3 | App: `PluginsDisabled` | так (плагіни) | |
| 2.6–2.8 налаштування, UI, `.opyx`, реліз | M+M+S | Event: `PluginSettings` | так | |
| 2.9 «Штраф замість зняття» | S | — | так | ✅ |
| 3.1–3.5 додаткові поля, імпорт | S+M+M+M+S | Event: `ParticipantCustomFields`, `ImportMappings` | так | ✅ |
| 4.1–4.7 естафета | M+L+M+M+L+M+M | Event: `Relay` | так | ✅ |
| 5.1–5.3 дистанції етапів | M×4 | Event: `GroupCourseVariant`, `LegCourseAssignment` | так | ✅ |
| 6.1–6.3 «Зважена естафета» | M+L+S | — | так | ✅ |
| 7, 8 | S…M | — | так | ✅ |

## Прийняті рішення

Усі відкриті питання закрито (2026-10-26); правила — [§18 спеки](plugins-plan.md#18-правила-нормативний-перелік),
Р-1…Р-58. Посилайтеся на номер правила в коментарях коду там, де логіка неочевидна (`// Р-29: перезапуск`).
Ключові: `AwardsRanks` (Р-7), Debug-знімок (0.1), `DayResultsCalculator` (1.3), група команди на естафеті (Р-22),
жеребкування команд (Р-36), номер `100.2` скрізь (Р-24), унікальний чип у дні (Р-25), без статі/року — команда без
місця (Р-48), вік поза таблицею — найближчий рядок (Р-47), округлення кожного етапу (Р-49), таблиця K на змагання
(Р-50), штраф для заданого й змішаного (Р-53).

## Чекліст

- [x] 0.1 Debug-знімок результатів (інструмент готовий; знімки 3 реальних змагань знімає користувач)
- [x] 1.1 `DisciplineId`, `UnknownDisciplineStrategy`
- [x] 1.2 `Competitors`, `UsesCourseVariants`, `UsesCoursePattern` (лише Mixed), `CourseOrder.CountControls`; прибрати порівняння з дисциплінами
- [x] 1.3 Знімок + `DayResultsAppData`; `DayResultsCalculator`; `ResolveStart`, `AdjustTime`, `Rank`, `AwardsRanks` у стратегіях
- [x] 1.4 `Extra`, `AdjustedTime`, `FinishStatusResult.Missed` (LCS / шаблон, Р-52a); CLAUDE.md (Disciplines)
- [ ] 2.1 Проєкт `OrientPyx.Plugins.Sdk` (явний `AssemblyVersion`), перенесення типів; CLAUDE.md (структура)
- [ ] 2.2 Toolkit: `Punches`, `CourseOrder`, `CourseMatcher`, `VariantMatcher`, `Ranking`, `Age`, `TimeFormat`
- [ ] 2.3 Контракт плагіна
- [ ] 2.4 `PluginDisciplineStrategy`, `DisciplineHost`, ізоляція винятків
- [ ] 2.5 Завантажувач, `PluginsDisabled`
- [ ] 2.6 `PluginSettingsRow` (області Day/Competition) + автоформа
- [ ] 2.7 UI: випадайки, «Налаштування дисципліни…», сторінка «Плагіни», банер, локалізація плагінів
- [ ] 2.8 `.opyx`-метадані, `plugins/`, крок у `build/publish.ps1`
- [ ] 2.9 Плагін «Штраф замість зняття» (заданий + змішаний; OVT після MP → OK)
- [ ] 3.1 `CustomFieldDef`, `ParticipantFieldValue`, каскад
- [ ] 3.2 Сервіс полів, `Fields` у рядках таблиць
- [ ] 3.3 UI полів, колонки мандатки/day-grid
- [ ] 3.4 Імпорт: `ImportTarget`, «+ Нове додаткове поле», шаблони зіставлення
- [ ] 3.5 Експорт і «Відомість»
- [ ] 4.1 Модель `Relay`
- [ ] 4.2 `RelayStrategy` + прохід калькулятора; `docs/relay-scenarios.md`
- [ ] 4.3 Сторінка «Команди»
- [ ] 4.4 Day-grid, зчитка, номер `100.2`, унікальний чип
- [ ] 4.5 Протоколи результатів і стартовий
- [ ] 4.6 Імпорт CSV / IOF `TeamEntry`
- [ ] 4.7 Жеребкування команд
- [ ] 5.1 `GroupCourseVariant`
- [ ] 5.1a Варіанти-шаблони (`VariantMatcher` через `CourseMatcher`)
- [ ] 5.2 ByLeg / ByBib
- [ ] 5.3 TeamSet + `TeamCourseAssignment`
- [ ] 6.1 Табличні налаштування
- [ ] 6.2 Динамічні колонки
- [ ] 6.3 Плагін «Зважена естафета» + `RangeTable`, `MarkUnranked`
- [ ] 7.1–7.4 Онлайн-контракт, фронтенд, підсумковий, монітор/спліти
- [ ] 8 `docs/plugin-api.md`
