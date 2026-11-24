# Перевірка готовності дня — план

> Статус: **реалізовано 2026-11-24** (нотатки підсистеми — `.claude/rules/day-readiness.md`). Відкриті питання закрито
> того ж дня — рішення в §10, текст плану їх уже враховує.
>
> Відхилення під час реалізації:
> - Перевіряються лише групи, у яких на день є учасники або команди (порожня група нічого не зіпсує, а «підтягнуті»
>   в день групи без заявок давали б шум).
> - №11 «група без дистанції» — не `NeededPunches == 0`, а: одна дистанція порожня або без жодного КП; при кількох —
>   немає жодного придатного варіанта. Дистанція, що не розбирається (№13), не дублюється як «без дистанції».
> - №12 перевіряє і кожен варіант групи з кількома дистанціями (клітинки варіантів на «Групах» цього не показують).
> - «Перейти» стоїть другою колонкою (одразу після «Рівень»), щоб кнопку було видно за будь-якої довжини тексту.
> - `SheetTable.RevealRow` скидає пошук синхронно (без debounce) — перевірено UI-тестом; колонку можна назвати ключем
>   колонки або ключем локалізації заголовка (`SheetColumn.HeaderKey`).

Мета: один список усіх проблем даних поточного дня, кожен рядок — з дією «Перейти», що відкриває потрібну сторінку і
виділяє проблемний рядок (і клітинку); плюс плитка «Проблеми: N» на «Панелі».

Принцип: більшість перевірок уже існує як червоні/бурштинові клітинки. Список не реалізує їх заново: логіку, що зараз
живе у view-моделях, переносимо в BusinessLogic, і клітинки та список рахують однією функцією — розійтися не можуть.
Тексти проблем — ті самі ключі, що й підказки клітинок.

## 0. Інвентаризація перевірок

Рівень: **П** — помилка (результат буде хибним / не буде), **У** — попередження. «нова» = підсвітки зараз немає.

