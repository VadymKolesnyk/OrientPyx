# Системні сповіщення для фінішу

> Статус: **Windows — зроблено** (2026-11-18), варіант C: `Presentation/Services/WindowsToasts.cs` (власна
> COM-інтероп до WinRT, беззвучне сповіщення, AUMID у HKCU, клік — подія `Activated`), підключено в `DesktopAlerts`
> і «Налаштування сповіщень» (колонка «Сповіщення Windows»). **macOS / Linux — не робили** (не критично).
> Нижче — дослідження і результати проб, на яких ґрунтується вибір.

## Результати проб (2026-11-18, Windows 11)

- **A, `Avalonia.Labs.Notifications` 12.1.0** — працює: сповіщення є в історії Windows, AUMID записано в HKCU,
  `CustomActivator` теж. Але в шаблоні XML пакета (видно в його DLL) немає `<audio>` і `scenario` → завжди грає
  системний звук Windows поверх наших сигналів; ще й у шаблоні зайвий текст `">` після атрибута `launch`.
  Відкинуто.
- **C, власна інтероп** — працює: сповіщення в історії з `<audio silent="true"/>`; подія `Dismissed` (тайм-аут
  банера, ~6–9 с) приходить у процес → делегати-COM працюють, тож `Activated` (клік) приходить тим самим шляхом.
  IID делегатів `TypedEventHandler<ToastNotification, …>` перевірено обчисленням (SHA-1 сигнатури WinRT):
  Activated `AB54DE2D-97D9-5528-B6AD-105AFE156530`, Dismissed `61C2402F-0ED0-5A18-AB69-59F4AA99A368`; подію `Failed`
  не використовуємо (IID з пам'яті не зійшовся з обчисленим).
- **Черга.** Windows 11 показує звичайні сповіщення по одному (до трьох «важливих» поруч + одне звичайне). Тому
  прокатний чіп має `Tag="collect-chip"` / `Group="finish"` (`IToastNotification2`, IID
  `9DFB9FD1-143A-490E-90BF-B9FBA7132DE7`) — нове замінює попереднє; на пробі з трьох поспіль в історії лишилося одне,
  останнє. Невідомий чіп — `scenario="urgent"` (Windows приймає з build 19041; старшим — звичайне): на пробі прийнято.
  Чи справді «важливе» проходить крізь «Не турбувати» — залежить від налаштувань Windows, не перевірено.
- Клік по сповіщенню руками не перевірено (проба не могла клацнути) — перевірити на справжньому фініші: чи
  розгортається вікно і чи Windows дає йому передній план (інакше — блимає кнопка на панелі задач).

## Навіщо

Коли вікно OrientPyx згорнуте або неактивне, суддя не бачить ні тостів, ні вікна призначення невідомого чіпа.
Гірше: вікно «Невідомий чіп» — це оверлей усередині головного вікна (`DialogService.Current`), а автозчитування
чекає, поки його закриють (`FileReadoutPoller` чекає обробника → `HandleNewReadsAsync` чекає
`PromptUnknownReadAsync`). Тобто згорнута програма + невідомий чіп = всі наступні зчитування стоять, чеки не
друкуються, доки суддя не повернеться до вікна.

Звук і блимання кажуть «щось сталося», системне сповіщення додає **текст**: що саме і з ким.

## Що показувати

- **Невідомий чіп:** «Невідомий чіп 123456 — чекає призначення». Клік → розгорнути вікно (вікно призначення вже
  відкрите в ньому).
- **Прокатний чіп:** «Заберіть прокатний чіп 123456 — №57 Іваненко Петро (М21)». Клік → розгорнути вікно.
- Тільки коли вікно **неактивне або згорнуте** — інакше дублюється тост у програмі.
- **Без власного звуку Windows** (`<audio silent="true"/>`): звучать наші два сигнали, а не ще й системне «дінь».
- У «Налаштування сповіщень» — колонка «Сповіщення Windows» на кожну подію; зберігається на машині
  (`IUiPreferencesService`), як решта налаштувань сигналів.
- Ніколи не єдиний сигнал: «Не турбувати» ховає сповіщення, тому звук лишається основним.

## Що з'ясовано (Windows)

### Ярлик не потрібен — реєстрація в реєстрі

Непакованій програмі (а Velopack-збірка саме така) для сповіщень потрібен AppUserModelID (AUMID). Старі інструкції
Microsoft вимагали ярлик у «Пуск» з AUMID; сучасні бібліотеки обходяться без нього — пишуть у
**`HKCU\Software\Classes\AppUserModelId\<AUMID>`**: `DisplayName` (назва в сповіщенні), `IconUri` (шлях до
картинки), `IconBackgroundColor`, і `CustomActivator` = `{CLSID}` COM-активатора для кліку. Так роблять і
`ToastNotificationManagerCompat` (Windows Community Toolkit), і `Avalonia.Labs.Notifications`. Адмін-права не
потрібні; працює й у збірці з `dotnet run` (інший шлях до exe → інший AUMID, окремий запис).

- AUMID у обох бібліотек за замовчуванням — з імені exe + хешу шляху. Velopack ставить програму в
  `%LocalAppData%\OrientPyx\current\OrientPyx.exe`, шлях не змінюється між оновленнями → AUMID стабільний. Краще все ж
  задати свій (`OrientPyx.Finish` чи подібний) — тоді й налаштування Windows для програми не «губляться».
- Що ставить Velopack на свої ярлики — не з'ясовано (документація мовчить); з реєстровим способом це неважливо.
- **Після видалення програми** ключ у реєстрі лишається (жодна бібліотека його не прибирає при видаленні). Прибрати
  в хуку Velopack перед видаленням (`VelopackApp` … `OnBeforeUninstall…`).

### Варіанти

| | Без зміни TFM | Тиша (`audio silent`) / `scenario` | Клік | Ризик / ціна |
|---|---|---|---|---|
| **A. `Avalonia.Labs.Notifications` 12.1.0** (жовт. 2026, MIT, Avalonia ≥ 12.0.1) | так — ціль `net8.0`, своя WinRT-інтероп через MicroCom, без CsWinRT | **ні**: XML будує сама, без `<audio>` і `scenario` → грає системний звук Windows | так: COM-активатор, подія `NotificationCompleted` (`IsActivated`) | «Labs» — експериментальний, API може мінятися; є дивні місця в шаблоні XML; тягне `Tmds.DBus.Protocol` (для Linux) |
| **B. `Microsoft.Toolkit.Uwp.Notifications` + TFM `net10.0-windows10.0.17763.0`** | **ні**: мультитаргетинг `OrientPyx.Presentation` (`net10.0;net10.0-windows…`), `-f` у `publish.ps1`, `dotnet run` | так: повний контроль XML (`ToastContentBuilder`) | так: `ToastNotificationManagerCompat.OnActivated` | **+~25 МБ** до інсталятора (`Microsoft.Windows.SDK.NET.dll` 24,9 МБ, перевірено в кеші NuGet); Toolkit 7.x — у режимі підтримки |
| **C. Власна мінімальна інтероп** (~200–300 рядків: `RoGetActivationFactory`, `HSTRING`, `IToastNotificationManagerStatics` → `CreateToastNotifierWithId`, `IXmlDocumentIO.LoadXml`, `IToastNotificationFactory`) + запис у реєстр | так | так | подія `Activated` (свій COM-делегат) або COM-активатор — найскладніша частина | своя відповідальність за інтероп; можна підглянути в код A (MIT) |
| D. Windows App SDK (`AppNotificationManager`) | ні | так | так | потрібен рантайм WinAppSDK — відкинуто |
| E. Запуск PowerShell зі скриптом сповіщення | так | так | ні | повільно, антивіруси не люблять — відкинуто |

### «Не турбувати»

У Windows 11 «Не турбувати» може вмикатися автоматично (програма на весь екран, дублювання екрана) — а на фініші
часто є другий екран з «Результати на монітор». Тоді сповіщення мовчки йде в центр сповіщень. Варто перевірити
`scenario="urgent"` («важливі сповіщення» Windows 11, що можуть проходити крізь «Не турбувати», якщо користувач
дозволить) — доступно лише у варіантах B і C.

## macOS / Linux

- **macOS:** `Avalonia.Labs.Notifications` має гілку для Apple (`UNUserNotificationCenter`, потрібен bundle id і
  macOS 10.14+); чи вона є в `net8.0`-збірці пакета — перевірити. Для непідписаних / ad-hoc-підписаних програм
  `requestAuthorization` часто відмовляє («Notifications are not allowed for this application») — наші macOS-збірки
  без Apple-підпису, тож розраховувати на це не можна, доки не перевірено на справжньому Mac. Запасний
  `osascript -e 'display notification …'` показується від імені Script Editor і на клік відкриває його — не годиться.
  Звук і підстрибування в доку вже є.
- **Linux** (тільки з вихідного коду): A працює через xdg-desktop-portal (потрібен `.desktop`-файл і portal ≥ 1.19);
  не пріоритет.

## Рекомендація і наступний крок

1. **Спайк на 1–2 години з варіантом A** у тимчасовій копії (не в репозиторії): показати два сповіщення зі
   встановленої і з dev-збірки, перевірити — чи з'являється, як виглядає (назва, іконка), чи грає системний звук і
   чи його можна прибрати, чи клік розгортає вікно (потрібен `Window.Activate()` з дозволом на передній план).
2. Якщо A показує сповіщення, але **звук Windows не вимикається** — варіант **C**, взявши за основу інтероп із A
   (MIT): свій XML з `<audio silent="true"/>` і, можливо, `scenario="urgent"`. B — лише якщо C виявиться надто
   крихким: +25 МБ і мультитаргетинг заради одного сповіщення — задорого.
3. macOS — окремо, коли буде на чому перевірити (і бажано Apple-підпис).

## Як вписати в код

- `Presentation/Services/` — `IDesktopNotifier` (`IsSupported`, `Show(kind, title, message)`) поруч із
  `DesktopAlerts`; перевірки `OperatingSystem.IsWindows()`; будь-яка помилка → журнал, без винятків назовні;
  `Muted` для тестів, як у `DesktopAlerts`.
- Виклик — там само, де звук: невідомий чіп у `FinishReadViewModel.AlertUnknownChip`, прокатний — у
  `NotifyCollectChip`. Одне сповіщення на подію; при порції з багатьох зчитувань — згрупувати («Невідомих чіпів: 3»).
- Налаштування: `CollectChipSystemNotification`, `UnknownChipSystemNotification` у `IUiPreferencesService`; колонка в
  `FinishAlertsView`.
- Тексти — ключі в обох `uk-UA.json` / `en-US.json`; документація — розділ «Сповіщення й звуки» в
  `website/content/race-day/finish.md`.
- Підписування: новий пакет (A) — чужа DLL, у `build/signpath/SigningRules.ps1` нічого не додається (там лише наші
  бінарники), але перевірити, що пакування не ламається.

## Джерела

- Код `Avalonia.Labs.Notifications`: <https://github.com/AvaloniaUI/Avalonia.Labs/tree/main/src/Avalonia.Labs.Notifications>
  (`Windows/AumidHelper.cs`, `Windows/NativeNotification.cs`, `AppNotificationOptions.cs`, `AppBuilderExtensions.cs`);
  пакет: <https://www.nuget.org/packages/Avalonia.Labs.Notifications>
- `ToastNotificationManagerCompat` (реєстр, COM-активатор, `Uninstall`):
  <https://github.com/CommunityToolkit/WindowsCommunityToolkit/blob/main/Microsoft.Toolkit.Uwp.Notifications/Toasts/Compat/ToastNotificationManagerCompat.cs>
- Microsoft, сповіщення з непакованих програм (реєстр AUMID):
  <https://learn.microsoft.com/en-us/windows/apps/design/shell/tiles-and-notifications/send-local-toast-other-apps>
- Microsoft, старий спосіб через ярлик з AUMID: <https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh802762(v=vs.85)>
- Ярлики Velopack: <https://docs.velopack.io/integrating/shortcuts>
- macOS, відмова `requestAuthorization` для непідписаних програм: <https://developer.apple.com/forums/thread/660899>,
  <https://mirror.b10c.me/bitcoin-core-gui/112/>
