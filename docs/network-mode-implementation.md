# Мережевий режим — implementation-план

> Статус: реалізовано 2026-11-05. Відхилення від плану, прийняті при реалізації: маршрутизатори та декоратори
> (`RoutingProxy<T>`, `NotifyingEventStore`/`NotifyingAppStore`) побудовані на `DispatchProxy`, а не вручну на 129
> методів; `IReadOnlySet<>` потребує конвертера (`RpcJson`); «Спільна робота» — глобальний оверлей (як Налаштування),
> а не сторінка shell, бо станція має підключитися до вибору змагання; `EventCatalogService` лишає свої `Directory.*`
> (на станції каталог цілком віддалений); збереження значення клітинки при обриві обмежене повторами `RpcChannel`
> (3 спроби), далі — помилка запису й перезавантаження сторінки після відновлення; `/version` опитується лише коли
> відомий поточний ідентифікатор. Актуальний контракт — `docs/network-protocol.md`.

Деталізація `docs/network-mode-plan.md` до рівня файлів, класів і сигнатур. Транспорт — JSON по HTTP/1.1
(Kestrel + `DispatchProxy` + `System.Text.Json`); gRPC відкинуто остаточно. Дата: 2026-11-04, уточнено
2026-11-05 (П15–П25). Нумерація етапів тут і в плані однакова: 1 ядро, 2 хост/станція, 3 стійкість і живі
оновлення, 4 автопідключення/діагностика/файли, 5 тести й реліз.

## 0. Що показала перевірка коду

Перевірено `IEventStore` (129 методів), сутності (`Entities/`), моделі (`Models/`), `SessionService`,
`EventCatalogService`, `CompetitionEditorService`, `EventArchiveService`, `FileActivityLog`, `TableLayoutStore`,
`MonitorResultsViewModel`, `PageViewModelBase`/`MainWindowViewModel`, DI обох шарів, publish-скрипти,
`build/mac/Info.plist`, тестовий проєкт.

