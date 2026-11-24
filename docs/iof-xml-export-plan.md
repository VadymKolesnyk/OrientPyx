# Експорт IOF XML 3.0 (результати зі сплітами, стартовий протокол) — план

> Статус: **реалізовано 2026-11-24** (усе, включно з доробками імпорту п. 12). Структура елементів звірена з
> офіційним `IOF.xsd` (github.com/international-orienteering-federation/datastandard-v3), який тепер лежить у
> `tests/OrientPyx.Tests/Iof/`. Рішення щодо відкритих питань — п. 11.
>
> Відхилення під час реалізації:
> - Учасник формату з командами без команди (рогейн «П/К», бігун естафети поза командою чи без етапу) з OK пишеться
>   як `NotCompeting` (місця в нього все одно немає).
> - Естафетна команда без статусу: поки день відкритий — `Active` (етап уже біг) або `Inactive`; після закриття —
>   `DidNotFinish` або `DidNotStart`. `Active` додано до `IofResultStatus`.
> - Етапи естафети судяться як «змішаний» шаблон (жадібний прохід), тож після пропущеного КП наступні відмітки
>   стратегія вважає позакурсовими: у сплітах той самий код буває і `Missing`, і `Additional`. Це погляд стратегії,
>   експорт нічого не перераховує.
> - Імпорт StartList / EntryList: команда без `BibNumber` з назвою іде шляхом «команда без номера» (за назвою, як UOF:
>   `RelayOnImportedDays`), інакше її рядки лише заповнили б «Команду».
> - Налаштування підсумку для кількох днів: свої налаштування змагань, інакше типові з програми (як на сторінці
>   «Протокол по сумі днів») — їх підставляє сторінка.

Мета: з одного дня змагань (або з кількох днів в одному файлі, п. 3.10) отримати файл IOF Data Standard 3.0 —
`ResultList` зі сплітами або `StartList`. Його без
правок мають приймати WinSplits Online, Livelox, RouteGadget і рейтингові / Eventor-подібні системи. Вимоги стандарту:
простір імен `http://www.orienteering.org/datastandard/3.0`, `iofVersion="3.0"` (обов'язковий атрибут
`BaseMessageElement`), `createTime`, `creator`, порядок елементів за `IOF.xsd` (його стереже тест).

Принципи:
- Ті самі обчислені результати, що в протоколах і онлайн-знімку, і ті самі спліти, що в «Спліти (HTML)». Власних правил
  суддівства немає.
- Нічого не порівнюється з id дисципліни. Використовуються лише `CompetitorModel`, `UsesControlPointPoints`,
  `UsesCoursePattern`, `ResolveStart`, `BuildSplits` (через наявний шлях), `SplitsView.Layout`, `Place` і статуси.
- Поділ як у спліт-експорті: редактор збирає сирі дані, будівник у BusinessLogic робить values-only документ, писач у
  DataAccess перетворює його на байти (за інтерфейсом, оголошеним у BusinessLogic).
- Дані читаються наявними методами: `GetDaySnapshotAsync` на кожен день файла, `IResultsAppData`, а для підсумку
  `GetSummaryProtocolDataAsync`. Нових методів `IEventStore` дві пари, і обидві не для даних експорту:
  `Get/SaveIofExportJsonAsync` (параметри сторінки, п. 5) і колонка назви курсу варіанта (п. 3.4). Тому
  `NetworkProtocol.Version` піднімається один раз на всю зміну.

