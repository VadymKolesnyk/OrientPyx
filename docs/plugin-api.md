# OrientPyx — API плагінів (v1)

> Для авторів плагінів-дисциплін. Контракт — збірка `OrientPyx.Plugins.Sdk` (`src/OrientPyx.Plugins.Sdk`), без
> залежностей. Приклади — `plugins/MissedControlPenalty` і `plugins/WeightedRelay`. Правила (Р-N) — §18
> [`plugins-plan.md`](plugins-plan.md). API version: **1**.

## 1. Що може плагін

- додати **дисципліну** (тип змагань) на основі вбудованої (`BasedOn`) і змінити її **правила**: перевірку фінішу,
  спліти, старт, коригування часу, ранжування, присвоєння розрядів;
- оголосити **налаштування** дисципліни (число, десяткове, прапорець, текст, вибір, таблиця) з областю «день» або
  «змагання» — автоформа на сторінці плагіна (меню «Плагіни» → назва плагіна);
- додати **колонки результатів** (day-grid, Word-протокол і прев'ю, монітор, онлайн для етапів естафети);
- оголосити **додаткові поля учасника** (`AddParticipantField`) і їх вимагати (`RequiredFields`);
- дати **переклади** (`AddStrings`).

Чого не може (v1): змінити структуру формату (`CompetitorModel`, колонки групи, бали КП — завжди від `BasedOn`),
додати сторінку UI, формат імпорту/експорту чи протокол. Плагін — довірений код без пісочниці.

## 2. Проєкт

```
plugins/
  OrientPyx.Plugins.sln
  Directory.Build.props      ← net10.0, EnableDynamicLoading, вихід у artifacts/plugins/<Назва>/,
                               ProjectReference на SDK з Private=false + ExcludeAssets=runtime
  MyPlugin/MyPlugin.csproj   ← один рядок: <Project Sdk="Microsoft.NET.Sdk" />
```

Збірка: `dotnet build plugins/OrientPyx.Plugins.sln -c Release` → `artifacts/plugins/MyPlugin/MyPlugin.dll`.
SDK у папку плагіна **не копіюється** — у рантаймі його дає програма (спільний контекст завантаження).
Сторонні NuGet-залежності плагіна лягають поруч з його DLL і резолвляться `AssemblyDependencyResolver`.

## 3. Де програма шукає плагіни

Одна папка — `<data root>/plugins/` (кнопка «Відкрити папку плагінів» на сторінці «Плагіни»; переживає
оновлення). Туди кладуть і власні плагіни, і туди ж програма на старті **синхронізує стандартні**: з
`<папка програми>/plugins/` (їх збирає й кладе в пакет `build/publish.ps1`) і, у Debug-збірці, з
`<repo>/artifacts/plugins/`. Файл копіюється, коли його там нема або джерело новіше (за часом зміни) — тож оновлення
програми оновлює стандартні плагіни, а новіша власна збірка з тією ж назвою папки не перезаписується. Заблокований
файл (працює інший екземпляр) пропускається із записом у журнал — завантажиться попередня версія.

Розкладка: `<папка>/<Назва>/<Назва>.dll`. Однаковий `Info.Id` у двох папках — перемагає та, що далі за абеткою
(Р-12). Знайдений плагін **вимкнений**, доки його не ввімкнуть на сторінці «Плагіни →
Керування плагінами…»; вмикання й вимикання діють одразу, а ввімкнений плагін отримує власний пункт у меню
«Плагіни» — сторінку своїх налаштувань. Новий або оновлений DLL — після перезапуску (гарячого перезавантаження немає). Битий DLL, виняток у `Register`, `MinApiVersion > ApiVersion.Current`
або дубль id → плагін пропускається, причина — на сторінці «Плагіни» і в журналі дій; програма стартує завжди.

## 4. Контракт

```csharp
public sealed class MyPlugin : IOrientPyxPlugin                // один публічний клас з конструктором без параметрів
{
    public PluginInfo Info => new("my", "Мій плагін", "1.0", MinApiVersion: 1, Author: "…", Description: "…");

    public void Register(IPluginBuilder p)
    {
        p.AddDiscipline<MyDiscipline>();
        p.AddParticipantField("gender", "Стать", FieldKind.Choice(new ChoiceOption("Ч", "M"), new ChoiceOption("Ж", "F")));
        p.AddStrings("uk-UA", new Dictionary<string, string> { ["name"] = "Моя дисципліна" });
        p.AddStrings("en-US", new Dictionary<string, string> { ["name"] = "My discipline" });
    }
}
```

`Info.Id` — стабільний: ним префіксуються id дисциплін (`"my.something"`, зберігаються в змаганні), ключі рядків
і колонок.

### 4.1. `Discipline`

| Член | За замовчуванням | Призначення |
|---|---|---|
| `Id`, `Name` | — | id `"<pluginId>.<name>"`; `Name` — ключ рядків плагіна або текст |
| `BasedOn` | `SetCourse` | `SetCourse`, `ScoreByCount`, `ScoreByTime`, `Rogaine`, `Mixed`, `Scatter`, `Relay` |
| `EvaluateFinish(FinishContext)` | `BasedOn` | статус DNF/MP/OVT/OK + `Detail` + `Missed` |
| `BuildSplits(SplitsContext)` | `BasedOn` | спліти під журналом зчитки |
| `ResolveStart(StartContext)` | `BasedOn` | де починається час (для етапів естафети ≥ 2 старт задає ядро, Р-28) |
| `AdjustTime(AdjustContext)` | без змін | коригування часу OK-результату (штраф, коефіцієнт) |
| `Rank(RankingContext)` | `BasedOn` | місця в групі (для естафети — команди, `ctx.Teams`) |
| `AwardsRanks` | `BasedOn` | чи можуть результати давати розряд (Р-7) |
| `Columns`, `Settings`, `RequiredFields` | порожні | колонки результатів, налаштування, потрібні поля |

`base.X(ctx)` викликає справжню вбудовану стратегію `BasedOn` з **тими самими** контекстами, що побудувало ядро
(проблемні КП уже прибрано, варіанти/дистанції етапу вже підставлено).

**Порядок обчислення дня (Р-4):** учасники змагання → старт → перевірка відміток → фактичний час →
`AdjustTime` (лише для OK, Р-6) → `Rank` → очки (формула — від скоригованого часу, Р-9) → розряди (від
фактичного часу, лише при `AwardsRanks`). Змінений `AdjustTime` час ядро округлює до секунди (половина — вгору);
незмінений лишається як є. Для естафети `AdjustTime` викликається на кожен OK-етап (`ctx.Leg` заповнено), час
команди = Σ скоригованих (Р-32). Суддівське перевизначення статусу завжди переважає (Р-10).

### 4.2. Контексти

- **`FinishContext`** — `ExpectedControls` (без старт/фініш і проблемних КП), `CourseOrderText` (шаблон),
  `PunchedControls`, `StartTime`, `FinishTime`, `TimeLimit`, `IsOverTime` (фактичний час > контрольного),
  `RequiredControlCount`, `CourseVariants`, `Setting<T>(key)`.
- **`StartContext`** — `ChipStart`, `AssignedStart`, `FinishTime`.
- **`AdjustContext`** — `Participant` (`ParticipantView`), `Time` (фактичний; для етапу — час етапу), `Date`
  (дата дня; не задана — дата найранішого фінішу дня, інакше сьогодні, Р-46), `Leg`, `Row`, `Setting<T>(key)`.
- **`RankingContext`** — `Group`, `Competitors`, `Finished` (OK, не «П/К», не `MarkUnranked`), `Teams`
  (естафета), `RankBy(IComparer<ResultRow>)` (рівні ділять місце, наступне — з пропуском), `Setting<T>`.
- **`ResultRow`** — `Participant`, `Status`, `Time`, `AdjustedTime`, `Score`, `Place`, `OutOfCompetition`,
  `Punches`, `Missed`, `Set(key, value)` / `Get<T>(key)` (значення для колонок), `MarkUnranked(reason)` — результат
  без місця й очок (для етапу естафети — уся команда, Р-48); `reason` — ключ рядків плагіна.
- **`TeamCompetitor`** — `Row` (результат команди), `Number`, `Name`, `Members` (рядки етапів), `Restarted`.
- **`ParticipantView`** — `FullName`, `Number`, `BirthYear`, `Club`, `Region`, `Team`, `Field(key)`.

### 4.3. Налаштування

```csharp
public override IEnumerable<SettingDef> Settings =>
[
    Setting.Int("minutes", "setting.minutes", defaultValue: 5, min: 0, max: 120),
    Setting.Decimal("rate", "setting.rate", 1.5m),
    Setting.Bool("strict", "setting.strict"),
    Setting.Text("note", "setting.note"),
    Setting.Choice("mode", "setting.mode", ["a", "b"]),
    Setting.Table("k", "setting.k", columns, TableValue.FromRows(columns, ["18–34", "1.00"]),
                  scope: SettingScope.Competition),
];
```

- `SettingScope.Day` (за замовчуванням) — значення на пару (день, дисципліна); `Competition` — одне на змагання
  (Р-11). Немає збереженого — дефолт. Збереження одразу перераховує результати всіх днів, яких воно стосується.
- Читання: `ctx.Setting<int>("minutes")`, `ctx.Setting<TableValue>("k")`.
- `TableValue` — рядки клітинок за ключами колонок (`TableColumnDef.Text/Number`); `Cell(row, col)`,
  `Number(row, col)` (кома й крапка). Редагується в окремій модалці-таблиці.

### 4.4. Колонки результатів

```csharp
public override IEnumerable<ResultColumnDef> Columns =>
[
    Column.Number("k", "column.k", r => r.Get<double?>("k"), format: "0.00", problem: r => r.Get<bool>("k.missing")),
    Column.Time("adj", "column.adj", r => r.Get<TimeSpan?>("adj")),
    Column.Text("note", "column.note", r => r.Get<string>("note")),
];
```

Значення беруться з `ResultRow.Set(...)`. `problem` — клітинка червона. Ключ колонки в шаблонах протоколів і
монітора — `"<pluginId>.<key>"`; колонка з'являється в редакторах колонок, лише коли день використовує дисципліну
плагіна. Невідомий збережений ключ ігнорується (і не губиться при повторному збереженні).

### 4.5. Додаткові поля

`AddParticipantField(key, label, kind)` — поле рівня учасника (Р-13), створюється в змаганні, коли дисципліну
плагіна вперше вибрано (Р-17), і не зникає разом з нею. `FieldKind.Text | Number | Bool | Choice(options)`;
`ChoiceOption(Value, params Aliases)` — синоніми, які імпорт і ручне введення зводять до `Value` (Р-18; `TextKey`,
без регістру). Значення поза списком зберігається як є і підсвічується червоним. Поле видно в мандатці, day-grid,
імпорті CSV/Excel, експорті, «Відомості».

### 4.6. Локалізація

`AddStrings(culture, dict)` реєструє рядки під префіксом `<pluginId>.`. `Name`, підписи налаштувань, колонок і
причини `MarkUnranked` шукаються як ключі; немає перекладу в поточній мові → en-US → сам текст ключа (тож простий
плагін може писати текст напряму).

### 4.7. Помилки в коді плагіна

Кожен виклик плагіна обгорнуто: виняток → для цього виклику береться результат вбудованої `BasedOn`, запис у журнал
дій, один банер на сесію «Плагін `<id>` дав помилку…» (Р-3). Відсутній або вимкнений плагін, чию дисципліну вибрано
в змаганні, → `UnknownDisciplineStrategy`: перевірка як у заданому, без місць/очок/розрядів, банер «Потрібен
плагін `<id>`» (Р-2); налаштування й значення полів зберігаються. `.opyx` записує потрібні плагіни в `plugins.json`;
імпорт попереджає про відсутні.

## 5. Toolkit (`OrientPyx.Plugins.Sdk.Toolkit`)

Ті самі реалізації, що використовує ядро:

| Тип | Що робить |
|---|---|
| `Patterns.CoursePattern` | розбір/валідація шаблонів змішаного типу (`<…>` ряд, `[N: …]` вибір, вкладення) |
| `CourseOrder` | `CountControls` / `CountCourseControls` — плоский підрахунок КП |
| `CourseMatcher` | `MatchOrder`, `MatchPattern` → `MatchResult {Ok, FirstMissing, Missed}`; `AlignOrder` для сплітів |
| `MissedControls` | повний список пропущених (LCS / максимальне задоволення шаблону, Р-52a) |
| `VariantMatcher` | `Best(variants, punched)` — автовизначення варіанта розсіювання |
| `TeamCourseAssignment` | `Assign(penalties)` — призначення «етап → дистанція» з мінімальним штрафом |
| `Punches` | `Split`, `WithoutStartFinish`, `WithoutDisabled` |
| `Ranking` | `AssignPlaces`, `ByTime`, `ByScoreThenTime` |
| `Age` | `InYear(birthYear, date)` = рік дати − рік народження |
| `RangeTable` | `FromSetting(TableValue, "range")`, `Lookup(age, key)` — «діапазон × ключ → значення»; поза діапазоном — найближчий рядок, останній відкритий угору (Р-47) |
| `RelayBib` | `Format(100, 2)` = «100.2», `TryParse`, `IsLegNumber` |
| `SiCard` | `Identify(number)` → модель картки SportIdent і місткість (відміток) за діапазоном номера; `TooSmall(number, punches)` |
| `TextKey` | порівняння назв зі схожими літерами (M12 = М12) |
| `TimeFormat` | формати часу як у протоколах |

## 6. Версіонування

- `ApiVersion.Current` = 1. Мінорні зміни SDK лише додають; несумісні — нова мажорна версія збірки.
- Плагін оголошує `PluginInfo.MinApiVersion`; більший за поточний → не вантажиться.
- `AssemblyVersion` SDK задано **явно** (`1.0.0.0`) у його `.csproj` і не залежить від `-p:Version` релізу —
  інакше плагін, зібраний проти SDK 1.0.0, падав би з `FileLoadException`.

## 7. Приклади

### «Штраф замість зняття» (`plugins/MissedControlPenalty`)

Дві дисципліни (`missed-penalty.set-course`, `missed-penalty.mixed`, Р-53). `EvaluateFinish`: вбудована перевірка;
MP з фінішем і `Missed.Count ≤ maxMissed` → OK (або OVT, якщо `ctx.IsOverTime`, Р-57). `AdjustTime`: час +
`minutes × Missed.Count` (Р-56), колонки «Пропущено КП» і «Штраф». `AwardsRanks => false`.

```csharp
public override FinishStatusResult EvaluateFinish(FinishContext ctx)
{
    var r = base.EvaluateFinish(ctx);
    if (r.Status != FinishStatus.Mp || ctx.FinishTime is null || r.Missed.Count > ctx.Setting<int>("maxMissed"))
        return r;
    return r with { Status = ctx.IsOverTime ? FinishStatus.Ovt : FinishStatus.Ok };
}

public override TimeSpan AdjustTime(AdjustContext ctx)
{
    var missed = ctx.Row.Missed.Count;
    var penalty = TimeSpan.FromMinutes(ctx.Setting<int>("minutes")) * missed;
    ctx.Row.Set("missed", missed);
    ctx.Row.Set("penalty", missed > 0 ? penalty : null);
    return ctx.Time + penalty;
}
```

### «Зважена естафета» (`plugins/WeightedRelay`)

Одна дисципліна `weighted.relay` (`BasedOn = Relay`). Поле `gender` (Choice «Ч»/«Ж»
із синонімами, Р-51), таблиця K (`Setting.Table`, область `Competition`, Р-50). `AdjustTime`: `K =
RangeTable.FromSetting(table).Lookup(Age.InYear(year, ctx.Date), gender)`, час × K; немає статі/року →
`MarkUnranked("k.missing")`, K = 1 (Р-48). Час команди = Σ round(Tᵢ·Kᵢ) (Р-49).

## 8. Не зроблено (за потреби)

- шаблон `dotnet new orientpyx-plugin`;
- джерело `.cs`-файлів, що компілює Roslyn (`SourcePluginSource`) — абстракція `IPluginSource` уже є;
- перевизначення структури формату, плагінні сторінки UI, формати імпорту/експорту.