| № | Перевірка | Рівень | Де зараз | Повторне використання | «Перейти» |
|---|-----------|--------|----------|-----------------------|-----------|
| 1 | Учасник дня без групи (не задана **або** її групи немає серед груп дня) | П | `ParticipantDayRowViewModel.cs:873-904` (`ResolveGroup`, `HasNoGroup`, бурштин); панель рахує лише `GroupId == null` (`CompetitionEditorService.cs:256`) | предикат «група ∉ `GroupDaySettings` дня» → BL; панель бере його ж | Учасники → рядок, «Група» |
| 2 | Без чіпа | У (рішення 2) | панель `CompetitionEditorService.cs:255` + фільтр `ParticipantQuickFilter.WithoutChip` | той самий предикат (`IsNullOrWhiteSpace(Chip)`) | Учасники, «Чіп» |
| 3 | Однаковий чіп у двох учасників дня | П | **нова**; інваріант «чіп унікальний на день» (`ParticipantDay.cs:9,27`) тримають лише редагування (`CompetitionEditorService.cs:1836-1842`), імпорт — ні (`EventStore.cs:2266-2283`); зчитка бере першого власника (`CompetitionEditorService.cs:3986-3988`) | групування `TextKey.Comparer` по `Chip.Trim()` | Учасники, «Чіп» (обидва рядки) |
| 4 | Чіп замалий для дистанції | У | бурштиновий чіп: `ParticipantDayRowViewModel.cs:989`, `GroupOption.cs:40-46`, `ChipCapacity.cs:18-19`, `SiCard.cs:72-73`; лише коли тип відмітки SportIdent (`ParticipantsViewModel.cs:1191-1195`) | `SiCard.TooSmall(chip, ChipCapacity.NeededPunches(row, strategy))` — вже BL/SDK; ключ `Participants.ChipCapacity.TooSmall` | Учасники, «Чіп» |
| 5 | Рік народження поза Min/MaxBirthYear групи | У | червона дата: `ParticipantDayRowViewModel.cs:969-983`, `Group.ViolatesAgeWindow` (`Group.cs:98-107`), текст `GroupOption.AgeViolationReason` (`GroupOption.cs:67-76`) | вибір «старший/молодший + межа» переносимо в `Group`; ключі `Participants.AgeViolation.TooOld/TooYoung` | Учасники, «Дата нар.» |
| 6 | Порожнє обов'язкове поле дисципліни плагіна | П | червона клітинка поля: `ParticipantsViewModel.cs:1109-1119`, `ParticipantFieldsViewModel.cs:104`; ключі з `GetRequiredFieldsAsync` (`CompetitionEditorService.cs:717-731`) | `strategy.RequiredFields` + `snapshot.FieldValues` | Учасники, `field.<key>` |
| 7 | Недопустиме значення додаткового поля | У | `CustomField.IsInvalid` (`CustomField.cs:82`) → червона клітинка | як є (BL) | Учасники, `field.<key>` |
| 8 | Однаковий номер у кількох учасників дня | У | лише журнал онлайн-публікації (`CompetitionEditorService.cs:5449-5512`); редагування відмовляє (`:4904-4907`) | правило з редагування: `TextKey.Same(Number.Trim())` | Учасники, «Номер» |
| 9 | Немає часу старту | У | **нова**; DNS без старту — `FinishEvaluator.cs:50-53` | §2.4: питаємо стратегію (`ResolveStart`), не id; у дня без жодного часу — одне попередження на день (рішення 6) | рядок → Учасники, «Старт»; група / день → Старт → Жеребкування |
| 10 | Естафета: перший етап без часу старту і без масового старту групи | У | **нова**; правило з `DayResultsCalculator.Relay.cs:129-135` (`link.StartTime ?? gs.MassStartTime`; етапи >1 стартують з передачі, 136-155) | те саме правило | Старт → Масовий старт |
| 11 | Група дня без дистанції | П | **нова** як підсвітка; визначення «0 = у групи ще немає дистанції» вже є (`ChipCapacity.NeededPunches`, `GroupOption.cs:30-33`) | `strategy.UsesColumn(GroupColumn.CourseOrder) && NeededPunches(row, strategy) == 0` | Групи → рядок, «Порядок КП» |
| 12 | Код у дистанції, якого немає серед КП дня (S1/F без КП старту/фінішу) | П | червона клітинка: `GroupDayRowViewModel.cs:486-503` (regex у VM), `GroupsViewModel.cs:612-615, 692-693`, `GroupsView.axaml.cs:105` | regex → BL дослівно; також для кожного варіанта групи з кількома дистанціями | Групи, «Порядок КП» |
| 13 | Помилковий шаблон змішаної дистанції / варіанта | П | превʼю «✕»: `GroupsViewModel.cs:473-499` | `CoursePattern.Parse(...).IsValid` (SDK) | Групи |
| 14 | Естафета «своя на кожен етап»: етап без жодного варіанта | П | **нова** як підсвітка; результати пишуть «дистанцію не призначено» (`course-formats-relay.md`, PerLeg) | етапи `1..LegCount` мінус `GroupCourseVariant.Leg` | Групи |
| 15 | ~~Вимкнений КП («Проблемні КП») у дистанції груп~~ — **відкинуто** (рішення 3): рішення судді, не помилка | — | — | — | — |
| 16 | Невідома дисципліна (немає плагіна) — день або перевизначення групи | П | банер: `PluginNoticeViewModel.cs:83-100` + `GetDayDisciplinesAsync` (`CompetitionEditorService.cs:5295-5309`) | `strategies.For(id).IsUnknown`; ключ `Banner.MissingPlugin` | день → Змагання → Дні; група → Групи, «Вид» |
| 17 | Естафета «за номером етапу»: номер етапу без придатної дистанції | П | червона «Група»: `RelayCellsViewModel.cs:141-157`; логіка `LegCourseCheck` (`CompetitionEditorService.Relay.cs:449-490`, private) | виносимо `LegCourseCheck` в internal-клас; ключі `Participants.Relay.Course.*` | Учасники, «Група» |
| 18 | Учасник у команді без етапу | П | червоний список на «Команди»: `CompetitionEditorService.Relay.cs:211-215`, `RelayTeamsViewModel.cs:289-300` | той самий предикат (`RelayTeamId != null && Leg == null`) | Команди → команда |
| 19 | Неповна команда | У | бурштиновий «Етап»: `RelayPlacement.cs:21`, `RelayCellsViewModel.cs:132-139` | `FilledLegs < LegCount` (рахуємо по командах, щоб порожні теж потрапили); ключ `Participants.Relay.Incomplete` | Команди → команда |
| 20 | Команда без номера | У | повідомлення на «Команди» (`RelayTeamsViewModel.cs:295-297`) | `Number is null`; у групі ByBib не дублюємо — там уже №17 | Команди → команда |
| 21 | ~~Зчитано чіп, якого немає в жодного учасника дня~~ — **відкинуто** (рішення 4): сторінка перевіряє лише підготовку; невідомі зчитки червоні в журналі «Зчитка на фініші» | — | — | — | — |
| 22 | «Варіанти за номерами»: (а) призначення з групою, відмінною від групи команди/учасника з цим номером (`NumberVariantRow.GroupMismatch` — калькулятор його не застосовує); (б) номер розсіювання без варіанта (рядок `Unassigned` нерелейної групи). Ноги естафет без варіанта з `Unassigned` не беремо — їх уже дає №17 | У | червоні клітинки `NumberVariants*`, логіка в `CompetitionEditorService.NumberVariants.cs:11-153` | статичний білдер над знімком (§2.1 п. 6) — сторінка і перевірка з однієї функції (рішення 7) | Естафета → Варіанти за номерами |