| # | Знахідка | Наслідок | Рішення |
|---|---|---|---|
| П1 | **Кортежі в сигнатурах**: `SetParticipantDayChipsBatchAsync(IReadOnlyList<(Guid ParticipantId, Guid DayId, string Chip)>)`, `SetParticipantDayStartTimesBatchAsync(IReadOnlyList<(Guid LinkId, TimeSpan StartTime)>)`, `SetParticipantNumbersBatchAsync(IReadOnlyList<(Guid ParticipantId, string Number)>)` | `System.Text.Json` типово ігнорує поля, а `ValueTuple` — це поля `Item1..Item3`; серіалізувалося б як `{}` | `JsonSerializerOptions.IncludeFields = true` у спільних опціях RPC (імена кортежних елементів існують лише в компіляторі, `Item1/Item2` достатньо). Публічних полів у `Models/`, `Entities/`, SDK немає — перевірено, закріплено контрактним тестом (§7) |
| П2 | **`DisciplineId`** — `readonly struct` із приватним полем, конструктором `(string)` і властивістю `Value` без setter (`BusinessLogic/Disciplines/DisciplineId.cs`). Є в `EventDay`, `GroupDaySettings`, `SaveDisciplineSettingsAsync` | STJ для struct бере неявний конструктор без параметрів → після десеріалізації завжди `SetCourse` | `DisciplineIdJsonConverter : JsonConverter<DisciplineId>` (рядок ↔ struct) у спільних опціях |
| П3 | **`IProgress<ImportProgress>`** в `ImportParticipantsBatchAsync` | Делегат не серіалізується | Механізм завдань `/jobs/{id}` (§3.5); `ImportProgress` — `readonly record struct` з властивостями, серіалізується |
| П4 | **Файлові операції повз `IEventStore`** в BusinessLogic: `CompetitionEditorService` — створення папки дня при `AddDay` (рядок 303), перенос папки при зміні номера дня (353–356), `SaveDayFileAsync` (архів імпортованого XML у `events/<id>/day{N}`, 387–420), `GetMonitorOutputDirectory`/`ResolveMonitorFilePath`/`BuildMonitorDocumentsAsync` (шляхи HTML монітора в папці дня, 5153–5169); `EventCatalogService` — створення папки змагання, перейменування (`Directory.Move`); `DayFolders.PathFor` | На станції `FolderPath` — ключ хоста; локальні `Directory.*` або створять сміття на диску станції, або впадуть | Новий інтерфейс `IEventFiles` (§1.6): редактор і каталог ходять до папки змагання лише через нього; локальна реалізація в DataAccess, на станції — проксі на хост. Монітор на станції пише HTML у **локальну** папку (`<data root>/monitor/<identifier>/day{N}`), бо табло підключене до тієї машини, де працює оператор |
| П5 | **`FileActivityLog.UseEventFolder(FolderPath)`** (DataAccess/Persistence/FileActivityLog.cs:47; викликається з `SessionService` і `CompetitionInfoViewModel`) пише лог у `<FolderPath>/logs` | На станції спробує створити папку за ключем хоста | У режимі станції `UseEventFolder` ігнорує аргумент і пише у `<data root>/logs/<hostId>-<identifier>/`; дії станції додатково дублюються на хост (`POST /log`) з іменем станції |
| П6 | **`TableLayoutStore`** (Presentation/Services) бере `CurrentScopeId` і шлях `views.json` із `CurrentEvent.FolderPath` (рядки 34, 68) | Те саме, що П5 | У режимі станції ключ області = `"<hostId>/<identifier>"`, файл — локальний `<data root>/views/<hostId>/<identifier>.json` |
| П7 | **`SessionService.SelectAsync`** викликає `EnsureCreatedAsync` (міграція) і `_appStore.SaveLastSessionAsync(identifier, dayNumber)`; `TryRestoreLastAsync` робить те саме при старті | На хості міграція вже відбулася; відновлення сесії станції при старті вимагає з'єднання; **без позначки режиму станція при наступному старті в Local-режимі шукатиме змагання хоста локально, а `EnsureCreatedAsync` створить порожню папку й базу з чужим ідентифікатором** | Хост виконує `EnsureCreatedAsync` під семафором як перевірку існування (ідемпотентно, дешево). `LastSessionRow` отримує `Mode` (`Local`/`Station`) і `HostId`; `TryRestoreLastAsync` у режимі станції спершу підключається (§3.6), а покажчик зі станційним режимом локально ніколи не відновлює (§2.5) |
| П8 | **`ReleaseAsync`/`CheckpointAsync`** — операції над файлом БД хоста (використовуються перейменуванням ідентифікатора й архівом) | Станція не повинна закривати пул з'єднань хоста | Проксі станції робить їх no-op; перейменування/архів виконуються на хості через `IEventCatalogService`/`IEventArchiveService`-проксі (§2.4) |
| П9 | `EventStore` **не кидає власних винятків** (жодного `throw new`); каталог кидає `ArgumentException`/`InvalidOperationException`, які ловить UI створення змагання | Треба зберегти типи для існуючих `catch` | Відповідь помилки містить `type` (повне ім'я BCL-типу), `message`, `kind`; проксі відтворює `ArgumentException`, `InvalidOperationException`, `KeyNotFoundException`, `OperationCanceledException`; решта → `RemoteCallException` |
| П10 | **Сутності без навігаційних властивостей**, моделі — `record`/POCO з `init`, словники лише з ключами `Guid`/`int`/`string` (підтримуються STJ без конвертера), `IReadOnlyCollection<Guid>`, `IReadOnlySet<int>` у `ParticipantImportScope` (підтримується) | Серіалізація без сюрпризів | Без змін; закріплено контрактним тестом |
| П11 | `EventSummary.FolderPath` використовують 7 файлів Presentation (поза `Program.cs`): `EventSelectionViewModel:109` і `CompetitionInfoViewModel:153` — як ключ; `CompetitionInfoViewModel:208` — лог (П5); `ResultsDebugSnapshot:65` — пише `_debug` у папку; **`ChipsViewModel:150`, `FinishReadViewModel:296`, `ParticipantsViewModel:2026` — будують дефолтні шляхи файлів зчитки** (`chips/rentchip.csv`, `day{N}/event.csv`) | Ключ — ок; шляхи файлів зчитки на станції мають бути локальними (зчитувач підключено до станції) | §4.4: `ResultsDebugSnapshot` ховається в режимі станції; три VM беруть дефолт із `IEventFiles.LocalReadoutDirectory` (П17) |
| П12 | `GetDaySnapshotAsync` — найважчий виклик (усі таблиці дня; на 600 учасників ~1–3 МБ JSON), викликається при кожному завантаженні сторінки і обчисленні результатів | Десятки мс у LAN — прийнятно; серіалізація на хості ~10–30 мс | Увімкнути `ResponseCompression` (gzip/br) у Kestrel; на станції кешувати знімок до наступної події `DataChanged` (§3.4). `EventDaySnapshot` — record з `init`-списками, кешований екземпляр ніхто не мутує |
| П13 | Host-паралельність: `EventStore` відкриває `DbContext` на виклик через `EventDbContextFactory.Create`, `busy_timeout=5s`; `EnsureCreatedAsync` = `Directory.CreateDirectory` + `MigrateAsync` | Паралельні RPC не потребують власних блокувань; лише `MigrateAsync` від двох станцій одночасно не повинен іти паралельно | `EnsureCreatedAsync` на хості під `SemaphoreSlim(1)`; решта без змін |
| П14 | `FrameworkReference Microsoft.AspNetCore.App` у DataAccess передається транзитивно у Presentation і тести; обидва publish-скрипти — `--self-contained true`, без `PublishTrimmed`/AOT (DispatchProxy і рефлексія працюють) | Self-contained пакет включає рантайм ASP.NET Core (+~30 МБ) | Крок у §1.5: заміряти розмір до/після |
| П15 | **Маршрутизатор не може жити в BusinessLogic**: `Routing*` має тримати і `EventStore` (DataAccess), і `RemoteEventStore` (DataAccess) | Порушення напрямку залежностей | `Routing*` — у `DataAccess/Network/Routing/`; у BusinessLogic лишаються `IWorkspaceMode`, `IDataChangeBus`, `NotifyingEventStore`. Ланцюг DI зафіксовано в §1.5 |
| П16 | **Дані для результатів з `app.db`**: `LoadDayResultsAppDataAsync` (CompetitionEditorService:3112) читає правила очок, кваліфікацію, умови й перелік розрядів з `IAppStore` | Станція з іншою app.db порахує інші очки/розряди; критерій етапу 2 «результати збігаються» не гарантований | Новий `IResultsAppData` (§1.7): локально → `IAppStore`, на станції → RPC `AppData` на хост; `LoadDayResultsAppDataAsync` тільки через нього; сторінки «Очки»/«Розряди» на станції read-only; `NotifyingAppStore` публікує `DataArea.AppData` |
| П17 | **Машинні шляхи, що зберігаються в event.db або будуються з `FolderPath`**: `ChipScanSettings.ReadoutFilePath` (per-competition у event.db, `SaveChipScanSettingsAsync`), дефолти в трьох VM (П11) | На станції шлях сканування став би шляхом хоста; файл зчитки лежить на машині зі зчитувачем | `IEventFiles.LocalReadoutDirectory(eventFolderPath, dayNumber?)` дає локальну папку (локально — папка дня / `chips`, на станції — `<data root>/local/<hostId>/<identifier>/…`); на станції `ReadoutFilePath` зберігається у локальному `machine.json` тієї ж папки (`IEventFiles.Get/SetMachineValueAsync`), а в `ChipScanSettings` на хост іде порожнім (§4.4) |
| П18 | **По мережі не можна передавати шлях хоста**: `eventFolderPath` від станції виконується як є (`EnsureCreatedAsync` → `Directory.CreateDirectory`, відкриття SQLite за довільним шляхом) | Станція з токеном могла б створювати папки й бази будь-де на хості | По мережі ходить ідентифікатор: `RpcDispatcher` перекладає `eventFolderPath`/`EventSummary.FolderPath` вхідних аргументів на `events/<identifier>` після `EventIdentifier.IsValid` (без роздільників, `..`), у відповідях — назад (§2.1) |
| П19 | **Експорт `.opyx` при активних станціях**: `EventArchiveService.ExportAsync` робить `CheckpointAsync` і читає живий `event.db` з `FileShare.ReadWrite`, поки станції пишуть | Архів може містити базу з уже застарілим WAL | `SqliteConnection.BackupDatabase` у temp-копію, яка й пакується; решта папки — як зараз (§4.2). Те саме локально |
| П20 | **macOS 14+ «Локальна мережа»**: UDP-broadcast і з'єднання до LAN-адрес потребують дозволу; `build/mac/Info.plist` не має `NSLocalNetworkUsageDescription` | На Mac-станції запит може не з'явитись, broadcast мовчки не працює — саме типовий сценарій плану | Додати ключ у `Info.plist`; перевірка `local-network-permission` у діагностиці станції (§4.3) |
| П21 | **Сесія тримає копію `EventDay`/`EventSummary`** (`CurrentDay` з дисципліною й блокуванням, `CurrentEvent` з назвою); оновлюються лише через `UpdateCurrentDay`/`UpdateCurrentEvent`, які викликають сторінки-редактори | Зміна дисципліни/блокування дня іншою станцією перезавантажить сторінки, але сесія лишиться старою | `SessionService` підписується на `IDataChangeBus`: на `Days`/`Competition` активного змагання перечитує день/метадані й викликає `UpdateCurrentDay`/`UpdateCurrentEvent` (§3.1) |
| П22 | **`PageViewModelBase` не має спільного хука перезавантаження**: `SessionChanged` обробляє `MainWindowViewModel` (CanExecute команд), кожна сторінка підписується і завантажується по-своєму | «ObserveData → той самий шлях, що й SessionChanged» нема до чого прив'язати | `PageViewModelBase.ReloadAsync()` (virtual) + переведення ~8 сторінок на нього (§1.4); етап 1 — 3 дні, не 2 |
| П23 | `IEventArchiveService.IsValidIdentifier` — синхронний чистий метод; `ExportAsync(identifier, destinationArchivePath)` і `PreviewImportAsync/ImportAsync(archivePath, …)` працюють із **локальними** шляхами файлів | DispatchProxy над `Task`-методами його не покриває; шляхи архіву — на машині станції | `RemoteArchiveService` — ручна реалізація, не DispatchProxy: `IsValidIdentifier` локально, експорт/імпорт — потоки через `/files/*` (§4.2). Контрактний тест має явний список «локальних» методів |
| П24 | `IEventFiles.ListDayFilesAsync` (перша версія §1.6) не має споживача в коді | Зайвий публічний контракт | Прибрано; додано лише те, що викликається сьогодні (папки днів, `SaveDayFileAsync`, локальні шляхи) |
| П25 | Два екземпляри над одним `./data` на одній машині (допускається CLAUDE.md) ділитимуть `StationTokens` і `HostEnabled` в `app.db` | Два хости на одній машині — не сценарій, але не повинні ламати одне одного | Хост займає порт при старті; якщо зайнятий іншим екземпляром — бере наступний і повідомляє; токени спільні, це безпечно |

Жодна знахідка не блокує підхід; усі закриваються в етапах 1–2, крім П19–П20 (етап 4).

## 1. Етап 1 — підготовка ядра (нічого не змінюється для користувача), 3 дні

### 1.1. Спільні JSON-опції — `BusinessLogic/Serialization/RpcJson.cs`

```csharp
public static class RpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,                       // П1: ValueTuple
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new DisciplineIdJsonConverter(), new JsonStringEnumConverter() },
    };
}
```
`DisciplineIdJsonConverter` — поруч. Enum як рядки (стійкість до перестановки членів між версіями).
BusinessLogic уже використовує `System.Text.Json` (14 файлів), нових залежностей не додається.

### 1.2. Контрактний тест — `tests/OrientPyx.Tests/Core/Network/RpcContractTests.cs`

Рефлексією по `IEventStore`, `IEventFolderScanner`, `IEventCatalogService`, `IEventFiles`, `IEventArchiveService`,
`IResultsAppData`:
- кожен параметр і результат (крім `CancellationToken`, `IProgress<>`) має бути серіалізовним: тест будує
  екземпляр через простий генератор (власний, ~60 рядків: рекурсивно заповнює властивості) і перевіряє round-trip
  `Serialize → Deserialize → Serialize` на рівність JSON;
- `IProgress<>` дозволений лише в методах зі списку `JobMethods` (§3.5);
- синхронні методи дозволені лише зі списку `LocalMethods` (`IEventArchiveService.IsValidIdentifier`,
  `IEventFiles.LocalReadoutDirectory`, `IEventFiles.MonitorOutputDirectory`) — вони виконуються на станції локально;
- жодних `Stream`, `Func<>`, `Action<>`, `IQueryable`, `DbContext`; жодних публічних полів у типах моделей
  (бо `IncludeFields`).
Падає на збірці, якщо хтось додасть метод із несеріалізовним типом.

### 1.3. Шина змін — `BusinessLogic/Interfaces/IDataChangeBus.cs`

```csharp
public enum DataArea { Competition, Days, ControlPoints, Groups, GroupDaySettings, CourseVariants, Chips,
    Regions, Clubs, Dusshes, Participants, ParticipantDays, Readouts, Fees, Settings, CustomFields, Relay,
    AppData, Any }

public sealed record DataChange(string EventFolderPath, DataArea Area, long Version, string Origin);

public interface IDataChangeBus
{
    long CurrentVersion(string eventFolderPath);
    void Publish(string eventFolderPath, DataArea area, string origin);
    event EventHandler<DataChange>? Changed;                              // на UI-потік не маршалиться — підписник сам
}
```
Реалізація `DataChangeBus` (BusinessLogic/Services): `ConcurrentDictionary<string,long>` версій на змагання +
**один глобальний** монотонний номер події (`Sequence`) і кільцевий буфер останніх 512 подій — `Last-Event-ID` SSE
один на потік, а станція може міняти змагання (§3.1). `AppData` публікується з `EventFolderPath = "*"`.

**Origin.** `IWorkspaceMode.SelfOrigin` — ім'я цієї машини (`StationName` на станції, `HostName` на хості, `"local"`
без мережі). На хості `RpcDispatcher` кладе ім'я станції-ініціатора в `AsyncLocal<string?> DataChangeOrigin.Current`,
`NotifyingEventStore` бере його (або `SelfOrigin`). **На станції `NotifyingEventStore` не публікує нічого** (роль
`Station` → пропуск): єдине джерело подій — SSE з хоста, тож одна зміна дає рівно одну подію.