IOF 3.0 уже є вхідним форматом програми:
- **CourseData** (КП, дистанції, сім'ї варіантів, розподіл варіантів OCAD за номерами) читає `IofXmlParser`.
- **EntryList** (`PersonEntry`, `TeamEntry` / `TeamEntryPerson` з номером, `Leg`, назвою команди) читає
  `IofEntryListReader`; імпорт учасників сам розпізнає його поруч з UOF (`UofXmlParser.Parse`, 46–48).

Обидва читачі зіставляють елементи за локальним ім'ям, тож константи простору імен у коді немає — заводимо її (п. 1).
Експорт замикає коло: зворотне відображення будуємо дзеркально до цих читачів, щоб StartList / ResultList читався назад
тими самими правилами (п. 3.9). Те, чого імпорт поки не вміє, і знахідки звірки імпорту зі схемою — у п. 12.

## 1. Модель (BusinessLogic/Models)

`Models/IofDataStandard.cs` — `static class IofDataStandard { const string Namespace =
"http://www.orienteering.org/datastandard/3.0"; const string Version = "3.0"; }`. Парсери не чіпаємо.

`Models/IofExportData.cs` — сирі дані вибраних днів, які віддає редактор:
- `enum IofListKind { ResultList, StartList }`.
- `IofExportData(IofEventData Event, IReadOnlyList<IofDayExport> Days, SummaryProtocolData? Summary,
  SummaryProtocolSettings? SummarySettings)`:
  - `Days` — у порядку номерів; один елемент, коли файл на один день;
  - `Summary` і `SummarySettings` — лише для ResultList з кількома днями (п. 3.10), інакше null.
- `IofDayExport(IofRaceData Race, ResultProtocolData? Results, StartProtocolData? Starts,
  IReadOnlyDictionary<string, IofClassInfo> Classes, IReadOnlyDictionary<Guid, IofRunnerData> Runners)`:
  - `Results` — лише для ResultList, `Starts` — лише для StartList;
  - `Classes` — за назвою групи (`TextKey.Comparer`), `Runners` — за `ParticipantId`.
- `IofEventData(string CompetitionName, string Organiser, int DayCount)`.
- `IofRaceData(Guid DayId, int DayNumber, DateOnly Date, TimeSpan Offset, bool IsLocked)` — `IsLocked` = `EventDay.IsLocked`
  (день закрито) вирішує долю учасників без зчитки (п. 3.1).
- `IofClassInfo(decimal? DistanceKm, int? ControlCount, CompetitorModel Competitors, bool UsesScore, bool HasLegCourses)`
  — відповіді стратегії групи, зібрані в редакторі, щоб будівник нічого не знав про стратегії.
- `IofRunnerData(string FsouCode, string Chip, TimeSpan? AssignedStart, DateTimeOffset? ResolvedStart, SplitsView? Splits)`.

`Models/IofXmlDocument.cs` — values-only дерево у формі IOF. Назви відповідають елементам стандарту, писач лише
серіалізує.
- Шапка й подія: `IofDocumentHeader(string Creator, DateTimeOffset CreateTime)`,
  `IofEvent(string Name, DateOnly StartDate, DateOnly? EndDate, string Organiser, IReadOnlyList<IofRace> Races)`,
  `IofRace(int Number, string Name, DateOnly Date)` — `Races` порожні для файла на один день.
- Протокол результатів:
  - `IofResultList(Header, Event, IofResultListStatus Status, IReadOnlyList<IofClassResult> Classes)`,
    `enum IofResultListStatus { Complete, Snapshot }` (п. 3.1);
  - `IofClassResult(string ClassName, IReadOnlyList<IofCourse> Courses, IReadOnlyList<IofPersonResult> Persons,
    IReadOnlyList<IofTeamResult> Teams)` — курс класу на кожен день (`SimpleRaceCourse` з `@raceNumber`).
- Особа й організація: `IofPerson(string Family, string Given, DateOnly? BirthDate, string? FsouId)`,
  `IofOrganisation(string Name, IofOrganisationType Type)`. `Type` — `Club` або `NationalRegion` (значення атрибута
  `Organisation/@type` зі стандарту).
- Результат учасника:
  - `IofPersonResult(IofPerson, IofOrganisation?, IReadOnlyList<IofRaceResult> Results)` — один `Result` на кожен день
    файла, де учасник є в цій групі;
  - `IofRaceResult(int? RaceNumber, int? Leg, int? LegOrder, string? Bib, DateTimeOffset? Start, DateTimeOffset? Finish,
    int? TimeSeconds, int? TimeBehindSeconds, int? Position, IofResultStatus Status, IReadOnlyList<IofScore> Scores,
    IofOverallResult? Overall, IofCourse? Course, IReadOnlyList<IofSplit> Splits, string? Chip)` — `RaceNumber` = номер
    дня, лише у файлі з кількома днями.
- Команди:
  - `IofTeamResult(string Name, IofOrganisation?, string? Bib, IReadOnlyList<IofTeamMemberResult> Members)`;
  - `IofTeamMemberResult(IofPerson, IofOrganisation?, IofRaceResult)`;
  - `IofOverallResult(int? TimeSeconds, int? TimeBehindSeconds, int? Position, IofResultStatus Status,
    IReadOnlyList<IofScore> Scores)`.
- Дрібні елементи: `IofCourse(int? RaceNumber, string? Name, string? Family, int? LengthMetres, int? NumberOfControls)`,
  `IofSplit(string Code, int? TimeSeconds, IofSplitStatus Status)` (`OK | Missing | Additional` — рівно ці три значення
  в XSD), `IofScore(string Type, decimal Value)`.
- `enum IofResultStatus { OK, Finished, MissingPunch, Disqualified, DidNotFinish, OverTime, NotCompeting, DidNotStart,
  Inactive }` — підмножина `ResultStatus` зі стандарту, імена збігаються з токенами, писач просто робить `ToString()`.
- Стартовий протокол:
  - `IofStartList(Header, Event, IReadOnlyList<IofClassStart>)`, `IofClassStart(ClassName, Courses, Persons, Teams)`;
  - `IofPersonStart(IofPerson, IofOrganisation?, IReadOnlyList<IofRaceStart> Starts)`, `IofTeamStart(string? Name,
    IofOrganisation?, string? Bib, Members)`, `IofTeamMemberStart(IofPerson, IofOrganisation?, IofRaceStart)`;
  - `IofRaceStart(int? RaceNumber, int? Leg, int? LegOrder, string? Bib, DateTimeOffset? Start, IofCourse? Course,
    string? Chip)`.
- Параметри: `IofExportOptions(IReadOnlySet<string>? Groups /* null = усі */, IofOrganisationSource Organisation
  /* Club | Region */, bool IncludeBirthDates, string EventName, string Creator, DateTimeOffset CreateTime)`.
- Збережені параметри сторінки (п. 5): `IofExportSettings { IofOrganisationSource Organisation = Club;
  bool IncludeBirthDates = true; bool MultiDay; List<Guid> DayIds; }` — JSON в `event.db`.

`StartProtocolRow` отримує `Guid ParticipantId { get; init; }` (як уже має `ResultProtocolRow`), щоб стартові рядки
знайшли свої `IofRunnerData`. Інших змін у моделях протоколів немає.

## 2. Дані (CompetitionEditorService.IofExport.cs + малий рефакторинг)

Новий метод `ICompetitionEditorService.GetIofExportDataAsync(IReadOnlyList<Guid> dayIds, IofListKind kind,
CancellationToken ct = default)` — у новому partial-файлі (як `CompetitionEditorService.MassStart.cs`).
- Коли змагання не вибрано або жодного з днів немає — повертає порожні дані; відсутні дні пропускаються.
- Автоматично проходить через `UiOffloadProxy`.

На кожен день одне читання: `_eventStore.GetDaySnapshotAsync(folder, dayId)` (на станції — через `SnapshotCache`), далі
лише чисті обчислення над знімком (кроки 1–6 — для кожного дня окремо). Для ResultList з кількома днями ще
`GetSummaryProtocolDataAsync` + `GetSummaryProtocolSettingsAsync` (нема рядка — `new SummaryProtocolSettings()`, як на
сторінці підсумкового протоколу); сам підсумок рахує будівник (п. 3.10).

1. **Результати (ResultList).**
   - Тіло `GetResultProtocolDataAsync` після читання знімка виділити в приватний
     `ReadResultProtocolDataAsync(folder, snapshot, day, ct)` → `(ResultProtocolData Data,
     IReadOnlyDictionary<Guid, ParticipantDayResult> ByLink)`.
   - Публічний метод = знімок + хелпер, поведінка та сама. Отже рядки, місця й естафетні команди ті самі, що в
     протоколі й онлайн-знімку.
2. **Спліти (ResultList).**
   - Цикл `GetDaySplitsExportDataAsync` (разом із кешем спільних КП команди рогейну) виділити в
     `BuildDaySplitViews(day, links, participants, settingsByGroup, controlPoints, readouts, variantsByGroup, results)` →
     `linkId → SplitsView`. Лише лінки зі зчиткою, правило «остання зчитка» за `Order`.
   - HTML-експорт і далі годує хелпер своїми окремими читаннями. IOF годує його зі знімка, а варіанти бере через
     `FinishEvaluator.BuildCourseVariants(_strategies, snapshot.CourseVariants, …)` — знімок їх уже має,
     `LoadCourseVariantsAsync` не потрібен.
   - Спліти будує стратегія через наявний `BuildSplitsForLink`: там і `ResolveStart`, і старт етапу естафети, і
     `VariantCode` курсу етапу.
3. **Старт (StartList).** Тіло `GetStartProtocolDataAsync` виділити в синхронний `ReadStartProtocolData(snapshot, day)`.
   Результати й спліти не рахуються.
4. **`Classes`.** Для кожної групи дня:
   - `DistanceKm`;
   - кількість КП — виділити наявну логіку протоколу в `ControlCountOf(settings, strategy, countExcluded)`; ним же
     користуватиметься протокол;
   - `strategy.Competitors`, `strategy.UsesControlPointPoints`;
   - `HasLegCourses = Competitors == TeamWithLegs && CourseAssignmentMode != RelayCourseMode.Group`.
5. **`Runners`** (за `ParticipantId`): `FsouCode`, `link.Chip.Trim()`, `link.StartTime` і `ResolvedStart`:
   - етап естафети — `result.Leg.Start`;
   - є зчитка — `strategy.ResolveStart(new StartContext { ChipStart, AssignedStart = link.StartTime, FinishTime })`
     (питаємо стратегію: плагін зі своїм правилом старту дасть свій старт);
   - без зчитки — null;
   - `Splits` — з п. 2.
6. **`Event` / `Race`.**
   - Назва й організація — з `snapshot.Info`; `IsLocked` — з `EventDay.IsLocked`.
   - Дата дня `D`: `EventDay.Date`, інакше `Info.StartDate + (Number − 1)`, інакше сьогодні.
   - Зсув `Z`: зсув того самого `DateTimeOffset`, інакше `TimeZoneInfo.Local.GetUtcOffset(D)`.
   - Дати зчиток не використовуємо: Sport Time пише синтетичну дату 2001-01-01, тож запасний варіант
     `DayResultsCalculator.DayDate` (найраніша зчитка) тут не годиться.

Мережа:
- `ICompetitionEditorService` не маршрутизується. Станція виконує метод локально над маршрутизованими `IEventStore` і
  `IResultsAppData`; на станції це таблиці хоста, тож очки ті самі.
- Для даних нового методу в `IEventStore` немає: усе потрібне, навіть `LegCourseAssignments`, уже є в знімку.
  `EventStoreMethods`, `RpcContractTests` і `NetworkProtocol.Version` зачіпають лише параметри сторінки (п. 5) і назва
  курсу варіанта (п. 3.4).
- Експорт лише читає: журнал undo не зачіпається, закритий день читається як звичайно.

## 3. Побудова (BusinessLogic: IIofExportBuilder / IofExportBuilder)

- Інтерфейс `Interfaces/IIofExportBuilder.cs`: `IofResultList BuildResultList(IofExportData data, IofExportOptions
  options)`; `IofStartList BuildStartList(IofExportData data, IofExportOptions options)`.
- Реалізація `Services/IofExportBuilder.cs`, DI — поруч з `ISplitExportBuilder`. Залежить від
  `ISummaryProtocolBuilder` (той самий шар) — для наростаючого підсумку (п. 3.10).
- Чисті internal-хелпери тестуються напряму, як `DrawHistoryScore`: `IofStatusMap`, `IofClock`, `IofNames`,
  `IofSplitList`.

### 3.1 Статуси

Значення `FinishStatus`: None, Ok, Mp, Ovt, Dnf, Dns, Dsq.

| У програмі | IOF |
|---|---|
| Ok, у конкурсі | OK (+ Position, якщо є `Place`) |
| Ok, «П/К» (`OutOfCompetition`) | NotCompeting (час є, місця немає) |
| Mp | MissingPunch |
| Ovt | OverTime |
| Dnf | DidNotFinish |
| Dns | DidNotStart |
| Dsq | Disqualified |
| None, зчитка є | Finished («фініш без перевірки») |
| None, зчитки немає, день закрито (`IsLocked`) | DidNotStart |
| None, зчитки немає, день відкритий | Inactive («ще не стартував») |

- **`ResultList/@status`:** `Complete`, коли всі дні файла закриті, інакше `Snapshot` («поточний стан під час
  змагань», за XSD). Атрибут пишеться завжди, хоча `Complete` — значення за замовчуванням.
- Не-OK учасник «П/К» лишає свій статус: MP інформативніший за NotCompeting.
- OK з `UnrankedReason` (плагін зняв з ранжування) — OK без Position.
- Команда — те саме правило над `TeamDayResult.Status` і `OutOfCompetition`.

### 3.2 Час (`IofClock`)

- **Годинник мітки** = `stamp.TimeOfDay` у її власному зсуві — час чіпа, як його прочитано. Не конвертуємо: для
  синтетичної дати Sport Time парсер дає зимовий зсув.
- **Якір учасника** `A` = `ResolvedStart`, а якщо його немає — фініш. `StartTime = D + clock(A) @ Z`;
  `FinishTime = StartTime + (finish − A)`. Перехід через північ дає D+1, часовий пояс машини не впливає.
- **Без зчитки:** `StartTime = D + AssignedStart @ Z`, якщо старт призначено.
- **`Time`** (xsd:double, секунди): OK — `RankingTime` (`AdjustedTime ?? ResultTime`, за ним ставляться місця; штрафи
  плагінів уже враховані); не-OK з обома мітками — `finish − start` (WinSplits і Livelox показують час MP). Секунди
  обрізаються, як у `TimeFormat.Result`; усі `DateTimeOffset` теж до цілої секунди.
- **`TimeBehind`** = `RankingTime` мінус найкращий `RankingTime` серед місць групи (як «Відставання» в протоколі,
  `LeaderTime`). Не пишеться для груп з `UsesScore` і для не-OK.

### 3.3 Особа й організація

- **Ім'я.** `FullName` («Прізвище Ім'я [По батькові]») ділиться на першому пробілі: `Family` — перше слово, `Given` —
  решта. Порожній `Given` дозволено, але обидва елементи обов'язкові (`PersonName`: Family 1..1, Given 1..1). Це
  дзеркало `IofEntryListReader.FullName`, тож наш імпорт прочитає назад той самий текст.
