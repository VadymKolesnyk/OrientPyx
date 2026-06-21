using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Turns the raw competition officials (<see cref="ProtocolOfficialsData"/>) plus the localized role captions
/// into the document's trailing signature block (<see cref="ProtocolOfficial"/> list). Shared by the results
/// and both start protocols so all three print the same block. Only configured officials appear: a blank name
/// is skipped, and the jury becomes one line per non-blank text line. The course-setter is per-group and is
/// NOT part of this block.
/// </summary>
public static class ProtocolOfficialsFactory
{
    public static IReadOnlyList<ProtocolOfficial> Build(
        ProtocolOfficialsData data,
        string chiefJudgeLabel,
        string chiefSecretaryLabel,
        string juryLabel)
    {
        var officials = new List<ProtocolOfficial>();

        Add(officials, chiefJudgeLabel, data.ChiefJudge, data.ChiefJudgeCategory);
        Add(officials, chiefSecretaryLabel, data.ChiefSecretary, data.ChiefSecretaryCategory);

        // Jury is free multi-line text — one signature line per non-blank line, all sharing the jury caption.
        // A category typed inline by the user stays as-is in the name (we don't parse it out).
        foreach (var line in SplitLines(data.Jury))
            officials.Add(new ProtocolOfficial(juryLabel, line, string.Empty));

        return officials;
    }

    private static void Add(List<ProtocolOfficial> list, string role, string name, string category)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > 0)
            list.Add(new ProtocolOfficial(role, trimmed, (category ?? string.Empty).Trim()));
    }

    private static IEnumerable<string> SplitLines(string text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
}