**`NotifyingEventStore`** (BusinessLogic/Services/Store/): декоратор `IEventStore`, що після успішного виклику
методу-запису публікує подію. Карта «метод → область» — явний словник у коді (не евристика по префіксу), з
тестом, що кожен метод `IEventStore` є або у словнику записів, або у списку читань (`Get*`, `Count*`,
`EnsureCreated`, `Checkpoint`, `Release`, `GetDaySnapshot`). Та сама карта віддає `IsWrite(method)` проксі станції
(`Idempotency-Key`, таймаут). **`NotifyingAppStore`** — такий самий декоратор над чотирма методами запису
`IAppStore` (очки, розряди, кваліфікація, умови) → `DataArea.AppData`.

### 1.4. Підписка сторінок — `PageViewModelBase`

```csharp
protected virtual Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask; // П22: новий спільний хук
protected void ObserveData(params DataArea[] areas);   // викликається у конструкторі нащадка
```
`ObserveData` підписує VM на `IDataChangeBus.Changed` лише поки сторінка активна (`IsActive`, виставляє
навігація); подія з відповідною областю для активного змагання (або `"*"`) → `ReloadAsync`. Правила: не частіше
1 раз/с (coalescing таймер на `DispatcherTimer`); якщо `SheetTable` у режимі редагування клітинки — відкласти до
`LostFocus`; подія з `Origin == IWorkspaceMode.SelfOrigin` ігнорується (сторінка-ініціатор уже оновилась; інші
сторінки неактивні й перечитають при показі). **Перезавантаження не чіпає стан оператора**: фільтри, пошук і
сортування живуть у `SheetTable` і переживають заміну рядків уже сьогодні, а от виділений рядок і прокрутка — ні
(`ParticipantsViewModel.ReloadContentAsync` робить `Participants.Clear()` і скидає `SelectedParticipant`); `ReloadAsync`
зобов'язаний запам'ятати Id виділеного рядка та зміщення прокрутки й відновити їх після заміни рядків (`SheetTable.
RestoreSelection(id, offset)`), інакше оператор хоста втрачатиме виділення щоразу, коли станція читає чіп. Кожна
сторінка переносить свій поточний шлях завантаження
(те, що вона робить на `SessionChanged`/при відкритті) у `ReloadAsync`: `ParticipantsViewModel`,
`FinishReadViewModel`, `GroupsViewModel`, `ChipsViewModel`, `CompetitionDaysViewModel`, `RelayTeamsViewModel`,
`DashboardViewModel`, протоколи (прев'ю). Це основний обсяг етапу 1.

Побічна користь уже на етапі 1: шина локальна на процес, тож два локальні екземпляри над одним `./data` досі не
бачать змін одне одного — не зміниться, але код підписки буде готовий.

### 1.5. Режим і маршрутизатори (П15)

```csharp
public enum WorkspaceRole { Local, Host, Station }
public interface IWorkspaceMode
{
    WorkspaceRole Role { get; }
    string SelfOrigin { get; }            // "local" | StationName | HostName
    StationInfo? Station { get; }         // режим Station: hostId, hostName, endpoint
    HostInfo? Host { get; }               // режим Host: endpoint, pin
    event EventHandler? Changed;
}
```
`RoutingEventStore`, `RoutingFolderScanner`, `RoutingCatalogService`, `RoutingEventFiles`, `RoutingArchiveService`,
`RoutingResultsAppData` — **`DataAccess/Network/Routing/`**: `Role == Station ? remote : local`, де `local` — клас
DataAccess/BusinessLogic (`EventStore`, `EventFolderScanner`, `EventCatalogService`, `LocalEventFiles`,
`EventArchiveService`, `LocalResultsAppData`), `remote` — `Remote*` (§2.2), на етапі 1 `null` (кидає
`InvalidOperationException`), роль завжди `Local`.

Ланцюг DI (`DataAccessServiceCollectionExtensions` + `BusinessLogicServiceCollectionExtensions`):
```
IEventStore            = NotifyingEventStore( RoutingEventStore( EventStore | RemoteEventStore ) )
IEventFolderScanner    = RoutingFolderScanner( EventFolderScanner | RemoteFolderScanner )
IEventCatalogService   = RoutingCatalogService( EventCatalogService | RemoteCatalogService )
IEventFiles            = RoutingEventFiles( LocalEventFiles | RemoteEventFiles )
IEventArchiveService   = RoutingArchiveService( EventArchiveService | RemoteArchiveService )
IResultsAppData        = RoutingResultsAppData( LocalResultsAppData | RemoteResultsAppData )
IAppStore              = NotifyingAppStore( AppStore )
```
Локальні реалізації реєструються **під своїми класами** (`AddSingleton<EventStore>()`), інтерфейси —
маршрутизаторами через фабричні лямбди; `EventCatalogService` (BusinessLogic) отримує в конструктор `IEventStore`
з DI, тобто вже зовнішній декоратор — для нього нічого не змінюється. Хост-диспетчер (§2.1) резолвить
`IEventStore` з DI, тому правки станцій проходять через `Notifying`.

Додати `<FrameworkReference Include="Microsoft.AspNetCore.App" />` у `OrientPyx.DataAccess.csproj`; зібрати
`dotnet build`, `dotnet test`, прогнати `build/publish.ps1` локально без `-Upload` і зафіксувати розмір
`OrientPyx-win-Setup.exe` до/після.

### 1.6. `IEventFiles` (П4, П17, П24) — `BusinessLogic/Interfaces/IEventFiles.cs`

```csharp
public interface IEventFiles
{
    // папка змагання (на станції — на хості)
    Task EnsureDayFolderAsync(string eventFolderPath, int dayNumber, CancellationToken ct = default);
    Task MoveDayFolderAsync(string eventFolderPath, int oldNumber, int newNumber, CancellationToken ct = default);
    Task<string> SaveDayFileAsync(string eventFolderPath, int dayNumber, string fileName, byte[] content, CancellationToken ct = default); // повертає збережене ім'я

    // локальні шляхи цієї машини (виконуються локально в обох ролях)
    string MonitorOutputDirectory(string eventFolderPath, string identifier, int dayNumber);
    string LocalReadoutDirectory(string eventFolderPath, string identifier, int? dayNumber);   // null → папка chips
    Task<string?> GetMachineValueAsync(string eventFolderPath, string key, CancellationToken ct = default);
    Task SetMachineValueAsync(string eventFolderPath, string key, string? value, CancellationToken ct = default);
}
```
`LocalEventFiles` (DataAccess/FileSystem/) переносить поточний код із `CompetitionEditorService` (`SaveDayFileAsync`,
`SameContent`, `ShortHash`) і `DayFolders`; локальні шляхи = папка дня / `chips` у папці змагання (як сьогодні),
`machine.json` локально не використовується (значення лишаються в event.db, поведінка Local не змінюється).
`RemoteEventFiles`: три перші методи — RPC, локальні шляхи → `<data root>/local/<hostId>/<identifier>/…`,
`machine.json` у тій самій папці. Редактор і каталог отримують `IEventFiles` у конструктор; у
`CompetitionEditorService` не лишається жодного `Directory.`/`File.`. `ResolveMonitorFilePath`/
`GetMonitorOutputDirectory` → через `MonitorOutputDirectory`.

### 1.7. `IResultsAppData` (П16) — `BusinessLogic/Interfaces/IResultsAppData.cs`

```csharp
public interface IResultsAppData
{
    Task<IReadOnlyList<PointsRule>> GetPointsRulesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SportRank>> GetRanksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RankQualificationRow>> GetRankQualificationAsync(CancellationToken ct = default);
    Task<(int MinParticipants, int MinRegions, int CountForRank)?> GetRankConditionsAsync(CancellationToken ct = default);
}
```
(Сигнатури збігаються з чотирма методами `IAppStore` (рядки 62, 100, 124, 147) і з полями `DayResultsAppData`;
кортеж умов — через `IncludeFields`, як П1.) `LocalResultsAppData`
(BusinessLogic/Services) делегує `IAppStore`; `LoadDayResultsAppDataAsync` і решта читань цих таблиць у
`CompetitionEditorService` переходять на `IResultsAppData`. Сторінки «Очки» та «Розряди» при `Role == Station`
показують банер «Редагується на хості» і вимикають редагування (їх `IAppStore`-записи не йдуть на хост).

Критерій завершення етапу 1: `dotnet test` зелений, `scripts/diff-results.ps1` — без змін, UI-тести зелені,
розмір пакета зафіксований.

## 2. Етап 2 — хост і станція (мінімум), 4 дні

### 2.1. Хост — `DataAccess/Network/Host/`

- `NetworkHost : INetworkHost` — `StartAsync(HostOptions)`, `StopAsync()`, `Endpoint`, `Pin`, `HostId` (GUID,
  генерується раз і зберігається в `AppSettingsRow.HostId`), `Stations` (`IReadOnlyList<ConnectedStation>`), подія
  `StationsChanged`. Усередині `WebApplication.CreateEmptyBuilder(new())` + `builder.WebHost.UseKestrelCore()` (П:
  `CreateSlimBuilder` усе ще читає `appsettings.json` і `ASPNETCORE_*`), `Listen(IPAddress.Any, port)`,
  `UseResponseCompression()`, `Logging.ClearProviders()`.
- `PairingEndpoint` — `POST /pair`: перевірка `protocolVersion`, PIN (4 цифри, генерується при старті, показується
  в UI; 5 помилок → пауза 30 с), відповідь `PairResponse(token, hostId, hostName, appVersion, protocolVersion,
  plugins[], serverTimeUtc)`. Токени — `StationTokenRow` в `app.db` (міграція `AppDbContext`: `StationTokens(Token PK,
  StationName, IssuedAt, LastSeenAt)`), щоб переживали перезапуск хоста.
- `RpcEndpoint` — `POST /rpc/{iface}/{method}`: `iface ∈ {EventStore, FolderScanner, Catalog, EventFiles, Archive,
  AppData}`. Тіло — `JsonElement[]`. Виконавець `RpcDispatcher`: кеш `MethodInfo` по імені (перевантажень у цих
  інтерфейсах немає — закріпити тестом), десеріалізація кожного аргументу у тип параметра, `CancellationToken` →
  `HttpContext.RequestAborted`, `IProgress<>` → `JobProgressSink` (§3.5). Результат → JSON, `Task` без значення →
  `204`. Помилка → `409`/`404`/`400`/`500` з тілом `RpcError(type, message, kind)` (П9).
- **Переклад ідентифікатор ↔ шлях** (П18): `EventPathMapper` у диспетчері — параметр з ім'ям `eventFolderPath` і
  властивість `EventSummary.FolderPath` у вхідних аргументах: `EventIdentifier.IsValid(value)` (інакше `400`) →
  `Path.Combine(eventsPath, value)`; у результатах (`EventSummary`, списки) — назад на `Path.GetFileName`. Станція
  ніколи не бачить шляхів хоста.
- `EnsureCreatedAsync` на хості — під `SemaphoreSlim(1)` на ідентифікатор (П13).
- **Ідемпотентність**: заголовок `Idempotency-Key` для записів → `IdempotencyCache` (`MemoryCache`, TTL 10 хв):
  повтор повертає збережену відповідь. Ключ генерує проксі станції на кожен виклик методу-запису.
- **Origin** (П/§1.3): `RpcDispatcher` виставляє `DataChangeOrigin.Current = station.Name` на час виклику.
- `GET /ping` (без токена): `{hostId, hostName, appVersion, protocolVersion, time}`.
- Усі хендлери — `Results.Json(…, RpcJson.Options)`; ніяких контролерів/MVC.

### 2.2. Станція — `DataAccess/Network/Station/`

- `RpcChannel` — один `HttpClient` (`SocketsHttpHandler`, `PooledConnectionLifetime = 5 min`), базова адреса,
  токен; `CallAsync<T>(iface, method, object?[] args, RpcCallKind kind, CancellationToken)` з таймаутом за
  категорією (читання 10 с, запис 60 с, job — без) і повторами (§3.3). Помилка → виняток за `RpcError.type` (П9).
- `RemoteProxy<TInterface>` — фабрика `DispatchProxy.Create<TInterface, RpcProxy>()`; `RpcProxy.Invoke` → знімає
  `CancellationToken`/`IProgress` з аргументів, визначає `IsWrite` за тією ж картою, що й `NotifyingEventStore`,
  викликає `RpcChannel`, розгортає `Task`/`Task<T>` через кешовані делегати по типу повернення.
- `RemoteEventStore = RemoteProxy<IEventStore>` з двома локальними перекриттями: `ReleaseAsync`/`CheckpointAsync`
  → `Task.CompletedTask` (П8); `GetDaySnapshotAsync` — через `SnapshotCache` (§3.4).
- `RemoteFolderScanner`, `RemoteCatalogService`, `RemoteResultsAppData` — проксі; `RemoteEventFiles` (три RPC +
  локальні шляхи, §1.6) і `RemoteArchiveService` (§4.2) — ручні реалізації (П23).
- `NetworkStation : INetworkClient` — `ConnectAsync(endpoint, pin)`, `DisconnectAsync()`, `State`
  (`Disconnected | Connecting | Connected | Reconnecting`), `HostInfo`; після `/pair` перемикає `IWorkspaceMode`
  на `Station` і підміняє реалізації в маршрутизаторах; `ISessionService.Clear()` → список змагань хоста.

### 2.3. UI — сторінка «Спільна робота» (Presentation)

- `NetworkViewModel` (singleton, `PageViewModelBase`), `NetworkView.axaml`; пункт меню «Файл → Спільна робота…»;
  індикатор ролі в `MainWindow` поруч із блоком фонових процесів (`NetworkActivity : IBackgroundActivity`:
  «Хост · 2 станції» / «Станція · Головний-ноут»).
- Картка хоста: перемикач, PIN великим шрифтом, адреси (усі IPv4), таблиця станцій (ім'я, IP, останній запит,
  кнопка «Відключити»).
- Картка станції: поле адреси (етап 4 додасть список), PIN, «Підключитися», стан з'єднання; поле «Ім'я цієї
  станції» (типово `Environment.MachineName`, зберігається в `AppSettingsRow.StationName` — міграція app.db).
- Локалізація: `Network.*` в обох JSON; довідка `Help.Network.*`.
- Налаштування хоста (`AppSettingsRow`: `HostEnabled`, `HostPort`, `HostName`, `HostId`) — щоб хост автостартував
  при запуску програми, якщо був увімкнений; якщо порт зайнятий іншим екземпляром — наступний вільний (П25).

### 2.4. Що виконується лише на хості

Через `RemoteCatalogService`/`RemoteArchiveService` на хості виконуються: створення змагання, перейменування
ідентифікатора, приховування, експорт/імпорт `.opyx` (§4.2). Перейменування і видалення змагання з активними
станціями на нього хост відхиляє `InvalidOperationException("Станції підключені: …")`.

### 2.5. Останній сеанс (П7)

`LastSessionRow` + `Mode` (`Local`/`Station`), `HostId`. `SaveLastSessionAsync` пише поточний режим. `TryRestoreLastAsync`:
`Mode == Station` → лише після `ConnectAsync` до того ж `HostId` (§3.6), інакше повертає `false` і UI показує картку
«Підключитися» із запам'ятованим хостом; у режимі `Local` рядок зі станційним режимом ігнорується.

Критерій завершення етапу 2: на двох машинах (Windows-хост, Mac-станція) мандатка, групи, чіпи, зчитка з
Mac-зчитувача у базу хоста; результати, очки й розряди на станції збігаються з хостом; `dotnet test` зелений.

## 3. Етап 3 — стійкість і живі оновлення, 3 дні

### 3.1. SSE на хості — `GET /changes`

`ChangeStreamEndpoint`: підписка на `IDataChangeBus.Changed`, `text/event-stream`, формат
`id: <seq>\nevent: change\ndata: {"eventPath":…,"area":…,"version":…,"origin":…}` (`seq` — глобальний номер події,
П/§1.3); heartbeat `: hb` кожні 10 с; при підключенні з `Last-Event-ID` досилає події з буфера або
`event: full-refresh`. Один потік на станцію.

**Сесія** (П21): `SessionService` підписується на шину; `Days` або `Competition` для `CurrentEvent` → перечитує
`GetDaysAsync`/метадані й викликає `UpdateCurrentDay`/`UpdateCurrentEvent` (вони вже піднімають `SessionChanged`).
Працює і на хості (правки станцій), і на станції (події з SSE).

### 3.2. Слухач на станції — `RemoteChangeListener`

`HttpClient` зі `Timeout = Infinite`, `ResponseHeadersRead`, читання рядків; таймаут тиші 30 с → перепідключення;
кожна подія → локальний `IDataChangeBus.Publish(...)` (із версією хоста й origin хоста/станції). Паралельно
`GET /version?event=` раз на 15 с → розбіжність версій → синтетичний `full-refresh`.

### 3.3. Повтори в `RpcChannel`

Читання: 3 спроби (0.5/1/2 с) на `HttpRequestException`/`TaskCanceledException` (таймаут). Записи: ті ж правила
**лише** з `Idempotency-Key`. Після вичерпання — `HostUnreachableException` → `NetworkStation.State = Reconnecting`,
банер у UI (`PluginNoticeViewModel`-подібний `NetworkNoticeViewModel` над сторінкою), редагування `SheetTable`
блокується (`IsReadOnly` через binding на стан), значення поточної клітинки зберігається у VM і відправляється
після відновлення.

### 3.4. Кеш знімка на станції

`SnapshotCache` у `RemoteEventStore`: `GetDaySnapshotAsync(dayId)` повертає кешований знімок, доки версія
змагання у `IDataChangeBus` не зміниться; інвалідація також після будь-якого власного запису. Зменшує трафік при
переключенні сторінок (П12).

### 3.5. Довгі операції — `/jobs`

`JobRegistry` на хості: `ImportParticipantsBatchAsync` виконується у фоні; `POST /rpc/EventStore/
ImportParticipantsBatchAsync` одразу повертає `{jobId}`; станція опитує `GET /jobs/{id}` (300 мс) → `JobStatus(stage,
current, total, finished, resultJson, error)` і прокидає у локальний `IProgress<ImportProgress>`. Список
`JobMethods` — явний (поки один метод); контрактний тест вимагає `IProgress<>` лише у них.

### 3.6. Перезапуски

Хост: токени в `app.db` (§2.1). Станція: `AppSettingsRow.StationHost` (`host:port`), `StationToken`, `StationHostId`;
при старті `TryRestoreLastAsync` у режимі станції спершу `ConnectAsync` (до 5 с, із показом «Підключення до
хоста…»), потім відновлює змагання/день (П7, §2.5).

## 4. Етап 4 — автопідключення, діагностика, файлові операції, 4 дні

### 4.1. Пошук хоста — `DataAccess/Network/Discovery/`

`DiscoveryResponder` (хост, `UdpClient` на `47831`, відповідь на `OPYX?` JSON `{hostId, name, version, ip[], port,
competition}`), `DiscoveryClient` (станція, запит кожні 2 с на **subnet-broadcast кожного IPv4-інтерфейсу**
(`NetworkInterface.GetAllNetworkInterfaces` → `UnicastAddresses` з маскою), бо `255.255.255.255` у Windows іде лише
одним інтерфейсом; список `DiscoveredHost` з TTL 6 с). **Код підключення**: `ConnectionCode.Encode(IPv4, port)` → 12
символів Base32 (без 0/O/1/I), `Decode` на станції.

### 4.2. Архів через хост (П19, П23)

`RemoteArchiveService`: `ExportAsync(identifier, localDestination)` → `GET /files/archive?identifier=` (хост збирає zip у
temp через локальний `EventArchiveService`, стрімить, видаляє) і зберігає у локальний шлях; `PreviewImportAsync`/
`ImportAsync(localArchivePath, …)` → `POST /files/import` (multipart, хост зберігає у temp і викликає локальний
сервіс); `IsIdentifierAvailableAsync` — RPC; `IsValidIdentifier` — локально. Повідомлення про прогрес — через `/jobs`.
`EventArchiveService.ExportAsync` (локальний, обидві ролі) замість читання живого `event.db` робить
`SqliteConnection.BackupDatabase` у temp-файл і пакує його під ім'ям `event.db`; `-wal`/`-shm` не пакуються.

### 4.3. Діагностика — `BusinessLogic/Interfaces/IStationDiagnostics.cs` + `DataAccess/Network/Diagnostics/`

```csharp
public sealed record DiagnosticCheck(string Key, CheckState State, string Detail, DiagnosticFix? Fix);
public sealed record DiagnosticFix(string Key, bool NeedsElevation);
public interface IStationDiagnostics
{
    Task<IReadOnlyList<DiagnosticCheck>> RunHostChecksAsync(int port, CancellationToken ct);
    Task<IReadOnlyList<DiagnosticCheck>> RunStationChecksAsync(DiscoveredHost? host, string? endpoint, CancellationToken ct);
    Task<DiagnosticCheck> ApplyFixAsync(string fixKey, CancellationToken ct);
}
```
Перевірки хоста (у порядку): `lan-address` (IPv4 не loopback/не 169.254), `port-free`, `firewall-rule`
(`HNetCfg.FwPolicy2` через COM — без прав; macOS: `socketfilterfw --getglobalstate`), `network-profile`
(`Get-NetConnectionProfile`, **лише інформаційна**, без fix — правило з `profile=any` діє в будь-якому профілі),
`self-reach` (підключення до `http://<lan-ip>:<port>/ping` з таймаутом 3 с). Виправлення: `open-hotspot-settings`
(`ms-settings:network-mobilehotspot`), `add-firewall-rule` (підвищений `netsh advfirewall firewall add rule
name="OrientPyx" dir=in action=allow protocol=TCP program="<exe>" profile=any` + UDP `47831`;
`ProcessStartInfo{Verb="runas",UseShellExecute=true}`; macOS — `osascript -e 'do shell script "…socketfilterfw --add …
--unblockapp …" with administrator privileges'`), `remove-firewall-rule`, `open-local-network-settings` (macOS,
`x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork`). Перевірки станції:
`local-network-permission` (macOS 14+: жодної відповіді на broadcast і `/ping` падає з `EHOSTUNREACH`/`EPERM` →
порада з кнопкою, П20), `host-discovered`, `host-reachable` (`/ping`), `version-compatible` (`protocolVersion`),
`plugins-match`, `clock-skew` (> 2 с), `readout-type-match`. Усі результати → `IActivityLog`.
`build/mac/Info.plist` отримує `NSLocalNetworkUsageDescription` (П20).

Сторінка «Спільна робота» показує перевірки списком зі станом і кнопкою «Виправити» (вимкнена, поки виконується);
після зелених перевірок — екран «Готово: на іншому комп'ютері оберіть „Підключитися“ → `<ім'я>`, PIN **482 913**».

### 4.4. Дрібниці режиму станції (П5, П6, П11, П17)

- `FileActivityLog.UseEventFolder` → у режимі станції пише локально (`<data root>/logs/<hostId>-<identifier>/`) і
  шле `POST /log` (батчем раз на 2 с) з іменем станції; хост дописує «[Mac-Реєстрація] …» у свій лог змагання.
- `TableLayoutStore.CurrentScopeId` → `"<hostId>/<identifier>"` у режимі станції; файл локальний.
- `ResultsDebugSnapshot` і «Відкрити папку змагання» (`FileReveal`) — ховати в режимі станції
  (`IWorkspaceMode.Role != Station`); монітор пише у `IEventFiles.MonitorOutputDirectory`.
- Дефолтні шляхи зчитки: `ChipsViewModel`, `FinishReadViewModel`, `ParticipantsViewModel` (сканування чіпів) беруть
  папку з `IEventFiles.LocalReadoutDirectory` замість `FolderPath`; `ChipScanViewModel`/`SaveChipScanSettingsAsync`
  у режимі станції тримають `ReadoutFilePath` у `machine.json` (`Get/SetMachineValueAsync("chipScan.readoutPath")`), а
  на хост відправляють `ChipScanSettings` з порожнім шляхом.
- `FileReadoutPoller` і друк — без змін (локальне залізо).

## 5. Етап 5 — тести, документація, реліз, 2 дні

### 5.1. Тести (`tests/OrientPyx.Tests/Core/Network/`)

- `LoopbackHostFixture`: піднімає `NetworkHost` на `127.0.0.1:0` над тимчасовим data root із `SyntheticCompetition`,
  і другий DI-контейнер у режимі станції, підключений до нього.
- `RemoteResultsGoldenTests`: `ComputeDayResultsAsync` через `RemoteEventStore` + `RemoteResultsAppData` для кожного
  дня == `Golden/autotest.results.json` (той самий `ResultsSnapshot`); документи (`OutputsTests`) через станцію ==
  локальні.
- `RpcContractTests` (§1.2), `NotifyingEventStoreMapTests` (кожен метод класифікований), `IdempotencyTests`
  (повтор запису з тим самим ключем не дублює учасника), `ReconnectTests` (хост зупинено → станція у `Reconnecting`,
  запущено → відновлення і `full-refresh`), `JobsTests` (імпорт із прогресом через `/jobs`),
  `EventPathMapperTests` (`..`, роздільники, невалідний ідентифікатор → `400`; відповіді без шляхів хоста),
  `SessionRefreshTests` (зміна дисципліни дня з «іншої станції» → `CurrentDay` оновлений),
  `ArchiveBackupTests` (експорт під час запису іншого з'єднання дає консистентну базу),
  `DiagnosticsTests` (над підробленим `INetworkEnvironment`).
- UI: `NetworkPageScreenshotTests` для обох карток.

### 5.2. Документація й реліз

`CLAUDE.md`: розділ «Мережевий режим» (межа — `IEventStore`/`IEventFiles`/`IResultsAppData`/каталог/архів; нові
методи — лише серіалізовні; класифікувати у `NotifyingEventStore`; файлові операції — тільки через `IEventFiles`;
по мережі — ідентифікатор, не шлях). `docs/network-protocol.md` — ендпоїнти, формати, коди помилок, версія
протоколу. Довідка у програмі. Реліз мінорною версією (Windows + macOS); у нотатках — інструкція на одну сторінку
з трьома кроками.

## 6. Порядок і контрольні точки

| Етап | Дні | Контрольна точка |
|---|---|---|
| 1 | 3 | зелені тести, результати без змін, розмір пакета відомий |
| 2 | 4 | **демо на двох машинах** — рішення про продовження |
| 3 | 3 | живі оновлення, обрив/відновлення без втрати введеного |
| 4 | 4 | підключення без введення адреси; усі перевірки з кнопками; експорт/імпорт через хост |
| 5 | 2 | тести §5.1 зелені, документація, реліз |

Разом ~16 робочих днів. Етапи 3 і 4 можна вести паралельно після демо.

## 7. Ризики реалізації (окрім мережі на місці)

- `DispatchProxy` і `Task<T>` з рефлексією: один раз написати і покрити тестом; кешувати делегати.
- `IncludeFields` вмикає й поля в інших типах — публічних полів у моделях сьогодні немає (перевірено), контрактний
  тест не дасть їм з'явитися непомітно.
- Kestrel у desktop-процесі: `WebApplication.CreateEmptyBuilder` + `UseKestrelCore` (не `CreateSlimBuilder`, який
  усе ще підхоплює `appsettings.json` і змінні оточення), `Logging.ClearProviders()`.
- Двійкова сумісність протоколу між версіями хоста й станції: `protocolVersion` інкрементується при будь-якій
  зміні сигнатур; розбіжність → відмова `/pair` з підказкою оновити.
- Переклад ідентифікатор ↔ шлях у диспетчері спирається на ім'я параметра `eventFolderPath` і тип `EventSummary`:
  контрактний тест перевіряє, що кожен метод мережевих інтерфейсів або має такий параметр, або явно позначений
  як «без змагання» (`IResultsAppData`, `IsIdentifierAvailableAsync`, `CreateEventAsync`).
