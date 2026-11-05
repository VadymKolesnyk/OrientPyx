# Мережевий протокол OrientPyx (хост ↔ станція)

Версія протоколу: `NetworkProtocol.Version = 1` (`BusinessLogic/Models/Network/NetworkModels.cs`). Інкрементується при
будь-якій зміні сигнатур мережевих інтерфейсів або формату нижче; хост і станція з різними версіями відмовляють у `/pair`.

Типи DTO: `DataAccess/Network/Protocol/Wire.cs` (спільні для хоста й станції). Серіалізація — завжди
`RpcJson.Options` (`BusinessLogic/Serialization/RpcJson.cs`): camelCase, `IncludeFields` (кортежі), enum як рядки,
`DisciplineId` як рядок.

## Транспорт

JSON по HTTP/1.1, відкритий (без TLS) у локальній мережі. Хост — Kestrel (`WebApplication.CreateEmptyBuilder` +
`UseKestrelCore`, `Listen(IPAddress.Any, port)`), стиснення відповідей gzip/br. Станція — один `HttpClient`
(`SocketsHttpHandler`, `PooledConnectionLifetime` 5 хв, `AutomaticDecompression`).

Порти: UDP `47831` (пошук, однаковий усюди), TCP `47832` типово (якщо зайнятий — наступний вільний; хост повідомляє
фактичний у відповіді на пошук і в коді підключення).

## Автентифікація

`POST /pair` → токен сесії станції. Далі кожен запит (крім `/ping`) несе `Authorization: Bearer <token>`.
Невідомий/відкликаний токен → `401` з тілом `RpcError{kind:"unauthorized"}`; станція тоді переходить у стан
`Disconnected` і просить PIN знову.

PIN — 6 цифр, генерується при старті хоста, показується на його екрані. 5 невдалих спроб → пауза 30 с (`429`).

## Ендпоїнти

