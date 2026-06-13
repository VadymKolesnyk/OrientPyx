using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One read-only row in the finish-read log: the log sequence id, the chip number, and — when a
/// participant on the current day holds that chip — their number, full name and group. An unrecognised
/// chip (held by nobody on the day) shows a localized "невідомий" marker instead.
/// </summary>
public sealed class FinishReadRowViewModel
{
    private readonly FinishReadoutRow _row;
    private readonly ILocalizationService _localization;

    public FinishReadRowViewModel(FinishReadoutRow row, ILocalizationService localization)
    {
        _row = row;
        _localization = localization;
    }

    /// <summary>Stable identity for the grid (the readout's entity id).</summary>
    public Guid Id => _row.Id;

    /// <summary>The per-day log sequence number, shown as the "Id" column.</summary>
    public int Order => _row.Order;

    public string ChipNumber => _row.ChipNumber;

    /// <summary>Finish time as "HH:mm:ss", or blank when the readout carried none.</summary>
    public string FinishTimeText => _row.FinishTime is { } t ? t.ToString("HH:mm:ss") : string.Empty;

    public bool IsKnown => _row.IsKnown;

    /// <summary>Participant bib number when known; otherwise blank.</summary>
    public string ParticipantNumber => _row.ParticipantNumber;

    /// <summary>Full name when known; otherwise the localized "unknown chip" marker.</summary>
    public string FullName => _row.IsKnown ? _row.FullName : _localization.Get("FinishRead.Unknown");

    /// <summary>Group when known; otherwise blank.</summary>
    public string GroupName => _row.GroupName;

    /// <summary>
    /// Short status code shown in the status column (OK / MP / OVT / DNF / DNS / DSQ). Blank when there
    /// is no status (unknown chip, or a discipline that doesn't evaluate finishes yet).
    /// </summary>
    public string StatusText => _row.Status switch
    {
        FinishStatus.Ok => "OK",
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dns => "DNS",
        FinishStatus.Dsq => "DSQ",
        _ => string.Empty
    };

    /// <summary>
    /// Tooltip detail for the status: for MP, the localized "missing control N"; otherwise blank.
    /// </summary>
    public string StatusDetail => _row.Status == FinishStatus.Mp && _row.StatusDetail.Length > 0
        ? string.Format(_localization.Get("FinishRead.Status.MpDetail"), _row.StatusDetail)
        : string.Empty;
}
