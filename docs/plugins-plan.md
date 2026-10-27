# OrientPyx — естафети, додаткові поля та плагіни-дисципліни (план)

> Статус: **реалізовано** (2026-10-27), крім онлайн-фронтенду під команди (§11.2, окремий репозиторій) і опційних
> пунктів §12 (`dotnet new`-шаблон, джерело `.cs`-файлів). Розділ §2 описує стан **до** реалізації. API для авторів
> плагінів — [`plugin-api.md`](plugin-api.md), контракт онлайн-знімка — [`online-snapshot.md`](online-snapshot.md).
> Покроковий план реалізації: [`plugins-implementation-plan.md`](plugins-implementation-plan.md).

## 1. Мета

Підсумкова ціль — змагання такого вигляду:

> **Зважена естафета** з довільною кількістю етапів. Кожен етап біжить свою дистанцію (будь-який шаблон
> змішаного типу: заданий порядок, вибір, їх поєднання). Команда сама вирішує, хто що біжить, але в сумі має
> закрити кожну дистанцію рівно один раз. Наступний етап стартує з фінішу попереднього (свою стартову станцію
> не відмічає), з можливим перезапуском. Час команди = **T1·K1 + T2·K2 + …**, де Kᵢ — коефіцієнт учасника за
> віком і статтю.

Задачу розкладено на шари, кожен корисний сам по собі:

| Шар | Де живе | Навіщо ще, крім цієї естафети |
|---|---|---|
| Підготовка ядра (рядкові id дисциплін, модель «учасник змагання», старт і ранжування в стратегіях) | ядро | передумова для всього нижче |
| **SDK плагінів** (контракт + спільні реалізації) і завантажувач | новий проєкт `OrientPyx.Plugins.Sdk` + DataAccess | будь-які локальні правила |
| **Додаткові поля учасника** + імпорт/експорт з довільними колонками | ядро | будь-які локальні дані без змін схеми |
| **Естафета** (команди, етапи, номери 100.1, передача, перезапуск, час і місце команди) | ядро, вбудована дисципліна | звичайні естафети |
| **Призначення дистанцій етапам** (за етапом / за номером / набір на команду) | ядро | естафети з розсіюванням |
| Тестовий плагін **«Штраф замість зняття»** | `plugins/MissedControlPenalty` | особисті змагання з м'яким правилом за пропущені КП |
| Тестовий плагін **«Зважена естафета»** (поле «Стать», таблиця K) | `plugins/WeightedRelay` | цільовий формат |

### Не-цілі

- Магазин плагінів, автозавантаження з інтернету, підписування — ні.
- Пісочниця — ні: плагін вважається довіреним кодом.
- Плагінні сторінки UI (Avalonia) — ні; лише декларативні налаштування.
- Тестові проєкти — ні (CLAUDE.md); перевірка — Debug-знімки результатів (diff) і ручні сценарії.

## 2. Поточний стан (від чого відштовхуємося)

| Що | Де | Значення для плану |
|---|---|---|
| `DisciplineType` — enum (6 значень) | `BusinessLogic/Enums/DisciplineType.cs` | Плагін не може додати значення. |
| Зберігання дисципліни | `EventDay.DefaultDiscipline`, `GroupDaySettings.DisciplineOverride`, `HasConversion<string>()` | ✅ У БД уже рядок — міграція даних не потрібна, якщо id = нинішні імена. |
| `IDisciplineStrategy` / `IDisciplineStrategyProvider` | `BusinessLogic/Disciplines/` | Добрий контракт (колонки, `EvaluateFinish`, `BuildSplits`), але без ранжування і без визначення старту. |
| Ранжування | `CompetitionEditorService.ComputeDayResultsAsync` (~2860) | Кожен учасник оцінюється незалежно; гілки `== Rogaine` (`RankRogaineTeams`), `== Scatter` (варіанти). |
| Читання для обчислення дня | `ComputeDayResultsAsync`, `AwardPointsAsync`, `AwardRanksAsync` | ⚠️ `GetDaySnapshotAsync` **не** використовується: 6–7 окремих читань event DB (учасники дня, зчитки, налаштування груп, КП, учасники, варіанти розсіювання, `CompetitionInfo`) + app DB (правила очок, таблиця кваліфікації, розряди, умови розрядів). `EventDaySnapshot` не містить зчиток і варіантів. |
| Пропущені КП | `SetCourseStrategy`, `MixedStrategy` (`CoursePattern.Match`), `ScatterStrategy` | ⚠️ Жадібний прохід: після першого пропуску «з'їдає» решту відміток; повертає **лише перший** пропущений КП (`FinishStatusResult.Detail`). Повного списку немає. |
| Інші перевірки типу | `CompetitionEditorService` (~565, 836–859, 2114–2121, 2657, 2702, 2915, 3580, 4012, 4063, 4387), `GroupsViewModel`, `GroupDayRowViewModel`, `CompetitionDaysView` | Порівняння з конкретною дисципліною в спільному коді. |
| Номер учасника | `Participant.Number` — рядок, рівень учасника | Номер етапу (100.2) не можна писати сюди (багатоденні змагання). |
| Команда | `Participant.Team` — вільний текст | Немає складу команди, порядку етапів, номера команди. |
| Змішаний тип | `Disciplines/CoursePattern/*` (`CoursePattern`, `CoursePatternVariants`, `PatternItems`, `PatternMath`), `MixedStrategy` | ✅ Мова дистанцій; чистий код без залежностей — кандидат у SDK. Синтаксис блоку вибору — **з двокрапкою** `[N: …]` (без неї — помилка `ChoiceMissingColon`); голий ряд `41 42 43` — заданий порядок. Простір імен і клас мають однакову назву (`CoursePattern.CoursePattern`). У файлі `CoursePatternVariants.cs` уже є record `CourseVariant` (один розгорнутий порядок). |
| Розсіювання | `ScatterStrategy`, таблиця `ScatterVariant(EventDayId, GroupId, Code, CourseOrder, Order)` | ✅ Автовизначення варіанта за зчиткою — основа режиму «набір на команду». Варіанти — **плоскі** порядки (`ScatterVariantData.Controls`), не шаблони. Вибір варіанта: повний збіг > більше пройдених відміток (`Consumed`) > раніший варіант — **не** «менше пропущених». `ControlCount` розсіювання — плоский підрахунок із `DisciplineStrategyBase`, не `CoursePatternVariants`. |
| Допоміжні функції | `TextKey` (зіставлення назв), `PointsFormula` (обчислювач формул), `SplitCodes`/`DisabledCodesOf` (private у `CompetitionEditorService`) | Кандидати в SDK. |
| Результат | `ParticipantDayResult` (фіксовані поля) | Немає місця для довільних значень (K, приведений час, час етапу). |
| Колонки протоколів / монітора / онлайну | `ProtocolColumn`, `ResultColumn`, `SummaryColumn` — enum | Не розширюються. |
| Імпорт CSV/Excel | `CsvParticipantField` (enum, 15 полів) + модалка зіставлення | Ціль — фіксований enum; немає полів команди/етапу та довільних полів. |
| Імпорт IOF XML | `IofXmlParser` (КП, групи/дистанції) | Немає `EntryList`/`TeamEntry` і призначень дистанцій командам. |
| Дата дня, рік народження | `EventDay.Date : DateTimeOffset?`, `Participant.BirthDate : DateTimeOffset?` | Дата дня може бути не задана; року як окремого поля немає — `BirthYear` похідний. |
| Реліз | `build/publish.ps1` (`dotnet publish --self-contained -p:Version=…` → `vpk pack`), скіл `/new-version` його викликає | ⚠️ `-p:Version` глобальна — перепише версію **кожного** проєкту, зокрема SDK. Без тримінгу й single-file — `AssemblyLoadContext` працює. |
| Локалізація | `ILocalizationService` (`Get`, `SetLanguage`), JSON-ресурси | Оверлеїв немає. DataAccess **не** посилається на Localization. |

## 3. Архітектурні рішення

