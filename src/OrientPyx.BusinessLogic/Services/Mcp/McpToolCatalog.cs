using System.Text.Json.Nodes;
using static OrientPyx.BusinessLogic.Services.Mcp.McpSchema;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>
/// The static description of one tool: what the assistant sees in <c>tools/list</c> and what the policy needs.
/// <paramref name="Undoable"/> = a successful call is one Ctrl+Z / <c>undo</c> step as a whole.
/// </summary>
public sealed record McpToolInfo(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    McpToolClass Class,
    bool Undoable = false,
    bool Destructive = false,
    bool Idempotent = false)
{
    public bool ReadOnly => Class == McpToolClass.Read;
}

/// <summary>
/// Every tool of the assistant (plan §6): names and descriptions in English (more reliable for tool choice), with the
/// Ukrainian terms in brackets. The bridge lists them from here even while OrientPyx is closed, so this file depends on
/// nothing but the schema builder.
/// </summary>
public static class McpToolCatalog
{
    private const string Days = "Day numbers (1-based). Default: every day.";

    private static readonly string[] ReportKinds =
        ["readiness", "dashboard", "map_counts", "summary", "finish_log", "relay_teams", "number_variants", "entry_fees", "start_list"];

    /// <summary>The settings a competition stores (get_settings / save_settings).</summary>
    public static readonly string[] SettingsKinds =
    [
        "result_protocol", "start_protocol", "judges_start_protocol", "summary_protocol", "draw_lanes", "draw_classic",
        "monitor", "online_publish", "statement", "iof_export", "chip_scan", "discipline", "plugin_report",
    ];

    public static readonly string[] ImportKinds = ["iof_courses", "iof_entries", "participants_table", "uof", "rental_chips", "readouts"];

    public static readonly string[] ExportKinds =
    [
        "result_protocol", "start_protocol", "judges_start_protocol", "summary_protocol", "splits_html", "iof_results",
        "iof_start_list", "statement", "participants_csv", "participants_xlsx", "plugin_report",
    ];

    public static readonly string[] ShowPages =
    [
        "participants", "roster", "groups", "controls", "days", "info", "finish", "results", "protocols", "start_protocols",
        "draw", "mass_start", "relay_teams", "readiness", "dashboard", "chips", "clubs", "regions",
    ];

    private static readonly JsonObject ParticipantFields = Map(
        "Fields to set. Person: full_name, bib (номер), birth_date (YYYY-MM-DD), club, region, dussh (ДЮСШ), rank (розряд), coach, " +
        "representative, team, fsou_code, fsou_member (bool), note, checked_in (мандатку пройдено, bool), pays_raised_fee (bool), " +
        "field.<key> (additional field, see describe_data), discount.<name> (bool). Day (needs 'day'): group, chip, start_time " +
        "(HH:mm:ss, null = none), out_of_competition (П/К, bool), status (OK/MP/DNF/DNS/DSQ/OVT, null = computed), bonus (int, ±«Бали»), " +
        "payment, raised_fee (bool), in_day (bool: add to / take off the day; off the only day = deleted). Club/region/dussh by name; null clears.");

