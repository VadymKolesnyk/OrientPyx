using OrientPyx.BusinessLogic.Interfaces;
using static OrientPyx.BusinessLogic.Interfaces.AnalyticsType;

namespace OrientPyx.BusinessLogic.Services.Mcp.Analytics;

/// <summary>A table of the analytics schema: its meaning and its fixed columns (participants adds field_* columns).</summary>
public sealed record AnalyticsTableInfo(string Name, string Description, IReadOnlyList<AnalyticsColumn> Columns);

/// <summary>
/// The tables the assistant queries (plan §5.2): English snake_case columns, the meaning with the Ukrainian terms.
/// Times are seconds with full precision (*_s); a *_text column is the text the protocol shows (fractions dropped,
/// never rounded). Clock times of the day (*_clock_s) are seconds since midnight.
/// </summary>
public static class AnalyticsSchema
{
    private static AnalyticsColumn C(string name, AnalyticsType type, string description) => new(name, type, description);

    private static readonly AnalyticsColumn DayCol = C("day", Integer, "Day number (день), 1-based.");
    private static readonly AnalyticsColumn ParticipantCol = C("participant_id", AnalyticsType.Text, "Participant id (joins participants).");
    private static readonly AnalyticsColumn GroupNameCol = C("group_name", AnalyticsType.Text, "Group name (група), e.g. М21, Ж14.");