| Метод і шлях | Тіло запиту | Відповідь |
|---|---|---|
| `GET /ping` | — | `PingResponse{hostId, hostName, appVersion, protocolVersion, time}` — без токена |
| `POST /pair` | `PairRequest{pin, stationName, appVersion, protocolVersion, plugins[], token?}` | `200 PairResponse{token, hostId, hostName, appVersion, protocolVersion, plugins[], serverTimeUtc, readoutType}`; `401` (PIN/токен), `409` `RpcError{kind:"protocol"}` (версія), `429` (пауза) |
| `POST /rpc/{iface}/{method}` | JSON-масив аргументів `[arg0, arg1, …]` (без `CancellationToken` і `IProgress<>`), заголовки `Authorization`, для записів `Idempotency-Key: <guid>` | результат методу як JSON; `204` для `Task` без значення; для job-методів `200 JobStarted{jobId}`; помилки — `RpcError` |
| `GET /changes` | заголовок `Last-Event-ID: <seq>` (необов'язково) | `text/event-stream`, див. нижче |
| `GET /version?event=<identifier>` | — | `VersionResponse{eventPath, version, sequence}` |
| `GET /jobs/{id}` | — | `JobStatus{id, finished, stage, current, total, result?, error?}`; `404` якщо немає |
| `GET /files/archive?identifier=<id>` | — | `application/zip` — архів `.opyx`, зібраний хостом (через його локальний `EventArchiveService` у temp) |
| `POST /files/import?identifier=<id>&overwrite=<bool>` | `application/octet-stream` — файл `.opyx` | `EventArchiveImportResult` (JSON); помилки — `RpcError` |
| `POST /files/preview` | `application/octet-stream` — файл `.opyx` | `EventArchivePreview` (JSON) |
| `POST /log` | `LogBatch{eventPath, lines[{at, level, message}]}` | `204`; хост дописує рядки у свій поточний лог з префіксом `[<ім'я станції>]` (`level` `ACTION` → `Action`, решта → `Info`); станція шле батч раз на 2 с, до 200 рядків, черга ≤ 1000, збої мовчки відкидаються |

Хост слухає на порту з `NetworkSettings.HostPort` (0 → `47832`); зайнятий порт → наступний вільний із 20 спроб.
Тіла стискаються gzip/br лише для `application/json` (потік `/changes` і архіви йдуть як є); ліміт розміру тіла
запиту знято (`.opyx` на `/files/import`). PIN перегенеровується при кожному старті хоста й через
`RegeneratePin()`; токени станцій переживають перезапуск (`app.db`), `DisconnectStationAsync`/`DisconnectAllAsync`
їх видаляють — наступний запит станції дістає `401`. «Онлайн» станція = запит за останні 60 с
(`ConnectedStation.IsOnline`, `ActiveStationsOn`); каталог хоста відмовляє в `RenameIdentifierAsync`, поки такі є.

`iface` ∈ `EventStore | FolderScanner | Catalog | EventFiles | Archive | AppData` (`Wire.Interfaces`). Перевантажень
у цих інтерфейсах немає — метод шукається за ім'ям (контрактний тест це закріплює).

### Аргументи

Хост зіставляє JSON-масив із параметрами методу по порядку, **пропускаючи** параметри типу `CancellationToken`
(→ `HttpContext.RequestAborted`) та `IProgress<>` (→ приймач прогресу завдання). Станція робить дзеркально: прибирає
ці параметри з масиву. Відсутні хвостові аргументи (необов'язкові параметри) = їхні значення за замовчуванням.

### Ідентифікатор ↔ шлях

По мережі ходить **ідентифікатор** змагання (назва папки), ніколи шлях хоста:
- вхідні аргументи: параметр з іменем `eventFolderPath` і властивість `EventSummary.FolderPath` → після
  `EventIdentifier.IsValid` (інакше `400`) хост підставляє `Path.Combine(eventsPath, identifier)`;
- результати: `EventSummary.FolderPath` (у т.ч. усередині `IReadOnlyList<EventSummary>`, `EventArchiveImportResult.Competition`)
  → назад на ідентифікатор (`Path.GetFileName`).
Для `IEventFolderScanner.ScanAsync(eventsPath)` хост ігнорує аргумент і сканує свою папку.
`IResultsAppData`, `IsIdentifierAvailableAsync`, `CreateEventAsync` змагання не адресують.

### Помилки

`RpcError{type, message, kind, dayNumber?}`:
- `type` — повне ім'я типу винятку на хості; станція відтворює `ArgumentException`, `InvalidOperationException`,
  `KeyNotFoundException`, `OperationCanceledException`, `DayLockedException` (з `dayNumber`),
  `EventArchiveFormatException`, `EventFolderInUseException`; решта → `RemoteCallException(type, message)`.
- `kind`: `bad-request` (400), `unauthorized` (401), `not-found` (404), `conflict` (409), `protocol` (409), `locked` (429,
  пауза після невдалих PIN), `cancelled` (499, станція скасувала запит — `OperationCanceledException`), `error` (500).
  Невідомий `iface`/метод у `/rpc` → `404` з `type = System.Collections.Generic.KeyNotFoundException`.
- Відповідність винятків хоста: `DayLockedException` → 409 (з `dayNumber`), `EventArchiveFormatException` → 400,
  `EventFolderInUseException` → 409, `ArgumentException` → 400, `KeyNotFoundException` → 404,
  `InvalidOperationException` → 409, `OperationCanceledException` → 499, решта → 500. Станція на 401 кидає
  `PairingRefusedException("Unauthorized")` і сама переходить у `Disconnected` (токен забуто).

### Ідемпотентність і повтори

Записи несуть `Idempotency-Key` (GUID на кожен виклик): для `IEventStore` — за картою `EventStoreMethods.IsWrite`
(`JobMethods` теж), для решти інтерфейсів — усе, що не починається з `Get/Find/Is/Scan/Count`
(`RpcProxy<T>.KindOf`). Хост зберігає відповідь **будь-якого** `/rpc`-виклику з ключем 10 хв (ключ діє в межах
токена станції); повтор із тим самим ключем повертає збережену відповідь (і помилку теж), не виконуючи метод;
одночасний дублікат чекає на перший виклик. Станція повторює й читання, і записи до 3 разів (паузи 0.5/1/2 с) на
`HttpRequestException` / таймаут; після вичерпання — `HostUnreachableException`, стан `Reconnecting`.

Таймаути: читання 10 с, записи 60 с, завдання — `POST /rpc` з таймаутом запису, далі опитування `/jobs` без
загального ліміту (кожен `GET` — таймаут читання). Файлові передачі `/files/*` — 10 хв, без повторів.

### Завдання (`/jobs`)

`EventStoreMethods.JobMethods` (поки лише `ImportParticipantsBatchAsync`): `/rpc` одразу повертає `JobStarted{jobId}`,
хост виконує у фоні й оновлює `JobStatus` (`stage` = `ImportStage` рядком, `current/total`); станція опитує
`GET /jobs/{id}` кожні 300 мс, передає прогрес у локальний `IProgress<ImportProgress>`, і після `finished`
повертає `result` (десеріалізований як тип повернення методу) або кидає `error`. Готові завдання зберігаються 10 хв.

### Потік змін (`/changes`)

```
id: <seq>
event: change
data: {"eventPath":"<identifier>","area":"Participants","version":12,"origin":"Mac-Реєстрація"}

: hb            ← heartbeat кожні 10 с

event: full-refresh
data: {}        ← станція відстала далі буфера (Last-Event-ID невідомий)
```

`seq` — глобальний номер події хоста (`IDataChangeBus.Sequence`); станція зберігає останній і при перепідключенні
передає `Last-Event-ID`. Хост відповідає досиланням пропущених (`IDataChangeBus.Since`) або `full-refresh`, потім
коментарем `: connected` і далі живим потоком (подія з номером, уже досланим із буфера, не дублюється). Станція
публікує кожну подію у свій `IDataChangeBus.Replay(...)` (`eventPath` — ідентифікатор, який на станції і є
`FolderPath`); на `full-refresh` — `DataArea.Any`. Кеш знімка дня на станції (`SnapshotCache`) скидається подією
по тому самому `eventPath` (або `Any`) і після власного запису в це змагання — не за номером версії.

Детектори обриву на станції: 30 с тиші у `/changes` або невдача `GET /version` (раз на 15 с, для поточного
змагання — `NetworkStation.CurrentEvent`, інакше пропускається) → `Reconnecting`, експоненційна пауза 1/2/5/10 с
без межі спроб; після відновлення — `DataArea.Any` (усі активні сторінки перезавантажуються). Опитувач версії
тримає власну таблицю «ідентифікатор → остання відома версія» (шина не запам'ятовує версію з `Any`-подій);
розбіжність → `Replay(DataChange(eventPath, Any, versionХоста, hostName))` і оновлення таблиці.

Стан підключення (`ConnectResult.ErrorKey` / `INetworkClient.LastErrorKey`, локалізується як `Network.Error.<key>`):
`HostRoleActive` (цей екземпляр — хост), `Unreachable` (`/ping` не відповів або збій запиту; `Detail` — текст
помилки), `Protocol` (`Detail` = `host X / station Y`), `PinRequired` (без PIN і без збереженого токена для цього
хоста, або збережений токен відхилено), `WrongPin`, `Locked` (429), `Unauthorized` (хост відкликав токен під час
роботи). Попередження в `ConnectResult.Warnings`: `plugins-match` (`missing: …; extra: …`), `readout-type-match`
(`host: N, station: M`), `clock-skew` (різниця в секундах) — лише коли є розбіжність.

## Пошук хоста (UDP)

Станція шле `OPYX?` (UTF-8) на subnet-broadcast кожного IPv4-інтерфейсу (і `255.255.255.255`) на порт `47831` раз на
2 с. Хост відповідає відправнику `OPYX {json}` — `DiscoveryResponse{hostId, name, version, ip[], port, competition}`.
Станція тримає список із TTL 6 с (`DiscoveryClient.Hosts`).

Код підключення (`ConnectionCode`): 12 символів Base32 без `0/O/1/I` = IPv4 + порт + контрольна сума; показується як
`XXXX-XXXX-XXXX`.

## Що лишається локальним на станції

`app.db` (налаштування машини), `views.json` (`<data root>/views/<hostId>/<identifier>.json`), лог дій
(`<data root>/logs/<hostId>-<identifier>/` + копія на хост через `/log`), файли зчитки
(`<data root>/local/<hostId>/<identifier>/day{N}/`, `…/chips/`), `machine.json` там само (`chipScan.readoutPath`),
HTML монітора (`<data root>/monitor/<identifier>/day{N}`). `ReleaseAsync`/`CheckpointAsync` на станції — no-op.
