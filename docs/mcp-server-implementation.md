# Локальний MCP-сервер («Асистент») — implementation-план

> Статус: **реалізовано** 2026-11-26 (усі етапи 0–4; рішення §7 ухвалено). Відхилення від цього плану, прийняті при
> реалізації:
> - **Інструменти — у BusinessLogic**, не в `DataAccess/Mcp/Tools`: це звичайні класи (`IMcpToolHandler`) над
>   `McpContext`, без SDK; метадані всіх 38 інструментів — один статичний `McpToolCatalog` (його ж віддає міст, коли вікно
>   не працює). SDK (`ModelContextProtocol.AspNetCore` **2.4.0**) — лише в `DataAccess/Mcp/` (сервер і міст). Файловий
>   ввід-вивід — через `IMcpFileAccess` (DataAccess), бо BusinessLogic файлів не торкається.
> - **Етап 0**: SDK на `WebApplication.CreateEmptyBuilder` працює; спільна `ToolCollection` у `ConfigureSessionOptions`
>   дає `tools/list_changed` на протоколах 2025-06-18 / 2025-11-25, а на 2026-07-28 (без сесій) новий список видно з
>   наступного запиту; `OrientPyx.exe --mcp-bridge` (WinExe) говорить stdio на Windows (`initialize` ≈ 2 с у Debug);
>   SQLitePCLRaw 3.0.3 дає authorizer / `sqlite3_limit` / `interrupt` / `stmt_readonly` / агрегати, JSON1 і `sqrt` є.
>   Не перевірено (немає клієнтів у середовищі розробки): Claude Desktop із `.mcpb` поза архівом (є запасний «Дописати в
>   налаштування»), реальні тайм-аути клієнтів (`ConfirmWait` = 40 с з запасом), macOS.
> - **Мітки протоколів**: `BuildLabels()` сторінок стали статичними `CreateLabels(ILocalizationService…)`, `IOutputLabels`
>   (Presentation) їх викликає; окремого `ProtocolLabelsFactory` не створено.
> - **Етап 3б**: з Presentation винесено `CsvParticipantMapper` (розбір CSV/XLSX: здогад колонок і перетворення),
>   `DrawLanes` (розподіл груп по коридорах), `XmlEncodingReader` / `CsvEncodingReader` (декодування файлів) — сторінки
>   й flow тепер на них. Модалки імпорту лишилися у flow; агент імпортує через ті самі методи редактора
>   (`PreviewParticipantImportAsync`, `FindImportDuplicatesAsync`, `ImportParticipantsAsync`, `Import*Async`).
> - **Експорт** пише в `exports` локальної папки дня (`IEventFiles.LocalReadoutDirectory` + `exports`; на станції — її
>   локальна папка), нового методу `IEventFiles.LocalExportDirectory` не знадобилося.
> - **«Невідмінний» крок**: окремого методу `IEventStore` «чи крок NotUndoable» не додано — `Undoable` статичний для
>   класу дії, а скасування NotUndoable-кроку повертає `UndoOutcome` з поясненням.
> - **Журнал дій агента** — у блоці «Асистент (MCP)» налаштувань (з «Скасувати»); активність «MCP» показує лічильники й
>   останню дію і відкриває налаштування (спільний шаблон блоку фонових процесів не має місця для списку).
> - **`NetworkProtocol.Version` = 10**, заголовок `X-OrientPyx-Origin-Tag` (`docs/network-protocol.md`).
> - Тести: `tests/OrientPyx.Tests/Core/Mcp/McpTests.cs` (політика, `EditorScope`, набори даних проти протоколу, пісочниця,
>   запис / undo / походження, `pending`, план жеребкування, HTTP-сервер з токеном / Origin, міст по stdio).

Деталізація `docs/mcp-server-plan.md` до рівня файлів, класів і сигнатур. Нумерація етапів та сама, що в плані
(0 прототип, 1 читання, 2 зміни, 3 операції, 4 реліз), але етап 3 розбито на 3а / 3б (М3).

## 0. Що показала перевірка коду

Перевірено `CompetitionEditorService` (6 303 рядки + partial-файли), `ICompetitionEditorService`, `SessionService`,
`UndoScope`, `UndoJournal`, `EditHistoryService`, `DataChangeBus` / `DataChangeOrigin`, `NotifyingEventStore`,
`PageViewModelBase.OnDataChanged`, `RpcDispatcher`, `NetworkHost`, `IEventFiles`, `Program.cs`, `AppDatabasePaths`,
`Controls/TextSearch.cs`, flow-сервіси імпорту й експорту в Presentation, `ProtocolsViewModel`, `DrawViewModel`, csproj.

**Що підтвердилося.** Усі ~50 методів редактора, названих у плані (§3.4, §6.3, §6.4), існують під цими назвами;
парсери `IIofXmlParser` / `IUofXmlParser` / `ICsvParser` / `ISpreadsheetParser` є; `UndoScope` і `DataChangeOrigin` — це
AsyncLocal, тож вони течуть через `UiOffloadProxy` і `Task.Run`; заголовок `X-OrientPyx-Undo-Step` і власник кроку = токен
станції працюють як описано; `NetworkHost` уже стоїть на `WebApplication.CreateEmptyBuilder` без Generic Host;
`PublishTrimmed` ніде не ввімкнено, тож рефлексія SDK працює; `TextSearch` чистий (лише `System.*` + `BusinessLogic.Models`)
і переноситься в BusinessLogic без змін. Підхід (сервер у процесі, SQL-пісочниця над плоскими таблицями, запис лише через
редактор) лишається правильним. Знахідки нижче — це уточнення, жодна з них не блокує підхід.

