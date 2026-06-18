using OrientDesk.BusinessLogic.Enums;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Shared builders for the finish-status dropdown used wherever a judge sets a manual status override
/// (the finish-read edit modal and the participant tables). The list is the "(автоматично)" sentinel
/// (clears the override) followed by each settable status shown by its standard short code (OK / MP /
/// OVT / DNF / DNS / DSQ — language-neutral competition codes).
/// </summary>
public static class FinishStatusOptions
{
    /// <summary>Statuses a judge can set manually. <see cref="FinishStatus.None"/> is the "auto" sentinel.</summary>
    public static readonly FinishStatus[] Settable =
    [
        FinishStatus.Ok, FinishStatus.Mp, FinishStatus.Ovt,
        FinishStatus.Dnf, FinishStatus.Dns, FinishStatus.Dsq
    ];

    /// <summary>The standard short code for a status (blank for <see cref="FinishStatus.None"/>).</summary>
    public static string ShortCode(FinishStatus status) => status switch
    {
        FinishStatus.Ok => "OK",
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dns => "DNS",
        FinishStatus.Dsq => "DSQ",
        _ => string.Empty
    };

    /// <summary>Builds the full option list: an "auto" sentinel, then each settable status.</summary>
    public static IReadOnlyList<FinishStatusOption> Build(ILocalizationService localization)
        => Build(localization, FinishStatus.None);

    /// <summary>
    /// Builds the option list with an "auto" sentinel whose label reflects the discipline-computed status,
    /// e.g. "(OK — автоматично)" / "(MP — автоматично)", so the dropdown shows what "auto" would resolve
    /// to. When <paramref name="computed"/> is <see cref="FinishStatus.None"/> the sentinel falls back to
    /// the plain "(автоматично)" label.
    /// </summary>
    public static IReadOnlyList<FinishStatusOption> Build(ILocalizationService localization, FinishStatus computed)
    {
        var auto = computed != FinishStatus.None
            ? string.Format(localization.Get("Participants.Status.AutoWith"), ShortCode(computed))
            : localization.Get("FinishRead.Edit.StatusAuto");
        var list = new List<FinishStatusOption> { FinishStatusOption.Auto(auto) };
        foreach (var s in Settable)
            list.Add(FinishStatusOption.ForStatus(s, ShortCode(s)));
        return list;
    }

    /// <summary>The option matching <paramref name="status"/>: the matching settable status, or the "auto"
    /// sentinel (first) for null / <see cref="FinishStatus.None"/> / an unlisted value.</summary>
    public static FinishStatusOption Select(IReadOnlyList<FinishStatusOption> options, FinishStatus? status) =>
        status is { } s && s != FinishStatus.None
            ? options.FirstOrDefault(o => o.Status == s) ?? options[0]
            : options[0];
}

/// <summary>
/// One choice in a finish-status dropdown: a leading "auto" sentinel (<see cref="Status"/> null = clear
/// the manual override and leave the discipline's computed status) or a settable status. Shared by the
/// finish-read edit modal and the participant tables.
/// </summary>
public sealed class FinishStatusOption
{
    private FinishStatusOption(FinishStatus? status, string label)
    {
        Status = status;
        Label = label;
    }

    /// <summary>The status to set, or null for the "auto" sentinel (clears the override).</summary>
    public FinishStatus? Status { get; }

    public string Label { get; }

    public static FinishStatusOption Auto(string label) => new(null, label);

    public static FinishStatusOption ForStatus(FinishStatus status, string label) => new(status, label);
}
