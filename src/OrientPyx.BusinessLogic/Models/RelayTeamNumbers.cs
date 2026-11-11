using System.Globalization;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A relay team's number as shown and sorted everywhere. A team may have no number yet (<see langword="null"/>): an
/// imported team without numbers (UOF) gets them later, by hand, so they match the course-setting program — until then
/// its number cells and leg numbers «100.2» are blank and it sorts after the numbered teams.
/// </summary>
public static class RelayTeamNumbers
{
    /// <summary>«100», or blank for a team without a number.</summary>
    public static string Text(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The leg number «100.2», or blank for a team without a number.</summary>
    public static string Bib(int? number, int leg) => number is { } n ? RelayBib.Format(n, leg) : string.Empty;

    /// <summary>Ascending sort key: numbered teams by number, then the teams without one.</summary>
    public static int SortKey(int? number) => number ?? int.MaxValue;

    /// <summary>«100», else «Азимут» in quotes — how a message names a team that may have no number.</summary>
    public static string Label(int? number, string name) =>
        number is { } n ? n.ToString(CultureInfo.InvariantCulture)
        : name.Trim().Length > 0 ? "«" + name.Trim() + "»"
        : "—";
}