- **`BirthDate`** — лише за `IncludeBirthDates` (за замовчуванням увімкнено — рейтингу дати потрібні; хто публікує
  файл, вимикає): `BirthDate.Value.Date` у власному зсуві, формат `yyyy-MM-dd`.
- **`<Id type="FSOU">`** з `Participant.FsouCode`, якщо код не порожній. Внутрішній Guid не пишемо: він ламає
  golden-стабільність і плутає рейтинг.
- **`Organisation`** (у `PersonResult` / `PersonStart` — лише одна, 0..1):
  - параметр `Club` — назва клубу (`type="Club"`), а коли клубу немає — регіон (`type="NationalRegion"`); параметр
    `Region` — навпаки;
  - за замовчуванням — клуб (рішення 11.1): WinSplits і Livelox показують його як клуб;
  - естафетна команда — `RelayProtocolTeam.ClubName / RegionName` за тим самим правилом; команда рогейну — організація
    першого учасника.
- **`BibNumber`** = `ResultProtocolRow.Number` (для етапу там уже `LegBib` «100.2»); порожній не пишеться.
- **`ControlCard`** = чіп, без `punchingSystem`: тип системи не зберігається.

### 3.4 Курси

- **Клас** (`ClassResult/Course`, тип `SimpleRaceCourse` = `SimpleCourse` + `@raceNumber`):
  `<Course><Name>група</Name><Length>DistanceKm×1000</Length><NumberOfControls>…</NumberOfControls></Course>`.
  - Пишеться, коли відомо хоча б одне значення; у файлі з кількома днями — по одному на день з `@raceNumber`.
  - `Name` — назва групи (рішення 11.6: назву курсу OCAD для групи не зберігаємо; курс «ЧЖ55», поділений на «Ч55» і
    «Ж55», дасть класам `Course/Name` «Ч55» і «Ж55»).
  - Для змішаного кількість КП уже рахує `strategy.ControlCount` (за шаблоном).
  - Не пишеться для естафети з курсами етапів (`HasLegCourses`).
  - `Climb` — ніколи: набір висоти не зберігається (є лише в `IofCourseData` під час імпорту).
  - Довжина — саме `DistanceKm`, яку друкує протокол; `CourseDistanceCalculator` для класу не залучаємо.
- **Учасник:** коли `Splits.VariantCode` не порожній (розсіювання або курс етапу естафети — його ставить
  `BuildSplitsForLink`), або зчитки немає, але є `Leg.CourseCode`, пишемо
  `<Course><Name>…</Name><CourseFamily>…</CourseFamily><NumberOfControls>…</NumberOfControls></Course>`.
  - **Назва з файла дистанцій (рішення 11.6, робиться перед експортом).** `GroupCourseVariant` отримує
    `SourceCourseName` і `SourceCourseFamily` (обидва `string?`) — назву курсу / варіації та сім'ю, як їх записано у
    файлі OCAD (3.0: `Course/Name` і `CourseFamily`; 2.0.3: назва варіації та курсу). Їх записує імпорт дистанцій
    (`ImportGroupsAsync`); повторний імпорт під тим самим кодом оновлює, ручне створення варіанта лишає null. Міграція
    `EventDbContext`; колонка не є посиланням (`UndoReferenceRules` не зачіпаються), але `GroupCourseVariant` іде в
    знімку на станцію, тож форма JSON змінюється → `NetworkProtocol.Version`.
  - Експорт пише `SourceCourseName` / `SourceCourseFamily`, коли вони є, інакше відновлює
    `«група»_«код»` / `група` — дзеркало `IofXmlParser` (3.0): назвою групи стає `CourseFamily` (302–327), а код
    варіанта — суфікс назви курсу після назви сім'ї та «_» (`VariantSuffix`, 425–438).
  - Так назва збігається з файлом OCAD і для перейменованої або поділеної групи, і для варіацій 2.0.3 з власними
    іменами; запасна назва — лише для варіантів, створених вручну або до цієї зміни.

### 3.5 Спліти (`IofSplitList`, з `SplitsView`, нічого не перераховуємо)

- **Що таке «заданий порядок».** Умова: `Splits.Layout == Ordered && !class.UsesScore`.
  - Самого `Layout` не досить: рогейн (`RogaineStrategy.BuildSplits`) повертає `Ordered` з каталогом КП у `Expected`.
    Без перевірки `UsesScore` кожен невзятий КП рогейну став би Missing.
  - Це та сама перевірка, що в `GetDaySplitsExportDataAsync`, без винятків для розсіювання й курсів етапів: в IOF у них
    просто свій курс учасника.
- **Заданий порядок:**
  - колонки — `Expected` без `Ignored` (вимкнені КП), так само як колонки HTML-сплітів;
  - кожен пункт `Passage` з `Kind == Control && OnCourse` стає на свою колонку за `CourseIndex`, а без нього — пошуком
    уперед за кодом. Алгоритм той самий, що в `HtmlSplitWriter.RunnerSplits.From`; дистанція «55 44 43 55 66» дає дві
    окремі колонки 55;
  - колонка з пунктом → `SplitTime` зі статусом OK і `Time = Elapsed` (секунди від старту, як вимагає XSD);
  - колонка без пункту → `status="Missing"` без часу;
  - пункти `!OnCourse` (зайві, чужі, повтори, вимкнені) → `status="Additional"` з часом, після курсових, у порядку чіпа;
  - фініш — не спліт, він іде в `FinishTime`.
  - **Перевірено (2026-11-24):** `CourseIndex` індексує саме список без вимкнених КП. `BuildOrderedSplits` вирівнює
    відмітки по `expected`, кладе його першим у `Expected` (індекси 0…n−1), а вимкнені КП дописує після нього з
    `Ignored` (`DisciplineStrategyBase.cs` 384–389). Тест 7.1 це закріплює.
- **Вільний порядок** (бали, кількість КП, рогейн): кожен пункт `Kind == Control` у порядку відмітки; `OnCourse`
  (зараховано) → OK, інакше `Additional`. Missing тут не буває.
- **Змішаний:** `Expected` — конкретний варіант, за яким судили, тож Missing коректні.
- **Естафета:** `Elapsed` рахується від старту етапу (`BuildSplitsForLink` ставить `leg.Start`) — саме цього IOF
  очікує для учасника команди.
- **Без зчитки** — жодного `SplitTime`.

### 3.6 Бали, очки, бонус, штраф

- **Групи з `UsesScore`** (`Score` — xsd:double з довільним `@type`):
  - `<Score type="Score">` = `Score` (нетто: штраф за запізнення й бонус уже враховані);
  - `<Score type="Penalty">` = `ScorePenalty`, якщо > 0;
  - `<Score type="Bonus">` = `Bonus`, якщо задано й ≠ 0;
  - `Score` іде першим, бо його читають споживачі.
- **«Очки» за правилом нарахування:** `<Score type="RankingPoints">` = `Points` для будь-якої групи, якщо не null
  (рішення 11.5).
- **Штраф часу плагіна** вже сидить у `Time` (`RankingTime`). `Extra`-колонки плагінів і «виконаний розряд» у файл не
  йдуть.

### 3.7 Формати (лише `IofClassInfo.Competitors`, жодних id)