1. **Ідентичність дисципліни — рядок** (`DisciplineId`). Вбудовані: `SetCourse`, `ScoreByCount`, `ScoreByTime`,
   `Rogaine`, `Mixed`, `Scatter`, `Relay` (новий). Плагінні: `"<pluginId>.<name>"`.
2. **Модель «учасник змагання»** замість прапорця «командний формат»: `CompetitorModel` = `Individual` |
   `Team` (рогейн: склад без порядку) | `TeamWithLegs` (естафета: склад з порядком етапів).
3. **Старт визначає дисципліна** (`ResolveStart`); для `TeamWithLegs` ядро оцінює етапи **послідовно**.
4. **Коригування часу — частина дисципліни** (`AdjustTime`, за замовчуванням без змін). Особиста дисципліна
   ранжує за скоригованим часом; естафета підсумовує скориговані часи етапів. Окремої сутності
   «коригувальник» немає — одна точка розширення простіша для автора і для UI.
5. **Додаткові поля учасника — механізм ядра**: поле створює користувач або оголошує плагін; таблиця
   «ключ–значення», без міграцій на кожне поле.
6. **Ціль зіставлення колонок в імпорті — рядковий ключ**: вбудовані поля, поля естафети, додаткові поля.
7. **Окремий проєкт `OrientPyx.Plugins.Sdk`** без залежностей: контракт плагіна + спільні чисті реалізації.
   Плагіни посилаються **тільки** на нього — межа API перевіряється компілятором. BusinessLogic посилається на SDK
   і використовує ті самі реалізації (одне джерело правди). Це п'ятий проєкт — **оновити CLAUDE.md** (§4.1).
8. **Завантаження плагінів — у DataAccess** (`IPluginCatalog` визначено в BusinessLogic). Джерело плагіна —
   абстракція (`IPluginSource`): спершу DLL, пізніше — папка `.cs`-файлів, які компілює Roslyn (§13).
9. **Відсутній плагін не ламає змагання**: невідома дисципліна → заглушка (Р-2). Поля, створені плагіном,
   лишаються в змаганні разом зі значеннями (Р-17).
10. **Версіонування**: версія збірки SDK = версія API. Мінорна — лише додавання; мажорна — несумісні зміни.
    Плагін оголошує `MinApiVersion`; несумісний — пропускається з повідомленням. `AssemblyVersion` SDK задається
    **явно** в його `.csproj` (`1.0.0.0`) і не залежить від `Version`: `publish.ps1` передає `-p:Version=<версія
    програми>` глобально, і без явного `AssemblyVersion` SDK отримав би, напр., `0.7.0` — тоді плагін, зібраний проти
    SDK 1.0.0, падав би з `FileLoadException` (версія в default-контексті менша за потрібну).

## 4. Структура рішення та SDK

### 4.1. Проєкти й залежності

```
OrientPyx.sln
src\
  OrientPyx.Plugins.Sdk\      ← НОВИЙ: контракт + toolkit, без залежностей
  OrientPyx.BusinessLogic\    ← вбудовані стратегії, адаптери плагінів
  OrientPyx.DataAccess\       ← завантажувач плагінів
  OrientPyx.Presentation\
  OrientPyx.Localization\
plugins\
  OrientPyx.Plugins.sln       ← окреме рішення для плагінів
  Directory.Build.props       ← спільні налаштування всіх плагінів
  MissedControlPenalty\
  WeightedRelay\
artifacts\plugins\<Назва>\    ← результат збірки плагінів (у .gitignore)
```

```
Presentation    -> BusinessLogic, DataAccess, Localization
DataAccess      -> BusinessLogic
BusinessLogic   -> Plugins.Sdk
Plugins.Sdk     -> (none)
Localization    -> (none)
плагіни         -> Plugins.Sdk (тільки)
```

Правка CLAUDE.md: розділи «Solution structure», «Architecture rules», «Dependency direction», «What NOT to do»
(«four-layer structure» → «чотири шари + SDK плагінів»); правило: **SDK не має залежностей, не знає про EF/файли/UI,
містить лише контракт і чисті алгоритми.**

### 4.2. `Plugins.Sdk` — контракт (`OrientPyx.Plugins.Sdk`)

```csharp
public interface IOrientPyxPlugin
{
    PluginInfo Info { get; }
    void Register(IPluginBuilder builder);
}

public sealed record PluginInfo(string Id, string Name, string Version, int MinApiVersion = 1,
                                string? Author = null, string? Description = null);

public interface IPluginBuilder
{
    void AddDiscipline<T>() where T : Discipline, new();
    void AddParticipantField(string key, string label, FieldKind kind);   // → CustomFieldDef, Source = plugin
    void AddStrings(string culture, IReadOnlyDictionary<string, string> strings);
}

public abstract class Discipline
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    /// Вбудована дисципліна, від якої успадковується все, що не перевизначено.
    public virtual BuiltinDiscipline BasedOn => BuiltinDiscipline.SetCourse;

    public virtual FinishStatusResult EvaluateFinish(FinishContext ctx);   // за замовч. → BasedOn
    public virtual SplitsView BuildSplits(SplitsContext ctx);              // за замовч. → BasedOn
    public virtual DateTimeOffset? ResolveStart(StartContext ctx);         // за замовч. → BasedOn
    public virtual TimeSpan AdjustTime(AdjustContext ctx) => ctx.Time;     // коригування часу бігуна / етапу
    public virtual void Rank(RankingContext ctx);                          // за замовч. → BasedOn

    public virtual bool AwardsRanks => host.AwardsRanks;                    // за замовч. → BasedOn (Р-7)

    public virtual IEnumerable<ResultColumnDef> Columns => [];
    public virtual IEnumerable<SettingDef> Settings => [];
    public virtual IEnumerable<string> RequiredFields => [];                // напр. "gender" → колонка в мандатці
}
```

- `Name`, підписи колонок і налаштувань — **ключі рядків плагіна** (`AddStrings`); якщо такого ключа немає, текст
  показується як є (тож простий плагін може писати текст напряму). Завантажувач (DataAccess) лише збирає рядки в
  каталог; в `ILocalizationService` їх реєструє Presentation після побудови контейнера (DataAccess не посилається на
  Localization).

- `BuiltinDiscipline` — лише **ідентифікатор** (`SetCourse`, `Mixed`, `Scatter`, `Relay`…). Реалізації живуть у
  BusinessLogic; при завантаженні хост передає дисципліні `IDisciplineHost`, тож `base.EvaluateFinish(ctx)` /
  `base.Rank(ctx)` викликають справжню вбудовану стратегію.
- `CompetitorModel`, `UsesControlPointPoints`, колонки групи тощо успадковуються від `BasedOn` і не перевизначаються
  (у v1) — плагін змінює правила, а не структуру формату.
- Контексти: `FinishContext` (відмітки, дистанція, статус КП, `FinishTime`, `TimeLimit`, `IsOverTime` — фактичний
  час > контрольного, `Setting<T>()`), `StartContext`,
  `AdjustContext` (`Participant`, `Time`, `Date : DateOnly` — дата дня за Р-46, `Leg?`, `Row`, `Setting<T>()`), `RankingContext` (`Group`,
  `Competitors`, `Finished`, `Setting<T>()`, `RankBy(...)`), `ResultRow` (`Status`, `Time`, `Score`, `Place`,
  `Punches`, `Missed`, `Set/Get`, `MarkUnranked(reason)` — без місця й очок; для етапу естафети — уся команда),
  `TeamCompetitor` (`Members` за етапами, `Time`, `Status`, `Place`), `ParticipantView` (ПІБ, `BirthYear`, клуб,
  регіон, `Field(key)`).
- Опис колонок і налаштувань: `ResultColumnDef` + фабрики `Column.Number/Time/Text`; `SettingDef` + фабрики
  `Setting.Int/Decimal/Bool/Text/Choice/Table` (кожне з `scope: SettingScope.Day` за замовчуванням або
  `SettingScope.Competition`, Р-11). `FieldKind` — **record з фабриками**, не enum: `FieldKind.Text`,
  `FieldKind.Number`, `FieldKind.Bool`, `FieldKind.Choice(params ChoiceOption[] options)`, де
  `ChoiceOption(string Value, params string[] Aliases)` має неявне перетворення з `string` (тобто
  `FieldKind.Choice("Ч", "Ж")` теж працює). Синоніми зберігаються у `CustomFieldDef.OptionsJson`; імпорт і ручне
  введення зводять синонім до `Value` (Р-18).