Відкинуто (див. §9): «прокатний чіп не в списку», «Етапів» червоне, одна хвилина старту на одній дистанції, оплата/мандатка,
вимкнені КП (№15), невідомі зчитки (№21).

### Знайдено під час дослідження (варто виправити незалежно від фічі)

- **Розбіжність ємності чіпа на «Учасниках».** `GetGroupsForDayAsync` (`CompetitionEditorService.cs:4876-4893`) викликає
  `ToRow(s, group)` без варіантів, тобто `ScatterMaxControlCount = 0`, а день за замовчуванням бере з дня сесії
  (`CurrentDayDefaultDiscipline`, `:6162`), а не з запитаного. Через це бурштиновий чіп на «Учасниках» не враховує
  варіанти розсіювання й курси етапів (хоча `chips.md` каже, що мав би), а групи інших днів у Мандатці отримують чужий
  вид дня (впливає й на `GroupOption.IsRelay`). Без виправлення список і клітинки розійдуться. **Виправляємо в кроці 1
  цієї зміни** (рішення 5): бурштинових чіпів у групах розсіювання й естафет з варіантами стане більше — рядок у changelog.
- **Плитка «Без групи» на панелі** рахує лише `GroupId == null` (`CompetitionEditorService.cs:256`), а таблиця дня
  показує «без групи» і тоді, коли групи учасника вже немає серед груп дня (`ParticipantDayRowViewModel.cs:873-881`).

## 1. Модель (BusinessLogic/Models/DayReadiness.cs)

```csharp
public enum ReadinessSeverity { Error, Warning }
public enum ReadinessKind { NoGroup, NoChip, DuplicateChip, ChipTooSmall, BirthYearOutOfGroup, RequiredFieldEmpty,
    InvalidFieldValue, DuplicateNumber, NoStartTime, GroupWithoutStarts, DayWithoutStarts, RelayNoMassStart,
    GroupWithoutCourse, UnknownCourseCodes, InvalidCoursePattern, RelayLegWithoutVariant, UnknownDiscipline,
    RelayLegCourse, RelayRunnerWithoutLeg, RelayTeamIncomplete, RelayTeamWithoutNumber,
    NumberVariantWrongGroup, NumberWithoutVariant }
public enum ReadinessTarget { Participant, Group, RelayTeam, Day, Draw, MassStart, NumberVariants }
public enum ReadinessField { None, Number, Group, Chip, BirthDate, StartTime, CustomField, CourseOrder, Discipline }

public sealed record ReadinessIssue(ReadinessKind Kind, ReadinessTarget Target, string Subject, string Number,
    string GroupName, string MessageKey, IReadOnlyList<string> Args)
{
    public ReadinessSeverity Severity => ReadinessRules.SeverityOf(Kind);
    public Guid? LinkId { get; init; }  public Guid? ParticipantId { get; init; }
    public Guid? GroupId { get; init; } public Guid? GroupSettingsId { get; init; }
    public Guid? RelayTeamId { get; init; } public Guid? DayId { get; init; }
    public int? TeamNumber { get; init; } public int? Leg { get; init; }   // №22: рядок «Варіантів за номерами»
    public ReadinessField Field { get; init; }
    public string FieldKey { get; init; } = "";
    public string Key => …;   // стабільний ключ рядка для RowSync (Kind + ідентифікатори + FieldKey + Subject)
}

public sealed record DayReadiness(Guid DayId, int DayNumber, IReadOnlyList<ReadinessIssue> Issues)
{
    public static DayReadiness Empty { get; } = new(Guid.Empty, 0, []);
    public int Errors => …; public int Warnings => …;
}
```

- Рівень — в одному місці: `ReadinessRules.SeverityOf(kind)`.
- BL не локалізує: `MessageKey` + `Args` (рядки, числа в інваріантній культурі) розкриває VM через
  `string.Format(Localization.Get(key), args)`.
- Де можна, ключі ті самі, що в підказках клітинок: `Participants.Group.Missing.Tooltip`,
  `Participants.ChipCapacity.TooSmall`, `Participants.AgeViolation.*`, `Groups.UnknownCodes.Tooltip`,
  `Participants.Relay.Incomplete`, `Participants.Relay.Course.*`, `Banner.MissingPlugin`.
- `Field` — намір («яка клітинка»), а не ключ колонки: колонки — справа Presentation.

## 2. Логіка (BusinessLogic/Services/Readiness/DayReadinessChecker.cs)

`public sealed class DayReadinessChecker(IDisciplineStrategyProvider strategies)` з
`DayReadiness Check(DayReadinessInput input)`, де
`DayReadinessInput(EventDay Day, EventDaySnapshot Snapshot, IReadOnlyList<CustomField> Fields, bool SportIdentCards)`.
Чистий (без store і часу, окрім §2.4), публічний (BusinessLogic не має `InternalsVisibleTo` для тестів). Створюється
`CompetitionEditorService` у конструкторі, як `_calculator`; DI-реєстрація не потрібна.