| # | Знахідка | Наслідок | Рішення |
|---|---|---|---|
| М1 | **Змагання беруться з сесії при кожному зверненні**: `CompetitionEditorService.FolderPath => _session.CurrentEvent?.FolderPath` (≈126 прямих читань `_session.Current*` у partial-файлах); 9 місць (`.Relay.cs`, `.NumberVariants.cs`) читають `_session.CurrentDay` **напряму**, минаючи `_dayOverride`; `ResolveDayAsync` порівнює з днем сесії; `EditHistoryService.RevertAsync` теж бере змагання із сесії | `EditorDayScope` з плану §3.3 покриває лише день. Якщо оператор перемкне змагання посеред пакетного виклику агента, решта пакета запишеться **в інші змагання**. Естафета й «Варіанти за номерами» ігнорували б день агента | Один `EditorScope` (змагання + день, AsyncLocal) замість `EditorDayScope`: `FolderPath` і `CurrentDay` редактора спершу дивляться в нього; 9 прямих читань переходять на приватні властивості; `ResolveDayAsync` порівнює з `CurrentDay`, `EditHistoryService` бере змагання зі scope. Перевірка на початку виклику (§3.3 плану) лишається, але після неї змагання вже закріплене (§2.1) |
| М2 | **Станція не побачить змін власного агента.** Хост ставить `DataChangeOrigin.Current = station.Name` (`RpcDispatcher:96`); сторінки (`PageViewModelBase:446`) і `SessionService:211` пропускають **будь-яку** подію з `SelfOrigin` своєї машини, а не лише подію «своєї сторінки», як сказано в плані §3.4 | Локально й на хості походження `mcp` ≠ `SelfOrigin`, тож усе гаразд. Але агент на станції пише через хост, подія повертається з іменем станції, і сторінки станції її пропускають: таблиця на тій самій машині не оновиться | Походження агента = `"<SelfOrigin>·mcp"`. Станція передає тег заголовком `X-OrientPyx-Origin-Tag: mcp`, хост штампує `"<station>·mcp"`. Строге порівняння з `SelfOrigin` тоді не спрацьовує, і сторінки перезавантажуються. `NetworkProtocol.Version` +1, оновити `docs/network-protocol.md` |
| М3 | **Оркестрація імпорту й експорту живе в Presentation**: `CsvImportFlow` / `XmlImportFlow` / `ParticipantImportFlow` (~790 рядків: розбір → зіставлення → попередній перегляд → дублікати → імпорт, з модалками всередині); `ProtocolsViewModel.GenerateAsync` (налаштування, плейсхолдери, `BuildLabels()` з локалізації, назва файлу); те саме в `StartProtocolsViewModel`, `StatementFlow`, `ParticipantExportFlow` | Інструменти в DataAccess не можуть цього викликати (напрямок залежностей). Основа «наявні парсери + `Import*Async`» з плану §6.4 — лише половина: правила зіставлення, область імпорту і склеювання протоколу довелося б продублювати | Етап 3б: перенести не-UI частину в BusinessLogic (`Services/Operations/`). Flow-сервіси лишаються оболонками з модалками над тими самими операціями. Підписи протоколів іде через `IOutputLabels` (реалізація в Presentation, як уже зроблено з `WinnersPrintLabelsFactory`). +2–3 дні до оцінки етапу 3 |
| М4 | **Локалізація недоступна в BusinessLogic і DataAccess** (`Localization` посилається лише з Presentation) | `get_help` (тексти `Help.*`), людський текст `ChangeSummary` у смузі підтвердження, назви prompts і підписи протоколів не зібрати в шарі, де план їх розмістив | `ChangeSummary` стає структурою (ключ + аргументи + рядки змін), а текст складає Presentation. `IMcpUi` дає `GetHelp(topic)`, назви prompts і `IOutputLabels` |
| М5 | **2 хв очікування підтвердження** (§7.3) — довше, ніж типовий тайм-аут запиту в клієнтах на TS SDK (60 с, зокрема Claude Desktop) | Клієнт обірве виклик, а дія все одно виконається, якщо людина натисне «Дозволити». Агент вирішить, що не вийшло, і повторить її | Чекати не довше за `ConfirmWait` (≤ 40 с, точне значення дає етап 0). Далі повертати `{status:"pending", confirmation_id}`, а агент чекає далі інструментом `await_confirmation`. Підтверджена дія виконується одразу, результат зберігається 10 хв. Поки чекаємо, надсилаємо progress-сповіщення, якщо клієнт дав `progressToken` **[рішення]** |
| М6 | **`preview: true` / `preview: false` — два незалежні обчислення** (§6.4) | Між ними інша станція чи оператор може змінити дані, і застосується не те, що людина бачила в «Деталях» | `preview` повертає `plan_id` (набір змін + версія даних, живе 15 хв); `apply_plan(plan_id)` застосовує саме його. Якщо версія змінилася, план перераховується, і коли результат інший — відмова «дані змінилися, перегляньте ще раз». «Деталі» в смузі показують той самий план **[рішення]** |
| М7 | **День «у вікні» не один**: день вибирають на кожній сторінці окремо (`day-is-per-page`), а `CurrentDay` сесії — це день, обраний останнім на будь-якій сторінці | Правило «ризикова — будь-що в іншому дні, ніж відкритий у вікні» (§7.2) спрацьовувало б випадково, а для багатоденного налаштування з чату — майже на кожен крок | Прибрати це правило. Ризикові дії лишаються такі: ті, що не скасовуються, понад 5 видалень або понад 50 оновлень, `open_competition`, `create_competition`, експорт поза папку змагань **[рішення]** |
| М8 | **Міст і stdout.** План ставить гілку `--mcp-bridge` після `VelopackApp.Run()`; будь-який рядок у stdout до мосту (лог Velopack, `Console.WriteLine`) зламає JSON-RPC. Шлях даних обчислює `RedirectDataRootIfInstalled()`, у якого є побічні дії (`MoveLegacyDataRoot`, `RescueDataFromInstallDir`) | Міст не стартує або перейменує папки даних, поки вікно відкрите | Гілка мосту йде **першою** в `Main`, до Velopack: хуки Velopack ніколи не запускаються з `--mcp-bridge`. Спершу `Console.SetOut(Console.Error)`. Корінь даних дає чиста `DataRootResolver.Resolve(appDir)`, витягнута з `RedirectDataRootIfInstalled` без побічних дій; файл виявлення шукаємо і в старому корені |
| М9 | **«Мінімальний набір» інструментів у мосту, коли OrientPyx не запущено** (§4.2) | Claude Desktop читає `tools/list` раз при старті. Якщо `list_changed` не підтримується, агент так і не побачить справжніх інструментів, доки людина не перезапустить клієнт | Міст — той самий exe, тож повний список він будує з тих самих типів інструментів, без їхнього виконання. Режим береться з файлу виявлення: після закриття програми файл лишається (pid мертвий → «OrientPyx не запущено»), видаляється лише при вимкненні MCP (→ «MCP вимкнено») |
| М10 | **Експорт на станції**: `IEventFiles.SaveDayFileAsync` пише в папку змагань **хоста** | Людина на станції не знайде файл, який для неї зробив агент | `IEventFiles.LocalExportDirectory(eventFolderPath, identifier, dayNumber)`: локально / на хості — `events/<id>/day{N}/exports`, на станції — `<data root>/local/<hostId>/<identifier>/day{N}/exports`, як уже зроблено з `LocalReadoutDirectory`. Метод локальний, тож його треба внести в список локальних у `RpcContractTests` |
| М11 | **Один сервер на комп'ютер без перехоплення**: друге вікно з увімкненим MCP показує «обслуговує інше вікно» і на цьому зупиняється | Перше вікно закрили, друге ще працює, а MCP мертвий | У стані `ServedElsewhere` сервер стежить за файлом виявлення (`FileSystemWatcher` + перевірка кожні 10 с) і займає порт, щойно власник зник |
| М12 | **SQL-пісочниця**: перевірка «лише SELECT» за текстом ненадійна; `hard_heap_limit` / `soft_heap_limit` діють на весь процес; у SQLite немає `median`; кеш скидається лише на `DataChange` змагань | Обхід перевірки через `WITH … ` і коментарі. Глобальний ліміт пам'яті вдарив би по живих `event.db`. Агенти постійно пишуть `median()` і отримують помилку. Зміна «Очок» в `app.db` (`DataArea.AppData`) лишила б у кеші застарілі `results.points` | `sqlite3_set_authorizer` (дозволено лише `SELECT`, `READ`, `FUNCTION`, `RECURSIVE`) + `sqlite3_stmt_readonly` + `sqlite3_limit` (`ATTACHED` = 0, `LENGTH` = 10 МБ, `SQL_LENGTH` = 100 КБ) + переривання таймером; жодних глобальних лімітів пам'яті. Свої агрегати `median`, `percentile` (`SqliteConnection.CreateAggregate`). Кеш скидається також на `AppData`. Наявність цих API у SQLitePCLRaw 3.0.3 перевіряє етап 0 |
| М13 | **DI для SDK**: `NetworkHost` будує власний контейнер `WebApplication` і отримує сервіси програми через `IServiceProvider` вручну; SDK шукає класи інструментів у контейнері `WebApplication` | Інструменти не отримають `ICompetitionEditorService` та інші сервіси програми | Один синглтон `McpToolContext` (сервіси програми) реєструється в контейнері `WebApplication`, і інструменти беруть його параметром. Режим (приховані інструменти) робимо фільтрами SDK для `tools/list` і `tools/call`. `list_changed` вимагає stateful-сесій, тож без `Stateless = true` |
| М14 | **Перевірка `Origin`**: CLI-клієнти не надсилають `Origin` | Сувора перевірка «Origin обов'язковий» відрізала б Claude Code і Gemini CLI | Без `Origin` — пропускаємо; з `Origin` — лише `http(s)://127.0.0.1|localhost[:port]`, інакше 403. `Host` — лише `127.0.0.1:<port>` / `localhost:<port>`, інакше 421 |
| М15 | Файл виявлення `mcp.json` у корені даних | Плутається з `mcp.json` VS Code, коли людина правитиме конфіги. На macOS і Linux токен лежить у файлі, який можуть читати інші користувачі | `mcp-endpoint.json`, на Unix права `0600` |
| М16 | macOS, непідписаний `.app`: Claude Desktop запускає бінарник із карантинною позначкою | Якщо OrientPyx жодного разу не відкривали вручну, Gatekeeper мовчки заблокує міст | Окремий пункт у документації: «спершу відкрийте OrientPyx». Міст пише зрозумілу помилку в stderr, а Claude Desktop показує його лог |

