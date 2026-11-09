# OrientPyx

**OrientPyx** — кросплатформна десктопна програма для проведення змагань зі
спортивного орієнтування: учасники, групи, дистанції, чіпи, зчитка на фініші,
підрахунок результатів та друк протоколів.

Інтерфейс українською (з можливістю перемкнути на англійську).

> Проєкт із повністю відкритим кодом під ліцензією [MIT](LICENSE).

## Можливості

- **Змагання та дні** — робота в контексті обраного змагання й дня; кожне змагання
  зберігається в окремій базі даних.
- **Учасники** — денний реєстр і «Мандатка» (зведена таблиця по всіх днях), імпорт із
  CSV/Excel, IOF XML та UOF XML, масове присвоєння номерів і чіпів, розрахунок стартового внеску.
- **Групи, дистанції, контрольні пункти, чіпи** — редаговані таблиці з імпортом з IOF XML.
- **Дисципліни** — задана дистанція, рогейн (командний залік), score-о (за кількістю/за часом),
  змішана; правила підрахунку інкапсульовані в стратегії дисципліни.
- **Зчитка на фініші** — фонове автозчитування CSV від систем хронометражу (SportIdent, Sport Time),
  спліти, друк спліт-роздруківки.
- **Результати** — обчислення статусів, місць, балів, розрядів; поза конкурсом; бонусні бали.
- **Протоколи** — експорт у Word (.docx): результати по групах, підсумок по сумі днів,
  стартові протоколи (звичайний і суддівський), спліти в HTML.
- **Старт** — жеребкування стартових лан та ручне редагування порядку старту.
- **Онлайн-результати** — публікація live-результатів у Supabase для окремого сайту глядачів.
- **Результати на монітор** — самооновлювані HTML-екрани для табло.

## Стек

- [.NET 10](https://dotnet.microsoft.com/) / C#
- [Avalonia 12](https://avaloniaui.net/) — кросплатформний UI (MVVM, CommunityToolkit.Mvvm)
- [Entity Framework Core](https://learn.microsoft.com/ef/core/) + SQLite — зберігання
- [Velopack](https://velopack.io/) — інсталятор та авто-оновлення (Windows, macOS)

## Архітектура

Три шари плюс бібліотека локалізації:

```
src/
  OrientPyx.Presentation/    Avalonia UI (Views, ViewModels, навігація, старт, DI)
  OrientPyx.BusinessLogic/   сутності, моделі, інтерфейси сервісів, правила дисциплін
  OrientPyx.DataAccess/      EF Core + SQLite: контексти БД, стори, файлова інфраструктура
  OrientPyx.Localization/    ILocalizationService + JSON-словники (uk-UA за замовч., en-US)
```

Напрямок залежностей: `Presentation → BusinessLogic, DataAccess, Localization`;
`DataAccess → BusinessLogic`; `BusinessLogic` та `Localization` ні від чого не залежать.
Уся робота з UI (Avalonia) — лише в Presentation, уся робота з БД (EF Core/SQLite) — лише в DataAccess.

## Збірка

```bash
dotnet restore
dotnet build
```

## Запуск

```bash
dotnet run --project src/OrientPyx.Presentation
```

Під час першого запуску створюється база даних застосунку. Дані змагань зберігаються
в папці `events` (шлях налаштовується на сторінці «Налаштування»).

## Інсталятор та оновлення

Готові до встановлення версії збираються через Velopack — див. [`build/README.md`](build/README.md):

- **Windows** — `OrientPyx-win-Setup.exe`, збирається GitHub Actions на тегу релізу (`build/publish.ps1`);
  підпис коду — див. [Code signing policy](#code-signing-policy);
- **macOS** — `OrientPyx-osx-arm64-Setup.pkg` (Apple Silicon) та `OrientPyx-osx-x64-Setup.pkg` (Intel), збираються
  GitHub Actions на тегу релізу (`build/publish-mac.sh`). Непідписаний пакет при першому запуску треба
  відкрити через правий клік → «Відкрити». Файли `.opyx` відкриваються з Finder так само, як з Провідника.
  На macOS недоступний друк на принтер (спліти, переможці, відомість) — експорт у Word/HTML працює.

Усі пакети лежать в одному GitHub Release. Встановлена програма перевіряє оновлення на
GitHub Releases (сторінка «Налаштування» → «Версія та оновлення»).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

> Status: Windows releases are not signed yet — signing through SignPath Foundation is planned.

Windows releases (`OrientPyx-win-Setup.exe` and the program's own files inside it) are built from this repository
by GitHub Actions ([`release-windows.yml`](.github/workflows/release-windows.yml)) and signed only from that build.
Third-party libraries shipped with the program are not re-signed. Details: [`docs/code-signing.md`](docs/code-signing.md).

**Team roles**

- Committers and reviewers: [repository contributors](https://github.com/VadymKolesnyk/OrientPyx/graphs/contributors)
- Approvers: [Vadym Kolesnyk](https://github.com/VadymKolesnyk) (repository owner)

**Privacy policy**

This program will not transfer any information to other networked systems unless specifically requested by the
user or the person installing or operating it. It connects only where the user asks it to: checking for and
downloading updates from GitHub Releases (the button on «Налаштування → Версія та оновлення»), publishing live
results to the Supabase project the user configures, and the LAN connection between computers in «Спільна робота».

*Українською:* Windows-версії підписуються безкоштовним сертифікатом SignPath Foundation лише зі збірки GitHub
Actions цього репозиторію. Програма нічого не надсилає в мережу без дії користувача: лише перевірка оновлень на
GitHub (кнопкою в «Налаштуваннях»), публікація онлайн-результатів у вказаний користувачем Supabase та робота
комп'ютерів у локальній мережі («Спільна робота»).

## Ліцензія

[MIT](LICENSE) © 2026 Vadym Kolesnyk
