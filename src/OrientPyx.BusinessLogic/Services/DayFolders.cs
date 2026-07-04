namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Convention for the per-day subfolder inside a competition folder: <c>&lt;eventFolder&gt;/day{N}</c>.
/// Each day gets its own folder (created on competition creation) that holds the files imported for
/// that day (e.g. the IOF XML the courses came from). Pure path logic — no I/O.
/// </summary>
public static class DayFolders
{
    /// <summary>Folder name for a 1-based day number, e.g. 1 → "day1".</summary>
    public static string FolderName(int dayNumber) => $"day{dayNumber}";

    /// <summary>Absolute path to a day's folder inside the competition folder.</summary>
    public static string PathFor(string eventFolderPath, int dayNumber)
        => Path.Combine(eventFolderPath, FolderName(dayNumber));
}