### 2.1 Винесення існуючої логіки (без зміни поведінки)

1. `Models/GroupCourseChecks.cs` (public static): `UnknownCodes(string? courseOrder, IReadOnlySet<string> dayCodes)` —
   regex дослівно з `GroupDayRowViewModel.cs:488-491`; `DayCodes(IEnumerable<ControlPoint>)` — з
   `GroupsViewModel.cs:614-615`; `HasSeveralCourses(IDisciplineStrategy, RelayCourseMode)` — з
   `GroupDayRowViewModel.cs:380`. `GroupDayRowViewModel` і `GroupsViewModel` викликають їх.
2. `Group.CheckAgeWindow(birthYear, min, max)` → `AgeViolation? (TooOld, BoundYear)`; `ViolatesAgeWindow` і
   `GroupOption.AgeViolationReason` спираються на нього.
3. `LegCourseCheck` (`CompetitionEditorService.Relay.cs:449-490`) → `Services/Relay/LegCourseCheck.cs`
   (`internal sealed`) з фабрикою `For(day, settings, assignments, variants, groups, isRelay)` (null, коли в дня немає
   групи ByBib). Тіло `LoadRelayPlacementsAsync` (`:395-426`) → `internal static RelayPlacements.Build(day, links, teams,
   settings, legCourseCheck, isRelay)`; асинхронний завантажувач лишає свої читання (лінивість «читати призначення лише
   коли є ByBib» зберігається), перевірка викликає `Build` над списками знімка.
4. `ToRow` (`CompetitionEditorService.cs:6154-6180`) → `internal static GroupDayRows.Create(s, group, dayDefault,
   participantCount, variantCount, maxVariantControls)`; розрахунок варіантів — з `GetGroupDayRowsAsync:584-589`.
   **Тут же виправляємо `GetGroupsForDayAsync`** (рішення 5): рядки через `GroupDayRows.Create` з варіантами дня та
   видом **запитаного** дня (не `CurrentDayDefaultDiscipline`). Поведінка «Учасників» змінюється навмисно (більше
   бурштинових чіпів у розсіюванні / естафетах з варіантами; групи інших днів у Мандатці — свій вид дня).
5. `ReadoutTypes.FromStored(int?)` (правило за замовчуванням з `AppSettingsService.cs:227-232`) — спільне для
   `AppSettingsService` і редактора.
6. Тіло `GetNumberVariantsAsync` (`CompetitionEditorService.NumberVariants.cs:16-152`) → `internal static
   NumberVariantRows.Build(EventDay day, EventDaySnapshot snapshot, Func<DisciplineId, bool> isRelay)` →
   `NumberVariantsData`; сервіс лише читає знімок і викликає його, перевірка №22 — над тим самим знімком (рішення 7).

### 2.2 Перевірки учасників (№1–8)

Лінки знімка, з'єднані з учасниками (сироти пропускаємо, як панель `CompetitionEditorService.cs:241-244`), плюс
`DistinctLinks`. Група учасника — лише та, що є серед `GroupDaySettings` дня, інакше №1, і перевірки, що залежать від
групи (4, 5, 6, 9), для нього пропускаємо. №4 — лише при `SportIdentCards`. №8 — серед учасників дня.

### 2.3 Перевірки груп і дистанцій (№11–14, 16)

Рядок групи — `GroupDayRows.Create` над знімком (з варіантами). Стратегія —
`strategies.For(s.DisciplineOverride ?? day.DefaultDiscipline)`. Дистанції групи: одна (`!HasSeveralCourses`) —
`s.CourseOrder`; кілька — `GroupCourseVariant.CourseOrder` кожного варіанта (Subject «Ч21 · варіант A»). №12 — лише
коли в дня є КП (як у клітинці). №13 — для `UsesCoursePattern` та всіх варіантів (як клітинка валідації варіанта,
Р-40). №16 — день (Target `Day`) і кожне перевизначення групи (Target `Group`). Вимкнені КП (`IsDisabled`) не
перевіряємо (рішення 3).

### 2.4 Час старту (№9, №10)

Не порівнюємо id дисципліни. Стратегія «сама дає старт», якщо
`strategy.ResolveStart(new StartContext { FinishTime = <будь-яка дата> }) is not null` — тобто без `ChipStart` і без
`AssignedStart`. Базова реалізація (`DisciplineStrategyBase.cs:75-76`: `ChipStart ?? CombineWithFinishDate(AssignedStart,
FinishTime)`) з таким контекстом повертає null, тож вбудовані перевіряємо; плагін, що дає свій старт, пропускаємо (виклик
захищений адаптером).

