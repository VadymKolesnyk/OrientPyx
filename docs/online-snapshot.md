# Онлайн-результати — контракт даних (Supabase)

> Що саме OrientPyx пише в Supabase-проєкт, який читає окремий React-фронтенд глядачів. Джерело правди в коді:
> `OnlineResultsSnapshot` (BusinessLogic, будує `ICompetitionEditorService.GetOnlineResultsSnapshotAsync`) і
> `SupabaseResultPublisher` (DataAccess, PostgREST). Оновлено: 2026-11-12 (учасники без номера — під від'ємним `bib`).
> Раніше: 2026-11-11 (додаткові колонки онлайн, «Наростаючий», естафета за `display_config`).

## Як публікується

- PostgREST `POST /rest/v1/<table>?on_conflict=<ключ>` з `Prefer: resolution=merge-duplicates,return=minimal`
  (upsert), ключ доступу — service-role з налаштувань програми.
- `events` + `event_days` — один раз на slug (`events` — ще й щоразу, коли змінився `display_config`: з'явилася нова
  додаткова колонка); `groups` — один раз на (slug, день); `results` і `relay_teams` — щотіку.
- «Перезавантажити за всі дні» видаляє `relay_teams`, `results`, `groups`, `event_days` змагання (`event=eq.<slug>`)
  і публікує всі дні заново. Рядок `events` лишається.
- Час — рядок `hh:mm:ss` (або `null`), `*_seconds` — ціле число секунд.

## Таблиці (наявні — без змін)

| Таблиця | Ключ | Поля |
|---|---|---|
| `events` | `id` | `id` (slug), `title`, `subtitle`, `days_count`, `standings`, `points`, `display_config` (jsonb: `version`=1, `points`, `standings`, `separateDsqLg`, `separateDsqSm`, `columns[{key, order, lg, sm, label?}]`), `updated_at` |
| `event_days` | `event,day` | `event`, `day`, `label` (`dd.MM.yyyy` або `null`), `ord` |
| `groups` | `event,name,day` | `event`, `name`, `day`, `distance_km`, `controls`, `ord` |
| `results` | `event,bib,day` | `event`, `bib` (ціле; від'ємне — учасник без номера, нижче), `day`, `grp`, `rk` (місце або `null`), `full_name`, `team` (команда рогейну; в особистих порожньо), `club`, `region`, `birth`, `qual`, `reason` (`MP`/`OVT`/`DNF`/`DSQ` лише при `status=dsq`), `start_time`, `finish_time`, `result_time` (зарахований час — зі штрафом / коефіцієнтом, якщо вид змагань його змінює), `result_seconds`, `points` (очки за правилом, інакше бали рогейну), `status`, `extra` (jsonb, нове — нижче), `updated_at` |

`status`: `finished` (OK з місцем), `finished_pending` (OK без місця або зчитано без статусу), `running`
(не зчитано), `dsq` (MP/OVT/DNF/DSQ), `dns`.

Особисті дисципліни публікуються **точно як раніше**. Назви схожих груп («M12»/«М12») зводяться до однієї.

**Учасник без номера** (порожній або не ціле додатне число) і другий власник номера, який уже має інший учасник
дня, публікуються під **від'ємним `bib`** — сталим ключем з Id учасника (`OnlineResultRow.UnnumberedKey`: 31 біт
Guid, від −1 до −2³¹+1; збіг у межах дня зсувається на −1). Ключ однаковий у всі дні (сайт складає «Суму» за `bib`)
і на всіх машинах (станція бачить Id хоста). Сайт показує лише додатний `bib` (`fmtBib`), тож у колонці «№» такий
учасник порожній; старіший сайт показав би від'ємне число. Схема бази не змінюється.

Коли такий учасник отримує номер (або вибуває з дня), його рядок під від'ємним ключем видаляється: перший тік дня
читає з сайту наявні від'ємні `bib` (`GET results?select=bib&event=eq.<slug>&day=eq.<N>&bib=lt.0`), наступні
порівнюють із надісланим, і зниклі видаляються (`DELETE … bib=in.(…)`, по 50). Рядки з додатними номерами, як і
раніше, лише upsert-яться — старий номер прибирає «Перезавантажити за всі дні».

## Колонки (`display_config.columns`)

Ключі вбудованих колонок — `rk`, `full_name`, `bib`, `birth`, `qual`, `team`, `club`, `region`, `start_time`,
`result_time`, `gap`, `cumulative`, `status`, `points` (`ResultColumnInfo.All`). Порядок і `lg`/`sm` — з
«Колонок результатів» онлайн-сторінки (`OnlineDisplayConfig`); колонка, якої немає в збереженому налаштуванні
(з'явилася пізніше), бере видимість з `OnlineDisplayConfig.Default` і стає після тієї, за якою йде там.

- **`cumulative`** («Наростаючий») — наростаючий час команди після етапу; лише в естафеті. Конфіг старішого
  OrientPyx його не має — сайт тоді ставить його видимим після `gap`.
- **Додаткові колонки** (§10.2): колонки плагінів (`"<pluginId>.<key>"`) і додаткові поля учасника
  (`"field.<key>"`) — у `columns` з полем **`label`** (уже перекладений підпис; у вбудованих його немає). Нова
  колонка плагіна стає видимою на великому екрані, нове поле — прихованим. Конфіг підписує всі додаткові колонки,
  які бачив публікатор за сесію (усі дні при «Перезавантажити за всі дні»); сайт ховає колонку, у якої в групі
  немає жодного значення.
- **Значення** — `results.extra` (jsonb `{key: "текст"}`, порожні не пишуться; `null`, коли значень немає) і
  `relay_teams.legs[].extra`. Колонку `results.extra` додають на наявній базі так:
  `alter table public.results add column if not exists extra jsonb;`. Без неї PostgREST відповідає 4xx з назвою
  колонки — публікатор запам'ятовує це (`IResultPublisher.ExtraUnsupported`), пише одне попередження в журнал і
  далі шле рядки без `extra`; сайт, якому база відмовила в `extra`, тягне без неї.

Естафетна таблиця сайту бере ті самі колонки: рядок команди заповнює `rk`, `full_name` (назва), `bib` (номер
команди), `club`, `region`, `result_time` (час команди), `gap`, `status`, `points`; рядок етапу — `full_name`
(бігун), `bib` («100.2»), `birth`, `qual`, `club`, `region`, `start_time`, `result_time` (зарахований час етапу),
`cumulative`, `status` і додаткові колонки.

## Естафета — нова таблиця `relay_teams`

Групи-естафети (`CompetitorModel.TeamWithLegs`) **не** пишуться в `results`: номер етапу «100.2» не є цілим `bib`,
а старий фронтенд показав би бігунів етапів як окремих особистих учасників. Натомість — окрема таблиця, яку
старий фронтенд просто не читає:

| Поле | Тип | Опис |
|---|---|---|
| `event` | text | slug |
| `day` | int | номер дня |
| `num` | int | номер команди (унікальний у дні, Р-20) |
| `grp` | text | група |
| `name`, `club`, `region` | text | команда |
| `rk` | int? | місце команди; `null` — без місця (не OK, «поза конкурсом», неповна, Р-48) |
| `result_time` / `result_seconds` | text? / int? | час команди = Σ скоригованих часів етапів (Р-32) |
| `points` | numeric? | очки команди (однакові кожному члену, Р-37) |
| `status`, `reason` | text | як у `results`; команда без статусу (ще на дистанції) — завжди `running`, скільки б етапів не було зчитано |
| `out_of_competition`, `restarted` | bool | «П/К» / «з перезапуском» |
| `columns` | jsonb | `[{key, label}]` — колонки плагіна з `legs[].extra` (підписи вже перекладені); для сайту, що показує їх рядком під ПІБ етапу. Поточний сайт бере колонки з `display_config` |
| `legs` | jsonb | масив етапів (нижче) |
| `updated_at` | text | ISO-8601 UTC |

Ключ upsert — **`event,day,num`** (потрібен унікальний індекс). Команда **без номера** (імпорт UOF створює команди
без номерів — їх вписують пізніше) не публікується, доки номер не з'явиться. Етап у `legs`:

```json
{ "leg": 2, "bib": "100.2", "full_name": "…", "birth": "dd.MM.yyyy", "qual": "I", "club": "…", "region": "…",
  "course": "B", "start_time": "10:20:00",
  "leg_time": "00:25:00", "result_time": "00:23:45", "cumulative": "00:45:00",
  "status": "finished|dsq|dns|running|pending", "reason": "MP", "restarted": false,
  "extra": { "weighted.time": "25:00", "weighted.k": "0.95", "weighted.adj": "23:45", "field.gender": "Ч" } }
```

- `leg_time` — «Час», чистий час етапу (фініш − старт етапу); `result_time` — «Результат», зарахований час етапу
  (скоригований видом змагань, напр. T·K зваженої естафети; інакше = `leg_time`); `cumulative` — наростаючий
  зарахований час (Р-32). `qual`, `club`, `region`, `start_time`, `result_time` додано 2026-11-11 — старіший
  OrientPyx їх не шле, сайт тоді показує `leg_time`.
- `extra` — значення додаткових колонок (плагінів і додаткових полів) за ключем.
- `status = pending` — етап «очікує» (попередній без фінішу й без перезапуску, Р-30).
- `course` — призначена дистанція етапу (Р-44), `null`, коли група біжить одну дистанцію.
- Етап без бігуна — `full_name: null`.

Схема для Supabase (приклад):

```sql
create table relay_teams (
  event text not null, day int not null, num int not null,
  grp text, name text, club text, region text, rk int,
  result_time text, result_seconds int, points numeric,
  status text, reason text, out_of_competition boolean, restarted boolean,
  columns jsonb, legs jsonb, updated_at text,
  primary key (event, day, num)
);
```

**Сумісність.** Якщо таблиці `relay_teams` у проєкті немає, PostgREST відповідає 4xx — публікатор запам'ятовує це
(`IResultPublisher.TeamsUnsupported`), одноразово пише в журнал попередження і далі публікує лише особисті
результати; помилкою тіку це не вважається. Повторна спроба — після зміни налаштувань публікації / перезапуску.

Фронтенд глядачів — окремий репозиторій [VadymKolesnyk/o-results](https://github.com/VadymKolesnyk/o-results)
(`web/` — React-сайт на GitHub Pages, `online-db/schema.sql` — схема Supabase разом із `relay_teams`; команди
естафет він уже показує, крок 7.2). Як розгорнути власну копію (Supabase + GitHub Pages) і підключити OrientPyx —
сторінка документації `website/content/race-day/online-site.md`.