Дрібниці, без окремих рішень: крок агента з власником `null` ділить ліміт 200 кроків на (змагання, власник) з оператором.
Пакет агента — це один крок, тож щоб витіснити кроки оператора, потрібно 200 викликів. Залишаємо, як є. `SessionChanged`
після зміни агента (`SessionService.OnDataChanged` → `UpdateCurrentDay`) іде з потоку не UI, але так само вже приходять
зміни станцій, тож обробники з цим справляються.

## 1. Етап 0 — прототип ризиків (1,5 дня, гілка `spike/mcp`, у `main` не зливається)

| # | Перевірка | Критерій «так» | Якщо «ні» |
|---|---|---|---|
| 0.1 | `ModelContextProtocol.AspNetCore` (остання стабільна; версію фіксуємо) на `WebApplication.CreateEmptyBuilder`, `McpToolContext` із сервісами програми; `get_context` + `query` із Claude Code (`claude mcp add --transport http … --header`) | виклики працюють, Generic Host не з'являється, пакет більший не більше ніж на 2 МБ | власна реалізація Streamable HTTP поверх Kestrel (JSON-RPC, без SSE): тоді `list_changed` недоступне |
| 0.2 | Фільтр інструментів за режимом + `notifications/tools/list_changed` | хто оновлює список без перезапуску: Claude Code / Gemini CLI / Claude Desktop через міст | документація: «після зміни режиму перезапустіть чат»; виклик прихованого інструмента однаково відмовляє |
| 0.3 | `OrientPyx.exe --mcp-bridge` (WinExe) запускає Claude Desktop на Windows; `OrientPyx.app/Contents/MacOS/OrientPyx --mcp-bridge` на macOS | `initialize` / `tools/list` / `tools/call` через успадковані дескриптори; у stdout лише JSON-RPC | окремий маленький консольний `OrientPyx.McpBridge.exe` поруч з основним: новий бінарник → `SigningRules.ps1` + `artifact-configuration.xml` |
| 0.4 | `.mcpb`, у якому `mcp_config.command` — абсолютний шлях поза архівом | Claude Desktop встановлює й запускає | запасний варіант із плану §4.3 (дописати `claude_desktop_config.json` з резервною копією) |
| 0.5 | Інструмент, що чекає 90 с, у Claude Desktop / Claude Code / Gemini CLI; чи подовжують progress-сповіщення тайм-аут | знайдено найменший тайм-аут | `ConfirmWait` = найменший тайм-аут мінус 15 с (М5) |
| 0.6 | SQLitePCLRaw 3.0.3: `sqlite3_set_authorizer`, `sqlite3_limit`, `sqlite3_interrupt`, `sqlite3_stmt_readonly`, `CreateAggregate`; JSON1, віконні й математичні функції в `e_sqlite3` | усе доступне | без authorizer: дозволити лише один оператор + `stmt_readonly` + `query_only`; математику (`sqrt`, `ln`) — своїми функціями |