    public static IReadOnlyList<AnalyticsTableInfo> Tables { get; } =
    [
        new("competition", "One row: the competition («Інформація»).",
        [
            C("name", AnalyticsType.Text, "Name."), C("identifier", AnalyticsType.Text, "Folder identifier."), C("venue", AnalyticsType.Text, "Venue."),
            C("organisation", AnalyticsType.Text, "Organiser."), C("start_date", AnalyticsType.Text, "YYYY-MM-DD."), C("end_date", AnalyticsType.Text, "YYYY-MM-DD."),
            C("day_count", Integer, "Number of days."), C("course_setter", AnalyticsType.Text, "Начальник дистанції."),
            C("chief_judge", AnalyticsType.Text, "Головний суддя."), C("chief_secretary", AnalyticsType.Text, "Головний секретар."), C("jury", AnalyticsType.Text, "Журі."),
            C("roster_enabled", AnalyticsType.Boolean, "Мандатка (roster) is used."),
            C("payment_per_day", AnalyticsType.Boolean, "Payment is kept per day."),
            C("raised_fee_enabled", AnalyticsType.Boolean, "Підвищений внесок is used."), C("raised_fee_amount", Real, "Raised fee surcharge."),
            C("chip_rental_price_per_day", Real, "Rental chip price per day."),
        ]),
        new("days", "One row per day.",
        [
            DayCol, C("day_id", AnalyticsType.Text, "Day id."), C("date", AnalyticsType.Text, "YYYY-MM-DD."), C("venue", AnalyticsType.Text, "Venue of the day."),
            C("discipline", AnalyticsType.Text, "Вид змагань of the day: SetCourse (заданий напрямок), ScoreByCount, ScoreByTime, Rogaine, Mixed, Scatter (розсіювання), Relay (естафета) or a plugin id."),
            C("locked", AnalyticsType.Boolean, "Closed for edits (закритий день)."), C("participants", Integer, "People taking part that day."),
        ]),
        new("groups", "Group × day: the group's settings on that day.",
        [
            DayCol, C("group_id", AnalyticsType.Text, "Group id."), GroupNameCol,
            C("discipline", AnalyticsType.Text, "Effective discipline (the group's override or the day's)."),
            C("course", AnalyticsType.Text, "Course: control order (порядок КП), e.g. '31 45 33'; for Mixed a pattern."),
            C("distance_km", Real, "Course length, km."), C("required_controls", Integer, "Мін. КП (score disciplines)."),
            C("time_limit_s", Integer, "Контрольний час, s."), C("max_late_s", Integer, "Макс. запізнення, s (past it: OVT)."),
            C("penalty_per_min", Real, "Penalty per late minute («Бали»)."), C("min_birth_year", Integer, "Age window: oldest birth year."),
            C("max_birth_year", Integer, "Age window: youngest birth year."), C("points_rule_id", AnalyticsType.Text, "Rule of «Очки» (points_rules)."),
            C("rank_level", AnalyticsType.Text, "Розряди: none/adult/junior."), C("master_count", Integer, "Masters of sport for the rank calculation."),
            C("leg_count", Integer, "Relay legs."), C("course_mode", AnalyticsType.Text, "Relay course assignment: group/by_bib/team_set/per_leg."),
            C("course_setter", AnalyticsType.Text, "Начальник дистанції of the group."), C("entry_fee", Real, "Стартовий внесок of the group."),
            C("participants", Integer, "People in the group that day."),
        ]),
        new("controls", "Control (КП) × day.",
        [
            DayCol, C("code", AnalyticsType.Text, "Control code."), C("type", AnalyticsType.Text, "control / start / finish."),
            C("points", Integer, "Бали of the control (rogaine / score)."),
            C("disabled", AnalyticsType.Boolean, "Проблемний КП (cancelled: not punching it is no MP)."),
            C("lat", Real, "Latitude."), C("lon", Real, "Longitude."), C("map_x", Real, "Map X, mm."), C("map_y", Real, "Map Y, mm."),
        ]),
        new("course_legs", "Group × day × leg of the prescribed course (start → first control … last → finish).",
        [
            DayCol, C("group_id", AnalyticsType.Text, "Group id."), GroupNameCol, C("leg_no", Integer, "1-based."),
            C("from_code", AnalyticsType.Text, "From control (S = start)."), C("to_code", AnalyticsType.Text, "To control (F = finish)."),
            C("length_km", Real, "Straight-line length from coordinates, km (null without coordinates)."),
        ]),
        new("participants", "One row per person. Additional fields (додаткові поля) come as field_<key> columns (see custom_fields).",
        [
            ParticipantCol, C("bib", AnalyticsType.Text, "Номер."), C("full_name", AnalyticsType.Text, "ПІБ."), C("birth_date", AnalyticsType.Text, "YYYY-MM-DD."),
            C("birth_year", Integer, "Рік народження."), C("club", AnalyticsType.Text, "Клуб."), C("region", AnalyticsType.Text, "Регіон / область."),
            C("dussh", AnalyticsType.Text, "ДЮСШ."), C("rank", AnalyticsType.Text, "Розряд / кваліфікація (as typed)."), C("coach", AnalyticsType.Text, "Тренер."),
            C("representative", AnalyticsType.Text, "Представник."), C("team", AnalyticsType.Text, "Команда (text field)."), C("fsou_code", AnalyticsType.Text, "Код ФСОУ."),
            C("fsou_member", AnalyticsType.Boolean, "Член ФСОУ."), C("note", AnalyticsType.Text, "Примітка."),
            C("checked_in", AnalyticsType.Boolean, "Мандатку пройдено."), C("pays_raised_fee", AnalyticsType.Boolean, "Платить підвищений внесок."),
            C("entry_fee", Real, "Total entry fee (внесок) over the days, discounts applied."), C("payment", AnalyticsType.Text, "Оплата (as typed)."),
            C("discounts", AnalyticsType.Text, "Discounts (знижки), comma-separated."), C("days", Integer, "Days taken part in."),
        ]),
        new("entries", "Person × day: what was set for the day (group, chip, start…).",
        [
            DayCol, ParticipantCol, C("group_id", AnalyticsType.Text, "Group id (null = «без групи»)."), GroupNameCol,
            C("chip", AnalyticsType.Text, "Чіп."), C("rental_chip", AnalyticsType.Boolean, "The chip is a rental one (орендний)."),
            C("start_time_s", Real, "Start time (from the draw / protocol), seconds since midnight."), C("start_time_text", AnalyticsType.Text, "HH:mm:ss."),
            C("out_of_competition", AnalyticsType.Boolean, "П/К (поза конкурсом): no place."),
            C("status_override", AnalyticsType.Text, "Status set by hand (null = computed)."), C("bonus", Integer, "Бонус ±«Бали»."),
            C("payment", AnalyticsType.Text, "Оплата of the day."), C("pays_raised_fee", AnalyticsType.Boolean, "Raised fee that day."),
            C("relay_team_id", AnalyticsType.Text, "Relay team id."), C("relay_team_number", Integer, "Relay team number."), C("leg", Integer, "Relay leg."),
        ]),
        new("results", "Person × day: the computed result, exactly as in the result protocol.",
        [
            DayCol, ParticipantCol, GroupNameCol,
            C("status", AnalyticsType.Text, "OK, MP (не відмічено КП), DNF, DNS, DSQ, OVT (перевищено контрольний час), none (no result yet)."),
            C("computed_status", AnalyticsType.Text, "Status before a manual override."), C("has_readout", AnalyticsType.Boolean, "The chip was read."),
            C("start_clock_s", Real, "Actual start, seconds since midnight."), C("finish_clock_s", Real, "Finish, seconds since midnight."),
            C("time_s", Real, "Result time, s (full precision)."), C("adjusted_time_s", Real, "Time after the discipline's adjustment (plugins), s."),
            C("time_text", AnalyticsType.Text, "Time as the protocol shows it."), C("place", Integer, "Місце (null: no place — status, П/К)."),
            C("behind_s", Real, "Gap to the winner of the group, s (time disciplines)."),
            C("out_of_competition", AnalyticsType.Boolean, "П/К."), C("score", Integer, "«Бали» (rogaine / score) after penalty and bonus — NOT «Очки»."),
            C("score_gross", Integer, "«Бали» before penalty."), C("score_penalty", Integer, "Penalty in «Бали»."), C("bonus", Integer, "Бонус."),
            C("points", Real, "«Очки» (ranking points by the points rule) — NOT «Бали»."), C("awarded_rank", AnalyticsType.Text, "Виконаний розряд."),
            C("course_variant", AnalyticsType.Text, "Scatter / relay course variant run."), C("missed", AnalyticsType.Text, "Missed controls, comma-separated."),
            C("unranked_reason", AnalyticsType.Text, "Why there is no place."), C("relay_team_id", AnalyticsType.Text, "Relay team."), C("leg", Integer, "Relay leg."),
            C("extra_json", AnalyticsType.Text, "Plugin / discipline extra columns as JSON (json_extract)."),
        ]),
        new("legs", "Person × day × leg as run (from the read-out). Leg statistics compare people of the same group (and course variant) on the same control pair.",
        [
            DayCol, ParticipantCol, GroupNameCol, C("course_variant", AnalyticsType.Text, "Variant (scatter / relay)."),
            C("leg_no", Integer, "1-based order in the passage."), C("from_code", AnalyticsType.Text, "From control (S = start)."),
            C("to_code", AnalyticsType.Text, "To control (F = finish)."), C("on_course", AnalyticsType.Boolean, "The punch counts for the course (false = an extra punch)."),
            C("leg_s", Real, "Leg time, s."), C("cumulative_s", Real, "Time from the start, s."), C("leg_km", Real, "Leg length, km."),
            C("pace_s_per_km", Real, "Pace, s/km."), C("leg_place", Integer, "Place on this leg among the comparable runners."),
            C("leg_best_s", Real, "Best time on this leg."), C("leg_median_s", Real, "Median time on this leg."),
            C("ratio_to_median", Real, "leg_s / leg_median_s (>> 1 = lost time, << 1 = suspiciously fast)."),
            C("behind_leg_best_s", Real, "leg_s − leg_best_s."), C("runners_on_leg", Integer, "How many comparable runners have this leg."),
        ]),
        new("punches", "Raw punches of every read-out (as stored).",
        [
            DayCol, C("readout_id", AnalyticsType.Text, "Read-out id."), ParticipantCol, C("chip", AnalyticsType.Text, "Чіп."), C("seq", Integer, "1-based."),
            C("code", AnalyticsType.Text, "Control code."), C("clock_s", Real, "Punch time, seconds since midnight."), C("clock_text", AnalyticsType.Text, "HH:mm:ss."),
        ]),
        new("readouts", "Finish read-outs (зчитка) of every day.",
        [
            DayCol, C("readout_id", AnalyticsType.Text, "Read-out id."), C("read_order", Integer, "Order of reading."), C("chip", AnalyticsType.Text, "Чіп."),
            C("participant_id", AnalyticsType.Text, "Who holds the chip that day (null = unknown chip)."),
            C("latest", AnalyticsType.Boolean, "The newest read-out of the chip (the one results use)."),
            C("start_clock_s", Real, "Start punch."), C("finish_clock_s", Real, "Finish punch."), C("punch_count", Integer, "Punches."),
            C("manual_status", AnalyticsType.Text, "Status set on the read-out by hand."), C("read_at", AnalyticsType.Text, "When it was read (ISO)."),
        ]),
        new("relay_teams", "Relay team × day with the team result.",
        [
            DayCol, C("team_id", AnalyticsType.Text, "Team id."), GroupNameCol, C("number", Integer, "Team number."), C("name", AnalyticsType.Text, "Team name."),
            C("club", AnalyticsType.Text, "Club."), C("region", AnalyticsType.Text, "Region."), C("status", AnalyticsType.Text, "Team status."), C("time_s", Real, "Team time, s."),
            C("place", Integer, "Team place."), C("out_of_competition", AnalyticsType.Boolean, "П/К."), C("points", Real, "«Очки» of the team."),
        ]),
        new("relay_legs", "Relay team × leg: who ran it and the leg result.",
        [
            DayCol, C("team_id", AnalyticsType.Text, "Team id."), C("team_number", Integer, "Team number."), C("leg", Integer, "Leg."),
            ParticipantCol, C("status", AnalyticsType.Text, "Leg status."), C("leg_time_s", Real, "Leg time, s."),
            C("cumulative_s", Real, "Team time after this leg, s."), C("restarted", AnalyticsType.Boolean, "Started at the restart."),
            C("course_code", AnalyticsType.Text, "Leg course variant."),
        ]),
        new("clubs", "Clubs.", [C("club_id", AnalyticsType.Text, "Id."), C("name", AnalyticsType.Text, "Name."), C("participants", Integer, "People.")]),
        new("regions", "Regions (області).", [C("region_id", AnalyticsType.Text, "Id."), C("name", AnalyticsType.Text, "Name."), C("participants", Integer, "People.")]),
        new("dussh", "ДЮСШ (sport schools).", [C("dussh_id", AnalyticsType.Text, "Id."), C("name", AnalyticsType.Text, "Name."), C("participants", Integer, "People.")]),
        new("rental_chips", "Орендні чіпи (rental chips) of the competition.", [C("number", AnalyticsType.Text, "Chip number."), C("note", AnalyticsType.Text, "Note.")]),
        new("custom_fields", "Additional participant fields (додаткові поля); values are participants.field_<key>.",
        [
            C("key", AnalyticsType.Text, "Key."), C("column", AnalyticsType.Text, "Column in participants."), C("label", AnalyticsType.Text, "Label shown in the program."),
            C("kind", AnalyticsType.Text, "text / number / bool / choice."), C("source", AnalyticsType.Text, "user or plugin:<id>."),
        ]),
        new("discounts", "Знижки на внесок (entry-fee discounts); a person's are participants.discounts.",
        [
            C("discount_id", AnalyticsType.Text, "Id."), C("name", AnalyticsType.Text, "Name."), C("percent", Real, "Discount, %."),
            C("applies_to_chip_rental", AnalyticsType.Boolean, "Also lowers the chip rental."), C("fsou_member", AnalyticsType.Boolean, "The ФСОУ-member discount."),
        ]),
        new("chip_prices", "Ціна оренди чіпа by chip note (overrides the competition's chip_rental_price_per_day).",
        [C("note", AnalyticsType.Text, "Rental chip note it applies to."), C("price_per_day", Real, "Price per day.")]),
        new("points_rules", "Rules of «Очки» the competition can use.",
        [C("rule_id", AnalyticsType.Text, "Id."), C("name", AnalyticsType.Text, "Name."), C("from_competition", AnalyticsType.Boolean, "Kept only in the competition's own copy.")]),
    ];

    private static readonly Dictionary<string, AnalyticsTableInfo> ByName =
        Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public static AnalyticsTableInfo? Find(string name) => ByName.GetValueOrDefault(name);

    public static IReadOnlyList<string> Names { get; } = Tables.Select(t => t.Name).ToList();

    /// <summary>The column name of an additional field: field_ + its key in [a-z0-9_].</summary>
    public static string FieldColumn(string key)
    {
        var chars = (key ?? string.Empty).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        return "field_" + new string(chars).Trim('_');
    }
}
