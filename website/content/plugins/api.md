# Довідник API

Повний перелік типів SDK плагінів — збірки `OrientPyx.Plugins.Sdk` (у репозиторії — `src/OrientPyx.Plugins.Sdk`).
Збірка не має жодних залежностей і не знає ні про базу даних, ні про файли, ні про інтерфейс: програма сама
готує дані, викликає правило плагіна й показує результат. Покроковий приклад — у
[Перший плагін крок за кроком](first-plugin.md), справжні плагіни — у [Розбір стандартних плагінів](examples.md).

Простори імен:

| Простір імен | Що в ньому |
|---|---|
| `OrientPyx.Plugins.Sdk` | контракт плагіна, `Discipline`, контексти правил, налаштування, колонки, поля, спільні прості типи |
| `OrientPyx.Plugins.Sdk.Toolkit` | готові алгоритми, якими користується й сама програма |
| `OrientPyx.Plugins.Sdk.Toolkit.Patterns` | розбір і перевірка шаблонів змішаного напрямку |

Версія API — `ApiVersion.Current` = 1. Що з цього випливає для сумісності — у [Поширення і версії](sharing.md#versions).

## Плагін {#plugin}

### `IOrientPyxPlugin`

Точка входу. У збірці плагіна має бути публічний неабстрактний клас із конструктором без параметрів, що реалізує
цей інтерфейс; програма бере перший такий клас і ігнорує решту.

| Член | Опис |
|---|---|
| `PluginInfo Info { get; }` | паспорт плагіна; читається одразу після створення класу |
| `void Register(IPluginBuilder builder)` | викликається один раз під час запуску програми — для кожного знайденого плагіна, увімкненого чи ні |

Виняток у `Register` або в `Info` — плагін не завантажується, причину видно на сторінці «Плагіни»
(див. [Налагодження](debugging.md#load-errors)).

### `PluginInfo`

`record PluginInfo(string Id, string Name, string Version, int MinApiVersion = 1, string? Author = null, string? Description = null)`

| Параметр | Опис |
|---|---|
| `Id` | стабільний ідентифікатор плагіна; не порожній. Ним програма позначає рядки, колонки й (за домовленістю) види змагань плагіна. Пишіть латиницею, цифрами й дефісом, **без крапок**: банер «Потрібен плагін» бере ідентифікатор плагіна з `Id` виду змагань до першої крапки |
| `Name` | назва на сторінці «Плагіни» і в меню «Плагіни». Звичайний текст — не перекладається |
| `Version` | довільний рядок версії, видно на сторінці «Плагіни» |
| `MinApiVersion` | найменша версія API, з якою плагін працює. Більша за `ApiVersion.Current` — плагін не завантажується |
| `Author`, `Description` | необов'язкові; звичайний текст на сторінці «Плагіни» |

### `IPluginBuilder`

Те, що плагін повідомляє програмі з `Register`. Реалізує програма.

| Метод | Опис |
|---|---|
| `AddDiscipline<T>() where T : Discipline, new()` | додає вид змагань. Програма одразу створює екземпляр `T`; порожній `Id` або той самий `Id` двічі в одному плагіні — помилка завантаження |
| `AddParticipantField(string key, string label, FieldKind kind)` | оголошує додаткове поле учасника (див. [Додаткові поля](#fields)); порожній `key` — помилка завантаження |
| `AddStrings(string culture, IReadOnlyDictionary<string, string> strings)` | написи однією мовою: `"uk-UA"` або `"en-US"` (див. [Написи](#strings)). Можна викликати кілька разів — ключі зливаються |

### `ApiVersion`

`public const int Current = 1` — версія API, яку надає програма. Нові можливості SDK лише додаються; несумісна
зміна підняла б цю версію й версію збірки SDK.

### `ParticipantFieldDef`

`record ParticipantFieldDef(string Key, string Label, FieldKind Kind)` — так програма запам'ятовує поле,
оголошене через `AddParticipantField`. Самому плагіну створювати його не потрібно.

## Вид змагань: `Discipline` {#discipline}

`abstract class Discipline` — вид змагань плагіна. Усе, що не перевизначено, працює як у виду змагань-основи
(`BasedOn`). Будова формату — команди, етапи, колонки груп, бали КП — завжди від основи; плагін змінює правила.

| Член | За замовчуванням | Опис |
|---|---|---|
| `string Id` | — (обов'язковий) | стабільний ідентифікатор, за домовленістю `"<Id плагіна>.<назва>"`. Зберігається в змаганні — не змінюйте його |
| `string Name` | — (обов'язковий) | назва у списках «Вид змагань»: ключ рядків плагіна або текст. Програма дописує до неї «(плагін)» |
| `BuiltinDiscipline BasedOn` | `SetCourse` | основа |
| `FinishStatusResult EvaluateFinish(FinishContext ctx)` | як в основи | статус однієї зчитки: OK, MP, OVT, DNF… |
| `SplitsView BuildSplits(SplitsContext ctx)` | як в основи | спліти однієї зчитки; для основ із балами КП — ще й «Бали» |
| `DateTimeOffset? ResolveStart(StartContext ctx)` | як в основи | момент, від якого рахується час |
| `TimeSpan AdjustTime(AdjustContext ctx)` | без змін | скоригований час OK-результату: штраф, коефіцієнт, фора |
| `void Rank(RankingContext ctx)` | як в основи | місця в групі |
| `bool AwardsRanks` | як в основи | чи можуть результати давати виконаний розряд |
| `IEnumerable<ResultColumnDef> Columns` | порожньо | власні колонки результатів |
| `IEnumerable<SettingDef> Settings` | порожньо | власні налаштування |
| `IEnumerable<string> RequiredFields` | порожньо | ключі полів учасника, які потрібні виду змагань. У таблицях учасників порожнє таке поле червоніє в учасників груп із цим видом змагань; на результати це не впливає. Саме поле однаково треба оголосити через `AddParticipantField` |
| `void Attach(IDisciplineHost host)` | — | службовий: програма під'єднує вбудовану основу. Не викликайте |

Виклик `base.EvaluateFinish(ctx)` (і так само для інших правил) запускає справжню вбудовану основу з тими самими
контекстами, що побудувала програма: проблемні КП уже прибрано, варіант розсіювання чи дистанцію етапу вже
підставлено. Типовий прийом — викликати основу й змінити її відповідь:

```csharp
public override FinishStatusResult EvaluateFinish(FinishContext ctx)
{
    var r = base.EvaluateFinish(ctx);   // вбудована перевірка
    // Один пропущений КП прощаємо; контрольний час основа після MP вже не перевіряє — перевіряємо самі.
    return r.Status == FinishStatus.Mp && r.Missed.Count <= 1 && ctx.FinishTime is not null
        ? r with { Status = ctx.IsOverTime ? FinishStatus.Ovt : FinishStatus.Ok }
        : r;
}
```

`FinishContext` і `SplitsContext` — записи (`record`), тож основу можна запустити й на зміненому контексті:
`base.EvaluateFinish(ctx with { ExpectedControls = … })`.

!!! warning "Властивості читаються один раз"
    `Name`, `Settings`, `Columns`, `RequiredFields` і `AwardsRanks` програма зчитує один раз, коли завантажує
    плагін, і пам'ятає до перезапуску. Вони мають повертати сталі значення, а не залежати від даних змагання.

!!! warning "Без стану, швидко, однаково"
    Екземпляр виду змагань один на весь час роботи програми, і його методи можуть викликатися з різних потоків
    одночасно. Результати не зберігаються — програма перераховує їх щоразу, коли сторінка чи протокол їх
    показує, на кожному комп'ютері окремо. Тому правила мають бути швидкими, без побічних ефектів і
    детермінованими: ті самі дані — той самий результат. Не тримайте стан у полях класу, не звертайтеся до
    файлів чи мережі.

### `BuiltinDiscipline`

Основа виду змагань плагіна.

| Значення | У програмі | Особливості для плагіна |
|---|---|---|
| `SetCourse` | «Заданий напрямок» | — |
| `ScoreByCount` | «За вибором по кількості КП» | `FinishContext.RequiredControlCount` — «Мін. КП» групи |
| `ScoreByTime` | «За вибором за часом» | КП мають бали; «Бали» = `BuildSplits(...).TotalPoints`; `EvaluateFinish` основи — OK з фінішем, DNF без нього (запізнення лише знімає бали, OVT не буває) |
| `Rogaine` | «Рогейн» | команди (`CompetitorModel.Team`); «Бали» = `BuildSplits(...).TotalPoints`; перевизначений `Rank` **не викликається** — місця команд рахує вбудований рогейн |
| `Mixed` | «Змішаний» | дистанція — шаблон у `FinishContext.CourseOrderText` |
| `Scatter` | «Розсіювання» | варіанти дистанції в `FinishContext.CourseVariants` |
| `Relay` | «Естафета» | естафетні команди (`CompetitorModel.TeamWithLegs`), `AwardsRanks` = false; правила викликаються для кожного етапу |

### `CompetitorModel`

Кому належить результат — визначається основою, плагін його не змінює. `Individual` — кожен учасник окремо
(усі основи, крім двох нижче); `Team` — учасники з однаковою командою в групі рахуються разом, без етапів
(«Рогейн»); `TeamWithLegs` — естафета: етап N стартує з фінішу етапу N−1, час команди — сума часів етапів.

### `IDisciplineHost`

Вбудована основа, до якої ведуть виклики `base.…` у `Discipline`. Члени повторюють правила `Discipline`:
`EvaluateFinish`, `BuildSplits`, `ResolveStart`, `AdjustTime`, `Rank`, `AwardsRanks`. Реалізує програма; плагіну
реалізовувати не треба. Якщо створити вид змагань вручну (наприклад, у власному модульному тесті) й не під'єднати
основу, виклик `base.…` кине `InvalidOperationException`.

### Порядок обчислення дня {#pipeline}

1. Для кожного учасника із зчиткою: `ResolveStart` → `EvaluateFinish`. Якщо старту немає (`ResolveStart`
   повернув null, а для етапу естафети старт етапу невідомий), програма ставить DNS і `EvaluateFinish` не
   викликає. Статус, поставлений суддею вручну, замінює те, що повернув `EvaluateFinish`. Фактичний час = фініш − старт, лише для OK.
2. Для основ із балами КП — «Бали»: `TotalPoints` з `BuildSplits`.
3. `AdjustTime` — для кожного результату OK з часом (зокрема поза конкурсом). Змінений час програма округлює до
   цілої секунди (половина — вгору); незмінений лишає як є.
4. `Rank` — один раз на кожну групу дня.
5. «Очки» за правилом групи: таблиця — від місця, формула — від **скоригованого** часу.
6. Розряди — лише якщо `AwardsRanks`, від **фактичного** часу.

Естафета: етапи кожної команди проходять по черзі. `ResolveStart` викликається лише для першого етапу
(`AssignedStart` — старт учасника або масовий старт групи); старт наступних етапів задає програма — фініш
попереднього етапу або перезапуск. `EvaluateFinish` отримує дистанцію етапу, без контрольного часу (він діє на
команду). `AdjustTime` — для кожного OK-етапу; час команди = сума скоригованих часів етапів.

## Перевірка відміток {#finish}

### `FinishContext`

`sealed record` — усе, що потрібно, щоб визначити статус однієї зчитки.

| Властивість | Опис |
|---|---|
| `IReadOnlyList<string> ExpectedControls` | коди КП, які треба взяти, у заданому порядку: без старту, фінішу й проблемних КП. Для етапу естафети — дистанція етапу |
| `string CourseOrderText` | порядок КП групи як введено — для шаблонів змішаного напрямку; порожній, якщо не задано |
| `IReadOnlyList<string> PunchedControls` | відмітки чіпа в порядку зчитки, без старту й фінішу (проблемні КП лишаються) |
| `DateTimeOffset? StartTime` | старт, який повернув `ResolveStart` (для естафети — старт етапу). Без старту `EvaluateFinish` не викликається — програма сама ставить DNS |
| `DateTimeOffset? FinishTime` | фініш зі зчитки; null — відмітки фінішу немає |
| `TimeSpan? TimeLimit` | контрольний час групи на день; null — немає (і завжди null для етапу естафети) |
| `int? RequiredControlCount` | «Мін. КП» групи для «За вибором по кількості КП»; інші ігнорують |
| `IReadOnlyList<CourseVariantData> CourseVariants` | варіанти розсіювання групи; порожньо для інших |
| `SettingValues Settings`, `T Setting<T>(string key)` | налаштування виду змагань на цей день |
| `bool IsOverTime` | true, коли контрольний час задано, старт і фініш відомі, і фактичний час більший за контрольний |

### `FinishStatusResult`

`readonly record struct FinishStatusResult(FinishStatus Status, string Detail, FinishDetailKind DetailKind = FinishDetailKind.MissingControl)`

| Член | Опис |
|---|---|
| `Status` | статус |
| `Detail` | коротке пояснення в підказці біля статусу, наприклад код пропущеного КП; порожньо, якщо нема що додати |
| `DetailKind` | як сформулювати `Detail` |
| `IReadOnlyList<string> Missed { get; init; }` | **усі** пропущені обов'язкові КП (за найбільшим збігом із відмітками), не лише перший. Вбудовані основи заповнюють його для MP на впорядкованій дистанції (заданий, змішаний, розсіювання); інакше порожньо |
| `static FinishStatusResult Of(FinishStatus status)` | результат без пояснення |

`r with { Status = … }` зберігає `Missed`. Програма передає `Missed` далі в `ResultRow.Missed`, тож
`AdjustTime` може нарахувати штраф за кожен пропущений КП.

### `FinishStatus`

`None` — статусу немає; `Ok` — дистанцію пройдено; `Mp` — бракує КП або порушено порядок; `Ovt` — перевищено
контрольний час; `Dnf` — немає фінішу; `Dns` — не стартував (програма ставить його сама, коли старт невідомий,
або суддя вручну); `Dsq` — дискваліфіковано, зазвичай ставить суддя вручну.

Час і місце отримує лише `Ok` з відомими стартом і фінішем.

### `FinishDetailKind`

`MissingControl` — `Detail` називає пропущений КП («бракує КП …»); `ControlCount` — «взято N з M» для «За вибором
по кількості КП»; `CourseNotAssigned` — етапу естафети не призначено дистанцію.

### `CourseVariantData`

`record CourseVariantData(string Code, IReadOnlyList<string> Controls, string CourseOrderText = "")` — одна названа
дистанція групи: варіант розсіювання або дистанція етапу естафети.

| Член | Опис |
|---|---|
| `Code` | код для показу, наприклад `"A"` |
| `Controls` | обов'язкові КП, підготовлені як `ExpectedControls` |
| `CourseOrderText` | порядок як введено |
| `bool IsPattern` | true, якщо це шаблон змішаного напрямку (є `<…>` або `[N: …]`) |
| `static bool IsPatternText(string? courseOrder)` | те саме для довільного тексту |
| `CoursePattern Pattern()` | розібраний шаблон (звичайний порядок — як один упорядкований ряд) |

## Старт: `StartContext` {#start}

| Властивість | Опис |
|---|---|
| `DateTimeOffset? ChipStart` | стартова відмітка чіпа; null — не відмічався |
| `TimeSpan? AssignedStart` | призначений час старту (час доби з жеребкування чи введений вручну; для першого етапу естафети — масовий старт групи); null — немає |
| `DateTimeOffset? FinishTime` | фініш зі зчитки — дає дату для `AssignedStart` |

Вбудовані основи повертають `ChipStart`, а якщо його немає — `AssignedStart` на дату фінішу. У `StartContext`
немає налаштувань виду змагань.

## Коригування часу: `AdjustContext` {#adjust}

| Властивість | Опис |
|---|---|
| `ParticipantView Participant` | учасник |
| `TimeSpan Time` | фактичний час; для етапу естафети — час етапу |
| `DateOnly Date` | дата дня змагань; не задана — дата найранішого фінішу дня; немає зчиток — сьогодні |
| `int? Leg` | номер етапу естафети; null для особистого результату |
| `ResultRow Row` | результат, який коригується: сюди кладуть значення для колонок і `MarkUnranked` |
| `SettingValues Settings`, `T Setting<T>(string key)` | налаштування виду змагань на цей день |

Виняток в `AdjustTime` — час лишається без змін.

### `ParticipantView`

Учасник лише для читання: `FullName`, `Number` (номер учасника), `int? BirthYear` (null — дата народження
невідома), `Club`, `Region`, `Team`, `IReadOnlyDictionary<string, string?> Fields` (додаткові поля за ключем) і
`string? Field(string key)` — значення поля, обрізане від пробілів; null, якщо поля немає або воно порожнє.

### `ResultRow`

Результат одного учасника (або команди естафети) так, як його бачать правила.

| Член | Опис |
|---|---|
| `ParticipantView? Participant` | учасник; null для рядка команди естафети |
| `FinishStatus Status` | статус |
| `TimeSpan? Time` | фактичний час; null, якщо не OK |
| `TimeSpan? AdjustedTime` | час, за яким ставлять місця (після `AdjustTime`) |
| `int? Score` | «Бали» для основ із балами; інакше null |
| `int? Place { get; set; }` | місце; записує `Rank` |
| `bool OutOfCompetition` | поза конкурсом |
| `IReadOnlyList<string> Punches` | відмічені коди в порядку чіпа, без старту й фінішу (заповнено в `AdjustTime`) |
| `IReadOnlyList<string> Missed` | пропущені КП з `FinishStatusResult.Missed`; порожньо, якщо статус поставив суддя |
| `void Set(string key, object? value)` | зберегти значення для колонок результатів |
| `T? Get<T>(string key)` | прочитати збережене значення (з перетворенням числових типів); default, якщо немає |
| `IReadOnlyDictionary<string, object?> Values` | усі збережені значення |
| `void MarkUnranked(string reason)` | результат лишається з часом, але без місця й очок; для етапу естафети — уся команда. `reason` — ключ рядків плагіна, перекладений текст друкується в протоколі після прізвища |
| `string? UnrankedReason` | причина з `MarkUnranked`; null — результат ранжується |
| `ResultRow()`, `ResultRow(IDictionary<string, object?> values)` | конструктори — знадобляться хіба у власних тестах |
| `Guid Id` | службовий ключ програми |

Кладіть у `Set` прості значення: числа, рядки, `bool`, `TimeSpan`.

## Місця: `RankingContext` {#rank}

| Член | Опис |
|---|---|
| `string Group` | назва групи |
| `IReadOnlyList<ResultRow> Competitors` | учасники групи (див. нижче, хто саме) |
| `IReadOnlyList<ResultRow> Finished` | ті, кому можна дати місце: OK, не поза конкурсом, без `MarkUnranked` |
| `IReadOnlyList<TeamCompetitor> Teams` | команди естафети; порожньо для особистих видів змагань |
| `SettingValues Settings`, `T Setting<T>(string key)` | налаштування виду змагань на цей день |
| `void RankBy(IComparer<ResultRow> order)` | розставляє `Finished` за порядком: рівні ділять місце, наступне — з пропуском (1, 1, 3) |
| `object? HostState` | службове: дані програми для вбудованої основи |

Що саме отримує перевизначений `Rank`:

- **Особистий вид змагань** — у `Competitors` і в `Finished` ті самі рядки: лише результати, яким можна дати
  місце. У рядках є значення, збережені в `AdjustTime` (`Get`), але немає `Punches`. Після `Rank` програма
  забирає лише `Place`; `Set` і `MarkUnranked` у `Rank` не діють.
- **Естафета** — `Competitors` — рядки всіх команд групи, `Finished` — команди з OK, не поза конкурсом, без
  `MarkUnranked` і з часом; `Teams` — ті самі команди з етапами. Після `Rank` програма забирає місце команди і
  значення `Set` її рядка, а місце копіює всім учасникам команди. Якщо не викликати `base.Rank(ctx)`, режим
  «Команди з перезапуском» групи не застосовується — враховуйте `TeamCompetitor.Restarted` самі.
- **Основа «Рогейн»** — перевизначений `Rank` не викликається.

`base.Rank(ctx)` розставляє місця вбудованою основою й записує їх у `Place` рядків — після цього їх можна
підправити.

### `TeamCompetitor`

Команда естафети для `Rank`.

| Член | Опис |
|---|---|
| `ResultRow Row` | результат команди: `AdjustedTime` — сума скоригованих часів етапів, `Time` — сума фактичних |
| `int Number` | номер команди (100 у «100.2») |
| `string Name` | назва команди |
| `IReadOnlyList<ResultRow?> Members` | рядки етапів по порядку (індекс 0 — етап 1); null, якщо на етап нікого не призначено |
| `bool Restarted` | хоча б один етап стартував з перезапуску |
| `Time`, `Status`, `Place` | скорочення для `Row.AdjustedTime`, `Row.Status`, `Row.Place` |

## Спліти {#splits}

`BuildSplits` будує те, що показує панель під журналом «Зчитка на фініші», спліт-роздруківка і спліти в HTML. Для
основ «За вибором за часом» і «Рогейн» з `TotalPoints` ще й беруться «Бали» результату — тому перевизначати
`BuildSplits` там треба обережно. Найпростіше — викликати `base.BuildSplits(ctx)` і змінити відповідь через `with`.

### `SplitsContext`

`sealed record`, налаштувань виду змагань у ньому немає.

| Властивість | Опис |
|---|---|
| `ExpectedControls`, `CourseOrderText`, `CourseVariants` | як у `FinishContext` |
| `IReadOnlySet<string> DisabledControls` | проблемні КП дистанції, вилучені з `ExpectedControls` |
| `IReadOnlyList<ChipPunch> Punches` | відмітки з часом, у порядку зчитки, без старту й фінішу |
| `IReadOnlyDictionary<string, int> PointsByCode` | бали КП (основи з балами) |
| `IReadOnlyDictionary<string, GeoPoint> CoordsByCode`, `GeoPoint StartCoord`, `FinishCoord` | координати WGS-84 для довжини перегонів |
| `IReadOnlyDictionary<string, MapPoint> MapByCode`, `MapPoint StartMap`, `FinishMap`, `int? MapScale` | координати на карті (мм) і масштаб — мають перевагу над WGS-84 |
| `DateTimeOffset? StartTime`, `FinishTime` | старт і фініш |
| `TimeSpan? TimeLimit`, `decimal? PenaltyPerMinute` | контрольний час і штраф балами за хвилину |
| `IReadOnlySet<string>? TeamCommonControls` | КП, які відмітила вся команда рогейну; null поза командою |

Коли «Бали» рахуються для результату, програма передає лише `ExpectedControls`, `Punches`, `PointsByCode`,
`StartTime`, `FinishTime`, `TimeLimit` і `PenaltyPerMinute`.

### `SplitsView`

| Член | Опис |
|---|---|
| `SplitsLayout Layout` (обов'язковий) | вигляд панелі |
| `IReadOnlyList<PassagePunch> Passage` | `Ordered`: усі відмітки чіпа по порядку |
| `IReadOnlyList<ExpectedControl> Expected` | `Ordered`: задана дистанція, кожен КП — взято чи ні |
| `IReadOnlySet<string> DisabledControls` | проблемні КП дистанції |
| `string PrescribedPattern` | шаблон змішаного напрямку з явними стартом і фінішем |
| `IReadOnlyList<ScoreEntry> Entries` | `Scored`: дозволені КП |
| `int TotalPoints`, `GrossPoints`, `Penalty` | бали: підсумок = зібрано − штраф (не менше 0) |
| `bool HasPoints` | показувати колонки балів |
| `int VisitedCount`, `ExpectedCount` | скільки КП зараховано / скільки очікувалось |
| `string VariantCode` | визначений варіант розсіювання |

### `SplitsLayout`

`Ordered` — задана дистанція: фактичний шлях поруч із правильним; `Scored` — вибір і рогейн: дозволені КП із
балами й наростаючим підсумком.

### `PassagePunch`

`record PassagePunch(int Index, string Code, bool OnCourse, DateTimeOffset? Time, TimeSpan? Leg, TimeSpan? Elapsed,
PassageKind Kind = Control, decimal? LegKm = null, double? PaceSecondsPerKm = null, int? Points = null,
int? RunningTotal = null, bool CountsForTeam = false, TimeSpan? DisplayLeg = null, decimal? DisplayLegKm = null,
double? DisplayPace = null, int? CourseIndex = null)` — одна відмітка фактичного шляху. `OnCourse` — відмітка
просунула дистанцію; `Leg`/`LegKm`/`PaceSecondsPerKm` — перегін дистанції (лише для відміток на дистанції);
`DisplayLeg`/`DisplayLegKm`/`DisplayPace` — від попередньої відмітки, для кожного рядка; `CourseIndex` — позиція на
дистанції (КП може бути на дистанції двічі).

### `PassageKind`

`Control` — відмітка КП; `Start`, `Finish` — рядки старту й фінішу.

### `ExpectedControl`

`record ExpectedControl(int Sequence, string Code, bool Taken, int? Points = null, bool CountsForTeam = false, bool Ignored = false)` —
КП заданої дистанції: номер по порядку (з 1), код, чи взято, бали, чи зараховано команді рогейну, чи це
проблемний КП.

### `ScoreEntry`

`record ScoreEntry(string Code, bool Visited, int Points, DateTimeOffset? PunchTime, TimeSpan? Elapsed, int RunningTotal)` —
дозволений КП у вигляді `Scored`.

### `ChipPunch`

`record ChipPunch(string ControlCode, DateTimeOffset? Time)` — одна відмітка чіпа: код (обрізаний) і час, якщо
файл зчитки його містить.

### `GeoPoint` і `MapPoint`

`readonly record struct GeoPoint(double? Latitude, double? Longitude)` — координати WGS-84;
`readonly record struct MapPoint(double? X, double? Y)` — координати на карті в міліметрах. В обох
`HasCoordinates` — true, коли відомі обидві координати.

## Налаштування {#settings}

Налаштування оголошують у `Discipline.Settings`. Програма сама будує форму на сторінці плагіна в меню «Плагіни»,
зберігає значення в змаганні й передає їх у контексти (`FinishContext`, `AdjustContext`, `RankingContext`).

```csharp
public override IEnumerable<SettingDef> Settings =>
[
    Setting.Int("minutes", "setting.minutes", defaultValue: 5, min: 0, max: 120),
    Setting.Decimal("rate", "setting.rate", defaultValue: 1.5m),
    Setting.Bool("strict", "setting.strict"),
    Setting.Text("note", "setting.note"),
    Setting.Choice("mode", "setting.mode", ["A", "B"]),
    Setting.Table("k", "setting.k", columns, defaultTable, scope: SettingScope.Competition),
];

// у правилі:
var minutes = ctx.Setting<int>("minutes");
var table = ctx.Setting<TableValue>("k");
```

### `Setting`

Фабрики `SettingDef`. У кожної є `key` (стабільний ключ збереження), `label` (ключ рядків плагіна або текст) і
`scope` (за замовчуванням `SettingScope.Day`).

| Фабрика | Читати як | Примітки |
|---|---|---|
| `Int(key, label, int defaultValue = 0, int? min = null, int? max = null, scope)` | `int` | введене значення обмежується `min`…`max` |
| `Decimal(key, label, decimal defaultValue = 0m, decimal? min = null, decimal? max = null, scope)` | `decimal` | так само |
| `Bool(key, label, bool defaultValue = false, scope)` | `bool` | прапорець |
| `Text(key, label, string defaultValue = "", scope)` | `string` | рядок |
| `Choice(key, label, IReadOnlyList<string> options, string? defaultValue = null, scope)` | `string` | список; за замовчуванням — перший варіант. Варіанти показуються як написано, без перекладу |
| `Table(key, label, IReadOnlyList<TableColumnDef> columns, TableValue defaultValue, scope)` | `TableValue` | таблиця, редагується в окремому вікні |

### `SettingDef`

`record SettingDef(string Key, string Label, SettingKind Kind, object? DefaultValue, SettingScope Scope = SettingScope.Day)`
з властивостями `decimal? Min`, `decimal? Max`, `IReadOnlyList<string> Choices` (для `Choice`) і
`IReadOnlyList<TableColumnDef> Columns` (для `Table`). Зазвичай його створюють фабриками `Setting`.

### `SettingKind`

`Int`, `Decimal`, `Bool`, `Text`, `Choice`, `Table` — редактор і тип значення.

### `SettingScope`

`Day` — окреме значення на кожен день (для пари «день + вид змагань»); `Competition` — одне значення на все
змагання для цього виду змагань, на сторінці налаштувань позначене «для всього змагання».

Значення зберігаються окремо для кожного `Discipline.Id`: два види змагань одного плагіна мають кожен свої
значення, навіть якщо ключі однакові. Перейменуєте ключ — збережене значення загубиться, діятиме значення за
замовчуванням. Після «Зберегти» на сторінці налаштувань результати одразу перераховуються.

### `SettingValues`

Чинні значення: збережене, інакше значення за замовчуванням. Правила зазвичай читають їх через
`ctx.Setting<T>(key)`.

| Член | Опис |
|---|---|
| `T Get<T>(string key)` | значення, перетворене до `T`; default, якщо ключа немає або перетворити не можна |
| `object? this[string key]` | значення без перетворення; null для невідомого ключа |
| `static readonly SettingValues Empty` | порожній набір (вид змагань без налаштувань) |
| `SettingValues(IReadOnlyDictionary<string, object?> values, IEnumerable<SettingDef> defs)` | конструктор — для власних тестів |
| `static T? ConvertTo<T>(object? value)` | перетворення, яким користуються `Get` і `ResultRow.Get` |

### Табличні налаштування: `TableValue`, `TableColumnDef`, `TableColumnKind`

`TableValue` — рядки клітинок за ключами колонок; клітинки зберігаються як текст. На сторінці налаштувань біля
табличного налаштування є кнопка «Редагувати…», що відкриває таблицю з кнопками «Додати рядок» і «Скинути до
типової»; порожні рядки не зберігаються.

| Член `TableValue` | Опис |
|---|---|
| `TableValue(IEnumerable<IReadOnlyDictionary<string, string?>> rows)` | таблиця з рядків «ключ колонки → текст» |
| `static TableValue FromRows(IReadOnlyList<TableColumnDef> columns, params string?[][] rows)` | таблиця з позиційних рядків — зручно для значення за замовчуванням |
| `IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows` | рядки |
| `string? Cell(int row, string column)` | текст клітинки (обрізаний); null — порожня |
| `decimal? Number(int row, string column)` | число з клітинки (кома або крапка); null — не число |
| `static decimal? ParseNumber(string? text)` | те саме для довільного тексту |
| `static readonly TableValue Empty` | порожня таблиця |

`record TableColumnDef(string Key, string Label, TableColumnKind Kind = TableColumnKind.Text)` — колонка таблиці;
фабрики `TableColumnDef.Text(key, label)` і `TableColumnDef.Number(key, label)`. `TableColumnKind`: `Text`,
`Number`. `Label` — ключ рядків плагіна або текст. Для таблиць «діапазон × ключ → число» є готовий
[`RangeTable`](#rangetable).

## Колонки результатів {#columns}

```csharp
public override IEnumerable<ResultColumnDef> Columns =>
[
    Column.Number("missed", "column.missed", r => r.Get<int?>("missed")),
    Column.Number("k", "column.k", r => r.Get<double?>("k"), format: "0.00", problem: r => r.Get<bool>("k.missing")),
    Column.Time("penalty", "column.penalty", r => r.Get<TimeSpan?>("penalty")),
    Column.Text("note", "column.note", r => r.Get<string>("note")),
];
```

### `Column`

Фабрики `ResultColumnDef`: `Number(key, label, value, string? format = null, problem = null)`,
`Time(key, label, value, problem = null)`, `Text(key, label, value, problem = null)`. `value` —
`Func<ResultRow, object?>`, `problem` — `Func<ResultRow, bool>`.

### `ResultColumnDef`

`record ResultColumnDef(string Key, string Label, ColumnKind Kind, Func<ResultRow, object?> Value, string? Format = null)`
з властивістю `Func<ResultRow, bool>? Problem` і методами `IsProblem(row)` та `FormatValue(row)`, якими користується
програма.

- **Звідки значення.** `Value` і `Problem` отримують рядок, відтворений лише зі значень, збережених через
  `ResultRow.Set` (в `AdjustTime` або, для команди естафети, в `Rank`). `Time`, `Status`, `Participant` у ньому
  порожні — усе потрібне для колонки збережіть через `Set`.
- **Формат.** null — порожня клітинка; `TimeSpan` — як час у протоколах (`m:ss` або `h:mm:ss`); число з `Format` —
  `ToString(Format)` за мовою інтерфейсу; решта — `ToString()`.
- **`Problem`** true — клітинка червона (наприклад, коефіцієнт не визначено).
- Виняток у `Value` чи `Problem` — порожня клітинка, без банера.
- **Де видно.** У таблиці дня на сторінці «Учасники» колонки з'являються самі, коли день або група використовує
  вид змагань плагіна. У протоколі результатів нова колонка плагіна додається в кінець і показується; її можна
  сховати чи пересунути. На моніторі її можна вибрати серед колонок. Онлайн-результати показують колонки
  плагіна лише для етапів естафети.
- **Ключ.** У шаблонах протоколів і монітора колонка зберігається як `"<Id плагіна>.<Key>"`. Колонки з однаковим
  `Key` у двох видах змагань одного плагіна — це одна колонка.

### `ColumnKind`

`Number`, `Time`, `Text` — тип колонки: числові й часові вирівнюються по центру й не переносяться.

## Додаткові поля учасника {#fields}

```csharp
p.AddParticipantField("gender", "Стать", FieldKind.Choice(
    new ChoiceOption("Ч", "M", "Чол", "Чоловіча"),
    new ChoiceOption("Ж", "F", "Жін", "Жіноча")));

// у правилі:
var gender = ctx.Participant.Field("gender");   // "Ч", "Ж" або null
```

- Поле створюється в змаганні, коли вид змагань увімкненого плагіна вибрано на сторінці «Дні» чи «Групи». Воно
  стає колонкою в «Мандатці» й таблиці дня, ціллю імпорту з CSV/Excel, колонкою експорту й «Відомості».
- Поле лишається, коли вид змагань більше не використовується; поки використовується — його не можна видалити.
- `label` — **звичайний текст**: він записується в змагання під час створення поля і не перекладається.
- `key` — без префікса плагіна. Якщо в змаганні вже є поле з таким ключем (створене користувачем чи іншим
  плагіном), нове не створюється — вони користуються одним полем.

### `FieldKind`

Тип поля: `FieldKind.Text`, `FieldKind.Number`, `FieldKind.Bool`, `FieldKind.Choice(params ChoiceOption[] options)`.
Властивості: `Name` (`"Text"`, `"Number"`, `"Bool"`, `"Choice"`) і `Options`.

### `ChoiceOption`

`record ChoiceOption(string Value, params string[] Aliases)` — варіант списку й синоніми. Імпорт і ручне введення
зводять синонім до `Value` без огляду на регістр і схожі латинські й кириличні літери. Значення поза списком
зберігається як є і підсвічується червоним. Рядок перетворюється на `ChoiceOption` неявно:
`FieldKind.Choice("Ч", "Ж")`.

## Написи {#strings}

`AddStrings(culture, strings)` реєструє рядки плагіна під префіксом `"<Id плагіна>."`. Як ключі рядків
програма шукає:

- `Discipline.Name`;
- `label` налаштувань (`SettingDef.Label`) і колонок таблиці (`TableColumnDef.Label`);
- `label` колонок результатів (`ResultColumnDef.Label`);
- причину `ResultRow.MarkUnranked`.

Порядок пошуку: поточна мова інтерфейсу → `"en-US"` → сам ключ без префікса. Тож простий плагін може писати в
`label` готовий текст.

!!! warning "Хоча б один виклик `AddStrings`"
    Повернення до самого ключа працює, лише якщо плагін зареєстрував рядки хоча б однією мовою (можна й
    порожній словник). Інакше текст показується разом із префіксом: `"handicap.Фора"`.

Не перекладаються: `PluginInfo.Name` і `Description`, підпис поля учасника, варіанти `Setting.Choice` і
`ChoiceOption`.

## Помилки в коді плагіна {#errors}

Кожен виклик коду плагіна програма захищає:

| Де виняток | Що використовується замість |
|---|---|
| `EvaluateFinish`, `BuildSplits`, `ResolveStart` | відповідь вбудованої основи |
| `AdjustTime` | час без змін |
| `Rank` | місця вбудованої основи |
| `Name` | `Id` виду змагань |
| `Settings`, `Columns`, `RequiredFields` | порожньо |
| `AwardsRanks` | значення основи |
| `ResultColumnDef.Value`, `Problem` | порожня клітинка, без підсвітки (без запису в журнал) |

Кожен виняток записується в журнал дій, і один раз за сеанс для кожного плагіна з'являється банер «Плагін «…» дав
помилку — результати пораховано за правилами «…»». Помилки під час завантаження (`Info`, `Register`,
`AddDiscipline`) показуються на сторінці «Плагіни». Детальніше — у [Налагодження](debugging.md).

## Toolkit {#toolkit}

`OrientPyx.Plugins.Sdk.Toolkit` — ті самі алгоритми, якими користується програма. Усі класи статичні, крім
`RangeTable`.

### `CourseMatcher` і `MatchResult`

Перевірка відміток проти заданої дистанції. Коди порівнюються без пробілів по краях і без огляду на регістр;
зайві й чужі відмітки ігноруються.

| Метод | Опис |
|---|---|
| `MatchResult MatchOrder(IReadOnlyList<string> expected, IReadOnlyList<string> punched)` | заданий порядок: кожен КП має бути в відмітках по черзі |
| `MatchResult MatchPattern(CoursePattern pattern, IReadOnlyList<string> punched, IReadOnlySet<string>? ignored = null)` | шаблон змішаного напрямку; `ignored` (проблемні КП) вважаються взятими |
| `MatchResult MatchPattern(string patternText, IReadOnlyList<string> punched, IReadOnlySet<string>? ignored = null)` | те саме з розбором тексту |
| `int[] AlignOrder(IReadOnlyList<string> punched, IReadOnlyList<string> expected)` | для кожної відмітки — позиція на дистанції або −1 для зайвої (найдовша спільна підпослідовність; так будуються спліти) |

`record MatchResult(bool Ok, string? FirstMissing, IReadOnlyList<string> Missed, int Depth = 0, int Consumed = 0)`:
`Ok` — усе взято в правильному порядку; `FirstMissing` — перший КП, який не знайшов жадібний прохід (текст MP);
`Missed` — **усі** пропущені за найбільшим збігом; `Depth`/`Consumed` — скільки КП задоволено й скільки відміток
пройдено (лише для впорядкованих дистанцій). Статус OK/MP у вбудованих основах — від того самого жадібного
проходу, тож OK тут і OK у програмі збігаються.

```csharp
var m = CourseMatcher.MatchOrder(["31", "32", "33", "34"], ["31", "33", "32", "34"]);
// m.Ok == false, m.FirstMissing == "33", m.Missed — один КП
```

### `VariantMatcher` і `VariantMatch`

Розсіювання: `VariantMatch Best(IReadOnlyList<CourseVariantData> variants, IReadOnlyList<string> punched)`
вибирає варіант, який пробіг учасник: повний збіг краще за неповний, далі — більше задоволених КП, далі — більше
пройдених відміток, далі — перший за порядком. Порожній список — `ArgumentException`.
`MatchResult Match(CourseVariantData variant, IReadOnlyList<string> punched)` перевіряє один варіант (звичайний
порядок або шаблон).

`record VariantMatch(CourseVariantData Variant, int Depth, int Consumed, string? FirstMissing, IReadOnlyList<string> Missed)`,
`Ok` — варіант пройдено повністю.

### `CourseOrder`

`int CountControls(string? courseOrder)` — кількість КП у тексті порядку без старту й фінішу (маркери — токени, що
починаються з S/F або кириличних С/Ф і не є числом); `int CountCourseControls(string? courseOrder)` — те саме, але
шаблон змішаного напрямку рахується за тим, що він вимагає (блок `[N: …]` — N).

### `Punches`

`IEnumerable<string> Split(string? text)` — коди з рядка через пробіли;
`WithoutStartFinish(IEnumerable<string> codes, IReadOnlySet<string> startFinishCodes)` і
`WithoutDisabled(IEnumerable<string> codes, IReadOnlySet<string> disabledCodes)` — без кодів старту/фінішу чи
проблемних КП.

### `Ranking`

| Метод | Опис |
|---|---|
| `IReadOnlyList<(T Item, int Place)> AssignPlaces<T>(IEnumerable<T> items, IComparer<T> order, Func<T, bool>? isOutOfCompetition = null)` | місця: рівні ділять місце, наступне — з пропуском (1, 1, 3). Ті, для кого `isOutOfCompetition` повертає true, не потрапляють у список і не займають місця. Сортування стабільне |
| `IComparer<T> ByTime<T>(Func<T, TimeSpan?> time)` | швидший — вище; без часу — в кінці |
| `IComparer<T> ByScoreThenTime<T>(Func<T, int> score, Func<T, TimeSpan?> time)` | більше балів — вище, далі швидший |

```csharp
public override void Rank(RankingContext ctx) =>
    ctx.RankBy(Ranking.ByTime<ResultRow>(r => r.AdjustedTime));
```

### `Age`

`int InYear(int birthYear, DateOnly date)` — вік за роком, як у віковому вікні групи: рік дати мінус рік
народження. Використовуйте з `AdjustContext.Date`.

### `RangeTable` {#rangetable}

Таблиця «діапазон × ключ → число», наприклад «вік × стать → коефіцієнт». Будується з табличного налаштування.

| Член | Опис |
|---|---|
| `static RangeTable FromSetting(TableValue? value, string rangeColumn = "range")` | колонка `rangeColumn` — діапазон рядка, решта колонок — числа за ключем колонки. Рядок із діапазоном, що не читається, пропускається |
| `double? Lookup(int number, string key)` | число для `number` у колонці `key` (ключ — без огляду на регістр і схожі літери). Менше першого діапазону — перший рядок; більше останнього — останній (відкритий угору, «70+»); у проміжку між рядками — найближчий (нижчий при рівній відстані). null — таблиця порожня або в рядку немає числа для ключа |
| `int Count` | кількість рядків, які вдалося прочитати |
| `static bool TryParseRange(string? text, out int min, out int max)` | розбір «8–10», «8-10», «18», «70+» |

### `RelayBib`

Номер етапу естафети «100.2» обчислюється з номера команди й етапу, у номер учасника не записується.
`string Format(int teamNumber, int leg)`; `bool TryParse(string? text, out int teamNumber, out int? leg)` — розуміє
«100.2», «100,2», «100/2», а «100» — команда без етапу; `bool IsLegNumber(string? text)` — номер з явним етапом.

### `TeamCourseAssignment`

`int?[] Assign(IReadOnlyList<IReadOnlyList<int>> penalties)` — розподіляє набір дистанцій команди по етапах
так, щоб сума штрафів була найменшою (штраф етапу на дистанції — 0, якщо пройдено чисто, інакше кількість
пропущених КП). Рядок — етап, колонка — дистанція набору; результат — номер колонки для кожного етапу або null,
якщо етапів більше, ніж дистанцій. При рівності нижчий етап отримує дистанцію, що стоїть у наборі раніше. Поки
етапів і дистанцій не більше за `ExhaustiveLimit` (8) — повний перебір, далі — угорський алгоритм. Кожен рядок
має містити по штрафу на кожну дистанцію, інакше — `ArgumentException`.

### `SiCard` {#sicard}

Модель картки SportIdent і її місткість за номером.

| Член | Опис |
|---|---|
| `SiCard.Info? Identify(string? number)` | модель за діапазоном номера; null — номер порожній, не число або невідомий діапазон |
| `SiCard.Info? TooSmall(string? number, int punches)` | картка, якій не вистачає місця на `punches` КП плюс запас `Reserve`; null — вистачає, картка невідома або `punches` ≤ 0 |
| `const int Reserve = 2` | запасні відмітки на випадок повторних чи помилкових відміток |

Вкладений тип `Info` (повна назва `SiCard.Info`): `record Info(string Model, int Capacity, int? ExtendedCapacity = null)` —
назва моделі («SI-Card 9», «SIAC»), гарантована кількість відміток із часом, більша місткість для моделі, що
залежить від налаштування станцій (SI-Card 6: 64, до 192), і `CapacityText` — «50» або «64 (192)».

### `TextKey`

Порівняння назв так, як їх бачить людина: без пробілів по краях і зайвих пробілів усередині, без невидимих
символів, без огляду на регістр, кириличні літери, що виглядають як латинські, дорівнюють латинським (М12 = M12).
`string Of(string? text)` — ключ для порівняння (лише для порівняння, не для показу); `bool Same(string? a, string? b)`;
`IEqualityComparer<string> Comparer` — для словників і `Distinct`.

### `TimeFormat`

Формати часу програми: `Duration(TimeSpan?)` і `Span(TimeSpan?)` — `m:ss` до години, `h:mm:ss` після (перший
повертає порожньо й для від'ємного); `Result(TimeSpan?)` — `h:mm:ss`, результат у протоколі; `Clock(TimeSpan?)` —
`hh:mm:ss`, час доби; `TotalHours(TimeSpan)` — `00:10:30`, години завжди двома цифрами.

## Шаблони змішаного напрямку {#patterns}

`OrientPyx.Plugins.Sdk.Toolkit.Patterns`. Мова шаблонів: `41 42 43` або `<41 42 43>` — КП по порядку;
`[2: 45 46 47]` — будь-які 2 з переліку в будь-якому порядку; блоки вкладаються:
`<30 [2: 41 42 43] 50>`. Старт `S…` на початку й фініш `F…` в кінці необов'язкові.

### `CoursePattern`

| Член | Опис |
|---|---|
| `static CoursePattern Parse(string? text, string? startCode = null, string? finishCode = null)` | розбір; порожній текст — порожній шаблон (підходить до будь-чого). `startCode`/`finishCode` — лише для показу |
| `IReadOnlyList<string> ControlCodes` | усі коди шаблону по порядку запису, з повтореннями |
| `int RequiredCount` | скільки відміток мінімально потрібно: блок `[N: …]` дає N |
| `IReadOnlyList<CoursePatternError> Errors`, `bool IsValid` | помилки розбору; `IsValid` — помилок немає |
| `string StartMarker`, `FinishMarker` | маркери для показу |
| `string NormalizedOrder()` | шаблон текстом з явними стартом і фінішем: `"S <41 42> [2: 45 46 47] F"` |
| `bool Match(IReadOnlyList<string> punches, out bool[] onCourse, out string firstMissing)` | перевірка відміток; `onCourse` — які відмітки використав шаблон; `firstMissing` — перший незадоволений КП |
| `bool Match(punches, IReadOnlySet<string>? ignoredCodes, out bool[] onCourse, out string firstMissing)` | те саме з проблемними КП, що вважаються взятими |
| `bool Match(punches, ignoredCodes, out onCourse, out firstMissing, out IReadOnlyList<ResolvedControl> resolved)` | плюс конкретний порядок, яким пройшов учасник |
| `bool CheckOrder(IReadOnlyList<string> order, out string firstMissing, out bool[] onCourse)` | чи підходить порядок під шаблон |
| `static IReadOnlyList<string> SplitOrder(string? text)` | коди з тексту (розділювачі — пробіл, кома, крапка з комою, тире, `<`, `>`), без старту й фінішу на краях |
| `CourseVariantSet EnumerateOrders(int limit = VariantLimit)` | усі конкретні порядки, які дозволяє шаблон, не більше `limit` |
| `const int VariantLimit = 500` | межа для `EnumerateOrders` |

### `CoursePatternError` і `CoursePatternErrorKind`

`readonly record struct CoursePatternError(CoursePatternErrorKind Kind, string Token)` — помилка й фрагмент, де
вона. `CoursePatternErrorKind`: `UnbalancedBracket` (дужки без пари), `ChoiceMissingColon` (немає `:` після числа),
`ChoiceBadCount` (число не задано або не ціле), `EmptyChoiceBlock` (`[N: ]` без варіантів),
`ChoiceCountTooLarge` (N більше за кількість варіантів), `EmptyOrderedBlock` (`<>`).

### `ResolvedControl`

`record ResolvedControl(string Code, bool Taken)` — КП конкретного порядку, яким пройшов учасник, і чи його
взято (з третього варіанта `Match`).

### `CourseVariant` і `CourseVariantSet`

`record CourseVariant(IReadOnlyList<string> Controls)` — один конкретний порядок; `Text` — одним рядком
(`"41 42 45"`). `record CourseVariantSet(IReadOnlyList<CourseVariant> Variants, long TotalCount, bool Truncated)` —
результат `EnumerateOrders`: `TotalCount` — скільки порядків дозволяє шаблон насправді, `Truncated` — список
обрізано.

## Інші спільні типи {#shared}

### `ControlPointType`

`Regular` — звичайний КП, `Start`, `Finish`. Це колонка «Тип» на сторінці «Змагання → КП»; програма зберігає
його в змаганні. У контексти правил не передається: старт і фініш уже прибрано з відміток і дистанції.