    public static IReadOnlyList<McpToolInfo> All { get; } =
    [
        // ── §6.1 Context and search
        new("get_context", "Context",
            "Start here. The competition open in the OrientPyx window (identifier, name, dates), its days (number, date, " +
            "discipline (вид), locked = closed for edits), the day selected in the window, the network role " +
            "(local/host/station), the assistant mode (read_only / confirm_changes / auto_changes / no_confirmation) with what waits for " +
            "«Дозволити», and the app version.",
            Obj(), McpToolClass.Read, Idempotent: true),
        new("list_competitions", "Competitions",
            "All competitions on this computer (or on the host when this one is a station): identifier, name, dates, days. " +
            "Only the one open in the window can be read or changed; open_competition switches.",
            Obj(Opt("include_hidden", Bool("Also hidden competitions."))), McpToolClass.Read, Idempotent: true),
        new("find", "Find",
            "Fuzzy search (wrong keyboard layout and Latin/Cyrillic look-alikes tolerated) for participants (by name, bib, chip), " +
            "groups, clubs, regions, ДЮСШ, controls (КП), rental chips and relay teams (by name or number; a relay leg number like 12.2 " +
            "finds the runner). Returns ids and a short card. Use the ids in later calls; " +
            "never guess between several people with the same name — ask the user.",
            Obj(Req("text", Str("What to look for.")),
                Opt("kinds", Arr(Enum("", "participant", "group", "club", "region", "dussh", "control", "chip", "team"), "Default: all.")),
                Day, Opt("limit", Int("Max results per kind (default 20)."))),
            McpToolClass.Read, Idempotent: true),
        new("get_participant", "Participant card",
            "Everything about one person: identity, every day (group, chip, start, П/К, status, result, place, «Бали», «Очки»), " +
            "entry fee, additional fields and, per day, the splits of the read-out.",
            Obj(Req("participant", Str("Participant id, bib or exact full name.")), Day,
                Opt("include_splits", Bool("Include splits (default true)."))),
            McpToolClass.Read, Idempotent: true),
        new("find_duplicates", "Find duplicate people",
            "Pairs of entries that may be one person: the same name (case, spaces, Latin/Cyrillic look-alikes ignored), the name in " +
            "another order, a typo of a letter or two, the same ФСОУ code. Each pair with both cards, the days both run and " +
            "likely_namesakes when the birth years differ. Show them to the user and ask which are one person before merge_participants.",
            Obj(Opt("day", Int("Only pairs with someone on this day.")), Opt("limit", Int("Max pairs (default 50)."))),
            McpToolClass.Read, Idempotent: true),
        new("describe_data", "Data schema",
            "The tables available to query (columns, types, meaning, Ukrainian terms). Read it before writing SQL.",
            Obj(Opt("table", Str("One table; default all."))), McpToolClass.Read, Idempotent: true),
        new("get_help", "Program help",
            "OrientPyx's own help texts («?» buttons) by topic — to explain to the user how something is done in the program. " +
            "Without a topic: the list of topics.",
            Obj(Opt("topic", Str("Help key or a word from it, e.g. 'Draw', 'Relay', 'Mcp'."))), McpToolClass.Read, Idempotent: true),

        // ── §6.2 Analytics and reports
        new("query", "SQL query",
            "The main analytics tool: one read-only SQLite SELECT (or WITH … SELECT) over the competition's tables " +
            "(describe_data). The numbers are exactly those of the protocols. Aggregate in SQL instead of fetching raw rows; " +
            "at most 500 rows come back. Extra functions: median(x), percentile(x, p). Errors come back as SQLite text — fix and retry.",
            Obj(Req("sql", Str("The SELECT statement.")), Opt("days", IntArr(Days))),
            McpToolClass.Read, Idempotent: true),
        new("get_results", "Results",
            "Day results as in the result protocol: per group the places, times (seconds + text), gaps, statuses, «Бали», «Очки», " +
            "relay teams and the discipline's extra columns.",
            Obj(Day, Opt("groups", StrArr("Group names; default all.")), Opt("limit_per_group", Int("Rows per group (default all)."))),
            McpToolClass.Read, Idempotent: true),
        new("get_splits", "Splits",
            "One participant's passage of a day as the discipline sees it (set course, scatter variant, mixed, rogaine points, relay leg): " +
            "per control the leg time, elapsed time, whether it counted.",
            Obj(Req("participant", Str("Participant id, bib or exact full name.")), Day), McpToolClass.Read, Idempotent: true),
        new("get_report", "Report",
            "A ready report: readiness (перевірка готовності дня), dashboard (панель), map_counts (кількість карт), summary " +
            "(сума днів), finish_log (журнал зчитки), relay_teams, number_variants (варіанти за номерами), entry_fees (внески), " +
            "start_list (стартовий протокол).",
            Obj(Req("kind", Enum("Which report.", ReportKinds)), Day), McpToolClass.Read, Idempotent: true),
        new("get_settings", "Settings",
            "Stored settings of the competition as JSON (the same that save_settings patches): protocols, draw, monitor, online " +
            "publishing (no keys), statement, IOF export, chip scan, a discipline's settings, a plugin report's settings.",
            Obj(Req("kind", Enum("Which settings.", SettingsKinds)), Day,
                Opt("discipline", Str("For kind=discipline: discipline id; default the day's.")),
                Opt("report", Str("For kind=plugin_report: report id."))),
            McpToolClass.Read, Idempotent: true),
        new("show_in_app", "Show in OrientPyx",
            "Opens a page of the OrientPyx window (and a day / a participant's row) so the user sees it — «покажи мені його».",
            Obj(Req("page", Enum("The page.", ShowPages)), Day, Opt("participant", Str("Participant id or bib to scroll to."))),
            McpToolClass.Read),
        new("get_background_status", "Background processes",
            "What runs in the OrientPyx window in the background (the top-bar «Процеси»: finish auto-read (автозчитка), results monitor, " +
            "online results, the assistant) with each one's status line and whether it is failing, plus the finish desk of the day: " +
            "read-outs, runners read, on course, unknown chips, the last read-out. Read only — starting / stopping is done by the user.",
            Obj(Day), McpToolClass.Read, Idempotent: true),
        new("inspect_file", "Look into a file",
            "Before import_file: what a local file the user named is — IOF courses / entry list, УОФ, a SPORTident or Sport Time read-out " +
            "file (and whether it matches «Тип відмітки»), a participants table (CSV/XLSX: columns, first rows, the column mapping the " +
            "import would use) — with its size in records. Reads only.",
            Obj(Req("path", Str("Full local path of the file.")), Day), McpToolClass.Read, Idempotent: true),

        // ── §6.3 Changes (undo: Ctrl+Z / undo)
        new("add_participants", "Add participants",
            "Adds people to the competition and to a day (with group, chip, start). Clubs/regions/ДЮСШ by name; unknown ones are " +
            "created when create_missing=true, otherwise the call is refused. One call = one undo step.",
            Obj(Competition, Day,
                Req("participants", Arr(Obj(Req("full_name", Str("ПІБ."))), "People; besides full_name every field of update_participants.")),
                Opt("days", IntArr("Days to add them to (default: 'day'). Empty array = the roster only (Мандатка).")),
                Opt("create_missing", Bool("Create unknown clubs/regions/ДЮСШ."))),
            McpToolClass.Write, Undoable: true),
        new("update_participants", "Change participants",
            "A batch of changes: each item names a participant (id, bib or exact name) and the fields to set. A bib taken by " +
            "another person is refused; a chip held by another person follows on_chip_conflict. Results of the day are recomputed. " +
            "One call = one undo step; more than 50 rows is a risky change. Taking someone off their only day (in_day=false) deletes " +
            "them from the competition and counts as a deletion.",
            Obj(Competition,
                Req("changes", Arr(Obj(Req("participant", Str("Id, bib or exact full name.")),
                    Opt("day", Int("Day for day fields; default the window's day.")), Req("fields", ParticipantFields)), "Changes.")),
                Opt("on_chip_conflict", Enum("Chip held by someone else: fail (default), reassign (take it), swap.", "fail", "reassign", "swap")),
                Opt("create_missing", Bool("Create unknown clubs/regions/ДЮСШ."))),
            McpToolClass.Write, Undoable: true),
        new("remove_participants", "Remove participants",
            "Takes people off a day (mode=from_day) or deletes them from the competition with every day (mode=delete). Taking someone " +
            "off their only day deletes them too (the answer says so). More than 5 deletions is a risky change. Undoable.",
            Obj(Competition, Day, Req("participants", StrArr("Ids, bibs or exact names.")),
                Req("mode", Enum("from_day or delete.", "from_day", "delete"))),
            McpToolClass.Write, Undoable: true, Destructive: true),
        new("merge_participants", "Merge duplicates",
            "Merges two entries of one person (only after the user said they are one): 'merge' is deleted, the days only it runs move to " +
            "'keep' with group, chip, start, status and relay leg; on a day both run the kept entry stays and the merged one's is dropped; " +
            "the kept one's blank fields (bib, birth date, club …), additional fields and discounts are filled from it — nothing it has " +
            "is overwritten. More than 5 merges is a risky change. One undo step.",
            Obj(Competition, Req("merges", Arr(Obj(Req("keep", Str("The entry that stays (id).")), Req("merge", Str("The entry merged into it and deleted (id)."))),
                "Pairs {keep, merge}; each person in one pair only."))),
            McpToolClass.Write, Undoable: true, Destructive: true),
        new("upsert_groups", "Groups",
            "Adds groups to a day or changes them: course (порядок КП, e.g. '31 45 33' or a pattern for mixed), distance, discipline " +
            "override, time limit, max late (макс. запізнення), penalty per minute, required control count, age window (birth years), " +
            "points rule, rank level (розряди), course setter, entry fee. Undoable.",
            Obj(Competition, Day,
                Req("groups", Arr(Obj(Req("name", Str("Group name (an existing one is changed)."))),
                    "Groups. Fields: name, new_name, course, distance_km, discipline, time_limit_min, max_late_min, penalty_per_min, " +
                    "required_controls, min_birth_year, max_birth_year, points_rule (name or id; null clears), rank_level (none/adult/junior), " +
                    "master_count, course_setter, course_setter_category, entry_fee (null clears). Null clears a value."))),
            McpToolClass.Write, Undoable: true),
        new("remove_groups", "Remove groups",
            "Takes groups off a day (their participants stay, without group). Undoable.",
            Obj(Competition, Day, Req("groups", StrArr("Group names."))), McpToolClass.Write, Undoable: true, Destructive: true),
        new("upsert_controls", "Controls",
            "Adds controls (КП) of a day or changes them: code, type (control/start/finish), points (бали КП, rogaine), coordinates " +
            "(lat/lon) and map position (map_x/map_y, mm). Undoable.",
            Obj(Competition, Day,
                Req("controls", Arr(Obj(Req("code", Str("Control code (an existing one is changed)."))),
                    "Controls. Fields: code, new_code, type, points, lat, lon, map_x, map_y."))),
            McpToolClass.Write, Undoable: true),
        new("delete_controls", "Delete controls",
            "Deletes controls of a day. Undoable.",
            Obj(Competition, Day, Req("codes", StrArr("Control codes."))), McpToolClass.Write, Undoable: true, Destructive: true),
        new("set_problematic_controls", "Problematic controls",
            "Sets the day's «Проблемні КП» (cancelled controls: not punching them is no MP) — the complete list; empty = none. " +
            "Results are recomputed. Undoable.",
            Obj(Competition, Day, Req("codes", StrArr("Every problematic control code of the day."))),
            McpToolClass.Write, Undoable: true, Idempotent: true),
        new("edit_readout", "Edit a read-out",
            "What the finish page's read-out window does, for one read-out of the day (id from get_report finish_log, or a chip number = " +
            "its latest read): give an unknown chip («Невідомий чіп») to a participant of the day (participant) or to a new one " +
            "(new_participant {full_name, group, bib, birth_date, club, region, dussh, rank, coach, create_missing}); set its status by hand " +
            "(OK/MP/DNF/DNS/DSQ/OVT, null = computed); correct its chip number or start / finish clock time; move the holder to another " +
            "group; set their «Бонус». A chip held by someone else is taken only with take_chip=true (ask the user first). Results are " +
            "recomputed. One undo step.",
            Obj(Competition, Day, Req("readout", Str("Read-out id or chip number.")),
                Opt("participant", Str("Participant (id, bib, exact name) who gets the chip.")),
                Opt("new_participant", Obj(Req("full_name", Str("ПІБ.")), Req("group", Str("Group of the day.")))),
                Opt("take_chip", Bool("The chip is held by someone else: take it from them.")),
                Opt("status", Str("OK, MP, DNF, DNS, DSQ, OVT or null (computed).")),
                Opt("group", Str("Group of the day for the holder.")), Opt("bonus", Int("«Бонус» of the holder (±«Бали»), null clears.")),
                Opt("chip", Str("Corrected chip number of the read-out.")),
                Opt("start_time", Str("HH:mm:ss, null clears.")), Opt("finish_time", Str("HH:mm:ss, null clears."))),
            McpToolClass.Write, Undoable: true),
        new("manage_directory", "Directories",
            "Adds, renames or deletes items of a competition directory: club, region, dussh (ДЮСШ), rental_chip (орендні чіпи: " +
            "number, note), discount (знижки: name, percent, applies_to_chip_rental), chip_price (ціна чіпа: note, price_per_day), " +
            "custom_field (додаткові поля: label, kind text/number/bool/choice, options). Undoable.",
            Obj(Competition,
                Req("kind", Enum("Directory.", "club", "region", "dussh", "rental_chip", "discount", "chip_price", "custom_field")),
                Req("action", Enum("add, update or delete.", "add", "update", "delete")),
                Req("items", Arr(Map("Item: name / number / label (identifies it), new_name / new_number / new_label, other fields."), "Items."))),
            McpToolClass.Write, Undoable: true),
        new("update_competition", "Competition info",
            "Changes «Інформація»: name, venue, organisation, start_date, end_date, officials (course_setter, chief_judge, " +
            "chief_secretary, jury and their *_category), raised_fee_enabled, raised_fee_amount, chip_rental_price_per_day, " +
            "roster_enabled (Мандатка), payment_per_day, default_points_rule. Not undoable — confirmed by the user in OrientPyx (except in mode no_confirmation).",
            Obj(Competition, Req("fields", Map("Fields to set."))), McpToolClass.Risky),
        new("manage_days", "Days",
            "add: a new day; update: date (YYYY-MM-DD) / venue of a day; set_discipline: the day's discipline (вид змагань: " +
            "SetCourse, ScoreByCount, ScoreByTime, Rogaine, Mixed, Scatter, Relay or a plugin id); lock: close the day for edits. " +
            "Opening a closed day and deleting a day are done only in the program. Confirmed by the user in OrientPyx (except in mode no_confirmation).",
            Obj(Competition, Req("action", Enum("What to do.", "add", "update", "set_discipline", "lock")), Day,
                Opt("date", Str("YYYY-MM-DD; null clears.")), Opt("venue", Str("Venue of the day.")),
                Opt("discipline", Str("Discipline id."))),
            McpToolClass.Risky),
        new("manage_relay", "Relay",
            "Relay (естафета) of a day: create_teams (group, count, first_number), update_team (team number or id; new number, name, " +
            "club, region), delete_team (team; its runners stay on the day without a team), place_runner (participant → team + leg), " +
            "release_runner (participant off their team; stays on the day), group_settings (group: leg_count, course_mode " +
            "group/by_bib/team_set/per_leg, restart ranking…), leg_courses (entries: team, leg, course variant). Undoable.",
            Obj(Competition, Day,
                Req("action", Enum("What to do.", "create_teams", "update_team", "delete_team", "place_runner", "release_runner", "group_settings", "leg_courses")),
                Opt("group", Str("Group name.")), Opt("count", Int("Teams to create.")), Opt("first_number", Int("First team number.")),
                Opt("team", Str("Team number or id.")), Opt("number", Int("New team number.")), Opt("name", Str("Team name.")),
                Opt("club", Str("Club name.")), Opt("region", Str("Region name.")),
                Opt("participant", Str("Participant id, bib or exact name.")), Opt("leg", Int("Leg (1-based).")),
                Opt("replace", Bool("For place_runner: the leg already has a runner — put this one there instead (the other stays on the day without a leg).")),
                Opt("settings", Map("For group_settings: fields of the group's relay settings (see get_report relay_teams).")),
                Opt("entries", Arr(Map("{team, leg, variant}"), "For leg_courses."))),
            McpToolClass.Write, Undoable: true),
        new("save_settings", "Save settings",
            "Applies a JSON merge patch to stored settings (as get_settings returns them): protocols, draw, monitor, online publishing, " +
            "statement, IOF export, chip scan, discipline, plugin report. Not undoable — confirmed by the user in OrientPyx (except in mode no_confirmation).",
            Obj(Competition, Req("kind", Enum("Which settings.", SettingsKinds)), Day,
                Req("patch", Map("JSON merge patch: keys to set, null to clear.")),
                Opt("discipline", Str("For kind=discipline.")), Opt("report", Str("For kind=plugin_report."))),
            McpToolClass.Risky),
        new("undo", "Undo",
            "Undoes one of your own earlier changes (the last one, or step_id from its answer) — the same as Ctrl+Z in the program. " +
            "If someone changed the same data since, nothing is undone and the conflict is described.",
            Obj(Opt("step_id", Str("step_id of the change; default your last undoable change."))),
            McpToolClass.Write),
        new("redo", "Redo",
            "Puts back one of your own changes you undid (the last undone one, or step_id) — the same as Ctrl+Y. If someone changed the " +
            "same data since, nothing is changed and the conflict is described.",
            Obj(Opt("step_id", Str("step_id of the undone change; default the last one you undid."))),
            McpToolClass.Write),
        new("list_changes", "Your changes",
            "Your own changes on the open competition since OrientPyx started, newest first: when, tool, day, what, step_id, done / " +
            "undone / redone, whether undo or redo can take it.",
            Obj(Opt("limit", Int("Max entries (default 30)."))), McpToolClass.Read, Idempotent: true),

        // ── §6.4 Operations (mostly not undoable → preview + apply_plan)
        new("import_file", "Import a file",
            "Imports a file the user named (local path): iof_courses (КП + дистанції, IOF XML), iof_entries (IOF entry list), " +
            "participants_table (CSV/XLSX; mapping = column header → field), uof (УОФ XML), rental_chips (read-out file of rental chips), " +
            "readouts (finish read-out file). preview=true shows added / updated / missing / duplicates and gives a plan_id. " +
            "Not undoable — applying is confirmed by the user (except in mode no_confirmation).",
            Obj(Competition, Req("kind", Enum("File kind.", ImportKinds)), Req("path", Str("Full local path of the file.")), Day,
                Opt("scope", Enum("Participants: all_days (default) or current_day.", "all_days", "current_day")),
                Opt("link_field", Enum("current_day scope: how a row finds a known person.", "fsou_code", "full_name")),
                Opt("mapping", Map("participants_table: column header → field (full_name, bib, birth_date, club, region, group, chip, …). " +
                    "Default: the mapping saved for these headers or matched by name.")),
                Opt("replace_existing", Bool("iof_courses: replace the day's controls; groups: update existing.")),
                Opt("clear_first", Bool("Participants: delete everyone first (never by default).")),
                Opt("merge_duplicates", Bool("Participants: a row that looks like exactly one existing person (same name, birth year, ФСОУ code) updates that person (default true).")),
                Opt("note", Str("rental_chips: note for the chips.")),
                Opt("format", Enum("rental_chips / readouts: the timing system of the file; default the one chosen in «Налаштування» («Тип відмітки»).", "sportident", "sporttime")),
                Preview),
            McpToolClass.Risky),
        new("draw_start_times", "Start draw",
            "Жеребкування of a day: method lanes (коридори; interval, first start, separation by club/region/team, settings as " +
            "get_settings draw_lanes) or classic. preview=true gives the start list and a plan_id. Not undoable — applying is confirmed (except in mode no_confirmation).",
            Obj(Competition, Day, Opt("method", Enum("lanes (default) or classic.", "lanes", "classic")),
                Opt("groups", StrArr("Groups to draw; default all.")),
                Opt("settings", Map("Overrides of the stored draw settings (get_settings draw_lanes / draw_classic).")), Preview),
            McpToolClass.Risky),
        new("mass_start", "Mass start",
            "Масовий старт: one start time per group (relay: leg 1 and restart). preview=true gives a plan_id. Not undoable.",
            Obj(Competition, Day,
                Req("groups", Arr(Obj(Req("group", Str("Group name."))), "{group, start_time (HH:mm:ss), restart_time?}")), Preview),
            McpToolClass.Risky),
        new("set_start_order", "Start order",
            "Sets a group's start order by hand: the listed people get the group's start minutes in this order. preview gives a plan_id.",
            Obj(Competition, Day, Req("group", Str("Group name.")), Req("order", StrArr("Participants (id, bib, name) in start order.")), Preview),
            McpToolClass.Risky),
        new("assign_numbers", "Assign bibs",
            "Gives sequential bibs (номери) from first_number to the day's participants (optionally one group, only those without a bib), " +
            "in the order of the day's table. preview gives a plan_id.",
            Obj(Competition, Day, Req("first_number", Int("First bib.")), Opt("groups", StrArr("Groups; default all.")),
                Opt("only_missing", Bool("Only people without a bib (default true).")), Preview),
            McpToolClass.Risky),
        new("assign_rental_chips", "Assign rental chips",
            "Gives unused rental chips (орендні чіпи) to the day's participants without a chip (optionally some groups). preview gives a plan_id.",
            Obj(Competition, Day, Opt("groups", StrArr("Groups; default all.")), Preview), McpToolClass.Risky),
        new("assign_groups", "Groups by rules",
            "Puts the day's participants into groups by rules (by default only those without a group): each rule {group, birth_year_min, " +
            "birth_year_max (default: the group's own age window; use_age_window=false for none), where {club, region, dussh, team, " +
            "current_group, field.<key> (e.g. field.gender) : value or [values]; \"\" = empty}}; the first rule that fits a person wins. " +
            "preview gives the counts per group, who fits no rule and a plan_id; applied as one undo step.",
            Obj(Competition, Day, Req("rules", Arr(Obj(Req("group", Str("Group of the day."))), "Rules in order.")),
                Opt("only_without_group", Bool("Only people without a group on the day (default true).")), Preview),
            McpToolClass.Risky, Undoable: true),
        new("copy_participants", "Copy between days",
            "Copies participants (group, chip, optionally start) from one day to another. preview gives a plan_id. Not undoable.",
            Obj(Competition, Req("from_day", Int("Source day.")), Req("to_day", Int("Target day.")),
                Opt("chips", Bool("Copy chips (default true).")),
                Opt("start_times", Bool("Copy start times (default false).")), Preview),
            McpToolClass.Risky),
        new("export", "Export",
            "Writes a document with the program's own settings for it: result/start/judges' start protocol, summary protocol (сума днів) " +
            "— .docx; splits — .html; IOF XML results / start list; statement (відомість) .docx; participants .csv/.xlsx; a switched-on " +
            "plugin's report (plugin_report + report id) .docx with the report's saved settings. Goes to the " +
            "day's exports folder of this computer unless 'folder' is given (then confirmed by the user, except in mode no_confirmation). Returns the file path.",
            Obj(Competition, Req("kind", Enum("Document.", ExportKinds)), Day,
                Opt("report", Str("For kind=plugin_report: the report id (without it the answer lists the reports).")),
                Opt("folder", Str("Target folder (default: the competition's exports folder).")),
                Opt("file_name", Str("File name (default: the program's own).")), Opt("overwrite", Bool("Replace an existing file."))),
            McpToolClass.File),
        new("create_competition", "New competition",
            "Creates a competition (it is not opened; use open_competition). Confirmed by the user in OrientPyx (except in mode no_confirmation).",
            Obj(Req("name", Str("Name.")), Req("identifier", Str("Folder identifier: letters, digits, '-', '_'.")),
                Opt("venue", Str("Venue.")), Opt("days", Int("Day count (default 1).")), Opt("start_date", Str("YYYY-MM-DD.")),
                Opt("end_date", Str("YYYY-MM-DD.")), Opt("officials", Map("course_setter, chief_judge, chief_secretary, jury."))),
            McpToolClass.Risky),
        new("open_competition", "Open competition",
            "Opens another competition (and day) in the OrientPyx window. Confirmed by the user in OrientPyx (except in mode no_confirmation).",
            Obj(Req("identifier", Str("Competition identifier.")), Day), McpToolClass.Risky),
        new("apply_plan", "Apply a plan",
            "Applies what a preview showed (plan_id from import_file, draw_start_times, mass_start, set_start_order, assign_numbers, " +
            "assign_rental_chips, assign_groups, copy_participants). The user confirms it in OrientPyx (except in mode no_confirmation). If the data changed since and the plan would " +
            "differ, nothing happens and a new preview comes back.",
            Obj(Req("plan_id", Str("plan_id from the preview."))), McpToolClass.Risky),
        new("await_confirmation", "Wait for confirmation",
            "When a change answered status=pending (the user has not clicked «Дозволити» in OrientPyx yet): waits again and returns " +
            "the outcome. Tell the user to look at the OrientPyx window.",
            Obj(Req("confirmation_id", Str("confirmation_id from the pending answer."))), McpToolClass.Write, Idempotent: true),
    ];

    private static readonly Dictionary<string, McpToolInfo> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static McpToolInfo? Find(string name) => ByName.GetValueOrDefault(name);
}