- Спільні прості типи переїжджають у SDK: `FinishStatus`, `FinishStatusResult`, `SplitsView`/`SplitsLayout`,
  `ControlPointType` — BusinessLogic використовує їх звідти.

### 4.3. `Plugins.Sdk` — toolkit (`OrientPyx.Plugins.Sdk.Toolkit`)

Чисті, стабільні реалізації, які використовує **і ядро, і плагіни** (переносяться з BusinessLogic, а не дублюються):

| Модуль | Що робить | Звідки |
|---|---|---|
| `CoursePattern` | розбір, валідація, підрахунок КП шаблонів змішаного типу (`<…>`, `[N: …]`, вкладення) | `Disciplines/CoursePattern/*` (простір імен → `Toolkit.Patterns`, щоб не збігався з назвою класу) |
| `CourseOrder` | плоский підрахунок КП у тексті порядку (без старт/фініш-маркерів) | статичний витяг із `DisciplineStrategyBase.ControlCount` |
| `CourseMatcher` | перевірка відміток проти шаблону → OK/MP, `FirstMissing` (як зараз — для `Detail`), **повний список пропущених** (`Missed`, новий алгоритм, Р-52a), проходження для сплітів | логіка `MixedStrategy`/`SetCourseStrategy` + новий підрахунок `Missed` |
| `VariantMatcher` | найкращий варіант розсіювання для зчитки — **нинішній** скоринг без змін: повний збіг > більше пройдених відміток > раніший варіант; результат містить і `Missed.Count` (для TeamSet, Р-42) | `ScatterStrategy.SelectBestVariant` |
| `TeamCourseAssignment` | призначення «етап → дистанція» для набору на команду (перебір / угорський алгоритм) | новий (§9.3) |
| `Punches` | розбір рядка відміток, відкидання старт/фініш-кодів і проблемних КП | `SplitCodes`, `DisabledCodesOf` |
| `Ranking` | розставлення місць з рівними (спільне місце, наступне — з пропуском), виключення «П/К», типові компаратори (час ↑; бали ↓ потім час ↑) | `ComputeDayResultsAsync` ~2996–3017 |
| `Age` | `Age.InYear(birthYear, date)` = рік змагань − рік народження (як вікове вікно групи, Р-46) | новий |
| `RangeTable<TKey, TValue>` | пошук у таблиці «діапазон × ключ → значення» (напр. вік × стать → K); поза діапазоном — найближчий рядок (Р-47); побудова з налаштування `Table` | новий |
| `TextKey` | порівняння назв з урахуванням схожих літер (M12 = М12) | `Models/TextKey.cs` |
| `TimeFormat` | формат часу як у протоколах | з протокольних білдерів |
| `Formula` *(пізніше)* | обчислювач формул | `Models/PointsFormula.cs` |

Правило для toolkit: **лише те, що стабільне й корисне кільком дисциплінам**. Усе тут стає публічним API з
обов'язками сумісності (§3.10), тож внутрішні деталі ядра сюди не потрапляють.

### 4.4. Проєкти плагінів

`plugins/Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OutputPath>$(MSBuildThisFileDirectory)..\artifacts\plugins\$(MSBuildProjectName)\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- компілюємося проти SDK, але не копіюємо його: у рантаймі його дає програма -->
    <ProjectReference Include="$(MSBuildThisFileDirectory)..\src\OrientPyx.Plugins.Sdk\OrientPyx.Plugins.Sdk.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

Кожен `<Плагін>.csproj` — один рядок `<Project Sdk="Microsoft.NET.Sdk" />`. Збірка:
`dotnet build plugins/OrientPyx.Plugins.sln -c Release` → `artifacts/plugins/<Назва>/<Назва>.dll`.

### 4.5. Де програма шукає плагіни

| Папка | Що там | Хто кладе |
|---|---|---|
| `<папка програми>\plugins\` | **стандартні** плагіни | `build/publish.ps1` (його викликає `/new-version`) збирає `plugins/OrientPyx.Plugins.sln` і копіює `artifacts\plugins\*` у publish-папку між `dotnet publish` і `vpk pack` |
| `<data root>\plugins\` | власні / сторонні | користувач; переживає оновлення (`AppDatabasePaths.BaseDirectory`) |
| `artifacts\plugins\` | плагіни, що розробляються | лише Debug-збірка (`dotnet run`) |

Однаковий id у кількох місцях: data root > папка програми > `artifacts` (Р-12; можна підмінити стандартний).
Кожен плагін можна вимкнути на сторінці «Плагіни». Поза випадайкою типу змагань і сторінкою «Плагіни» плагін не
показується (колонки, поля, налаштування), доки його дисципліну не вибрано для дня/групи.

## 5. Етап 1 — підготовка ядра (зовні нічого не змінюється)

**Мета:** прибрати з ядра все, що заважає естафеті й плагінам. Перед початком зняти Debug-знімки результатів
(JSON) 3 реальних змагань (рогейн, розсіювання, заданий/змішаний) і порівнювати з ними після кожного кроку
(план реалізації, крок 0.1).

1.1. **`DisciplineId` замість `DisciplineType`.**
   - `readonly record struct DisciplineId(string Value)` з константами вбудованих.
   - `EventDay.DefaultDiscipline`, `GroupDaySettings.DisciplineOverride` → `DisciplineId` / `DisciplineId?`;
     value converter у `EventDbContext`. Значення в БД ті самі (перевірити, що `migrations add` не дає змін).
   - `IDisciplineStrategy.Type` → `Id`; `IDisciplineStrategyProvider.For(DisciplineId)`; невідомий id →
     `UnknownDisciplineStrategy` замість винятку.
   - Опції Presentation (`DisciplineTypeOption`, `DisciplineOverrideOption`) будувати з `Provider.All`.

1.2. **Прибрати порівняння з конкретними дисциплінами зі спільного коду.** Нові члени стратегії:
   - `CompetitorModel Competitors` — замість `== Rogaine` у підрахунку, «П/К», протоколі (`:2114`), зчитці (`~4012`).
   - `bool UsesCourseVariants` — замість `== Scatter`.
   - `bool UsesCoursePattern` — **лише Mixed** (прев'ю й валідація шаблону в «Групах» зараз тільки для змішаного;
     увімкнення для Scatter змінило б поведінку).
   - `ControlCount` уже в стратегії — прибрати `discipline == Mixed` біля `:2121`.
   - `_strategies.For(Scatter)` як лічильник КП варіантів (`:565`, `GroupsViewModel:314`) → плоский підрахунок
     (`CourseOrder.CountControls`), бо саме його зараз дає `ScatterStrategy` (не `CoursePatternVariants`).
   - Колонку «Команда» (`UsesParticipantColumn(Team)`) лишити окремим прапорцем.

1.3. **Старт, коригування і ранжування в стратегію.**
   - `ResolveStart(StartContext)` — нинішня логіка `resolvedStart`.
   - `AdjustTime(AdjustContext)` — без змін за замовчуванням.
   - `Rank(RankingContext)` — нинішнє «бали ↓, час ↑, рівні ділять місце»; `RogaineStrategy.Rank` — `RankRogaineTeams`.
   - `AwardsRanks` (Р-7), за замовчуванням `true`.
   - Обчислення дня виноситься з `CompetitionEditorService` в окремий `DayResultsCalculator` (без доступу до БД) з
     етапами (Р-4): (a) учасники змагання за `CompetitorModel`; (b) старт і перевірка (для `TeamWithLegs` —
     послідовно по етапах); (c) `AdjustTime`; (d) `Rank` по групах; (e) очки, розряди.
   - Вхід калькулятора: `EventDaySnapshot`, **розширений** зчитками дня й варіантами розсіювання (одне читання — одна
     транзакція), плюс `DayResultsAppData` з app DB (правила очок, таблиця кваліфікації, розряди, умови розрядів).
     Сьогодні `ComputeDayResultsAsync` читає все це окремими запитами; перехід на знімок заодно прибирає розбіжність
     між рядками протоколу (знімок) і результатами (окремі читання).
   - Очки за формулою беруть **скоригований** час (Р-9); для вбудованих він дорівнює фактичному, тож diff порожній.

1.4. **Розширюваний результат.** `ParticipantDayResult.Extra : IReadOnlyDictionary<string, object?>`,
   командний `TeamDayResult` (час, статус, місце, етапи), `ParticipantDayResult.AdjustedTime`,
   `FinishStatusResult.Missed` (список пропущених КП). Зараз є лише перший пропущений у `Detail`, і жадібний прохід
   після пропуску пропускає решту відміток, тож `Missed` — **новий алгоритм** (Р-52a). Статус і `Detail` лишаються
   такими, як дає нинішній жадібний прохід (diff порожній); `Missed` рахується окремо.

**Критерій:** немає `DisciplineType` і `== <дисципліна>` поза стратегіями; diff знімків порожній.
Оновити розділ «Disciplines» у CLAUDE.md.

## 6. Етап 2 — SDK, завантажувач і плагін «Штраф замість зняття»

Першим плагіном перевіряємо API на особистому форматі — ще до естафети.

- **Проєкт `OrientPyx.Plugins.Sdk`**: контракт (§4.2) і перший набір toolkit (§4.3): `CoursePattern`,
  `CourseMatcher`, `VariantMatcher`, `Punches`, `Ranking`, `Age`, `TextKey`, `TimeFormat`. Перенести з BusinessLogic,
  ядро перевести на них. Оновити CLAUDE.md (§4.1). Diff знімків порожній.

- **Адаптери в BusinessLogic**: `PluginDisciplineStrategy : IDisciplineStrategy` (загортає `Discipline`,
  реалізує `IDisciplineHost` для `base.*` → вбудована стратегія `BasedOn`); захист від винятків плагіна
  (журнал + запасний варіант `BasedOn` + одне повідомлення користувачу).

- **Завантажувач** (`DataAccess/Plugins/`): `IPluginSource` → `DllPluginSource`; сканування трьох папок (§4.5);
  кожен плагін — у своєму `AssemblyLoadContext`, SDK резолвиться зі спільного контексту; перевірка
  `MinApiVersion`, дублі id, винятки в `Register` → плагін пропускається, запис в `IActivityLog`. Програма стартує
  завжди. Виклик при композиції DI до `BuildServiceProvider`.

- **Налаштування дисципліни (прості типи)**: `PluginSettingsRow(EventDayId?, DisciplineId, Json)` (міграція; без дня — область `Competition`, Р-11);
  кнопка «Налаштування дисципліни…» біля випадайки типу на сторінці днів/груп; автоформа для Int/Decimal/Bool/
  Text/Choice; `Setting<T>(key)` читає збережене або дефолт.

- **UI**: випадайки «Тип змагань» показують плагінні дисципліни (з позначкою плагіна); сторінка налаштувань
  «Плагіни» (назва, версія, автор, стан, вимкнути, «Відкрити папку плагінів»); банер «Потрібен плагін `<id>`».
  `.opyx`: список потрібних плагінів у метаданих, попередження при імпорті. Рядки — `uk-UA.json` і `en-US.json`.

- **Структура `plugins/`** (§4.4), Debug-сканування `artifacts\plugins\`, крок у `/new-version`.

- **Плагін «Штраф замість зняття»** (`plugins/MissedControlPenalty`):

```csharp
public sealed class MissedControlPenaltyPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("missed-penalty", "Штраф замість зняття", "1.0");

    public void Register(IPluginBuilder p)
    {
        p.AddDiscipline<SetCoursePenalty>();                 // Р-53
        p.AddDiscipline<MixedPenalty>();
        p.AddStrings("uk-UA", Strings.Uk);                   // назви, підписи налаштувань і колонок
        p.AddStrings("en-US", Strings.En);
    }
}

