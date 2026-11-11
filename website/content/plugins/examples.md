# Розбір стандартних плагінів

Два плагіни, що входять у кожну інсталяцію OrientPyx, — це й найкращі приклади: короткі, але справжні. Тут
розібрано їхній код. Як ними користуватися на змаганнях — у розділі [Стандартні плагіни](../guides/standard-plugins.md).

Повний код лежить у репозиторії:

- [`plugins/MissedControlPenalty/MissedControlPenaltyPlugin.cs`](https://github.com/VadymKolesnyk/OrientPyx/blob/main/plugins/MissedControlPenalty/MissedControlPenaltyPlugin.cs)
- [`plugins/WeightedRelay/WeightedRelayPlugin.cs`](https://github.com/VadymKolesnyk/OrientPyx/blob/main/plugins/WeightedRelay/WeightedRelayPlugin.cs)

Нижче код наведено з україномовними коментарями; логіка — та сама, що в репозиторії.

## «Штраф замість зняття»

**Що робить.** Пропущений КП не знімає учасника, а додає штраф часу: кілька хвилин за кожен пропущений КП.
Якщо пропущено забагато — учасника знімають, як звичайно. Плагін дає два види змагань: на основі «Заданого
напрямку» і на основі «Змішаного».

**Чого навчитися:** спільні правила для кількох основ; виклик основи й зміна її відповіді; налаштування на
день; колонки результатів; `AdjustTime`, що використовує `Missed`.

### Плагін і два види змагань

```csharp
public sealed class MissedControlPenaltyPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("missed-penalty", "Штраф замість зняття", "1.0",
        Author: "OrientPyx", Description: "Пропущений КП — штраф часу замість зняття.");

    public void Register(IPluginBuilder p)
    {
        p.AddDiscipline<SetCoursePenalty>();
        p.AddDiscipline<MixedPenalty>();
        p.AddStrings("uk-UA", Strings.Uk);
        p.AddStrings("en-US", Strings.En);
    }
}

public sealed class SetCoursePenalty : MissedPenaltyDiscipline
{
    public override string Id => "missed-penalty.set-course";
    public override string Name => "name.setCourse";
    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.SetCourse;
}

public sealed class MixedPenalty : MissedPenaltyDiscipline
{
    public override string Id => "missed-penalty.mixed";
    public override string Name => "name.mixed";
    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.Mixed;
}
```

Правила записано один раз в абстрактному класі `MissedPenaltyDiscipline`, а два маленькі класи відрізняються
лише `Id`, назвою й основою. Реєструються тільки вони — `AddDiscipline<T>()` вимагає неабстрактний клас із
конструктором без параметрів.

Ідентифікатори видів змагань починаються з ідентифікатора плагіна (`missed-penalty.`): так їх не сплутати з
видами змагань інших плагінів, і банер «Потрібен плагін» назве правильний плагін, якщо змагання відкриють без
нього.

### Налаштування, колонки, розряди

```csharp
public abstract class MissedPenaltyDiscipline : Discipline
{
    // Результат із прощеними КП не дає розряду.
    public override bool AwardsRanks => false;

    public override IEnumerable<SettingDef> Settings =>
    [
        Setting.Int("minutes", "setting.minutes", defaultValue: 5, min: 0, max: 120),
        Setting.Int("maxMissed", "setting.maxMissed", defaultValue: 3, min: 0, max: 50),
    ];

    public override IEnumerable<ResultColumnDef> Columns =>
    [
        Column.Number("missed", "column.missed", r => r.Get<int?>("missed")),
        Column.Time("penalty", "column.penalty", r => r.Get<TimeSpan?>("penalty")),
    ];
```

- Два цілі налаштування: «Штраф за пропущений КП, хв» (5) і «Найбільше пропущених КП без зняття» (3). Область
  не вказано, тож значення окремі для кожного дня. Значення зберігаються окремо для кожного з двох видів
  змагань, хоч ключі в них однакові.
- Дві колонки — «Пропущено КП» і «Штраф». Їх значення кладе `AdjustTime` нижче.
- Підписи — ключі рядків (`"setting.minutes"`, `"column.missed"`); переклади — у класі `Strings` наприкінці файлу.

### Перевірка відміток

```csharp
    public override FinishStatusResult EvaluateFinish(FinishContext ctx)
    {
        var r = base.EvaluateFinish(ctx);                    // вбудована перевірка
        if (r.Status != FinishStatus.Mp
            || ctx.FinishTime is null                        // без фінішу — DNF, штраф не допомагає
            || r.Missed.Count > ctx.Setting<int>("maxMissed"))
            return r;
        // Вбудована перевірка повертає MP раніше, ніж дивиться на контрольний час, тож для прощеного MP
        // контрольний час перевіряємо тут — за фактичним часом, без штрафу.
        return r with { Status = ctx.IsOverTime ? FinishStatus.Ovt : FinishStatus.Ok };
    }
```

Плагін не перевіряє відмітки сам — це вже вміє основа, зокрема для шаблонів змішаного напрямку. Він бере її
відповідь і змінює лише статус. `r.Missed` — усі пропущені КП (а не лише перший, названий у `Detail`), і
`with` зберігає їх у результаті: так вони дійдуть до `AdjustTime`. Якщо організатор поставить
«Найбільше пропущених КП без зняття» = 0, учасника з пропущеним КП знімають, як у звичайному заданому напрямку.

### Штраф часу

```csharp
    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        var missed = ctx.Row.Missed.Count;
        var penalty = TimeSpan.FromMinutes(ctx.Setting<int>("minutes")) * missed;
        ctx.Row.Set("missed", missed);
        ctx.Row.Set("penalty", missed > 0 ? penalty : null); // без штрафу — порожня клітинка
        return ctx.Time + penalty;
    }
}
```

`AdjustTime` викликається для кожного OK-результату, зокрема для щойно «прощених». `ctx.Row.Missed` — ті самі
пропущені КП з `EvaluateFinish`. Якщо статус поставив суддя вручну, `Missed` порожній — штрафу немає: рішення
судді остаточне.

Що далі робить програма: місця ставить за часом зі штрафом, «Очки» за формулою — теж від нього, розрядів немає
(`AwardsRanks => false`). У протоколі в колонці «Результат» — час зі штрафом, поруч — «Пропущено КП» і «Штраф».

## «Зважена естафета»

**Що робить.** Естафета, у якій час кожного етапу множиться на коефіцієнт K учасника за віком і статтю; час
команди — сума зважених часів етапів. Таблиця коефіцієнтів одна на все змагання.

**Чого навчитися:** додаткове поле учасника зі списком; табличне налаштування на все змагання; `RangeTable` і
`Age`; `MarkUnranked`; червона клітинка; естафета як основа.

### Поле «Стать»

```csharp
public sealed class WeightedRelayPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("weighted", "Зважена естафета", "1.0",
        Author: "OrientPyx", Description: "Час етапу × коефіцієнт за віком і статтю; час команди = Σ T·K.");

    public void Register(IPluginBuilder p)
    {
        p.AddParticipantField("gender", "Стать", FieldKind.Choice("Ч", "Ж"));
        p.AddDiscipline<WeightedRelayDiscipline>();
        p.AddStrings("uk-UA", Strings.Uk);
        p.AddStrings("en-US", Strings.En);
    }
}
```

Поле `gender` зі списком «Ч»/«Ж» з'явиться в змаганні, щойно «Зважену естафету» виберуть на сторінці «Дні» чи
«Групи». В імпорті й під час введення регістр не важливий: «ч» стане «Ч». Підпис «Стать» — звичайний текст, він не
перекладається.

### Вид змагань

```csharp
public sealed class WeightedRelayDiscipline : Discipline
{
    public override string Id => "weighted.relay";
    public override string Name => "name.relay";
    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.Relay;

    public override bool AwardsRanks => false;

    public override IEnumerable<string> RequiredFields => ["gender"];

    public override IEnumerable<SettingDef> Settings =>
        [Setting.Table("k", "setting.k", Coefficients.Columns, Coefficients.Default, scope: SettingScope.Competition)];

    public override IEnumerable<ResultColumnDef> Columns =>
    [
        Column.Time("time", "column.time", r => r.Get<TimeSpan?>("time")),   // чистий час етапу T
        Column.Number("k", "column.k", r => r.Get<double?>("k"), format: "0.00",
            problem: r => r.Get<bool>(MissingKey)),          // червона, коли K не визначено
        Column.Time("adj", "column.adj", r => r.Get<TimeSpan?>("adj")),
    ];

    private const string MissingKey = "k.missing";
```

- Основа — «Естафета»: команди, етапи, передача, перезапуски, дистанції етапів, місця команд — усе вбудоване.
  Плагін змінює лише час етапу.
- `RequiredFields` — порожня «Стать» червоніє в таблицях учасників у тих, хто біжить у групі «Зваженої естафети»;
  на результати підсвітка не впливає. Саме поле створюється завдяки `AddParticipantField`.
- Таблиця K має область `SettingScope.Competition`: одна на всі дні й групи з цим видом змагань.
- Колонки «Час», «K» і «T·K» — у такому порядку вони стоять у таблиці дня й у протоколі. «Час» — чистий час етапу:
  у колонці «Результат» уже T·K.
- Колонка «K» з форматом `"0.00"` і `problem` — клітинка червона, коли коефіцієнт визначити не вдалося.

### Таблиця коефіцієнтів

```csharp
internal static class Coefficients
{
    public static readonly IReadOnlyList<TableColumnDef> Columns =
    [
        TableColumnDef.Text("range", "table.age"),
        TableColumnDef.Number("Ч", "table.male"),
        TableColumnDef.Number("Ж", "table.female"),
    ];

    public static readonly TableValue Default = TableValue.FromRows(Columns,
        ["8–10", "0.80", "0.78"],
        ["11–12", "0.86", "0.82"],
        // … ще 10 рядків …
        ["70+", "0.71", "0.58"]);
}
```

Ключі колонок «Ч» і «Ж» дорівнюють значенням поля «Стать» — тож значення поля можна прямо передати в
`RangeTable.Lookup` як ключ колонки. Колонка `"range"` — діапазон віку; `RangeTable.FromSetting` читає її за
замовчуванням. `TableValue.FromRows` будує значення за замовчуванням з позиційних рядків; кнопка «Скинути до
типової» в редакторі таблиці повертає саме його.

### Коефіцієнт для етапу

```csharp
    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        ctx.Row.Set("time", ctx.Time);   // «Час» — і тоді, коли K не визначено

        double? k = null;
        if (ctx.Participant.BirthYear is { } year && ctx.Participant.Field("gender") is { } gender)
        {
            var table = RangeTable.FromSetting(ctx.Setting<TableValue>("k"));
            k = table.Lookup(Age.InYear(year, ctx.Date), gender);   // поза таблицею — найближчий рядок
        }

        if (k is not { } coefficient)
        {
            // Немає року народження чи статі (або K для них): команда без місця й очок,
            // етап рахується з K = 1.
            ctx.Row.Set(MissingKey, true);
            ctx.Row.MarkUnranked(MissingKey);
            return ctx.Time;
        }

        // T·K округлюємо до секунди на кожному етапі — навіть при K = 1.00, коли програма
        // незмінений час не округлила б. Так сума колонки «T·K» завжди дорівнює часу команди.
        var adj = RoundToSecond(ctx.Time * coefficient);
        ctx.Row.Set("k", coefficient);
        ctx.Row.Set("adj", adj);
        return adj;
    }

    private static TimeSpan RoundToSecond(TimeSpan time) =>
        TimeSpan.FromSeconds(Math.Round(time.TotalSeconds, MidpointRounding.AwayFromZero));
}
```

- `AdjustTime` викликається для кожного OK-етапу; `ctx.Time` — час етапу, `ctx.Leg` — номер етапу.
- Вік — `Age.InYear(рік народження, ctx.Date)`: рік дня змагань мінус рік народження. Якщо дату дня не задано,
  програма бере дату найранішого фінішу дня.
- `RangeTable.Lookup`: вік, менший за перший діапазон, бере перший рядок; більший за останній — останній
  («70+»); у проміжку між рядками — найближчий.
- `MarkUnranked(MissingKey)` на етапі знімає з місця й очок **усю команду**, але її час рахується й показується.
  Причина — ключ рядків: у протоколі після прізвища друкується «K не визначено». Щойно організатор заповнить
  дату народження чи стать, результати перераховуються й команда отримує місце.
- Програма складає скориговані часи етапів у час команди й ставить місця вбудованим правилом естафети — разом із
  правилами для перезапусків. `Rank` плагін не перевизначає.

## Що з цього взяти у свій плагін

- Починайте з `base.…` і змінюйте відповідь основи, а не пишіть перевірку з нуля.
- Усе, що треба показати в колонці, кладіть у `ctx.Row.Set` — колонка бачить лише ці значення.
- Числа, які можуть відрізнятися на різних змаганнях, виносьте в налаштування; одну таблицю на все змагання —
  в налаштування з `SettingScope.Competition`.
- Учасник без потрібних даних — це `MarkUnranked` з поясненням, а не виняток.
- Ідентифікатори плагіна, видів змагань, ключі налаштувань і колонок вибирайте один раз і назавжди.