**`Individual`** — заданий напрям, змішаний, розсіювання, бали, кількість КП, плагіни на їх основі й
`UnknownDisciplineStrategy` (у нього немає місць, тож просто без Position).
- `PersonResult` на кожен рядок (у XSD `Person` там обов'язковий).
- Порядок — за місцем, далі решта за ПІБ, як у протоколі.

**`Team`** (рогейн) — у термінах IOF це «патруль».
- `TeamResult` на команду: `Name` — назва команди, групування за `ResultProtocolRow.Team` через `TextKey.Comparer`,
  порядок як у `ResultProtocolBuilder.BuildTeamRows`.
- Кожен учасник — `TeamMemberResult` з `Leg = 1`, `LegOrder = 1…n` (за ПІБ), зі своїми статусом, часом і сплітами.
- У кожного учасника `OverallResult` команди:
  - `Time` — найбільший `ResultTime` серед учасників у конкурсі (як рядок команди в протоколі);
  - `Position` = `Place`;
  - `Status` — OK, коли команда має місце; інакше найгірший статус учасника (DSQ > MP > OVT > DNF > DNS); усі «П/К» →
    NotCompeting;
  - `<Score type="Score">` команди пишеться тут, а не в результаті учасника.
- Учасники без команди («П/К») — `PersonResult` у тому самому `ClassResult`.

**`TeamWithLegs`** (естафета, «Зважена естафета»).
- `TeamResult` на кожну `RelayProtocolTeam`:
  - порядок: місце, далі `RelayTeamNumbers.SortKey`, далі назва;
  - `Name` у XSD обов'язковий (1..1): пишемо назву як є, навіть порожню (`<Name/>` валідний). `RelayTeamNumbers.Label`
    не підставляємо — інакше повторний імпорт назве команду «100» або «—»;
  - `BibNumber` = номер команди; команда без номера — без `BibNumber`.
- `TeamMemberResult` на кожен етап, де є бігун (порожні етапи пропускаються):
  - `Leg` і `BibNumber` «100.2»;
  - `StartTime / FinishTime` = `Leg.Start / Leg.Finish`, якір — старт етапу;
  - `Time` = `AdjustedTime ?? LegTime`, для не-OK — `finish − start`;
  - `Status` етапу, `Course` (курс етапу, п. 3.4), спліти, `ControlCard`.
- `OverallResult` учасника: `Time` = `Leg.Cumulative`, `Status` = статус команди; на останньому етапі з бігуном ще
  `Position` = місце команди і `TimeBehind` = час команди мінус найкращий час команди групи.
- Позиції й відставання на етапі (`TeamMemberRaceResult/Position|TimeBehind` з обов'язковим `@type` Leg/Course) не
  пишемо.
- Бігуни групи без етапу чи команди (`Result.Team == null`) — `PersonResult` зі своїм статусом.

### 3.8 StartList

- **`ClassStart`** на кожну групу (той самий фільтр груп) з тим самим `Course` класу.
- **`PersonStart`:** Person, Organisation, `Start{BibNumber, StartTime, ControlCard}` (`Start` — 1..n, тож завжди один
  елемент). `StartTime = D + StartTime @ Z`; коли старту немає — не пишеться. Порядок — час старту, потім ПІБ, як у
  стартовому протоколі.
- **Рогейн:** `TeamStart{Name, Organisation, TeamMemberStart{… Start{Leg = 1, LegOrder, BibNumber, StartTime,
  ControlCard}}}`. Учасник без команди — `PersonStart`.
- **Естафета:** `TeamStart{Name (як є), Organisation, BibNumber = номер}`, у ньому `TeamMemberStart` на кожен етап з
  бігуном: `Start{Leg, BibNumber «100.2», StartTime, ControlCard}`. `StartTime` — лише для етапу 1:
  `StartProtocolTeam.StartTime` (свій старт, інакше масовий). Бігуни без команди — `PersonStart`.
- **Масовий старт** окремо не позначається. В IOF це просто однаковий `StartTime`: `ApplyMassStartAsync` уже вписав
  `ParticipantDay.StartTime`, а для естафети є `MassStartTime` через `StartProtocolTeam`.
- **Подія:** `Event{Name = options.EventName, StartTime{Date = D}, Organiser{Name}}` (`StartTime` — тип
  `DateAndOptionalTime`: `Date` обов'язковий, `Time` ні); організатор — лише коли не порожній. Кілька днів — п. 3.10.

### 3.9 Зворотна сумісність з імпортом

Що пише експорт і що з того прочитає наявний імпорт EntryList (`IofEntryListReader`); перевірено по коду і по XSD.

| Дані | Імпорт (EntryList) | Експорт (StartList / ResultList) | Коло |
|---|---|---|---|
| Група | `Class/Name` → Group (перший `Class` — за XSD це перша перевага) | `ClassStart|ClassResult/Class/Name` | збігається |
| ПІБ | `Family + " " + Given` (93–98) | ділимо на першому пробілі | той самий текст |
| Дата народження | `Person/BirthDate` → дата @+00:00 (100–103) | `yyyy-MM-dd` (за параметром) | збігається |
| Організація | `Organisation/Name` → Club (учаснику команди — запасна команди, як і каже XSD); `@type` ігнорується | клуб або регіон за параметром | «Клуб» — так; «Регіон» — регіон потрапить у «Клуб» (п. 12.4) |
| Номер | `PersonEntry/BibNumber` → Number — **такого елемента в XSD немає**, це толерантність до нестандартних файлів | `…/Start|Result/BibNumber` (стандартне місце) | інше місце: доробка (2) |
| Чіп | `ControlCard` — прямий нащадок запису, «0» = немає (105–109) | у `Start` / `Result`, «0» не пишемо | інше місце: доробка (2) |
| Команда естафети | номер: `TeamEntry/BibNumber` (теж не в XSD), інакше числовий `Id`, інакше наступний вільний (24–30, 85–91); назва: `TeamEntry/Name` | `TeamStart|TeamResult/BibNumber`, `Name` | після доробки (2); після E3 команда без номера так і лишається без номера (п. 12.4) |
| Етап | `TeamEntryPerson/Leg` | `TeamMemberStart/Start/Leg` | інше місце: доробка (2) |
| Номер етапу | у EntryList не читається | «100.2» | `IofXmlParser.SplitMemberBib` бере «101-2 / 101.2 / 101/2» (413–423), `Toolkit.RelayBib.TryParse` — «100.2 / 100,2 / 100/2», без дефіса (19–40); для доробки (2) розбирати як `SplitMemberBib` |
| Команда рогейну | `TeamEntry` — команду й етап застосовують лише в днях з естафетою; назва → Team | `TeamStart|TeamResult` як патруль (Leg = 1) | у дні рогейну назва команди збігається |
| Код ФСОУ | `Person/Id` у `FsouCode` не читається (лише перший `Id` у `Raw["Id"]`, без `type`) | `<Id type="FSOU">` | доробка (3) |
| Стать | `Person/@sex` → `Raw["sex"]` → додаткове поле | не пишемо | — |
| Час старту | у EntryList немає | `StartTime` | доробка (2) |
| Дні | `RaceNumber` ігнорується, кожен запис іде на всі дні | файл на один день — без `raceNumber`; на кілька днів — `Start/@raceNumber` = номер дня (п. 3.10) | одноденний StartList вводити режимом «лише поточний день»; багатоденний — після доробки (1) і (2) |

Тест (до п. 7.2): поділ імені — точна інверсія `IofEntryListReader.FullName`. Після доробок (2) і (3) додати повне коло:
StartList → імпорт у порожні змагання → ті самі групи, ПІБ, дати, клуби, номери, чіпи, старти, команди й етапи.

### 3.10 Кілька днів в одному файлі (рішення 11.8)

Сторінка питає «Один день» чи «Кілька днів» (п. 5). Файл на кілька днів будується за тими самими правилами 3.1–3.9
для кожного дня, а відрізняється лише обгорткою (атрибути звірено з `IOF.xsd`):

- **Подія.** `Event{Name = назва змагань (без «— День N»), StartTime{Date = дата першого дня файла},
  EndTime{Date = дата останнього}, Organiser, Race*}`; `Race{RaceNumber = номер дня, Name = «День N» (Header.Day),
  StartTime{Date = D}}` на кожен вибраний день. За XSD `Race` іде після `Organiser` і `Class`.
- **`raceNumber` = номер дня** (не порядковий номер у файлі): так доробка 12.1 прочитає файл назад на ті самі дні. Атрибут
  мають лише `PersonRaceResult`, `TeamMemberRaceResult`, `PersonRaceStart`, `TeamMemberRaceStart` і `SimpleRaceCourse`.
  `ClassResult`, `ClassStart`, `TeamResult`, `TeamStart` його не мають.
- **Класи.** `ClassResult` / `ClassStart` — на кожну назву групи, що є хоч в одному вибраному дні (`TextKey.Comparer`),
  у порядку груп. `Course` — по одному на день з `@raceNumber` (п. 3.4).
- **Особа.** Один `PersonResult` на учасника в класі, у ньому `Result` на кожен день, де учасник біжить у цій групі.
  Хто змінив групу між днями, з'являється в двох класах, у кожному зі своїми днями. Так само `PersonStart` з кількома
  `Start` (за XSD `Start` 1..n).
- **Команди.** Команда естафети чи рогейну існує в межах дня, тож `TeamResult` / `TeamStart` — окремий на кожен день, а
  день видно з `@raceNumber` у `Result` / `Start` учасників.
- **Підсумок «по сумі днів» — лише в індивідуальних групах** (`CompetitorModel.Individual` того дня; у командах
  `OverallResult` лишається результатом команди, п. 3.7):
  - правила — збережені `SummaryProtocolSettings` без змін: режим за очками / за часом, які дні враховуються,
    пріоритетний день, «без пріоритету». Тож підсумок той самий, що на «Підсумковому протоколі»;
  - **наростаючий:** у `Result` дня N — підсумок за врахованими днями ≤ N. Будівник викликає
    `ISummaryProtocolBuilder.RankGroups` для кожного N з копією налаштувань, де прапорці днів > N зняті;
  - день N не враховується в підсумку (прапорець знято) → `OverallResult` у цьому дні не пишеться;
  - пріоритетний день пізніший за N → нічиї в підсумку дня N розв'язує перший врахований день ≤ N (як «пріоритет не
    задано»); «без пріоритету» лишається без пріоритету;
  - вміст: учасник з місцем → `Status = OK`, `Position` = місце, за часом — `Time` = сума й `TimeBehind` від лідера, за
    очками — `<Score type="RankingPoints">` = сума очок; без місця (поза конкурсом, не всі дні) → `OverallResult` не
    пишеться;
  - `RankGroups` віддає суму лише текстом (`TotalText`): додати в `SummaryRankedEntry` числові `TimeSpan? TotalTime` і
    `decimal? TotalPoints` (адитивно, протокол і роздруківка переможців їх не помічають).
- **Статус списку** — `Snapshot`, якщо хоч один день файла відкритий (п. 3.1).
- **Час** — кожен день зі своїми `D` і `Z` (п. 3.2): дата старту в `Result` дня 2 — дата дня 2.

## 4. Запис (DataAccess/Documents/IofXmlWriter)

- **Розташування.** Інтерфейс `Interfaces/IIofXmlWriter.cs` у BusinessLogic: `byte[] Write(IofResultList list)`,
  `byte[] Write(IofStartList list)`. Реалізація `DataAccess/Documents/IofXmlWriter.cs` поруч з `HtmlSplitWriter`; DI —
  біля `ISplitHtmlWriter`. Так само поділено спліти, монітор і .docx: будівник у BusinessLogic, писач виходу в
  DataAccess (System.Xml.Linq — це BCL, але виходи живуть у DataAccess; CSV-писач у BusinessLogic — виняток для
  «чистого тексту», тут його не наслідуємо).
- **Документ.** `XDocument` з `XNamespace ns = IofDataStandard.Namespace` як простором імен за замовчуванням. Корінь
  `ResultList` / `StartList` з атрибутами `iofVersion="3.0"`, `createTime`, `creator`; для ResultList ще
  `status="Complete"` або `"Snapshot"` (п. 3.1). `XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n" }` —
  однакові байти на Windows і macOS, що потрібно для golden.
- **Формати:** дата `yyyy-MM-dd`; dateTime `yyyy-MM-ddTHH:mm:sszzz`; числа — `XmlConvert.ToString` (інваріантно);
  секунди — цілі.
- **Порядок елементів** — за `IOF.xsd` (звірено):
  - `ClassResult`: Class, Course*, PersonResult*, TeamResult*.
  - `PersonResult`: EntryId?, Person, Organisation?, Result*.
  - `PersonRaceResult`: BibNumber, StartTime, FinishTime, Time, TimeBehind, Position, Status, Score*, OverallResult,
    Course, SplitTime*, ControlAnswer*, Route, ControlCard*, …
  - `TeamResult`: EntryId?, Name (обов'язковий), Organisation*, BibNumber, TeamMemberResult*.
  - `TeamMemberResult`: EntryId?, Person?, Organisation?, Result*.
  - `TeamMemberRaceResult`: Leg, LegOrder, BibNumber, StartTime, FinishTime, Time, TimeBehind*(@type), Position*(@type),
    Status, Score*, OverallResult, Course, SplitTime*, …, ControlCard*.
  - `OverallResult`: Time, TimeBehind, Position, Status, Score*.
  - `SplitTime`: ControlCode, Time; атрибут `status` = OK | Missing | Additional (за замовчуванням OK).
  - `SimpleCourse`: Id, Name, CourseFamily, Length, Climb, NumberOfControls.
  - `Person`: Id*, Name{Family, Given}, BirthDate.
  - `Organisation`: Name + атрибут `type`.
  - `Event`: Id?, Name, StartTime?, EndTime?, Status?, Classification?, Form*, Organiser*, Official*, Class*, Race*, …
  - `Race`: RaceNumber, Name, StartTime?, … (`RaceNumber` і `Name` обов'язкові).
  - `@raceNumber` (optional xsd:integer) — на `PersonRaceResult`, `TeamMemberRaceResult`, `PersonRaceStart`,
    `TeamMemberRaceStart`, `SimpleRaceCourse`; пишеться лише у файлі з кількома днями.
  - `ClassStart`: Class, Course*, StartName*, PersonStart*, TeamStart*.
  - `PersonStart`: EntryId?, Person?, Organisation?, Start+.
  - `PersonRaceStart`: BibNumber, StartTime, Course, ControlCard*.
  - `TeamStart`: EntryId?, Name?, Organisation*, BibNumber, TeamMemberStart*.
  - `TeamMemberRaceStart`: Leg, LegOrder, BibNumber, StartTime, Course, ControlCard*.
  - Остаточно порядок стереже XSD-тест (п. 7).

## 5. UI (Presentation)

**Реєстрація.** Нова сторінка `IofExportViewModel` (singleton, `PageViewModelBase`) + `IofExportView`; реєстрація в
`PresentationServiceCollectionExtensions` (як `SplitsExportViewModel`) і `DataTemplate` у `ShellView.axaml`.

**Два входи в один VM** (прецедент — `OpenStartProtocolKindAsync` у `MainWindowViewModel`):
- «Результати → Експорт IOF XML»: `OpenIofExportCommand`, Kind = ResultList; у меню після «Спліти (HTML)».
- «Старт → Стартовий протокол (IOF XML)»: `OpenIofStartListCommand`, Kind = StartList; після «Стартовий суддівський
  протокол».
- Сторінку «Спліти (HTML)» з IOF не змішуємо: її поля шапки (підзаголовок, тип, місце) для IOF не потрібні.

**Вигляд сторінки.** Угорі: заголовок + «?» (`HelpKeyPrefix = "Help.IofExport"`), `DaySelector` з `ShowLock=False`,
accent-кнопка «Експортувати IOF XML». Вибір дня перемикає день сесії — так поводиться
`SplitsExportViewModel.OnSelectedDayChanged` (хоча коментар класу там каже протилежне). Картки:
- «Що експортувати» — перемикач «Результати зі сплітами» / «Стартовий протокол»;
- «Дні» (лише коли днів більше одного; рішення 11.8) — перемикач «Один день» (день із `DaySelector`) / «Кілька днів в
  одному файлі» з прапорцями днів (за замовчуванням усі). У режимі кількох днів `DaySelector` ховається; кнопка
  неактивна, коли не вибрано жодного дня. Для ResultList підказка: «Підсумок по сумі днів — за налаштуваннями
  «Підсумкового протоколу»»;
- «Групи» — «Усі групи» + прапорці груп (`GroupToggle` з `MonitorFileViewModel.cs`, список із `GetGroupDayRowsAsync`;
  у режимі кількох днів — об'єднання груп вибраних днів за назвою); за замовчуванням вибрано всі; кнопка неактивна,
  коли не вибрано жодної;
- «Параметри» — «Організація учасника» (клуб, якщо порожній — регіон / регіон, якщо порожній — клуб) і «Додати дати
  народження» (за замовчуванням увімкнено);
- інфо-картка — для кого цей файл, що час береться з дати дня («Змагання → Дні»), що спліти є лише в учасників зі
  зчиткою, що поки день не закрито, файл позначено як проміжний (`Snapshot`), а учасники без зчитки — «ще не
  стартував».

**Параметри зберігаються** в `event.db` (рішення «Зберігати параметри сторінки»): `IofExportSettings` (п. 1) як JSON у
`IofExportSettingsRow` (синглтон, як `ChipScanSettingsRow`) через `IEventStore.Get/SaveIofExportJsonAsync` →
`ICompetitionEditorService.Get/SaveIofExportSettingsAsync`. Міграція `EventDbContext`; нові методи — в
`EventStoreMethods` (запис → свій `DataArea` або наявний, як у `SaveChipScanJsonAsync`), undo-класифікація як у
сусідніх рядків налаштувань, `NetworkProtocol.Version` (разом з п. 3.4). Вибір груп не зберігається (він залежить від
дня), дні — за `Guid`, зниклі відкидаються. Зберігаються одразу після зміни, як «Сканування чіпів».

**Оновлення.** `ObserveData(bus, session, mode, DataArea.Days, DataArea.Groups, DataArea.GroupDaySettings,
DataArea.Competition)` перезавантажує день і групи; вибір груп зберігається за назвою.

**`GenerateAsync()`:**
- для ResultList спершу `_dayLock.OfferCloseBeforeSplitsAsync(day)` — у файлі спліти; пропозиція не блокує експорт.
  Закриття дня тепер ще й робить файл `Complete` (п. 3.1). У режимі кількох днів пропозицію не показуємо (не
  питаємо про кожен день); відкриті дні дадуть `Snapshot`, що пояснює інфо-картка;
- `EventName` — назва змагань (+ « — День N», коли днів більше одного, а файл на один день; ключ `Header.Day`; у файлі
  на кілька днів — без суфікса, дні йдуть у `Race`);
- `Creator` = `OrientPyx {версія}` (`Assembly.GetName().Version?.ToString(3)`, як в `AboutViewModel`);
  `CreateTime = DateTimeOffset.Now`;
- у `_busy.RunAsync`: `GetIofExportDataAsync` → будівник → писач;
- повертає `(byte[] Bytes, string SuggestedFileName)`; ім'я файла — `ExportFileName.Build(Localization,
  "IofExport.NamePart.Results" | "IofExport.NamePart.Start", назва, "xml", day, multiDay)`; для файла на кілька днів —
  без номера дня (перевірити, чи `ExportFileName.Build` це вміє, інакше передати `day = null`).

**Збереження.** Code-behind як у `SplitsExportView.axaml.cs`: `SaveFilePickerAsync` (тип «IOF XML», `*.xml`,
`application/xml`) → `IExportFileSaver.SaveAsync`; файл не відкриваємо, натомість тост
`IToastService.Show(…, onClick: () => FileReveal.TryReveal(saved))`. На станції все так само: дані з хоста через знімок,
файл пишеться на цьому комп'ютері.

## 6. Локалізація (uk-UA.json + en-US.json)

- Меню й сторінка: `Menu.Protocols.IofExport` («Експорт IOF XML»), `Menu.Start.IofStartList` («Стартовий протокол (IOF
  XML)»), `Nav.IofExport`, `Page.IofExport.Title`, `Page.IofExport.Text`.
- Елементи сторінки: `IofExport.Kind.Title/Results/Start`, `IofExport.Days.Title/Single/Multi/SummaryHint`,
  `IofExport.Groups.Title/All/None`, `IofExport.Options.Title`, `IofExport.Organisation`,
  `IofExport.Organisation.Club/Region`, `IofExport.BirthDates`, `IofExport.Info`, `IofExport.Generate`.
- Назва забігу в `Race/Name` — наявний `Header.Day` («День») + номер, як у суфіксі « — День N».
- Ім'я файла й тост: `IofExport.NamePart.Results` («Результати IOF»), `IofExport.NamePart.Start` («Старт IOF»),
  `IofExport.Saved.Title`, `IofExport.Saved.Message` («{0} — клацніть, щоб показати в папці»).
- Довідка: `Help.IofExport.Title/What/Why/How`.
- Токени IOF (OK, MissingPunch…) не перекладаються.

## 7. Тести (tests/OrientPyx.Tests)

### 7.1 `Core/IofExportBuilderTests.cs` — чисті, без бази

1. **Статуси:** кожен `FinishStatus` × «П/К» × є/немає зчитки × день закрито/відкритий дає таблицю 3.1 (DidNotStart /
   Inactive); `ResultList/@status` — Complete лише коли всі дні закриті; OK з `UnrankedReason` — без Position.
2. **Час:** перехід через північ (старт 23:50, фініш 00:20 → FinishTime на D+1); мітка Sport Time 2001-01-01 зі зсувом
   +02:00 на літній день +03:00 → правильний годинник на D; старт лише з протоколу; результат не залежить від
   `TimeZoneInfo.Local`.
3. **Ім'я:** «Іваненко Петро Іванович» → Іваненко / Петро Іванович; одне слово → порожній Given; інверсія
   `IofEntryListReader.FullName`.
4. **Спліти.** Заданий порядок: MP посередині (Missing, наступні КП з часом); зайвий пункт → Additional у кінці;
   дистанція «55 44 43 55 66»; вимкнений КП. Вільний порядок: рогейн (`Layout = Ordered`!) — без Missing; повтор →
   Additional.
5. **Естафета:** OverallResult на кожному етапі; Position і TimeBehind — лише на останньому; порожній етап пропущено;
   команда без назви чи номера (порожній `<Name/>`, без `BibNumber`).
6. **Курс учасника:** без збереженої назви — «Ч21_A» + `CourseFamily` «Ч21», і `IofXmlParser` розкладає назад на ту
   саму групу й код; зі збереженою (`SourceCourseName` «ЧЖ21.A», `SourceCourseFamily` «ЧЖ21») — саме вона.
7. **Кілька днів:** `Race` на кожен день з `RaceNumber` = номер дня; `PersonResult` з двома `Result` і правильними
   `@raceNumber`; учасник, що змінив групу, — у двох класах; команди — окремо на кожен день; дата старту дня 2 — дата
   дня 2.
8. **Наростаючий підсумок:** за часом і за очками; після дня 1 і дня 2 — різні місця; невраховуваний день — без
   `OverallResult`; пріоритетний день 2 не діє на підсумок дня 1; учасник без місця — без `OverallResult`; у команді
   `OverallResult` — командний.

### 7.2 `Core/IofXmlExportTests.cs` — через `CompetitionBuilder` і справжній редактор

1. **Golden.** Один день (дата 2026-06-06 +03:00 з `CompetitionBuilder`) з групами: заданий напрям (два OK з однаковим
   часом, MP, DNF, DSQ-перекриття, «П/К», без зчитки, ФСОУ-код); розсіювання з двома варіантами; рогейн (команда з двох
   + учасник без команди); естафета (2 команди × 2 етапи, масовий старт). Еталони — `Golden/iof-resultlist.xml` і
   `Golden/iof-startlist.xml`; `Creator` і `CreateTime` фіксовані. Розбіжність пише `.actual.xml` у
   `tests/TestResults/<час>/`, як `ResultsGoldenTests`. Плюс багатоденний еталон `Golden/iof-resultlist-2days.xml`
   (два дні, один закритий → `Snapshot`; підсумок за налаштуваннями підсумкового протоколу).
2. **XSD:** обидва файли валідні. Хелпер `Checks.IsIofXml(bytes)`: `XmlSchemaSet` + `XDocument.Validate` (BCL).
3. **Параметри:** фільтр груп і організація (клуб / регіон з відкатом); `IofExportSettings` зберігаються й читаються
   назад (зниклий день відкидається).
4. **Назва курсу варіанта:** імпорт дистанцій OCAD 3.0 з поділеною групою «ЧЖ21» → `SourceCourseName` у
   `GroupCourseVariant` → той самий `Course/Name` в експорті; повторний імпорт оновлює назву.
5. **Узгодженість з протоколом:** місце й статус кожного учасника в IOF збігаються з `GetResultProtocolDataAsync` — це
   страхує рефакторинг п. 2.

### 7.3 Розширити наявні тести

- `OutputsTests.DayLevelDataAndDocumentsBuild` (кожне змагання й день — `autotest` і фікстури) та `DisciplineSweepTests`
  (кожна стратегія, включно з плагінними): побудувати обидва списки й перевірити XSD.
- Один випадок з невідомою дисципліною (`UnknownDisciplineStrategy`): файл валідний, без Position.
- `RpcContractTests` — нові методи `Get/SaveIofExportJsonAsync` (сам упаде, якщо не класифікувати).

### 7.4 Регресія рефакторингу

- `ResultsGoldenTests`, `ProtocolBuilderTests` (зокрема `SplitsExport_RowsCarryResultPlaceAndPassage`), `RelayTests` і
  `OutputsTests` проходять без змін golden.
- `HeadlessUiTests` сам відкриє обидві нові команди (рефлексія по `Open*Command`).
- `LocalizationTests` перевіряє ключі й `Help.IofExport.*` в обох мовах.

### 7.5 XSD у репозиторії

- Файл `tests/OrientPyx.Tests/Iof/IOF.xsd` (≈178 КБ) комітимо без змін (рішення 11.7), з README про джерело
  (github.com/international-orienteering-federation/datastandard-v3, коміт) і про те, що файла ліцензії в репозиторії
  IOF немає; плюс `<None Include="Iof\**" CopyToOutputDirectory="PreserveNewest" />` у `.csproj`.

## 8. Документація (website/)

- **`content/race-day/results.md`** — новий розділ «Експорт IOF XML {#iof-xml}» після «Спліти в HTML»: для чого
  (WinSplits Online, Livelox, RouteGadget, рейтинг); кроки (день, що експортувати, групи, організація, дати народження,
  «Експортувати IOF XML», куди зберегти); що потрапляє у файл (статуси простими словами, «П/К», команди рогейну й
  естафети, спліти лише зі зчиткою); порада виставити дату дня; робота на станції. Знімок `shots/iof-export.png` — у
  `DocShotTests` поруч зі `splits-export`.
- **`content/prepare/draw.md`**, розділ «Стартові протоколи»: абзац про «Старт → Стартовий протокол (IOF XML)» з
  посиланням на `../race-day/results.md#iof-xml`.
- **`docs-map.yml`:** до `race-day/results.md` додати `src/OrientPyx.*IofExport*` і `src/OrientPyx.*IofXmlWriter*`.
  Шаблон `*Iof*` у `prepare/courses.md` звужуємо до `*IofXmlParser*` / `*IofCourse*` / `*IofEntryList*`, щоб зміни
  експорту не позначали ту сторінку застарілою.
- **`content/race-day/results.md`, багатоденка:** коли брати «Кілька днів в одному файлі», що підсумок береться з
  налаштувань «Підсумкового протоколу», що поки день відкритий, файл проміжний.
- **`content/prepare/courses.md`:** рядок про те, що імпорт дистанцій тепер запам'ятовує назву курсу з OCAD (для
  IOF-експорту).
- **«Довідник екранів»** згенерується з `Help.IofExport.*` сам.
- **Інше:** запис у changelog перед релізом; рядок про IOF-експорт у `.claude/rules/outputs.md` і шляхи
  `**/*IofExport*`, `**/*IofXmlWriter*` у його `paths`.

## 9. Порядок робіт

0. Окрема зміна перед експортом: `GroupCourseVariant.SourceCourseName/SourceCourseFamily` + імпорт дистанцій
   (п. 3.4), міграція, `NetworkProtocol.Version`, тест 7.2.4 (частина про імпорт).
1. Моделі (п. 1), `IofExportBuilder` з хелперами (разом з п. 3.10 і числовими сумами в `SummaryRankedEntry`), тести
   7.1 на зібраних вручну `IofExportData`.
2. `IofXmlWriter`, `IOF.xsd` у тестах, `Checks.IsIofXml`; XSD-перевірка документів з кроку 1.
3. Рефакторинг редактора: `ReadResultProtocolDataAsync`, `BuildDaySplitViews`, `ReadStartProtocolData`,
   `ControlCountOf`, `StartProtocolRow.ParticipantId`. Прогнати `dotnet test tests/OrientPyx.Tests.sln` — golden без
   змін. Потім `GetIofExportDataAsync` і тести 7.2, 7.3.
4. `IofExportSettingsRow` + `Get/SaveIofExportJsonAsync` (міграція, `EventStoreMethods`). Сторінка, меню, локалізація,
   довідка; `dotnet build`, повний набір тестів; ручна перевірка файла у WinSplits Online / Livelox (імпорт ResultList).
5. Сайт документації, `docs-map.yml`, знімок, `outputs.md`, changelog.
6. Доробки імпорту з п. 12 — усі вибрані (рішення 11.9), кожна окремою невеликою зміною після експорту, у такому
   порядку (п. 12.5): 12.1 (дні) → 12.3 (`Person/Id`) → 12.2 (StartList) → E3 → дистанції (C1, C3, C2) → C4/C5 →
   дрібниці EntryList (E5, E6, E7).

## 10. Поза межами

- Формат IOF 2.0.3.
- Експорт CourseData (дистанції для RouteGadget / Livelox) та EntryList.
- Підсумок «по сумі днів» для команд (у командах `OverallResult` — результат команди дня).
- Назва курсу OCAD для курсу групи (рішення 11.6: лише варіанти) — клас пише назву групи.
- `RemoteRoundTripTests` для IOF (не вибрано; станція й так рахує через ті самі маршрутизовані інтерфейси).
- Позиції й відставання на етапі (`type="Leg"`).
- Курси етапів естафети за номерами у StartList — `LegCourseAssignments` уже є в знімку, тож це легке продовження.
- Довжина варіантів розсіювання через `CourseDistanceCalculator`.
- Стать (`sex`), громадянство, `Climb` в експорті.
- «Виконаний розряд» і колонки плагінів (`Extensions`).
- Пряме завантаження в Eventor / WinSplits / Livelox.
- Імпорт результатів чи сплітів з ResultList як зчиток.

## 11. Рішення (2026-11-24)

Колишні відкриті питання, закриті з користувачем.

| № | Питання | Рішення | Де в плані |
|---|---------|---------|------------|
| 11.1 | Організація учасника | клуб з відкатом на регіон, перемикач «регіон → клуб» на сторінці | 3.3, 5 |
| 11.2 | `Person/Id` | `<Id type="FSOU">` | 3.3 |
| 11.3 | Дати народження | прапорець, **за замовчуванням увімкнено** | 3.3, 5 |
| 11.4 | Без зчитки й без статусу | за станом дня: закрито → DidNotStart, відкритий → Inactive; `ResultList/@status` Complete / Snapshot | 3.1 |
| 11.5 | «Очки» | `<Score type="RankingPoints">` | 3.6 |
| 11.6 | Назва курсу варіанта | **спершу зберігати** назву (лише варіанти: `GroupCourseVariant.SourceCourseName/Family`), запасна — «група_код» | 3.4, 9 (крок 0) |
| 11.7 | `IOF.xsd` | комітимо в `tests/` з README | 7.5 |
| 11.8 | Багатоденка | **сторінка питає**: один день або кілька днів в одному файлі (прапорці днів, для ResultList і StartList); у ResultList — наростаючий підсумок для індивідуальних груп за налаштуваннями «Підсумкового протоколу»; невраховуваний день — без підсумку; пріоритетний день > N → перший врахований ≤ N | 3.10, 5 |
| 11.9 | Доробки імпорту | усі: 12.1, 12.2, 12.3, C1 + C3, C4/C5, E3, E5, E6, E7, C2 | 9 (крок 6), 12 |
| 11.10 | Необов'язкові пункти | звузити `*Iof*` у `docs-map.yml`; параметри сторінки зберігати в `event.db`; `RemoteRoundTripTests` — ні | 5, 8, 10 |

Що лишилося перевірити по коду (не рішення, а звірка під час реалізації):
- C6 — як вікно імпорту зараз зіставляє курси з групами (`ClassCourseAssignment` не читається).
- `ExportFileName.Build` для файла на кілька днів (п. 5).
- Події змін для `SaveIofExportJsonAsync` — який `DataArea` (п. 5).

## 12. Доробки імпорту (необов'язково, окремо від експорту)

### 12.1 `RaceNumber` → дні

- Перевірено: `IofEntryListReader` ніколи не заповнює `DayNumbers` (коментар 12–13); порожні `DayNumbers` означають усі
  дні — і в попередньому перегляді (`CompetitionEditorService.cs` 5082), і в збереженні (`EventStore.cs` 2230–2235);
  режим «усі дні» створює відсутні дні до найбільшого номера (`CompetitionEditorService.cs` 4956–4980); режим «лише
  поточний день» інформацію про дні ігнорує.
- За XSD елементи **називаються по-різному**: `PersonEntry/RaceNumber` (0..n) і `TeamEntry/Race` (0..n); «не вказано»
  = усі забіги. Номери відповідають `Event/Race/RaceNumber` (1, 2, …).
- План: читати їх у `DayNumbers` (учасник команди успадковує дні команди): лише додатні, без повторів, відсортовані;
  немає — порожньо, як зараз. `RelayDayNumbers` не чіпаємо: команда й етап і далі застосовуються в днях з естафетою.
  Поведінка та сама, що в UOF `ProgEvent`, включно зі створенням відсутніх днів. DTO не змінюється → RPC і
  `NetworkProtocol.Version` без змін.
- Тест в `ImportTests`: записи з `RaceNumber` 1, 2 і без нього; `TeamEntry` з `Race` 2 → лінки на відповідні дні.

### 12.2 StartList як джерело учасників

- Перевірено: `UofXmlParser.Parse` приймає лише корені `EntryList` і `UOFData` (45–50); в `UofParticipant` немає часу
  старту (`UofParticipantData.cs` 31–100); у CSV немає `ImportTarget` для старту (`CsvParticipantData.cs` 44–48);
  `ImportParticipantsBatchAsync` не пише `ParticipantDay.StartTime`.
- Читач `IofEntryListReader.IsStartList` / `ReadStartList` з тим самим виходом `UofParticipantData`:
  - `ClassStart/Class/Name` → Group;
  - `PersonStart`: Person, `Organisation` → Club, `Start/BibNumber` → Number, `Start/ControlCard` → Chip,
    `Start/StartTime` → старт;
  - `TeamStart`: `BibNumber` і `Name` → номер і назва команди;
  - `TeamMemberStart`: `Organisation`; `Start/Leg` → RelayLeg, а без нього етап — з номера «100.2» / «101-2» (як
    `SplitMemberBib`);
  - підключити поруч з `EntryList` в `UofXmlParser.Parse`.
- Старт: додати `TimeSpan? StartTime` в `UofParticipant` — годинник `dateTime` у його власному зсуві (обернена операція
  до «D + годинник @ Z» в експорті); `ImportParticipantsBatchAsync` записує його на кожен день, куди вводиться рядок,
  порожнє значення нічого не стирає; у режимі «лише поточний день» — новий прапорець
  `ParticipantUpdateFields.StartTime` («Старт») у вікні імпорту (рішення 11.9): організатор сам вирішує, чи переписувати
  вже проведене жеребкування. `UofParticipantData` — параметр методу `IEventStore`, тож змінюється форма JSON →
  підняти `NetworkProtocol.Version` (`RpcContractTests` пройде круговий JSON сам, `EventStoreMethods` не змінюється).
  Альтернатива без зміни DTO — `SetParticipantDayStartTimesBatchAsync` після імпорту — не рекомендую: довелося б
  повторити зіставлення рядків з учасниками (ФСОУ / ім'я / злиття).
- Кілька `Start` з `raceNumber` — поза межами: беремо перший.
- ResultList як джерело учасників має ту саму форму (`ClassResult/PersonResult/Result`: BibNumber, StartTime,
  ControlCard) — після StartList майже безкоштовно, необов'язково і лише як склад.

### 12.3 Стать і `Person/Id`

- Стать: основного поля немає; `RawValues(person, entry)` (`UofXmlParser.cs` 138–151) уже кладе `Person/@sex` (за XSD
  M / F) у `UofParticipant.Raw`, і будь-яке додаткове поле «sex» / «Стать» підхопить його через вікно імпорту (так
  працює поле плагіна «Зважена естафета», `WeightedRelayPlugin.cs` 33–38). У ядрі нічого не потрібно.
- `Person/Id` (за XSD 0..n, з `@type` — видавцем ідентифікатора): читати `Person/Id[@type="FSOU"]` (саме цей тип пише
  експорт) у `UofParticipant.FsouCode`. Тоді код автоматично стає ключем злиття: «усі дні» зіставляє за `FsouCode`
  (`EventStore.cs` 2051–2054, 2118–2121; попередній перегляд — `CompetitionEditorService.cs` 5066–5070), «поточний
  день» — за `LinkField.FsouCode`, при злитті порожній код дозаповнюється (2106–2109). DTO не змінюється. Інші типи `Id`
  (Eventor, IOF) — лише через `Raw`; зараз `RawValues` бере перший `Id` і губить `@type` — варто класти їх як
  `Id:<type>`. **Рішення:** `Id` без `type` теж стає кодом ФСОУ, але лише коли в `Person` немає жодного іншого `Id`
  (єдиний безтиповий). Коли `Id` кілька, береться лише `type="FSOU"`, решта — у `Raw`.

### 12.4 Звірка наявного імпорту з `IOF.xsd` (2026-11-24)

Звірено `IofEntryListReader` (EntryList) і `IofXmlParser` (CourseData 3.0) з офіційною схемою. Декодування файла
коректне: `XmlEncodingReader` бере кодування з прологу/BOM, тож UTF-8 IOF і windows-1251 UOF читаються правильно.

**EntryList — правильно:** `Person/Name` Family + Given (у XSD обидва рівно по одному); `BirthDate` (xsd:date);
`Organisation/Name`; перший `Class` (за XSD класи йдуть у порядку переваги); `TeamEntryPerson/Organisation` з відкатом на
організацію команди (XSD прямо каже опускати її, коли вона та сама); `TeamEntryPerson` без `Person` пропускається (за XSD
це вакансія); `Leg`.

**EntryList — розбіжності:**

| № | Що | Наслідок | Пропозиція | Вага |
|---|----|----------|------------|------|
| E1 | `RaceNumber` / `Race` не читаються | у багатоденці кожен заявлений біжить усі дні | 12.1 | висока |
| E2 | `Person/Id` не читається | код федерації не потрапляє у ФСОУ, повторний імпорт не зливає за кодом | 12.3 | середня |
| E3 | Номер команди з `TeamEntry/Id`, коли немає `BibNumber` | `Id` — ідентифікатор системи заявок (у Eventor — номер заявки, 6–7 цифр), а не номер команди: команди отримають номери на кшталт 1234567 | **рішення:** прибрати відкат на `Id` і на «наступний вільний»; без `BibNumber` — команда без номера (програма це підтримує, як після UOF) | середня |
| E4 | `BibNumber` у `PersonEntry` / `TeamEntry` | такого елемента в XSD немає; стандартний файл номерів не дає | лишити як толерантність, але не покладатися; номер зі стандарту — лише з StartList (12.2) | інфо |
| E5 | `Organisation/@type` ігнорується | `type="NationalRegion"` (область) потрапляє в «Клуб» | `NationalRegion` → «Регіон», решта → «Клуб» | низька |
| E6 | `ControlCard` — береться перший | за XSD карток може бути кілька (напр. SI і Emit, з `@punchingSystem`) | коли їх кілька — брати SI / без `punchingSystem` | низька |
| E7 | `BirthDate` через `DateTime.TryParse` | xsd:date дозволяє зсув («2005-03-14+05:00»); при зсуві більшому за місцевий дата зсувається на день назад | розбирати перші 10 символів як `yyyy-MM-dd` (`DateOnly.ParseExact`) | низька |

**CourseData 3.0 — правильно:** `Control/Id`, `Position` (lng/lat WGS-84), `CourseControl/@type` Start/Finish, `Course/Name`,
групування за `CourseFamily`, `TeamCourseAssignment` (BibNumber + `TeamMemberCourseAssignment` Leg / CourseName /
CourseFamily), `PersonCourseAssignment` за BibNumber, `Map/Scale`.

**CourseData 3.0 — розбіжності:**

| № | Що | Наслідок | Пропозиція | Вага |
|---|----|----------|------------|------|
| C1 | Читається лише перший `RaceCourseData` (`IofXmlParser.cs:49`) | за XSD їх 1..n, по одному на забіг (`@raceNumber`); файл з усіма днями дасть дистанції першого дня для будь-якого дня | **рішення:** `RaceCourseData` з `raceNumber` = номер дня, якщо такий є; інакше вікно імпорту пропонує вибрати забіг; один `RaceCourseData` — без питань | середня (у ваших файлах зараз по одному) |
| C2 | `Length` / `Climb` через `int.TryParse` | за XSD це xsd:double; «3420.0» або «4.5» → значення губиться | розбирати як double і округлювати | низька (OCAD пише цілі; у ваших файлах — цілі) |
| C3 | `CourseControl/@type` `CrossingPoint` / `EndOfMarkedRoute` стає звичайним КП і йде в дистанцію (`MapV3Type`, `ReadCoursesV3` 356–357) | на обов'язковому переході станції немає → MP у всіх | пропускати такі пункти в дистанції і в списку КП (як уже робиться для 2.0.3, де `<CrossingPoint>` ігнорується) | середня, якщо OCAD додає переходи в 3.0 (у ваших файлах 3.0 їх немає; у 2.0 є) |
| C4 | `CourseControl` з кількома `<Control>` — береться перший | за XSD це «відмітитися на одному з них»; хто взяв інший → MP | **рішення:** перетворювати на шаблон змішаного (12.5) | низька |
| C5 | `@randomOrder="true"` ігнорується | ділянка з довільним порядком судиться як задана → MP | **рішення:** перетворювати на шаблон змішаного (12.5) | низька |
| C6 | `ClassCourseAssignment` не читається | відповідність «курс → класи» з OCAD не використовується; група = назва курсу / сім'ї | підказувати відповідність груп у вікні імпорту (**не перевірено**, як воно зараз зіставляє) | інфо |
| C7 | `CourseControl/Score`, `PunchingUnitId` ігноруються | бали КП і кілька станцій з різними кодами на одному КП не переносяться | поза межами, поки немає потреби | інфо |

Підсумок: базові поля імпорту читаються правильно. Найважливіші розбіжності — E1 (дні), E3 (номер команди з `Id`),
C1 (кілька забігів у файлі дистанцій) і C3 (обов'язкові переходи).

### 12.5 Що беремо в роботу (рішення 11.9)

Усе, крім інформаційних E4, C6 і C7; кожен пункт — окрема невелика зміна після експорту:

- **12.1** (E1), **12.2** (StartList, з прапорцем «Старт»), **12.3** (E2, `Id` без `type` — лише коли він єдиний).
- **E3** — без відкату на `Id` і на «наступний вільний».
- **Дистанції 3.0:** C1 (вибір забігу), C3 (переходи не є КП), C2 (`Length` / `Climb` як double, округлення).
- **C4 / C5 → шаблон змішаного.** `CourseControl` з кількома `Control` стає блоком «будь-який один» `[1 31 32]`, а
  ділянка поспіль `randomOrder="true"` — блоком «усі в будь-якому порядку» `[3 41 42 43]` (форму `[N …]` звірити з
  граматикою шаблону в `CourseMatcher`). Такий курс отримує `CoursePattern` замість `CourseOrder`, а групі дня
  ставимо дисципліну «Змішаний» (`DisciplineId.Mixed`, власне перевизначення групи, як `SwitchDayToRelayAsync` робить
  для естафети). Звичайні курси того самого файла не змінюються. Вікно імпорту показує, які групи стали змішаними.
  Потрібен розбір: як `IofCourse` / `ImportGroupsAsync` передадуть шаблон (DTO через `IEventStore` → JSON →
  `NetworkProtocol.Version`).
- **Дрібниці EntryList:** E5 (`NationalRegion` → «Регіон»), E6 (кілька `ControlCard` → SI або без `punchingSystem`), E7
  (`BirthDate` через `DateOnly.ParseExact` перших 10 символів).