«Має старт»: у звичайній групі — `link.StartTime`; в естафетній (`Competitors == TeamWithLegs`) — лише перший етап,
`link.StartTime ?? gs.MassStartTime` (етапи >1 стартують з передачі й не перевіряються ніколи). Порядок (рішення 6):

1. Якщо серед усіх перевірюваних учасників дня (без груп, стратегія яких дає свій старт) **ні в кого** немає старту —
   одне `DayWithoutStarts` на день (Target `Draw`): «часу старту немає ні в кого — жеребкування не проведено або старт за
   станцією». Жодних рядків по учасниках чи групах — день зі стартом за станцією дає один рядок, а не по рядку на групу.
2. Інакше погрупно: естафетна група без жодного старту першого етапу — одне `RelayNoMassStart` (№10, Target
   `MassStart`); інша група без жодного часу — одне `GroupWithoutStarts` (Target `Draw`); група, де частину
   розжеребовано, — `NoStartTime` для кожного без часу (Target `Participant`).

Рівень — попередження: старт може прийти з відмітки станції «Старт», знати це наперед не можна. Налаштування дня
(«старт за станцією», нова колонка + міграція) не додаємо.

### 2.5 Естафета (№14, 17–20) і «Варіанти за номерами» (№22)

Команди — зі знімка; №17 — через `RelayPlacements.Build` (ті самі `CourseIssue`, що в клітинках). №22 — через
`NumberVariantRows.Build` (§2.1 п. 6): кожен рядок `Rows` з `GroupMismatch` → `NumberVariantWrongGroup`; кожен рядок
`Unassigned`, чия група не естафетна → `NumberWithoutVariant` (естафетні ноги без варіанта вже дає №17). Subject —
`Bib` рядка, `TeamNumber`/`Leg` — для виділення. Зчитки не перевіряємо (рішення 4).

### 2.6 Порядок

Помилки → попередження; далі за Target (день → жеребкування / масовий старт → групи → учасники → команди → варіанти за
номерами), за `GroupDaySettings.Order`, за Subject.

## 3. Дані (CompetitionEditorService.Readiness.cs, GetDashboardAsync)

- `ICompetitionEditorService.GetDayReadinessAsync(Guid dayId, CancellationToken)`: без змагання / невідомий день →
  `DayReadiness.Empty`; одне `GetDaySnapshotAsync`, плюс `GetCustomFieldDefsAsync` (визначення полів у знімок не
  входять; змінюються рідко) і `_appStore.GetReadoutTypeAsync` (локальне налаштування — так само, як
  `ParticipantsViewModel`). Спільний приватний `BuildReadinessAsync(folder, day, snapshot, ct)`.
- `GetDashboardAsync` (`CompetitionEditorService.cs:213-338`): спершу знімок; передає його в
  `ComputeDayResultsAsync(..., snapshot: snapshot)` (параметр уже є, `:3247-3254`) і в `BuildReadinessAsync`;
  `dayLinks` / `groupsToday` / `readouts` — теж зі знімка, тож плитки й список з одного знімка (окремими лишаються
  прокатні чіпи і лінки всіх днів). `DashboardInfo` (`DashboardInfo.cs`) + `ReadinessErrors`, `ReadinessWarnings`;
  `WithoutGroup` = кількість `NoGroup` (визначення таблиці дня).
- Нових методів `IEventStore` немає → контракт, `RpcContractTests`, `EventStoreMethods`, `NetworkProtocol.Version` без
  змін; міграції немає. На станції працює через роутер; знімок кешує `SnapshotCache`.
- Undo: сторінка лише читає, журнал не зачіпається.

## 4. UI (Presentation)

### 4.1 Де живе список

Окрема сторінка **«Старт → Перевірка готовності дня»** — перший пункт меню «Старт», перед «Жеребкуванням»
(`MainWindow.axaml:382-397`; рішення 1) + плитка на «Панелі». Модальне вікно закривається при «Перейти» і не оновлюється наживо; блок на самій «Панелі»
неможливий — панель це `ScrollViewer` (`DashboardView.axaml:311`), а SheetTable у ньому рендерить усі рядки
(`sheet-table.md`). Сторінка дає SheetTable з фільтрами/сортуванням, живе оновлення і повернення з меню чи плитки.

### 4.2 Сторінка

- `DayReadinessViewModel : PageViewModelBase` (singleton, реєстрація за зразком
  `PresentationServiceCollectionExtensions.cs:89`) + `ReadinessRowViewModel`; `DayReadinessView.axaml(.cs)`,
  DataTemplate у `ShellView.axaml:80`.
- Шапка: заголовок, «?» (`ShowHelpCommand`), `controls:DaySelector` (показується при >1 дні; вибір перемикає день
  сесії, як `NumberVariantsViewModel.cs:167-196` / `NumberVariantsView.axaml:30-39`; без замка — без `UseDayLock`).