Результат етапу: короткий розділ «Результати етапу 0» в цьому файлі, обрані значення (`ConfirmWait`, версія SDK, спосіб
мосту) і оновлений план, якщо щось із запасних варіантів стало основним.

## 2. Етап 1 — ядро й читання (6 днів)

### 2.1. Контекст виклику (М1) — `BusinessLogic/Services/EditorScope.cs`

```csharp
public sealed record EditorContext(EventSummary Competition, EventDay? Day);

/// The competition + day a call works on when it is not the session's (an MCP call). AsyncLocal, leased like UndoScope.
public static class EditorScope
{
    public static EditorContext? Current { get; }
    public static IDisposable Enter(EventSummary competition, EventDay? day);
}
```

- `CompetitionEditorService`: приватні `CurrentEvent => EditorScope.Current?.Competition ?? _session.CurrentEvent`,
  `FolderPath` з нього, `CurrentDay => _dayOverride.Value ?? EditorScope.Current?.Day ?? _session.CurrentDay`.
  Механічна заміна всіх `_session.CurrentEvent` / `_session.CurrentDay` у `CompetitionEditorService*.cs` на ці
  властивості. Сесію напряму читає лише `ResolveDayAsync`, і порівнює він з `CurrentDay`.
- `EditHistoryService.RevertAsync` бере змагання з `EditorScope.Current ?? session`.
- Без scope поведінка не змінюється. Доказ: повний `tests/OrientPyx.Tests.sln` + golden-знімки без змін.
- Тести `Core/Mcp/EditorScopeTests`: scope не змінює `ISessionService.CurrentDay`; паралельні потоки з різними scope
  не перетинаються; перемикання сесії посеред виклику під scope не переносить запис.

### 2.2. Дрібна підготовка

- `Controls/TextSearch.cs` → `BusinessLogic/Services/Text/TextSearch.cs` (namespace змінюється, виклики в Presentation
  оновлюються).
- `DataRootResolver.Resolve(string appDir)` (Presentation, поруч із `Program`) — чиста частина
  `RedirectDataRootIfInstalled` (М8).

### 2.3. Налаштування — `app.db`

- `AppSettingsRow`: `McpEnabled` (bool, false), `McpMode` (int, 0 = ReadOnly), `McpPort` (int?, null до першого
  вмикання), `McpToken` (string?, null до першого вмикання). Міграція `AddMcpSettings` (`--context AppDbContext`,
  `Persistence/Migrations/App`).
