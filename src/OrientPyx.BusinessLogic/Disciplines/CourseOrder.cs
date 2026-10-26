namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>Flat control counting for a free-text course order (no pattern brackets).</summary>
public static class CourseOrder
{
    /// <summary>
    /// Counts control points in a free-text course order, skipping start/finish markers. A token is
    /// treated as a start/finish marker when it begins with a start/finish letter — Latin S/F or the
    /// Cyrillic С(старт)/Ф(фініш), case-insensitive — and is not purely numeric (e.g. "S1", "F",
    /// "Start", "С1", "Ф1"); everything else counts as a control point. Markers in any position are
    /// skipped, so a start/finish that appears mid-order (physically not punched) is not counted.
    /// </summary>
    public static int CountControls(string? courseOrder)
    {
        if (string.IsNullOrWhiteSpace(courseOrder))
            return 0;

        var count = 0;
        foreach (var token in courseOrder.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsStartOrFinish(token))
                continue;
            count++;
        }
        return count;
    }

    private static bool IsStartOrFinish(string token)
    {
        // Start/finish letters: Latin S/F and the Cyrillic С (старт, U+0421) / Ф (фініш, U+0424) that
        // Ukrainian OCAD exports use (e.g. "С1"/"Ф1"). Note the Cyrillic С is a distinct code point from
        // the Latin C, so it is listed explicitly.
        var first = token[0];
        if (first is not ('S' or 's' or 'F' or 'f' or 'С' or 'с' or 'Ф' or 'ф'))
            return false;

        // "31" stays a control point; "S1"/"F"/"Start"/"С1"/"Ф1" are markers. Purely numeric tokens never
        // reach here (they don't start with a marker letter), so any non-numeric marker-lettered token is a marker.
        return !ulong.TryParse(token, out _);
    }
}
