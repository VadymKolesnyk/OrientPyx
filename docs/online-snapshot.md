# Онлайн-результати — контракт даних (Supabase)

> Що саме OrientPyx пише в Supabase-проєкт, який читає окремий React-фронтенд глядачів. Джерело правди в коді:
> `OnlineResultsSnapshot` (BusinessLogic, будує `ICompetitionEditorService.GetOnlineResultsSnapshotAsync`) і
> `SupabaseResultPublisher` (DataAccess, PostgREST). Оновлено: 2026-10-27 (крок 7.1 плану плагінів).

## Як публікується

- PostgREST `POST /rest/v1/<table>?on_conflict=<ключ>` з `Prefer: resolution=merge-duplicates,return=minimal`
  (upsert), ключ доступу — service-role з налаштувань програми.
- `events` + `event_days` — один раз на slug; `groups` — один раз на (slug, день); `results` і `relay_teams` — щотіку.
- «Перезавантажити за всі дні» видаляє `relay_teams`, `results`, `groups`, `event_days` змагання (`event=eq.<slug>`)
  і публікує всі дні заново. Рядок `events` лишається.
- Час — рядок `hh:mm:ss` (або `null`), `*_seconds` — ціле число секунд.

## Таблиці (наявні — без змін)

| Таблиця | Ключ | Поля |
|---|---|---|
| `events` | `id` | `id` (slug), `title`, `subtitle`, `days_count`, `standings`, `points`, `display_config` (jsonb: `version`=1, `points`, `standings`, `separateDsqLg`, `separateDsqSm`, `columns[{key, order, lg, sm}]`), `updated_at` |
| `event_days` | `event,day` | `event`, `day`, `label` (`dd.MM.yyyy` або `null`), `ord` |
| `groups` | `event,name,day` | `event`, `name`, `day`, `distance_km`, `controls`, `ord` |
| `results` | `event,bib,day` | `event`, `bib` (ціле), `day`, `grp`, `rk` (місце або `null`), `full_name`, `team`, `club`, `region`, `birth`, `qual`, `reason` (`MP`/`OVT`/`DNF`/`DSQ` лише при `status=dsq`), `start_time`, `finish_time`, `result_time`, `result_seconds`, `points` (очки за правилом, інакше бали рогейну), `status`, `updated_at` |

`status`: `finished` (OK з місцем), `finished_pending` (OK без місця або зчитано без статусу), `running`
(не зчитано), `dsq` (MP/OVT/DNF/DSQ), `dns`.

Особисті дисципліни публікуються **точно як раніше**. Учасник без цілого номера в `results` не потрапляє
(попередження в журналі). Назви схожих груп («M12»/«М12») зводяться до однієї.

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
| `columns` | jsonb | `[{key, label}]` — додаткові колонки плагіна, що є в `legs[].extra` (підписи вже перекладені) |
| `legs` | jsonb | масив етапів (нижче) |
| `updated_at` | text | ISO-8601 UTC |

Ключ upsert — **`event,day,num`** (потрібен унікальний індекс). Команда **без номера** (імпорт UOF створює команди
без номерів — їх вписують пізніше) не публікується, доки номер не з'явиться. Етап у `legs`:

```json
{ "leg": 2, "bib": "100.2", "full_name": "…", "birth": "dd.MM.yyyy", "course": "B",
  "leg_time": "00:25:00", "cumulative": "00:45:00",
  "status": "finished|dsq|dns|running|pending", "reason": "MP", "restarted": false,
  "extra": { "weighted.k": "0.95", "weighted.adj": "23:45" } }
```

- `leg_time` — фактичний час етапу (фініш − старт етапу); `cumulative` — наростаючий скоригований час (Р-32).
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