- `BusinessLogic/Models/McpSettings.cs` (`record McpSettings(bool Enabled, McpMode Mode, int? Port, string? Token)`),
  `enum McpMode { ReadOnly, ConfirmChanges, AutoChanges }`.
- `IAppSettingsService.GetMcpSettingsAsync` / `SaveMcpSettingsAsync` / `RegenerateMcpTokenAsync`. Токен — 32 байти
  `RandomNumberGenerator`, base64url. Ніколи не потрапляє в лог.

### 2.4. Контракти — `BusinessLogic/Interfaces/`

```csharp
public enum McpServerState { Off, Running, ServedElsewhere, PortBusy, Failed }

public interface IMcpServer
{
    McpServerState State { get; }
    int? Port { get; }
    McpMode Mode { get; }
    string? ServedByCompetition { get; }        // ServedElsewhere: the other window's competition name
    int ConnectedClients { get; }
    Task ApplySettingsAsync(CancellationToken cancellationToken = default);  // start / stop / mode from McpSettings
    Task StopAsync();
    event EventHandler? Changed;
}

public interface IMcpUi                          // implemented in Presentation, runs on the UI thread
{
    Task<McpDecision> ConfirmAsync(McpChangeRequest request, CancellationToken cancellationToken);   // stage 2
    Task OpenCompetitionAsync(EventSummary competition, EventDay day, CancellationToken cancellationToken);
    Task ShowAsync(McpShowTarget target, CancellationToken cancellationToken);                       // stage 3
    string? GetHelp(string topic);
    IReadOnlyList<string> HelpTopics { get; }
    void Record(McpActionRecord record);                                                             // agent journal
}
```

### 2.5. Сервер — `DataAccess/Mcp/`

| Файл | Що робить |
|---|---|
| `McpServer.cs` (`IMcpServer`) | `WebApplication.CreateEmptyBuilder`, `Listen(IPAddress.Loopback, port)`, `AddMcpServer().WithHttpTransport().WithTools…`, `MapMcp("/mcp")`. Порт: збережений, а при першому вмиканні 47833 або наступний вільний, і запам'ятовується. Зайнятий збережений порт → `PortBusy`. Живий власник у файлі виявлення → `ServedElsewhere` + спостереження (М11) |
| `McpAuthMiddleware.cs` | `Authorization: Bearer` (`CryptographicOperations.FixedTimeEquals`) → 401; `Origin` / `Host` (М14) → 403 / 421 |
| `McpEndpointFile.cs` | `<data root>/mcp-endpoint.json`: `{port, token, pid, processStart, mode, appVersion}`. Запис атомарний (tmp + rename), на Unix `0600`. Видаляється лише при вимкненні MCP (М9) |
| `McpToolContext.cs` | сервіси програми для інструментів (М13) + `CallScope`: перевірка змагання й дня, `EditorScope`, лог виклику |
| `McpInstructions.cs` | `instructions` сервера (§6.7 плану), англійською, з українськими термінами |
| `McpServiceCollectionExtensions.cs` | `AddOrientPyxMcp()` у DI програми |

Кожен виклик — запис `IActivityLog.Info("MCP <tool> <короткі аргументи> → <результат> <мс>")`. Аргументи довші за
200 символів обрізаємо, токен і шляхи хоста не пишемо.

### 2.6. Набори даних — `BusinessLogic/Services/Mcp/`

- Новий метод редактора `Task<AnalyticsDayData> GetAnalyticsDayDataAsync(Guid dayId, CancellationToken)` (у
  `CompetitionEditorService.Analytics.cs`): **один** `GetDaySnapshotAsync` → `ComputeDayResultsAsync(snapshot)` +
  `BuildDaySplitViews` + сирі зчитки зі знімка. Результат — плоскі рядки з id, без форматування. Нових методів
  `IEventStore` не з'являється.
- `AnalyticsDatasetBuilder` — таблиці з §5.2 плану. Статистику перегонів (`leg_place`, `leg_best_s`, `leg_median_s`,
  `ratio_to_median`) рахуємо тут, серед тих, хто біг ту саму пару КП у межах групи або `course_variant`.
- `AnalyticsSchema` — статичний опис колонок (EN + українські терміни), сюди ж додаткові поля й колонки плагінів
  змагань. З нього будуються `describe_data` і ресурс `orientpyx://schema`.
- Golden-тест `Core/Mcp/AnalyticsDatasetTests`: на демо-змаганнях `results` / `legs` збігаються з
  `ResultProtocolData` / `SplitExportData` (час, місця, статуси); розсіювання, змішаний, рогейн, естафета, П/К, невідомий
  плагін.

### 2.7. Пісочниця (М12) — `DataAccess/Mcp/AnalyticsSandbox.cs`

- Кеш на (змагання, `IDataChangeBus.CurrentVersion`): одне з'єднання `Data Source=:memory:`, таблиці створюються
  ліниво (згадані в запиті назви × потрібні дні), запити на одне з'єднання йдуть під замком. Кеш скидається на
  `DataChange` цих змагань і на `AppData`.
- Захист: authorizer, `stmt_readonly`, `sqlite3_limit`, таймер 5 с → `sqlite3_interrupt`. Відповідь —
  `{columns, rows, total_rows, truncated}`, до 500 рядків і до 256 КБ JSON.
- Агрегати `median(x)` і `percentile(x, p)`.
- Тести `Core/Mcp/SandboxTests`: `INSERT` / `UPDATE` / `ATTACH` / `PRAGMA` / два оператори / `load_extension` відкидаються;
  `WITH RECURSIVE` без кінця переривається; ліміти рядків і байтів; кеш скидається після `DataChange` і `AppData`.