public sealed class SetCoursePenalty : MissedPenaltyDiscipline
{
    public override string Id => "missed-penalty.set-course";
    public override string Name => "name.setCourse";
    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.SetCourse;
}

public sealed class MixedPenalty : MissedPenaltyDiscipline
{
    public override string Id => "missed-penalty.mixed";
    public override string Name => "name.mixed";
    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.Mixed;
}

public abstract class MissedPenaltyDiscipline : Discipline
{
    public override bool AwardsRanks => false;               // Р-58

    public override IEnumerable<SettingDef> Settings =>
    [
        Setting.Int("minutes", "setting.minutes", defaultValue: 5, min: 0, max: 120),
        Setting.Int("maxMissed", "setting.maxMissed", defaultValue: 3, min: 0, max: 50),
    ];

    public override IEnumerable<ResultColumnDef> Columns =>
    [
        Column.Number("missed", "column.missed", r => r.Get<int?>("missed")),
        Column.Time("penalty", "column.penalty", r => r.Get<TimeSpan?>("penalty")),
    ];

    public override FinishStatusResult EvaluateFinish(FinishContext ctx)
    {
        var r = base.EvaluateFinish(ctx);                    // вбудована перевірка (Р-52)
        if (r.Status != FinishStatus.Mp
            || ctx.FinishTime is null                        // без фінішу — DNF (Р-54)
            || r.Missed.Count > ctx.Setting<int>("maxMissed"))
            return r;
        // Вбудована перевірка повертає MP раніше, ніж дивиться на контрольний час (DNF > MP > OVT),
        // тож OVT для «прощеного» MP перевіряємо тут — за фактичним часом (Р-57).
        return r with { Status = ctx.IsOverTime ? FinishStatus.Ovt : FinishStatus.Ok };   // Р-55
    }

    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        var missed = ctx.Row.Missed.Count;
        var penalty = TimeSpan.FromMinutes(ctx.Setting<int>("minutes")) * missed;
        ctx.Row.Set("missed", missed);
        ctx.Row.Set("penalty", missed > 0 ? penalty : null); // «—», коли штрафу немає
        return ctx.Time + penalty;                           // Р-56
    }
}
```

  Колонки «Пропущено КП» і «Штраф» до етапу 6 (динамічні колонки) видно лише в day-grid; у протоколах — після §10.2.

**Критерій:** DLL у папці → дисципліна в списку; учасник з 1–3 пропущеними КП має OK і час + штраф, з 4+ — MP;
учасник з 2 пропущеними і перевищенням контрольного часу — OVT;
видалення DLL → банер, програма працює; битий DLL → програма стартує; diff знімків вбудованих форматів порожній.

## 7. Етап 3 — додаткові поля учасника та гнучкий імпорт/експорт

- **Модель.** Таблиці `event.db` (міграція `ParticipantCustomFields`):
  - `CustomFieldDef(Key, Label, Kind, OptionsJson, Source, Order)`: `Kind` = Text | Number | Choice | Bool
     (у БД — рядок; `OptionsJson` — варіанти Choice разом із синонімами);
     `Source` = `user` або `plugin:<id>`.
  - `ParticipantFieldValue(ParticipantId, Key, Value)` з каскадним видаленням разом з учасником (пам'ятати про
     `DeleteParticipantAsync`; див. memory «Orphaned ParticipantDay links»).
  - Поля рівня учасника (не дня).

- **UI.**
  - «Додаткові поля» змагання (сторінка «Інформація» або меню колонок мандатки): додати / перейменувати / видалити
     (з підтвердженням), тип, варіанти для Choice.
  - Мандатка та day-grid: колонка на кожне поле (Text/Number — `Text`, Choice — `Combo`, Bool — прапорець);
     ключ для збереження вигляду — `field.<key>`. Пошук і фільтри працюють і для них.
  - Поля плагіна (`AddParticipantField`) з'являються, коли змагання використовує дисципліну цього плагіна.

- **Імпорт CSV/Excel з довільними колонками.**
  - Ціль зіставлення — рядковий ключ (`ImportTarget`): вбудовані (нинішні `CsvParticipantField`), поля естафети
     (`relay.teamNumber`, `relay.teamName`, `relay.leg` — з етапом 4), додаткові поля (`field.<key>`).
  - Пункт **«+ Нове додаткове поле…»** у випадайці зіставлення: створює поле з назвою колонки файлу й одразу зіставляє.
  - Автозіставлення за назвою заголовка враховує назви додаткових полів.
  - **Шаблони зіставлення**: останнє зіставлення для набору заголовків (per competition у `event.db`, засівається
     з app-default).
  - Імпорт лишається в одній транзакції (`ImportParticipantsBatchAsync`).

- **Експорт учасників** (CSV/Excel) і «Відомість»: додаткові поля серед доступних колонок.

- `ParticipantView.Field(key)` у контекстах SDK.

## 8. Етап 4 — естафета (вбудована дисципліна)

### 8.1. Модель даних (міграція `Relay`)

- `RelayTeam(Id, EventDayId, GroupId, Number, Name, ClubId?, RegionId?, OutOfCompetition, StatusOverride?)`;
  унікальний індекс `(EventDayId, Number)`.
- `ParticipantDay`: `RelayTeamId?`, `Leg int?`; унікальний індекс `(RelayTeamId, Leg)`.
- **Номер етапу** `{Number}.{Leg}` (100.1, 100.2, …) обчислюється з `RelayTeam.Number` + `Leg` і показується замість
  `Participant.Number` на днях-естафетах (day-grid, протоколи, зчитка). У `Participant.Number` не пишеться.
- `GroupDaySettings` для естафети: `LegCount`, `MassStartTime?`, `RestartTimes` (JSON: етап → час), `RestartRanking`,
  `CourseAssignmentMode` (етап 5).
- Чип — у кожного етапу свій (`ParticipantDay.Chip`).

### 8.2. Правила

Нормативно — Р-20…Р-39 (§18); тут — огляд.

- **Старт етапу 1** — стартова відмітка чипа, інакше призначений час старту (`ParticipantDay.StartTime`, вручну чи
  жеребкуванням команд — роздільний старт працює без окремої логіки), інакше `MassStartTime` групи (Р-27).
- **Старт етапу N** = фініш етапу N−1 зі зчитки. Стартову станцію бігун не відмічає; якщо відмітив — ігнорується.
- Етап N−1 ще не зчитаний → етап N «очікує» (без часу й статусу).
- **Перезапуск.** `RestartTimes` — на групу, окремо для кожного етапу з 2-го (порожнє — без перезапуску).
  **Старт етапу N = min(фініш N−1, `RestartTime`)**; без фінішу N−1 — `RestartTime`. Без `RestartTime` і без фінішу
  N−1 — етап без часу, команда без результату. Етап з перезапуску позначається («перезапуск»).
- **Команда з перезапуском** ранжується за `RestartRanking`: `Normal` | `AfterClean` (після всіх без перезапуску,
  за замовчуванням) | `Unranked` («П/К»).
- **Час етапу** `Tᵢ = фінішᵢ − стартᵢ`, далі `AdjustTime` дисципліни (для вбудованої — без змін).
- **Перевірка етапу** — за дистанцією етапу (етап 5) засобами toolkit `CourseMatcher`.
- **Час команди** = `Σ AdjustTime(Tᵢ)`.
- **Статус команди**: OK, лише якщо всі `LegCount` етапів OK (і набір закрито в режимі TeamSet); інакше — найгірший
  статус етапу (DSQ > MP > OVT > DNF > DNS > «очікує»). Суддівське перевизначення — на команді й на окремому етапі
  (Р-33, Р-34). Контрольний час — до фактичного часу команди (Р-31).
- **Місце** — у групі за часом команди; рівні ділять місце; «поза конкурсом» — «П/К».
- **Неповна команда** (учасників менше за `LegCount`) — автоматично «поза конкурсом»: час показується, місця немає.
- **Одна людина — один етап**: забезпечено самою моделлю (один `ParticipantDay` на людину на день).
- **Група учасника** на дні-естафеті — група команди (Р-22); **чип** унікальний у межах дня (Р-25).
- **Вимоги до складу** (стать, вік) — поза v1.
- **Очки**: правило очок групи застосовується до місця команди; усі члени отримують однакові очки (вони ж ідуть у
  підсумковий залік, §11.3). **Розряди** для естафет не присвоюються.

### 8.3. UI

- Сторінка «Команди» (день, група естафети): SheetTable команд (номер, назва, клуб, регіон, «П/К») + етапи обраної
  команди; призначення учасника на етап (пошук за ПІБ).
- «Масове створення команд»: N команд від номера X.
- Day-grid: колонки «Команда №», «Етап», «Номер етапу» (100.2), коли день/група — естафета; номер `100.2`
  приймається в пошуку, фільтрі, швидкому знятті (Р-24).
- Жеребкування: одиниця — команда, час отримує 1-й етап (Р-36).
- Зчитка: команда й етап у журналі; спліти етапу за його дистанцією; позначка «перезапуск»; підказка для невідомого
  чипа — вибір команди й етапу.

### 8.4. Протоколи

- **Результати (естафета)**: рядок команди (місце, №, назва, клуб/регіон, час, статус) + рядки етапів (100.1, ПІБ, рік,
  дистанція, час етапу, наростаючий час, статус, колонки дисципліни). Розширення `ResultProtocolDocument` вкладеними
  рядками + прев'ю.
- **Стартовий (естафета)**: команди зі складом за етапами; час старту 1-го етапу.

### 8.5. Імпорт

- **CSV/Excel**: цілі `relay.teamNumber`, `relay.teamName`, `relay.leg`; **рядок на бігуна** (колонки «Номер команди» +
  «Етап», або одна колонка «Номер» у форматі `100.2`, що розбирається автоматично). **Рядок на команду** («Етап 1 ПІБ»,
  «Етап 2 ПІБ»…) — після мінімальної версії. Команди зіставляються за номером, учасники — за наявними правилами
  (Код ФСОУ / ПІБ, `TextKey`).
- **IOF XML v3**: `EntryList` з `TeamEntry` / `TeamEntryPerson` (`Leg`).

## 9. Етап 5 — призначення дистанцій етапам

### 9.1. Дистанції

Дистанції естафети групи на день — іменовані шаблони змішаного типу: `A: <31 32 33 …>`, `B: [5: 41 42 … 50]`,
будь-які поєднання й вкладення (`<31 [2: 41 42 43] 32>`). Окремих «типів етапу» немає. Зберігання — узагальнити
`ScatterVariant` до сутності **`GroupCourseVariant(EventDayId, GroupId, Code, CourseOrder, Order)`** (ті самі поля;
назва `CourseVariant` уже зайнята record-ом розгорнутого порядку в `CoursePatternVariants`), міграція з
перенесенням; один редактор для розсіювання й естафети.

Наявні варіанти розсіювання — плоскі порядки; голий ряд — валідний шаблон (заданий порядок), тож старі дані
лишаються коректними. Але зіставлення зчитки з **шаблоном**-варіантом — нова робота: `VariantMatcher` має йти через
`CourseMatcher` по шаблону, а не розгортати шаблон у порядки (`CoursePatternVariants` обмежений `Limit`, блок
`[N: …]` росте факторіально).

### 9.2. Режими (`CourseAssignmentMode`)

| Режим | Як визначається дистанція бігуна | Приклад |
|---|---|---|
| **ByLeg** | етап i → ключ дистанції | етап 1 → A, етап 2 → B |
| **ByBib** | таблиця «номер етапу → ключ» | 100.1 → A, 101.1 → B…; імпорт з OCAD (IOF `TeamCourseAssignment`) або CSV |
| **TeamSet** | команда має набір дистанцій (мультимножина розміру `LegCount`), кожну — рівно стільки разів, скільки вона в наборі, у будь-якому порядку | **{A, B}**: один бігун заданку, інший вибір |

### 9.3. Алгоритм TeamSet (toolkit `TeamCourseAssignment`)

1. Для кожного зчитаного етапу команди — оцінка відповідності кожній дистанції набору (`CourseMatcher`:
   штраф 0 для OK, інакше кількість `Missed`; Р-42). Це **не** скоринг автовизначення розсіювання.
2. Призначення «етап → дистанція» з мінімальним сумарним штрафом за умови кратностей набору (при `LegCount ≤ 8` —
   перебір перестановок, інакше — угорський алгоритм).
3. Етап перевіряється за призначеною дистанцією; MP на найкращому призначенні → етап MP, команда без місця.
   Незчитані етапи не призначаються; призначення перераховується щоразу з нуля.
4. Призначена дистанція показується в зчитці, протоколі, сплітах («Етап 2 — дистанція B»).

## 10. Етап 6 — плагін «Зважена естафета», колонки, табличні налаштування

10.1. **Табличні налаштування**: `Setting.Table(...)` → модалка із SheetTable; зберігається в `PluginSettingsRow`.

10.2. **Динамічні колонки результатів**: рядкові ключі поряд з enum (`builtin.place`, `field.<key>`,
   `<pluginId>.<key>`) у шаблонах Word-протоколу (+ прев'ю), монітора (`HtmlMonitorWriter`), онлайн-знімка, day-grid;
   рядки етапів естафетного протоколу. Невідомий ключ у збереженому шаблоні ігнорується.

10.3. **Плагін «Зважена естафета»** (`plugins/WeightedRelay`):

```csharp
public sealed class WeightedRelayPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("weighted", "Зважена естафета", "1.0");

    public void Register(IPluginBuilder p)
    {
        p.AddParticipantField("gender", "Стать", FieldKind.Choice(                    // Р-51
            new ChoiceOption("Ч", "M", "Чол", "Чоловіча"),
            new ChoiceOption("Ж", "F", "Жін", "Жіноча")));
        p.AddDiscipline<WeightedRelayDiscipline>();
    }
}