- Рядок-підсумок «Помилок: X · Попереджень: Y» (класи `warning`/`muted`) або «Проблем не знайдено».
- `Border Classes="card"` + `controls:SheetTable` (`LayoutKey="readiness.day"`, `LayoutStore`). Колонки в code-behind
  через `SheetColumnBuilder` (перебудова при зміні мови): «Рівень» (`CellTint` червоний/бурштиновий), «Номер»,
  «Що / хто», «Група», «Проблема» (широка), «Де виправити», «Перейти» (`Custom`-клітинка з кнопкою:
  `Command = _vm.GoToCommand`, параметр — прив'язка до рядка, як `CompetitionDaysView.axaml.cs:162-170`). Плюс дія рядка
  F8 (`RowActionLabel`/`RowActionRequested`, `SheetTable.cs:207-210`) для одного виділеного рядка.
- Рядки синхронізуються `RowSync` за `Issue.Key` (виділення переживає оновлення). Тексти при зміні мови оновлює
  сторінка, рядки на сервіс локалізації не підписуються.
- Живе оновлення: `ObserveData(bus, session, mode, Days, ControlPoints, Groups, GroupDaySettings, CourseVariants,
  Participants, ParticipantDays, CustomFields, Relay)` (призначення за номерами — область `Relay`, як на
  `NumberVariantsViewModel.cs:62-64`) + `ReloadAsync` → `LoadAsync`; `SessionChanged` →
  `LoadAsync`; `IPluginCatalog.EnabledChanged` → `RequestReload()`.
- Шина пропускає власні записи цієї машини, тож після виправлення на іншій сторінці список оновлює перезавантаження при
  відкритті: `Open…Async` спершу викликає `LoadAsync` (як `MainWindowViewModel.cs:536-541`).

### 4.3 «Перейти»

- Існуючі зразки: перехід з панелі з відкладеним запитом — `MainWindowViewModel.cs:671-721` →
  `ParticipantsViewModel.RequestQuickFilter` (`:551-586`, примусовий режим дня `_drillInDayGrid`) →
  `ParticipantsView.axaml.cs:133-170` (запит забирається і при приєднанні view, і за подією); виділення рядка за id
  після undo — `ParticipantsViewModel.OnUndoApplied` (`:191-206`).
- Новий маршрут: VM сторінки піднімає `GoToRequested(ReadinessIssue)`; `MainWindowViewModel` підписується (як на
  `QuickActionRequested`, `:238`) і маршрутизує за `issue.Target` (switch по Target, не по дисципліні):
  `Participant` → `_participants.RequestReveal(LinkId, columnKey)` + `OpenParticipantsAsync()` (режим дня примусово, як
  drill-in); `Group` → `_groups.RequestReveal(GroupSettingsId)`; `RelayTeam` → `_relayTeams` (`SelectedTeam`; список
  «без етапу» видно для вибраної команди); `Day` → `_competitionDays`; `Draw` / `MassStart` — просто відкрити сторінку;
  `NumberVariants` → відкрити «Варіанти за номерами» (рядки там без `Id`, ключ — `(Number, Leg)`; виділення рядка за цим
  ключем — якщо дешево, інакше лише відкрити: проблемні клітинки там і так червоні).
- Спільний `PendingReveal(Guid Id, string? ColumnKey)`; на кожній із чотирьох VM — `RequestReveal`,
  `ConsumePendingReveal()`, подія `RevealRequested`; view знаходить рядок за `Id` (усі рядкові VM мають `Id`:
  `ParticipantDayRowViewModel.cs:507`, `GroupDayRowViewModel.cs:198`, `RelayTeamsViewModel.cs:761`,
  `DayRowViewModel.cs:98`).
- **Новий** `SheetTable.RevealRow(object row, string? columnKey)`: якщо рядка немає у `VisibleItems`
  (`SheetTable.cs:1031`) — очистити фільтри (`ClearAllFilters`, `:878`) і пошук (синхронно, без debounce — деталь
  реалізації **не перевірена**); поставити `SelectedItem` (`:60-61, 614-618`), прокрутити до рядка, сфокусувати клітинку
  колонки (`FindColumnByKey`).
- Ключі колонок «Учасників»: `id:Participants.Col.Chip|Group|BirthDate|StartTime|Number` (`DayColumnBuilder.cs:271`) і
  `CustomField.ColumnPrefix + key` (`CustomFieldColumns.cs:33`); відображення `ReadinessField → ключ` живе у view.

### 4.4 Панель

`DashboardQuickAction.DayReadiness` + `case` в `OnDashboardQuickActionAsync`. Плитка в ряду показників
(`DashboardView.axaml:212-308`): «Проблеми дня», велике N = помилки + попередження (червоне, якщо є помилки; бурштинове,
якщо лише попередження; «Усе готово» зеленим при 0), підпис «помилок X · попереджень Y». `ObserveData` панелі
(`DashboardViewModel.cs:123-125`) доповнити областями ControlPoints, CourseVariants, CustomFields. Таймер 5 с лишається;
перевірка — обробка списків у пам'яті, сервіс виконується поза UI-потоком через `UiOffloadProxy`.

### 4.5 Інше

Довідка: префікс `Help.DayReadiness` (виводиться з імені класу, `PageViewModelBase.cs:61-62`); сторінку додати в список
підписки на «?» (`MainWindowViewModel.cs:277-283`).

## 5. Локалізація (uk-UA.json + en-US.json)

- Меню і сторінка: `Menu.Start.DayReadiness`, `Nav.DayReadiness`, `Page.DayReadiness.Title`, `Page.DayReadiness.Text`.
- Таблиця: `DayReadiness.Col.Severity|Number|Subject|Group|Problem|Where|GoTo`, `DayReadiness.Severity.Error|Warning`,
  `DayReadiness.Summary`, `DayReadiness.Empty`, `DayReadiness.GoTo.Action` (F8).
- Тексти нових перевірок: `Readiness.Issue.NoChip|DuplicateChip|DuplicateNumber|RequiredFieldEmpty|InvalidFieldValue|
  NoStartTime|GroupWithoutStarts|DayWithoutStarts|RelayNoMassStart|GroupWithoutCourse|InvalidCoursePattern|
  RelayLegWithoutVariant|RelayRunnerWithoutLeg|RelayTeamWithoutNumber|NumberVariantWrongGroup|NumberWithoutVariant`
  (для №22 — повторно підказки клітинок «Варіантів за номерами», якщо вони вже є).
- Панель: `Dashboard.Stat.Readiness`, `Dashboard.Stat.Readiness.Detail`, `Dashboard.Stat.Readiness.None`.
- Довідка: `Help.DayReadiness.Title|What|Why|How`; оновити `Help.Dashboard.How` (плитка).
- Назви цілей («Де виправити») — з існуючих `Menu.*` (`Menu.Competition.Groups`, `Menu.Finish.Read`, …), щоб збігалися
  з меню; підказки клітинок — повторно з §1.

## 6. Документація (website/)

- Нова сторінка `website/content/prepare/readiness.md` «Перевірка готовності дня»: що перевіряється (таблиця перевірка →
  рівень → де виправити), «Перейти» і F8, плитка на панелі, живе оновлення, обмеження (час старту при старті за
  станцією — один рядок на день; вимкнені КП і зчитки на фініші не перевіряються).
- `mkdocs.yml` nav — після `prepare/draw.md`; `docs-map.yml` —
  `prepare/readiness.md: [src/OrientPyx.*Readiness*, src/OrientPyx.Presentation/*Dashboard*]`.
- Оновити `start/overview.md#dashboard` (рядки 57-68: плитка) і згадку меню «Старт» (рядок 55); у `prepare/draw.md` —
  посилання.
- DocShot `day-readiness` поруч із `map-counts` (`DocShotTests.cs:126-127`); демо-змагання мусить мати кілька проблем
  (перевірити або додати детерміновано, не ламаючи інших знімків — **не перевірено**).
- «Довідник екранів» згенерується з `Help.DayReadiness.*`.

## 7. Тести

`tests/OrientPyx.Tests/Core/DayReadinessTests.cs` (CompetitionBuilder, через `GetDayReadinessAsync`):

1. Чистий день → 0 проблем.
2. Учасники: без групи (null і «група не на дні»), без чіпа (попередження), дубль чіпа (обидва рядки), SI-Card 5 на 29 КП,
   рік поза вікном, дубль номера, choice-поле з чужим значенням.
3. Групи: порожня дистанція (set course і score-by-time); розсіювання з варіантами не позначається; «S1 31 F» без КП S1;
   битий шаблон mixed/варіанта; PerLeg без варіанта для етапу 2; вимкнений КП у дистанції — **жодної** проблеми;
   невідома дисципліна дня і групи (`new DisciplineId("missing.plugin")`).
4. Старт: нікого в дні → одне `DayWithoutStarts` і жодного `GroupWithoutStarts` / `NoStartTime`; група A розжеребована,
   група B ні → одне `GroupWithoutStarts` для B; 2 з 3 у групі → одне `NoStartTime`; естафета з `MassStartTime` → нічого,
   без нього (коли в дні є інші старти) → `RelayNoMassStart`; етап 2 — ніколи.
5. Естафета: неповна команда, без номера, учасник без етапу; ByBib — сценарій `RelayLegCourseIssueTests`: множина
   `RelayLegCourse` = рядки з `Relay.CourseIssue != null` у `GetParticipantDayRowsAsync`.
6. «Варіанти за номерами»: призначення з чужою групою → `NumberVariantWrongGroup`; номер розсіювання без варіанта →
   `NumberWithoutVariant`; нога естафети ByBib без варіанта — лише `RelayLegCourse`, без `NumberWithoutVariant`; паритет:
   множини дорівнюють `GroupMismatch` / нерелейним `Unassigned` у `GetNumberVariantsAsync`. Зчитка невідомого чіпа —
   **жодної** проблеми.
7. Панель: `ReadinessErrors/Warnings` = лічильники `GetDayReadinessAsync`; `WithoutGroup` = кількість `NoGroup`.
8. `GetGroupsForDayAsync`: група розсіювання, у якої варіант довший за `CourseOrder`, → `ChipTooSmall` за найдовшим
   варіантом (і бурштин у таблиці дня); група іншого дня в Мандатці отримує вид свого дня.

Також:

- `GroupCourseChecks` (у `ToolkitTests` або окремо): «S1 31 F» з `{31}` → «S1, F»; `[2: 31 32]` не позначає «2»;
  порожній набір кодів → нічого. `GroupCourseOrderCellTests` лишається зеленим.
- `tests/OrientPyx.Tests/Ui/DayReadinessUiTests.cs`: плитка → сторінка, рядків стільки ж, скільки проблем; «Перейти» до
  учасника, прихованого активним фільтром → «Учасники» в режимі дня, `SelectedParticipant.Id == LinkId`, рядок у
  `VisibleItems` (за зразком `DashboardDrillInTests`); «Перейти» до групи → `SelectedGroup`; паритет: для рядків таблиці
  дня `ChipTooSmall / HasNoGroup / BirthDateViolatesAge / GroupTint == RelayCourse` ⇔ відповідні проблеми.
- `HeadlessUiTests.B_EveryPageOpensAndRenders` (рядки 58-66) підхопить нову команду сам; `LocalizationTests` перевіряє
  ключі та «?».
- Повний прогін `dotnet test tests/OrientPyx.Tests.sln`; золоті знімки результатів не змінюються.

## 8. Порядок робіт

1. Винесення §2.1 без зміни поведінки (включно з `NumberVariantRows.Build`); прогін тестів (golden, Relay*,
   NumberVariants*, GroupCourseOrderCellTests). Потім окремим кроком — виправлення `GetGroupsForDayAsync` (зміна
   поведінки, тест 8).
2. Модель, checker, `GetDayReadinessAsync`, Core-тести 1–6.
3. Панель: знімок один раз, `DashboardInfo`, `WithoutGroup`, плитка, quick action; тест 7.
4. Сторінка, меню, реєстрація, довідка, локалізація.
5. `SheetTable.RevealRow`, `RequestReveal` на чотирьох сторінках (Учасники, Групи, Команди, Дні), маршрут у `MainWindowViewModel`; UI-тести.
6. Документація, DocShot, changelog (окремий рядок про бурштинові чіпи в розсіюванні / естафетах).

## 9. Поза межами

- **Одна хвилина на одній дистанції** — це перевірка розкладки смуг (`DrawViewModel.cs:478-542`); після масового старту
  однакова хвилина — норма, а режим старту дня ніде не зберігається.
- **«Прокатний чіп не в списку»** — червоний жирний чіп означає власний чіп, це не помилка (`RentalChipRegistry.IsNonRental`).
- **Червоне «Етапів»** — лише під час введення; збережене значення завжди ≥1 (`GroupDayRow.LegCount`).
- **Вимкнені КП у дистанціях** (№15) — рішення судді, а позначки «ігнорувати» немає: плитка лишалася б бурштиновою.
- **Невідомі зчитки** (№21) — це вже день змагань; червоні в журналі «Зчитка на фініші».
- **Налаштування дня «старт за станцією»** — замість нього одне `DayWithoutStarts` на день (§2.4).
- Оплата і «Мандатку пройдено»; автовиправлення; режим «усі дні»; позначка «перевірено, ігнорувати»; експорт/друк списку.

## 10. Рішення (2026-11-24)

1. **Меню** — «Старт», першим пунктом (перед «Жеребкуванням»).
2. **«Без чіпа»** — попередження, не помилка (чіпи часто видають на мандатці / старті).
3. **Вимкнені КП** — не показувати (№15 відкинуто).
4. **Невідомі зчитки** — не показувати; сторінка перевіряє лише підготовку (№21 відкинуто).
5. **`GetGroupsForDayAsync`** — виправити в кроці 1 цієї зміни (варіанти + вид запитаного дня).
6. **Старт за станцією** — одне попередження на день, якщо часу старту немає ні в кого; погрупні / поучасникові — лише
   коли частину дня вже розжеребовано. Без міграції.
7. **№22 «Варіанти за номерами»** — входить у цю зміну (статичний білдер над знімком).