### 2.8. Інструменти читання — `DataAccess/Mcp/Tools/`

`ContextTools` (`get_context`, `list_competitions`), `FindTools` (`find`, `get_participant`), `DataTools`
(`describe_data`, `query`), `ResultTools` (`get_results`, `get_splits`, `get_report`), `SettingsReadTools`
(`get_settings`: без ключів Supabase і токенів), `HelpTools` (`get_help` через `IMcpUi`). Кожен має анотації
`ReadOnly = true`, `Idempotent = true`. Помилки — `McpException` з текстом для агента (англійською), без stack trace.

### 2.9. Міст (М8, М9) — `DataAccess/Mcp/McpBridge.cs` + `Program.cs`

```csharp
// Program.Main — the very first lines, before StartupTimeline and Velopack
if (args.Contains("--mcp-bridge"))
{
    Console.SetOut(Console.Error);
    Environment.Exit(McpBridge.Run(DataRootResolver.Resolve(AppContext.BaseDirectory), stdin, stdout));
}
```

- Міст — це stdio-сервер SDK з обробниками `tools/list`, `tools/call`, `prompts/*`, `resources/*`. Він пересилає
  виклики в HTTP-клієнта SDK, якого створює заново, коли змінюються pid або порт у файлі виявлення. Сповіщення
  `list_changed` теж пересилає.
- Програму не запущено → список з тих самих типів інструментів для режиму з файлу виявлення, а виклик повертає «OrientPyx
  не запущено…». Файлу немає → «MCP вимкнено…».
- Тест `Core/Mcp/BridgeTests`: `McpBridge.RunAsync(Stream, Stream)` проти запущеного тестового сервера, без нього і з
  перезапуском сервера посеред розмови.

### 2.10. UI (Presentation)

- «Налаштування» → блок «Асистент (MCP)» (`SettingsViewModel`, властивості `Mcp*`): перемикач (при першому вмиканні
  попередження про передачу даних провайдеру моделі), режим, стан, [Підключити до Claude Desktop], [Скопіювати
  налаштування для… ▾], [Новий токен], «?» (`Help.Mcp.Settings`).
- `Services/Mcp/McpClientConfigs.cs` — фрагменти для Claude Code, Gemini CLI (`httpUrl` + `headers`), VS Code (рівень
  користувача: `servers` / `type: "http"`), «URL + заголовок». Це конфігурація, а не UI-текст.
- `DataAccess/Mcp/McpbPackage.cs` — генерує `.mcpb` у temp і відкриває через `FileReveal`.
- `Services/Mcp/McpActivity.cs` (`IBackgroundActivity`): «MCP · підключено · N змін», flyout поки що лише зі станом.
- `Services/Mcp/McpUi.cs` — реалізація `IMcpUi` (на цьому етапі `OpenCompetitionAsync`, `GetHelp`).
- Ключі `Mcp.*`, `Help.Mcp.*` — в обох JSON (`LocalizationTests`).

### 2.11. Інтеграційні тести — `Core/Mcp/McpServerTests`

Сервер на випадковому loopback-порту + клієнт SDK: `initialize`, `tools/list` (у «лише читання» 11 інструментів),
`query`, `get_results`; без токена → 401; чужий `Origin` → 403; чужий `Host` → 421; друге вікно → `ServedElsewhere`,
потім перехоплення порту після зупинки першого.

**Контрольна точка:** функцію можна випустити окремо («Асистент — лише читання», позначка «експериментально»)
**[рішення]**.

## 3. Етап 2 — зміни (5 днів)

### 3.1. Політика — `BusinessLogic/Services/Mcp/McpToolPolicy.cs`

```csharp
public enum McpToolClass { Read, Write, Risky, Never }
public enum McpVerdict { Allow, Confirm, Deny, Hidden }

public static class McpToolPolicy
{
    public const int RiskyDeletes = 5, RiskyUpdates = 50;
    public static McpToolClass ClassOf(string tool, McpCallShape shape);   // shape: row count, kind, undoable
    public static McpVerdict Decide(McpToolClass cls, McpMode mode, bool allowedForSession);
    public static IReadOnlyDictionary<string, string> NeverViaMcp { get; } // §6.6 → localisation key «де в програмі»
}
```

Тест: кожен зареєстрований інструмент має клас; список §6.6 повний; у `ReadOnly` запис → `Hidden`, а виклик → `Deny`.

### 3.2. Конвеєр запису — `BusinessLogic/Services/Mcp/McpWriteRunner.cs`

1. `SemaphoreSlim(1)`: записи агента йдуть по одному;
2. `competition` == `session.CurrentEvent.Identifier`, інакше відмова; день → `EventDay` або помилка;
3. політика → за потреби `IMcpUi.ConfirmAsync` (§3.4);
4. `using EditorScope.Enter(competition, day)`, `using UndoScope.Enter(step)`, `DataChangeOrigin.Current = McpOrigin.For(mode)` (М2);
5. виклик методів редактора; `DayLockedException` → «День N закритий для змін — відкрити його можна лише в програмі»;
6. `IMcpUi.Record(...)` → `McpWriteResult(stepId, summary, warnings, undoable)`.

Поле `undoable` визначає журнал. Якщо `IEventStore` ще не вміє відповісти «крок `NotUndoable`?», додаємо метод читання
за звичайними правилами: `EventStoreMethods`, `RpcContractTests`, `NetworkProtocol.Version`.

