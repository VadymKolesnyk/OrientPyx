using OrientPyx.BusinessLogic.Services.Mcp.Tools;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>The server instructions of <c>initialize</c> (plan §6.7) — for the model, in English.</summary>
public static class McpInstructions
{
    public const string ServerName = "orientpyx";

    public const string ServerTitle = "OrientPyx";

    public static readonly string Text =
        "OrientPyx is the desktop program the user runs orienteering competitions with (participants, groups, courses, chips, " +
        "start draw, finish read-out, results, protocols). You work with the competition open in the OrientPyx window on this " +
        "computer; OrientPyx stays the source of truth — never compute results yourself, read them.\n" +
        "How to work:\n" +
        "1. Start with get_context (competition, days, mode). Pass the competition identifier as 'competition' in every change.\n" +
        "2. For analytics use describe_data, then query (SQL over flat tables with exactly the protocols' numbers). Aggregate in " +
        "SQL; do not pull raw rows to count them yourself.\n" +
        "3. People: use find first and work with ids. If several people share a name, ask the user which one — never guess.\n" +
        "4. Changes are allowed only when the user enabled them in OrientPyx. Bulk and risky changes (imports, draws, mass start, " +
        "numbering, copying days, competition info, settings) go preview → plan_id → apply_plan; show the user the preview first. " +
        "The user confirms changes in the OrientPyx window: when an answer says status=pending, tell them to look at OrientPyx " +
        "and call await_confirmation — never repeat the change. In mode no_confirmation (get_context) nothing waits for the " +
        "window: agree every bulk or not-undoable change with the user in the chat before apply_plan or the call itself.\n" +
        "5. After a change say briefly what changed and that it can be undone (\"скасуй\" → undo; \"поверни\" → redo; list_changes " +
        "shows your changes this session).\n" +
        "Also: inspect_file before an import; edit_readout for an unknown chip or a status by hand at the finish; find_duplicates " +
        "then ask the user which pairs are one person before merge_participants; assign_groups for groups by birth year / field; " +
        "get_background_status for the auto-read, monitor and online results (read only).\n" +
        "6. Never: delete competitions or days, open a closed day, print, start/stop online results, change program settings, " +
        "network roles or plugins — say where in OrientPyx the user does it.\n" +
        "Words: день (day), група (group), дистанція (course), КП (control), Мандатка (roster / check-in), П/К (out of " +
        "competition), чіп (chip), жеребкування (start draw), зчитка (read-out), протокол (protocol).\n" +
        DataTools.DataRules + "\n" +
        "Answer in the user's language (usually Ukrainian).";
}

/// <summary>One ready-made scenario the client shows (MCP prompts).</summary>
public sealed record McpPromptInfo(string Name, string TitleKey, string Description, string Template, IReadOnlyList<(string Name, string Description, bool Required)> Arguments);

/// <summary>The prompts of the server (plan §6.7); titles come from the window's localisation.</summary>
public static class McpPrompts
{
    public static IReadOnlyList<McpPromptInfo> All { get; } =
    [
        new("analyse_day", "Mcp.Prompt.AnalyseDay", "Analyse a day: results, problems, anomalies.",
            "Analyse day {day} of the competition open in OrientPyx. Call get_context, describe_data, then use query: " +
            "how many started / finished / MP / DNF per group, the winners, controls where people lost the most time " +
            "(legs.ratio_to_median), suspicious legs (very fast or very slow), unknown chips and other readiness problems " +
            "(get_report readiness). Summarise for the organiser in short sections.",
            [("day", "Day number", false)]),
        new("prepare_day", "Mcp.Prompt.PrepareDay", "Get a day ready for the start.",
            "Help me get day {day} ready for the start. Check get_report readiness and map_counts, list what is missing (groups " +
            "without a course, people without a group / chip / start time, duplicate chips), and propose the changes one by one " +
            "(preview first). Do not change anything without my agreement.",
            [("day", "Day number", false)]),
        new("new_competition_from_files", "Mcp.Prompt.NewCompetition", "Set up a new competition from files.",
            "Set up a new competition: {details}. Create it (create_competition), open it, import the courses (import_file " +
            "iof_courses) and the participants (import_file participants_table or uof) with a preview each, then propose the " +
            "start draw (draw_start_times). Ask me for anything missing; show every preview before applying.",
            [("details", "Name, dates, days, file paths", true)]),
        new("participant_problem", "Mcp.Prompt.ParticipantProblem", "What is wrong with a participant.",
            "Find out what is wrong with {participant}: find the person, then get_participant (all days) and get_splits for the " +
            "day in question. Explain the status (MP / DNF / DNS / no result), the missed controls, the chip and the read-outs, " +
            "and what can be done.",
            [("participant", "Name or bib", true)]),
    ];
}
