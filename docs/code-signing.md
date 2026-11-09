# Підпис коду Windows (SignPath Foundation)

Мета: щоб `OrientPyx-win-Setup.exe` не показував «Невідомий видавець». Сертифікат безкоштовний, від
[SignPath Foundation](https://signpath.org/) (програма для open source). Видавцем у Windows буде
**SignPath Foundation**. Умови: [signpath.org/terms](https://signpath.org/terms).

## Як це працює

- Сертифікат зберігається в апаратному сховищі (HSM) SignPath, ключ ми ніколи не отримуємо.
- SignPath підписує лише те, що зібрав GitHub Actions на серверах GitHub (GitHub-hosted) з цього публічного
  репозиторію. Його конектор перевіряє походження файлів. Файл, зібраний на власному комп'ютері, не підпишуть.
- Кожен релізний запит на підпис затверджує вручну Approver на сайті SignPath, і workflow чекає на це до години.
- Підписуються лише **наші** файли. Сторонні бібліотеки (Avalonia, SkiaSharp, SQLite, ClosedXML…) за правилами
  SignPath не перепідписуються.

### Чому два запити на реліз

Velopack має підписувати файли сам, під час `vpk pack`: він додає в пакет `Update.exe` (як `Squirrel.exe`) і
стартовий exe, а одразу після підпису рахує хеші пакета для фіду оновлень. SignPath же підписує лише артефакт,
вивантажений кроком workflow. Тому `.github/workflows/release-windows.yml` пакує двічі з однієї збірки:

1. **Пробне пакування** з хуком `build/signpath/vpk-sign-hook.ps1 -Mode Collect` збирає файли, які vpk підписав
   би, у `build/signing/unsigned`. → **Запит 1**: SignPath повертає їх підписаними.
2. **Справжнє пакування** з `-Mode Swap` підставляє підписані копії (за SHA-256 непідписаних байтів).
3. `OrientPyx-win-Setup.exe` Velopack будує останнім, і в хеші фіду він не входить. → **Запит 2**: його
   підписують після пакування.
4. `build/signpath/Test-ReleaseSignatures.ps1` перевіряє підписи (Setup, повний пакет, portable zip), і лише
   після цього `vpk upload`.

Що вважається «нашим», визначає `build/signpath/SigningRules.ps1`: `OrientPyx.exe`, `OrientPyx*.dll`, стандартні
плагіни (`plugins\<Name>\<Name>.dll`), стартовий exe Velopack (`OrientPyx_ExecutionStub.exe`), `Squirrel.exe`
(Update.exe) і `Setup.exe`. Ті самі файли перелічено в `build/signpath/artifact-configuration.xml`.

SignPath вимагає, щоб у підписаних файлах **назва продукту = OrientPyx**, а **версія продукту = версія релізу**:
- `src/Directory.Build.props` задає `Product=OrientPyx` для всіх збірок і вимикає суфікс `+<commit sha>` у
  ProductVersion;
- `plugins/Directory.Build.props` робить те саме для плагінів;
- `build/publish.ps1` збирає плагіни з `-p:InformationalVersion=<версія>`. Їхні власні Version/AssemblyVersion
  не змінюються.

Поки підпис не налаштовано (немає секрету й змінної, див. нижче), той самий workflow збирає **непідписаний**
пакет, як і раніше.

## Що зроблено в репозиторії

| Файл | Що |
|---|---|
| `.github/workflows/release-windows.yml` | Windows-реліз у GitHub Actions на тег `v*`: створює реліз, завантажує попередній (для дельт), пакує, підписує (якщо налаштовано), вивантажує |
| `build/publish.ps1` | нові параметри `-OutputDir`, `-SkipBuild`, `-SignTemplate`, `-NoDelta`; локальний запуск без них — як раніше |
| `build/signpath/vpk-sign-hook.ps1` | хук `--signTemplate` (Collect / Swap) |
| `build/signpath/SigningRules.ps1` | які файли наші |
| `build/signpath/Test-ReleaseSignatures.ps1` | перевірка підписів перед вивантаженням |
| `build/signpath/artifact-configuration.xml` | artifact configuration, яку треба вставити в SignPath |
| `src/Directory.Build.props`, `plugins/Directory.Build.props` | метадані продукту |
| `README.md` → «Code signing policy» | сторінка політики, обов'язкова за умовами SignPath |

## Що зробити власнику (по кроках)

### 1. До заявки

- [ ] **2FA на GitHub** для кожного, хто комітить у репозиторій. Це обов'язкова умова SignPath для всієї
      команди, і для SignPath-акаунтів теж.
- [ ] Перевірити розділ **Code signing policy** у `README.md`: ролі (Committers and reviewers, Approvers) і текст
      про приватність. Якщо підтверджуватиме підпис інша людина, впишіть її в Approvers.
- [ ] Бажано, щоб Windows-реліз уже хоч раз зібрався через `release-windows.yml` (без підпису). Так видно, що
      збірка відтворювана з репозиторію.

### 2. Заявка

Форма: <https://signpath.org/apply>. Рішення ухвалюють на власний розсуд, розгляд триває кілька тижнів. Чернетка
відповідей (англійською, як і форма):

> **Project:** OrientPyx — <https://github.com/VadymKolesnyk/OrientPyx> (MIT)
>
> **Description:** Cross-platform desktop application (.NET 10, Avalonia) for running orienteering competitions:
> entries, groups and courses, SportIdent / Sport Time chip read-out at the finish, result computation for several
> disciplines (set course, score, rogaine, relay…), Word/HTML protocols and live online results. The UI is
> Ukrainian; it is used by orienteering event organisers in Ukraine. 42 releases since July 2026.
>
> **Download page:** <https://github.com/VadymKolesnyk/OrientPyx/releases> (code signing policy:
> <https://github.com/VadymKolesnyk/OrientPyx#code-signing-policy>)
>
> **Build:** GitHub Actions on GitHub-hosted `windows-latest` runners, workflow
> `.github/workflows/release-windows.yml`, triggered by a `v*` tag. The installer and updater are built with
> Velopack.
>
> **What we want to sign:** the Velopack installer `OrientPyx-win-Setup.exe` and the program's own binaries
> inside the package: `OrientPyx.exe`, `OrientPyx*.dll`, two standard plugin DLLs, and Velopack's launcher stub and
> updater (`Update.exe`) that are part of every installation. Third-party libraries are not signed. Artifact
> configuration: `build/signpath/artifact-configuration.xml`.
>
> **Why:** organisers install the program on several laptops before every event, and Windows shows "Unknown
> publisher" / SmartScreen warnings for the unsigned installer.

### 3. Після схвалення — налаштування SignPath

(Частину цього SignPath робить під час онбордингу сам. Перевірте, що все є.)

- [ ] **Trusted build system** «GitHub.com» додано в організацію й прив'язано до проєкту.
- [ ] Встановлено **SignPath GitHub App** з доступом до `VadymKolesnyk/OrientPyx`.
- [ ] **Artifact configuration**: вміст `build/signpath/artifact-configuration.xml` як конфігурація проєкту
      **за замовчуванням** (або задайте її slug у змінній `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`).
- [ ] Політики підпису: `test-signing` (тестовий сертифікат, без підтвердження) і `release-signing` (сертифікат
      Foundation, ручне підтвердження Approver'ом).
- [ ] Користувач-submitter (можна окремий «CI user») з правом подавати запити в обидві політики, і його
      **API token**.

### 4. Налаштування GitHub

Репозиторій → Settings → Secrets and variables → Actions:

| Тип | Назва | Значення |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | API token із кроку 3 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | id організації (з адреси або налаштувань SignPath) |
| Variable | `SIGNPATH_PROJECT_SLUG` | лише якщо slug проєкту не `OrientPyx` |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | спершу `test-signing`, потім `release-signing` |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | лише якщо конфігурація не за замовчуванням |

Підпис вмикається, щойно є і секрет, і `SIGNPATH_ORGANIZATION_ID`.

### 5. Тестовий прогін (test-signing)

```bash
gh workflow run release-windows.yml --ref main -f version=<наступна, ще не випущена версія> -f upload=false
gh run watch
```

`upload=false` нічого не публікує. Перевірте в лозі:
- кроки «Sign the package files» і «Sign the installer» пройшли;
- крок «Put the signed installer in place…» вивів `ok` для кожного файлу (статус тестового сертифіката не
  `Valid`, і це нормально);
- в артефакті `orientpyx-windows-<версія>` є `OrientPyx-win-Setup.exe`. Встановіть його на тестовій машині
  і переконайтеся, що програма запускається й оновлюється.

### 6. Бойовий режим

- [ ] Змінна `SIGNPATH_SIGNING_POLICY_SLUG` = `release-signing`.
- [ ] Прибрати рядок «Status: …» з розділу Code signing policy у `README.md`.
- [ ] На кожен реліз затвердити **два** запити в SignPath (сповіщення приходять на пошту Approver'а). Workflow
      чекає кожен до години. Якщо не встигли, `gh run rerun <id> --failed`.

## Реліз

`/new-version` пушить тег, а далі все робить GitHub Actions: `release-windows.yml` (Windows, з підписом) і
`release-macos.yml` (macOS). Локальний `build/publish.ps1 -Upload` — лише аварійний варіант, коли workflow не
працює. Він публікує **непідписаний** Windows-реліз, і запускати його разом із workflow для того самого тегу не
можна.

## Якщо щось пішло не так

- **«… differs from every collected file»** у кроці «Pack with the signed files»: два пакування побачили різні
  байти (наприклад, нова версія vpk змінила стартовий exe). vpk у workflow закріплено (`--version 1.2.161`).
  Оновлюючи його, перевірте `vpk-sign-hook.ps1` проти вихідного коду Velopack (`CodeSign.cs`,
  `WindowsPackCommandRunner.cs`).
- **SignPath відхиляє файл через метадані** (product name/version): подивіться
  `(Get-Item <файл>).VersionInfo | Format-List` у зібраному `build/publish`. Найімовірніше, новий проєкт або плагін
  не отримав `Product=OrientPyx` чи збирається поза `publish.ps1`.
- **SignPath не дозволяє підписувати `Squirrel.exe`** (це бінарник Velopack): приберіть його з `SigningRules.ps1`
  і `artifact-configuration.xml`. Непідписаний Update.exe не впливає на попередження під час встановлення.
- **Новий власний exe/dll**, якого немає в `SigningRules.ps1`, залишиться непідписаним. Додайте правило (і рядок в
  artifact configuration, якщо назва не підпадає під наявні шаблони).

## Чого очікувати від Windows

- «Невідомий видавець» у вікні UAC зникає з першим підписаним релізом.
- Вікно SmartScreen «Windows захистив ваш ПК» залежить від репутації сертифіката й файлу. Воно може ще
  з'являтися, доки не накопичаться завантаження (EV-сертифікати теж із 2024 року не обходять цього одразу).
