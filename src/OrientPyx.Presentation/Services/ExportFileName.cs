using System.Globalization;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Builds the file name suggested in every "save as" dialog. One shape for all exports:
/// <c>&lt;type&gt; - [&lt;qualifier&gt; - ]&lt;competition&gt; - [День N - ]&lt;yyyy-MM-dd&gt;.&lt;ext&gt;</c>.
/// The type comes first so that a folder sorted by name groups protocols of the same kind together
/// and the user can tell a start protocol from a results one at a glance.
/// The date is the competition day's own date (not "today"), so re-exporting the same day later
/// keeps producing the same name; it falls back to today only when the day has no date set.
/// </summary>
internal static class ExportFileName
{
    /// <summary>
    /// What joins the name's parts. A plain hyphen (not an em dash) — the name is typed, searched and pasted
    /// into other tools, where a hyphen is the safer character. <see cref="ExportFileSaver"/> also splits on
    /// it to find where the leading type part ends.
    /// </summary>
    public const string Separator = " - ";

    /// <summary>
    /// Composes the name. <paramref name="typeKey"/> is the localization key of the leading type part
    /// (e.g. <c>Protocols.NamePart</c>); <paramref name="day"/> adds a "День N" segment and supplies the
    /// date when given; <paramref name="date"/> overrides the date for day-less exports (e.g. the summary
    /// protocol, which uses the competition's last day). <paramref name="qualifier"/> — what this particular
    /// export is of (the statement's applied filters) — goes right after the type part; blank adds nothing.
    /// </summary>
    public static string Build(
        ILocalizationService localization,
        string typeKey,
        string? competitionName,
        string extension,
        EventDay? day = null,
        DateTimeOffset? date = null,
        string defaultNameKey = "Protocols.DefaultName",
        string? qualifier = null)
    {
        var competition = string.IsNullOrWhiteSpace(competitionName)
            ? localization.Get(defaultNameKey)
            : competitionName;

        var parts = new List<string>(5) { localization.Get(typeKey) };
        if (!string.IsNullOrWhiteSpace(qualifier))
            parts.Add(qualifier.Trim());
        parts.Add(competition);

        if (day is not null)
            parts.Add($"{localization.Get("Header.Day")} {day.Number}");

        parts.Add(Stamp(day?.Date ?? date));

        return $"{Sanitize(string.Join(Separator, parts))}.{extension}";
    }

    /// <summary>The date segment: the day's own date when known, otherwise today.</summary>
    private static string Stamp(DateTimeOffset? date) =>
        (date ?? DateTimeOffset.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Turns a heading-style text («Регіон: м.Київ · Представник: Іванов») into a qualifier segment: the
    /// «label: » colons become spaces (a colon is not allowed in a file name and «_» reads badly), whitespace is
    /// collapsed and the result is cut to <paramref name="maxLength"/> so a long filter list cannot push the name
    /// past what the file system takes.
    /// </summary>
    public static string Qualifier(string? text, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var cleaned = string.Join(' ', text.Replace(": ", " ").Replace(':', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd() + "…";
    }

    /// <summary>Replaces characters the file system rejects so the save dialog accepts the default name.</summary>
    public static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }
}