Для «рядок цілком» (`UpdateParticipantDayRowAsync`, `UpdateGroupDayRowAsync`) є хелпер `ReadModifyWriteAsync`: свіжий
рядок → зміна полів → збереження, без кешу.

### 3.3. Походження (М2)

- `McpOrigin.For(IWorkspaceMode)` → `"<SelfOrigin>·mcp"`.
- Станція: `RpcChannel` додає `X-OrientPyx-Origin-Tag: mcp`, коли `DataChangeOrigin.Current` закінчується на `·mcp`.
  Хост (`RpcDispatcher`) ставить `station.Name + "·mcp"`. Нова константа в `Wire.cs`, `NetworkProtocol.Version` +1,
  `docs/network-protocol.md`.
- Тест: сторінка (`PageViewModelBase`) на станції перезавантажується після запису агента цієї ж станції.

### 3.4. Підтвердження (М5)

- `BusinessLogic/Services/Mcp/McpConfirmations.cs`: запити, що чекають, з `confirmation_id`; `ConfirmWait` з етапу 0.
  Після тайм-ауту — `pending`. Інструмент `await_confirmation(confirmation_id)` чекає ще `ConfirmWait`. Схвалена дія
  виконується одразу, її результат зберігається 10 хв. Відхилено або минуло 10 хв → «Користувач не підтвердив».
- `McpChangeRequest(tool, ChangeSummary, preview rows ≤ 200)`. `ChangeSummary` — структура (М4), текст збирає
  `Presentation/Services/Mcp/ChangeSummaryText.cs` з ключів `Mcp.Change.*`.
- UI: `ViewModels/Shell/McpConfirmBarViewModel` — немодальна смуга в `MainWindow`, [Деталі] (таблиця змін), [Дозволити],
  [Відхилити], ☐ «Дозволяти такі зміни до кінця сесії» (у пам'яті, на пару (інструмент, клас); скидається при
  перезапуску MCP або програми), миготіння панелі задач + тост (наявні сервіси фінішних сповіщень).

### 3.5. Інструменти запису — `DataAccess/Mcp/Tools/`

`ParticipantTools` (`add_participants`, `update_participants`, `remove_participants`), `GroupTools`, `ControlTools`
(+ `set_problematic_controls`), `DirectoryTools` (`manage_directory`), `CompetitionTools` (`update_competition`),
`DayTools` (`manage_days`: `lock: false` → `Never`), `RelayTools`, `SettingsWriteTools` (`save_settings`: JSON merge
patch → десеріалізація в типізовану модель налаштувань як перевірка → `Save*SettingsAsync`), `UndoTools` (`undo`:
лише кроки з журналу агента → `IEditHistoryService.RevertAsync`; конфлікт → текст `UndoOutcome`). Пошук за ПІБ, якщо
збігів більше одного, — відмова зі списком кандидатів.

### 3.6. Журнал дій агента

`Presentation/Services/Mcp/McpActionJournal` (останні 100: час, текст зведення, `stepId`, `undoable`) → flyout
`McpActivity` з [Скасувати] (`IEditHistoryService.RevertAsync` під busy overlay) і [Перейти]. Кроки агента не потрапляють
в `IUndoHistory` сторінок, тож Ctrl+Z на сторінці їх не чіпає.

### 3.7. Тести — `Core/Mcp/WriteTests`, `Core/Mcp/PolicyTests`, мережеві

Виклик = один крок, `undo` повертає дані (порівняння знімків); `DataChange` з походженням `·mcp`; невідповідність
`competition` і закритий день дають зрозумілі помилки; `pending` → `await_confirmation` з фейковим `IMcpUi`; перемикання
змагань посеред пакета не переносить запис (М1); станція: запис агента видно на хості, сторінка станції оновлюється,
скасування працює.

## 4. Етап 3 — операції (7 днів)

### 4.1. Плани змін (М6) — `BusinessLogic/Services/Mcp/McpPlanStore.cs`

`McpPlan(planId, tool, competition, dayId, dataVersion, payload, preview, expiresAt)`, у пам'яті, 15 хв, не більше 20
одночасно. Кожна операція з `preview` повертає `plan_id` + зведення. `apply_plan(plan_id)` іде через `McpWriteRunner`
(підтвердження показує той самий план). Якщо версія даних змінилася, план перераховується, і коли результат інший —
відмова.

### 4.2. Етап 3а — наявні API редактора (3 дні)

| Інструмент | Збирається з |
|---|---|
| `draw_start_times` | `GetDrawPrepDataAsync` + `GetDrawSettingsAsync` (налаштування сторінки як типові) + `IStartDrawService.Draw` / `DrawClassic` → план → `SaveDrawStartTimesAsync` |
| `mass_start` | `GetMassStartDataAsync` → план → `ApplyMassStartAsync` |
| `set_start_order` | `GetStartOrderDataAsync` → план → той самий запис, що «Дії → порядок старту» |
| `assign_numbers`, `assign_rental_chips` | вибірка за фільтром → план → `SetParticipantNumbersBatchAsync` / `SetParticipantDayChipsBatchAsync` |
| `copy_participants` | план → `CopyParticipantsBetweenDaysAsync` |
| `create_competition`, `open_competition` | `IEventCatalogService.CreateEventAsync`; `IMcpUi.OpenCompetitionAsync` → `ISessionService.SelectAsync` |
| `show_in_app` | `IMcpUi.ShowAsync` → `INavigationService` + `SheetReveal` (як «Перейти» в перевірці готовності) |