public sealed class WeightedRelayDiscipline : Discipline
{
    public override string Id => "weighted.relay";
    public override string Name => "Зважена естафета (T·K)";
    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.Relay;   // етапи, передача, перезапуск, набір дистанцій
    public override IEnumerable<string> RequiredFields => ["gender"];

    public override IEnumerable<SettingDef> Settings =>
        [Setting.Table("k", "Коефіцієнти", Coefficients.Columns, Coefficients.Default,
                       scope: SettingScope.Competition)];                                  // §14, Р-50

    public override IEnumerable<ResultColumnDef> Columns =>
    [
        Column.Number("k", "K", r => r.Get<double?>("k"), format: "0.00"),
        Column.Time("adj", "T·K", r => r.Get<TimeSpan?>("adj")),
    ];

    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        if (ctx.Participant.BirthYear is not { } year || ctx.Participant.Field("gender") is not { } gender)
        {
            ctx.Row.MarkUnranked("k.missing");                                     // Р-48: команда без місця
            return ctx.Time;
        }
        var table = RangeTable.FromSetting(ctx.Setting<TableValue>("k"));          // toolkit
        var k = table.Lookup(Age.InYear(year, ctx.Date), gender);                  // поза таблицею — найближчий рядок (Р-47)
        var adj = ctx.Time * k;
        ctx.Row.Set("k", k);
        ctx.Row.Set("adj", adj);
        return adj;                       // ядро округлить до секунди (Р-49) і підсумує: час команди = Σ Tᵢ·Kᵢ
    }
}
```

   За потреби той самий плагін реєструє й особистий варіант (`weighted.set-course`, `BasedOn = SetCourse`) з тим
   самим `AdjustTime` — місце за `T·K`.

**Критерій:** команда з двох бігунів (Ч 40 р., Ж 12 р.) на {заданка, вибір} отримує час `T1·0.95 + T2·0.82`;
колонки «K» і «T·K» — у рядках етапів протоколу; правка таблиці K у налаштуваннях одразу перераховує результати.

## 11. Етап 7 — онлайн, монітор, підсумковий протокол

11.1. **Контракт знімка** `docs/online-snapshot.md` (`GetOnlineResultsSnapshotAsync`): команда (місце, №, назва, клуб,
   час, статус) + етапи (100.N, ПІБ, дистанція, час етапу, наростаючий, статус, додаткові колонки). Особисті змагання
   в знімку не змінюються (сумісність із чинним фронтендом).
11.2. **Онлайн-фронтенд** (React, окремий репозиторій) доопрацьовується під команди.
11.3. **Підсумковий протокол**: день-естафета дає в залік очки кожного учасника (однакові для команди); у заліку
   «за часом» день-естафета не враховується.
11.4. **Монітор і HTML-спліти** — естафетний режим (команда + етапи).

## 12. Етап 8 — зручність для автора плагінів

- `docs/plugin-api.md`: контракт, toolkit, приклади (обидва тестові плагіни), правила версіонування.
- Шаблон `dotnet new orientpyx-plugin` (за потреби).
- **Джерело `.cs`-файлів** (`SourcePluginSource`): папка з `.cs` у `plugins/`, компіляція Roslyn (`CSharpCompilation`,
  звичайні класи, не `.csx`) з кешем за хешем файлів; помилки компіляції — на сторінці «Плагіни». Той самий код
  можна зібрати як DLL. Ціна: +15–20 МБ інсталятора — робити лише якщо знадобиться правка без SDK.

## 13. Можливі розширення пізніше

- Перевизначення структури формату плагіном (`CompetitorModel`, колонки групи) — у v1 лише через `BasedOn`.
- Формульна дисципліна без коду (toolkit `Formula` зі змінними віку, статі, додаткових полів).
- Плагінні формати імпорту/експорту, додаткові протоколи.
- Поля рівня дня; вимоги до складу команди.

## 14. Таблиця коефіцієнтів «Зважена естафета» (значення за замовчуванням)

| Вікова група | K (Ч) | K (Ж) |
|---|---|---|
| 8–10 | 0.80 | 0.78 |
| 11–12 | 0.86 | 0.82 |
| 13–14 | 0.92 | 0.84 |
| 15–17 | 0.97 | 0.87 |
| 18–34 | 1.00 | 0.89 |
| 35–39 | 0.98 | 0.87 |
| 40–44 | 0.95 | 0.84 |
| 45–49 | 0.91 | 0.80 |
| 50–54 | 0.88 | 0.76 |
| 55–59 | 0.84 | 0.72 |
| 60–64 | 0.80 | 0.68 |
| 65–69 | 0.76 | 0.63 |
| 70+ | 0.71 | 0.58 |

Вік = рік змагань − рік народження. Час команди = `Σ Tᵢ × Kᵢ`.

## 15. Вирішені питання

| Питання | Рішення |
|---|---|
| Окремий проєкт для плагінів | Так: `OrientPyx.Plugins.Sdk` (контракт + toolkit), п'ятий проєкт, оновити CLAUDE.md |
| DLL чи скрипти | DLL; джерело `.cs`-файлів — опційно на етапі 8 |
| Окремий «коригувальник часу» | Ні: `AdjustTime` у `Discipline` |
| Дистанція етапу | Будь-який шаблон змішаного типу |
| Одна людина на кілька етапів | Ні (забезпечено моделлю) |
| Неповна команда | «Поза конкурсом» |
| Старт 1-го етапу | Стартова відмітка чипа, інакше призначений час старту, інакше масовий старт групи (Р-27) |
| Перезапуск | Так, у v1; на групу й етап; ранжування за `RestartRanking` |
| Номер етапу 100.2 | Обчислюється, не пишеться в `Participant.Number` |
| Вимоги до складу команди | Поза v1 |
| Очки / розряди | Очки — від місця (за скоригованим часом; в естафеті — місце команди, однаково всім); розряди — від фактичного часу, для естафет не присвоюються |
| Вік для K | Рік змагань − рік народження |
| Підсумковий залік | Очки кожного учасника з дня-естафети |
| Онлайн | Фронтенд буде доопрацьовано; спершу контракт знімка |
| Розряди для плагінних дисциплін | Прапорець `AwardsRanks`; «Штраф замість зняття», естафета, «Зважена естафета» — `false` (Р-7) |
| Група учасника на дні-естафеті | Група команди; власна група учасника на цей день не діє (Р-22) |
| Жеребкування естафети | Жеребкуються команди, час отримує 1-й етап (Р-36) |
| Пошук за номером `100.2` | Усюди, де вводять номер, приймається і номер етапу, і номер учасника (Р-24) |
| Один чип на двох етапах | Заборонено: чип унікальний у межах дня (Р-25) |
| Немає статі / року для K | Команда без місця, час показується, з попередженням (Р-48) |
| Вік поза таблицею K | Найближчий рядок (Р-47) |
| Округлення T·K | Кожен етап до секунди, потім сума (Р-49) |
| Таблиця K | Одна на змагання; налаштування мають область «день» або «змагання» (Р-11, Р-50) |
| «Штраф» для змішаного типу | Так, друга дисципліна в тому ж плагіні (Р-53) |
| Повний список пропущених КП | Новий алгоритм максимального збігу (Р-52a); статус і `Detail` — як зараз |
| Дата дня не задана (вік) | Дата першого фінішу дня, інакше поточна (Р-46) |
| Округлення скоригованого часу | Лише коли дисципліна змінює час; вбудовані — без округлення (Р-6) |
| Синоніми значень Choice | `ChoiceOption(Value, Aliases…)` у `FieldKind.Choice` (Р-18, Р-51) |
| Скоринг варіантів | Розсіювання — нинішній (повний збіг > пройдені відмітки > порядок); TeamSet — кількість `Missed` (Р-42) |
| Версія збірки SDK | Явний `AssemblyVersion` у `.csproj` SDK, незалежний від `-p:Version` релізу (§3.10) |

Повний нормативний перелік правил — §18.

## 16. Ризики

- **Обсяг етапу 1 і перенесення в SDK.** `CompetitionEditorService` (~5600 рядків) багато де залежить від
  дисципліни; ризик регресій у рогейні (команди, бонус, П/К) і розсіюванні. Пом'якшення: Debug-знімки результатів і
  diff після кожного кроку.
- **Послідовна оцінка етапів** змінює структуру `ComputeDayResultsAsync`; стежити за продуктивністю (~600 учасників)
  і за правилом «один знімок БД» (memory «Duplicate rows: snapshot race»).
- **Публічний toolkit** — кожен перенесений тип стає API з обов'язками сумісності; переносити лише стабільне.
- **`IDisciplineHost` / `base.*`**: виклик вбудованої стратегії з плагіна має отримувати ті самі дані, що й ядро
  (проблемні КП, варіанти дистанції) — контексти будує ядро, плагін їх лише передає.
- **Онлайн-фронтенд** — окремий застосунок; ризик розсинхрону контракту знімка → `docs/online-snapshot.md`.
- **Мультиінстанс**: плагіни читаються при старті кожного екземпляра; гарячого перезавантаження немає.

## 17. Чекліст

Чекліст і нумерація кроків — у [плані реалізації](plugins-implementation-plan.md) (фази 0–8). Номери пунктів у
розділах 5 і 8–12 цієї спеки (1.1, 8.2, 10.3…) — номери розділів спеки, а не кроків плану.

## 18. Правила (нормативний перелік)

Цей розділ — джерело правди для реалізації. Якщо інший розділ йому суперечить, діє цей. Номери правил (Р-N)
стабільні: на них посилаються код-коментарі й план реалізації.

### 18.1. Загальні

- **Р-1.** Дисципліна групи на день = `GroupDaySettings.DisciplineOverride`, інакше `EventDay.DefaultDiscipline`.
- **Р-2.** Невідома дисципліна (плагіна немає або його вимкнено): відмітки перевіряються як у заданому напрямку,
  статус і час показуються; місць, очок і розрядів немає; банер «Потрібен плагін `<id>`». Збережені налаштування
  й значення полів лишаються як були.
- **Р-3.** Виняток у коді плагіна: для цього виклику береться результат вбудованої дисципліни `BasedOn`; запис у
  журнал дій; банер — один раз на сесію.
- **Р-4.** Порядок обчислення дня: учасники змагання → старт → перевірка відміток → фактичний час → коригування →
  місця → очки → розряди. Кожен крок бачить результат попереднього.
- **Р-5.** Фактичний час = фініш − старт; старт для особистих дисциплін — як зараз (стартова відмітка чипа, інакше
  призначений час старту).
- **Р-6.** Скоригований час = `AdjustTime(фактичний)`. Якщо дисципліна змінила час (результат ≠ фактичного), його
  округлено до цілої секунди (половина — вгору); інакше він дорівнює фактичному **без** округлення (вбудовані
  дисципліни, а також нинішні нічиї й місця не змінюються). `AdjustTime` викликається лише для результату зі статусом OK.
- **Р-7.** Розряди присвоюються лише дисциплінам з `AwardsRanks = true` і рахуються від **фактичного** часу.
  `AwardsRanks = false`: «Штраф замість зняття», естафета, «Зважена естафета» (обидва варіанти).
- **Р-8.** Місце отримують лише результати OK, не «поза конкурсом». Порядок: за скоригованим часом (для бальних —
  бали ↓, потім скоригований час ↑). Однаковий результат — однакове місце, наступне з пропуском (1, 1, 3).
- **Р-9.** Очки — за правилом очок групи: таблиця — від місця; формула — від скоригованого часу учасника й
  скоригованого часу переможця. Без очок: не OK, «поза конкурсом».
- **Р-10.** Суддівське перевизначення статусу переважає будь-яке правило дисципліни чи плагіна.
- **Р-11.** Кожне налаштування дисципліни оголошує область: `Day` (за замовчуванням) — зберігається на пару
  (день, дисципліна); `Competition` — одне значення на змагання для цієї дисципліни (рядок без дня). Немає
  збережених — дефолт налаштування. Зміна налаштувань одразу перераховує всі дні, яких вона стосується.
- **Р-12.** Плагіни з однаковим id: data root перемагає папку програми, папка програми перемагає `artifacts`.
  Вимкнений плагін = відсутній (Р-2). `MinApiVersion` більша за поточну версію API → плагін не вантажиться,
  причину видно на сторінці «Плагіни».

### 18.2. Додаткові поля

- **Р-13.** Поле — рівня учасника: одне значення на всі дні змагання.
- **Р-14.** Ключ поля незмінний після створення (латиниця, цифри, `-`; генерується з назви); назву можна міняти.
- **Р-15.** Видалення поля (з підтвердженням) видаляє всі його значення. Поле плагіна не можна видалити, поки
  дисципліна цього плагіна використовується в змаганні.
- **Р-16.** Видалення учасника видаляє і значення його полів.
- **Р-17.** Поле плагіна створюється, коли дисципліну плагіна вперше вибрано в змаганні, і не зникає, коли її
  прибрали (дані не губляться).
- **Р-18.** Choice: значення порівнюються через `TextKey` без урахування регістру — і з варіантами, і з їхніми
  синонімами (`ChoiceOption.Aliases`); синонім зберігається як `Value` варіанта. Значення поза списком варіантів і
  синонімів (з імпорту, старі дані) зберігається як є і підсвічується червоним.
- **Р-19.** Імпорт: порожня клітинка не стирає наявне значення (як для вбудованих полів). Number приймає кому і
  крапку як десятковий роздільник; не-число обробляється як у Р-18.

### 18.3. Естафета — склад і номери

- **Р-20.** Команда належить одному дню й одній групі. Номер команди — ціле число, унікальне в межах дня.
- **Р-21.** Етапи нумеруються 1…`LegCount`. На етапі — не більше одного учасника; учасник на дні — не більше ніж на
  одному етапі однієї команди.
- **Р-22.** На дні-естафеті група учасника = група його команди; власна група учасника на цей день не діє (у
  day-grid — лише для читання). Учасник групи-естафети без команди не ранжується; він показується окремо як «без команди».
- **Р-23.** Номер етапу `{номер команди}.{етап}` (100.2) обчислюється; `Participant.Number` не змінюється. На
  днях-естафетах у таблицях, протоколах і зчитці показується номер етапу.
- **Р-24.** Скрізь, де вводять номер (пошук, швидке зняття, модалка невідомого чипа, фільтр), приймається і номер
  етапу, і номер учасника; «100» знаходить усю команду.
- **Р-25.** Чип унікальний у межах дня. Призначення чипа, який уже має інший учасник/етап дня, іде через наявне
  підтвердження перепризначення (з попереднього власника чип знімається).
- **Р-26.** Неповна команда (призначено менше учасників, ніж `LegCount`) — автоматично «поза конкурсом».

### 18.4. Естафета — старт, час, статус, місце

- **Р-27.** Старт 1-го етапу: стартова відмітка чипа, інакше призначений `StartTime`, інакше `MassStartTime`
  групи. Нічого немає → етап без часу, команда без результату.
- **Р-28.** Старт етапу N ≥ 2 = фініш етапу N−1. Стартова відмітка етапу N ігнорується.
- **Р-29.** Перезапуск: якщо для етапу N задано `RestartTime`, старт = min(фініш N−1, `RestartTime`); без фінішу
  N−1 — `RestartTime`. Етап, що стартував з перезапуску, позначається «перезапуск», команда — «з перезапуском».
- **Р-30.** Фінішу етапу N−1 немає і `RestartTime` не задано → етап N «очікує» (без часу й статусу), навіть якщо
  сам уже зчитаний.
- **Р-31.** Статус етапу — перевірка відміток за дистанцією етапу правилами заданого/змішаного типу; без фінішу —
  DNF. Контрольний час групи застосовується до фактичного часу команди (фініш останнього етапу − старт першого).
- **Р-32.** Час етапу Tᵢ = фініш − старт; скоригований — за Р-6. Час команди = Σ скоригованих Tᵢ. Наростаючий
  час етапу = сума скоригованих часів до нього включно.
- **Р-33.** Статус команди: перевизначення судді; інакше OK, якщо всі `LegCount` етапів OK (і набір дистанцій
  закрито, Р-41); інакше найгірший статус етапу за порядком **DSQ > MP > OVT > DNF > DNS > «очікує»**.
- **Р-34.** Суддівське перевизначення статусу окремого етапу впливає на статус команди через Р-33.
- **Р-35.** Місце: лише команди зі статусом OK, не «поза конкурсом»; за часом команди; однаковий час ділить місце.
  `RestartRanking`: `Normal` — разом з усіма; `AfterClean` (за замовчуванням) — після всіх команд без перезапуску,
  між собою за часом; `Unranked` — «П/К».

### 18.5. Естафета — жеребкування, очки, залік

- **Р-36.** Жеребкування на дні-естафеті: одиниця — команда; час старту отримує учасник 1-го етапу; розведення —
  за клубом/регіоном команди.
- **Р-37.** Очки: правило очок групи від місця команди (формула — від часу команди); кожен член отримує однакові
  очки; «поза конкурсом» і не OK — без очок.
- **Р-38.** Розряди за естафету не присвоюються.
- **Р-39.** Підсумковий залік: «за очками» — очки учасника з дня-естафети; «за часом» — день-естафета не
  враховується. Стартові внески — на учасника, як зараз.

### 18.6. Дистанції етапів

- **Р-40.** Дистанція — іменований шаблон змішаного типу (група, день).
- **Р-41.** Режими: `ByLeg` — етап i → дистанція; `ByBib` — номер етапу → дистанція; `TeamSet` — набір дистанцій
  команди (або спільний для групи) розміром `LegCount`, кожна дистанція рівно стільки разів, скільки вона є в
  наборі, порядок довільний. Дистанцію не призначено (`ByLeg`/`ByBib`) → етап MP з поясненням «дистанцію не призначено».
- **Р-42.** `TeamSet`: обирається призначення з найменшим сумарним штрафом (штраф етапу на дистанції: 0 для OK,
  інакше кількість пропущених КП за Р-52a). При рівних варіантах етап з меншим номером отримує дистанцію, що стоїть раніше в наборі.
- **Р-43.** Незчитаний етап дистанції не отримує («не визначено»). Призначення перераховується з нуля після кожної зчитки.
- **Р-44.** Призначена дистанція показується в зчитці, сплітах і протоколі («Етап 2 — дистанція B»).

### 18.7. «Зважена естафета»

- **Р-45.** Kᵢ = таблиця K (вік, стать) учасника етапу; скоригований Tᵢ = Tᵢ × Kᵢ.
- **Р-46.** Вік = рік дня змагань − рік народження (`Participant.BirthDate`). Дата дня = `EventDay.Date`; якщо не
  задана — дата найранішого фінішу серед зчиток дня; якщо зчиток немає — поточна дата.
- **Р-47.** Вік менший за перший рядок таблиці → перший рядок; останній рядок відкритий угору (70+).
- **Р-48.** Немає року народження або статі в учасника етапу → K не визначено: команда **без місця й без очок**
  (як «поза конкурсом»), її час рахується з K = 1.00 для такого етапу й показується; клітинка K у day-grid червона,
  у протоколі біля етапу — примітка «K не визначено». Щойно дані заповнено — перерахунок, команда ранжується.
- **Р-49.** Tᵢ × Kᵢ округлюється до секунди (половина — вгору) для кожного етапу окремо; час команди = сума
  округлених значень (сума етапів у протоколі завжди збігається з часом команди).
- **Р-50.** Таблиця K — одна на змагання (налаштування з областю `Competition`, Р-11) для всіх днів і груп з цією
  дисципліною; значення за замовчуванням — §14.
- **Р-51.** Стать — поле `gender` зі значеннями «Ч»/«Ж». Синоніми «M», «Чол», «Чоловіча» → «Ч» і «F», «Жін»,
  «Жіноча» → «Ж» плагін оголошує в `FieldKind.Choice` (Р-18); імпорт і ручне введення їх розпізнають. Особистий варіант `weighted.set-course`: місце за T × K.

### 18.8. «Штраф замість зняття»

- **Р-52.** Пропущений КП — той, якого бракує за вбудованою перевіркою (зокрема взятий не в тому порядку).
  Проблемні (вимкнені) КП не рахуються.
- **Р-52a.** Список пропущених (`FinishStatusResult.Missed`) = обов'язкові КП дистанції, що не ввійшли в
  **максимальний** впорядкований збіг з відмітками (найдовша спільна підпослідовність для заданого порядку /
  варіанта розсіювання; для шаблону змішаного типу — максимальне задоволення шаблону, де блок `[N: …]`, у якому
  взято k < N варіантів, дає N − k пропущених — у `Missed` ідуть невзяті варіанти блоку за порядком запису, перші
  N − k). Статус OK/MP і текст `Detail` (перший пропущений) лишаються від нинішнього жадібного проходу; для OK
  `Missed` порожній.
- **Р-53.** Дві дисципліни в одному плагіні: `missed-penalty.set-course` (на основі заданого) і
  `missed-penalty.mixed` (на основі змішаного типу).
- **Р-54.** Немає фінішу → DNF; штраф цього не змінює.
- **Р-55.** Пропущено ≤ `maxMissed` → OK; більше → MP. `maxMissed = 0` — поведінка вбудованої дисципліни.
- **Р-56.** Скоригований час = фактичний + `minutes` × кількість пропущених КП.
- **Р-57.** Контрольний час (OVT) перевіряється за фактичним часом; штраф до нього не додається. Оскільки вбудована
  перевірка повертає MP раніше, ніж дивиться на OVT, плагін перевіряє OVT сам, коли перетворює MP на OK
  (`FinishContext.IsOverTime`).
- **Р-58.** Місце й очки — за часом зі штрафом; розряди не присвоюються (Р-7). Колонки: «Пропущено КП», «Штраф».
