using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>What a tool call does to the competition, which decides whether it is listed and confirmed.</summary>
public enum McpToolClass
{
    /// <summary>Reads only (also «show in the window»): listed in every mode, never confirmed.</summary>
    Read,

    /// <summary>Writes only files (an export into the competition folder): listed when changes are on, not confirmed.</summary>
    File,

    /// <summary>A change journalled whole for undo: confirmed in «Зміни з підтвердженням», direct in the two modes after it.</summary>
    Write,

    /// <summary>Not undoable, or touches a lot: confirmed in OrientPyx in every mode but «Без підтвердження».</summary>
    Risky,
}

/// <summary>The decision for one call.</summary>
public enum McpVerdict
{
    Allow,
    Confirm,
    Deny,
}

/// <summary>
/// The rules of the assistant's access (plan §7): which tools are listed in a mode, when a call waits for «Дозволити»,
/// the thresholds of a "risky" bulk change and the list of things done only in the program.
/// </summary>
public static class McpToolPolicy
{
    /// <summary>More deletions than this in one call are risky (§14, п. 2).</summary>
    public const int RiskyDeletes = 5;

    /// <summary>More updated rows than this in one call are risky (§14, п. 2).</summary>
    public const int RiskyUpdates = 50;

    /// <summary>Whether a tool of <paramref name="cls"/> is listed (and may be called) in <paramref name="mode"/>.</summary>
    public static bool IsListed(McpToolClass cls, McpMode mode) =>
        cls == McpToolClass.Read || mode != McpMode.ReadOnly;

    /// <summary>The verdict for a call of <paramref name="cls"/>; <paramref name="allowedForSession"/> = the user ticked
    /// «Дозволяти такі зміни до кінця сесії» for this tool.</summary>
    public static McpVerdict Decide(McpToolClass cls, McpMode mode, bool allowedForSession = false)
    {
        if (!IsListed(cls, mode))
            return McpVerdict.Deny;
        return cls switch
        {
            McpToolClass.Read or McpToolClass.File => McpVerdict.Allow,
            _ when mode == McpMode.NoConfirmation => McpVerdict.Allow,
            McpToolClass.Write when mode == McpMode.AutoChanges || allowedForSession => McpVerdict.Allow,
            McpToolClass.Risky when allowedForSession => McpVerdict.Allow,
            _ => McpVerdict.Confirm,
        };
    }

    /// <summary>A write escalated by its size: more than <see cref="RiskyDeletes"/> deletions or <see cref="RiskyUpdates"/>
    /// updated rows is risky.</summary>
    public static McpToolClass BySize(McpToolClass cls, int deletions, int updates) =>
        cls == McpToolClass.Write && (deletions > RiskyDeletes || updates > RiskyUpdates) ? McpToolClass.Risky : cls;

    /// <summary>
    /// Things the assistant never does (plan §6.6): the request → where in the program the user does it (a localisation
    /// key of the window, and the English text the assistant gets). <c>PolicyTests</c> keeps it complete.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NeverViaMcp { get; } = new Dictionary<string, string>
    {
        ["delete_competition"] = "Deleting a competition is done only in OrientPyx: the competition list, the bin button.",
        ["rename_identifier"] = "Changing the competition identifier is done only in OrientPyx: «Змагання → Інформація».",
        ["delete_day"] = "Deleting a day is done only in OrientPyx: «Змагання → Дні».",
        ["delete_all_participants"] = "Deleting all participants is done only in OrientPyx: «Учасники → Дії → Видалити всіх».",
        ["clear_readouts"] = "Clearing the finish read-outs is done only in OrientPyx: the finish page.",
        ["unlock_day"] = "Opening a closed day for editing is done only in OrientPyx: «Змагання → Дні», the lock button.",
        ["print"] = "Printing is done only in OrientPyx (split slips, winners, statements).",
        ["online_publishing"] = "Starting or stopping the online results is done only in OrientPyx: «Протоколи → Онлайн-результати».",
        ["network_roles"] = "Host / station roles are set only in OrientPyx: «Файл → Спільна робота».",
        ["app_settings"] = "Program settings (paths, Supabase keys, language) are changed only in OrientPyx: «Налаштування».",
        ["points_and_ranks_tables"] = "The «Очки» and «Розряди» tables of the program are edited only in OrientPyx.",
        ["plugins"] = "Plugins are managed only in OrientPyx: «Плагіни → Керування плагінами».",
    };
}