### 4.3. Етап 3б — винести оркестрацію (М3, 4 дні)

- `BusinessLogic/Services/Operations/ParticipantImportOperation.cs`: `ParseAsync(kind, bytes)` → `ResolveMappingAsync`
  (збережене зіставлення за хешем заголовків `GetImportMappingAsync`, інакше зіставлення агента) → `PreviewAsync(scope)` →
  `FindDuplicatesAsync` → `ApplyAsync`. `CsvImportFlow` / `ParticipantImportFlow` / `XmlImportFlow` переходять на
  неї, модалки лишаються у flow. Окремо — `CourseImportOperation` (IOF: `ImportControlPointsAsync` +
  `ImportGroupsAsync`), зчитки, орендні чіпи.
- `BusinessLogic/Services/Operations/OutputExportService.cs`: протокол результатів, стартовий, суддівський, сума днів
  (docx), спліти (HTML), IOF XML, відомість, учасники (CSV / XLSX). Налаштування — збережені для дня/виду (як на
  сторінці), `IOutputLabels` (BusinessLogic) реалізує Presentation: `BuildLabels()` з `ProtocolsViewModel` /
  `StartProtocolsViewModel` переходять у `ProtocolLabelsFactory`, як уже `WinnersPrintLabelsFactory`. Сторінки
  перебудовуються на сервіс.
- `IEventFiles.LocalExportDirectory` (М10). Інша папка → ризикова дія. Розширення лише з білого списку, без
  `overwrite: true` наявний файл не перезаписується, розмір файлу імпорту ≤ 50 МБ (`McpFilePolicy`).
- Тести: попередній перегляд імпорту агента = попередній перегляд flow для того самого файлу; байти експорту агента =
  байти експорту сторінки з тими самими налаштуваннями (наявні golden-виходи); наявні UI-тести імпорту зелені без змін.

### 4.4. Prompts — `DataAccess/Mcp/McpPrompts.cs`

«Аналіз дня», «Підготовка дня до старту», «Нові змагання з файлів», «Що не так з учасником». Назви й описи — мовою
програми через `IMcpUi`, тіло — англійською з українськими термінами.

## 5. Етап 4 — реліз (2,5 дня)

- `tests/mcp-evals.md` (~30 сценаріїв українською, з очікуваним результатом), прогін на Claude Desktop, Claude Code,
  Gemini CLI.
- `website/content/…/assistant.md`: що вміє і чого ні, підключення, режими, підтвердження, скасування, приватність,
  macOS (М16), приклади питань. DocShots без токенів і шляхів.
- `.claude/rules/mcp.md` (`paths: src/**/Mcp/**`) + рядок у списку CLAUDE.md «Subsystem notes»; один рядок у CLAUDE.md
  «Network mode» про `·mcp` (М2).
- Повний прогін `tests/OrientPyx.Tests.sln`, `/new-version`.

## 6. Порядок і контрольні точки

| Після | Що має працювати | Оцінка |
|---|---|---|
| Етапу 0 | рішення 0.1–0.6 записані, значення обрані | 1,5 дня |
| 2.1 | `EditorScope` у `main`, golden без змін — безпечний рефакторинг, корисний і без MCP | +1 день |
| Етапу 1 | аналітика з Claude Desktop (одним кліком) і Claude Code; можна випускати | +5 днів |
| Етапу 2 | зміни з підтвердженням і скасуванням, станція | +5 днів |
| 3а | жеребкування, масовий старт, номери/чіпи, створення/відкриття змагань | +3 дні |
| 3б | імпорт і експорт з чату; flow-сервіси на спільних операціях | +4 дні |
| Етапу 4 | документація, evals, реліз | +2,5 дня |

Разом ≈ 22 дні проти 15–17 у плані. Різниця: М1 (рефакторинг ≈126 звернень, +1), М3 (винесення оркестрації, +3), М5/М6
(підтвердження, що чекає, і плани змін, +1), М2/М10/М11 (+1).

## 7. Рішення, які треба ухвалити

1. **М5** — підтвердження з `pending` + `await_confirmation` замість 2 хв очікування. Рекомендую так.
2. **М6** — `preview` → `plan_id` → `apply_plan` замість повторного обчислення. Рекомендую так.
3. **М7** — прибрати «інший день, ніж у вікні» з ризикових дій. Рекомендую прибрати.
4. Випускати етап 1 окремо як «експериментальне, лише читання». Рекомендую так: аналітика вже корисна, а ризику немає.

## 8. Ризики реалізації

- **Рефакторинг `EditorScope` (2.1)** зачіпає ≈126 місць найбільшого класу. Пом'якшення: механічна заміна на дві
  приватні властивості окремим комітом, повний прогін тестів + `scripts/diff-results.ps1`.
- **SDK MCP ще рухається** (перейменування типів між версіями). Версію фіксуємо, інструменти тонкі, логіка в
  BusinessLogic — оновлення SDK зачіпає лише `DataAccess/Mcp/`.
- **Винесення flow-сервісів (3б)** — найбільший ризик регресії для звичайного користувача. Пом'якшення: окремий PR,
  наявні UI-тести імпорту / експорту й golden-виходи до і після.
- **Клієнти по-різному підтримують `list_changed`, тайм-аути й elicitation** — етап 0 фіксує поведінку, а документація
  каже, що робити.
