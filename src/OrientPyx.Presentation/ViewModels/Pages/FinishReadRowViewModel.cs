using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

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

    /// <summary>
    /// True when this row's chip is a rental chip the runner should hand back now — it's the last day
    /// they use it. Drives the chip cell's "collect the rental chip" highlight + tooltip.
    /// </summary>
    public bool CollectRentalChip => _row.CollectRentalChip;

    /// <summary>Tooltip for a highlighted rental-chip cell (blank unless flagged, which suppresses the tip).</summary>
    public string CollectRentalChipTooltip =>
        _row.CollectRentalChip ? _localization.Get("FinishRead.Chip.CollectRental") : string.Empty;

    /// <summary>
    /// Start time as "HH:mm:ss", or blank when none is known. For a recognised chip this is the resolved
    /// start used for evaluation (chip read-out start, else the assigned start); for an unrecognised chip
    /// — which has no resolved start — it falls back to the raw start the readout file carried, so an
    /// unknown chip still shows the start it was read with.
    /// </summary>
    public string StartTimeText => (_row.ResolvedStartTime ?? _row.StartTime) is { } t
        ? t.ToString("HH:mm:ss")
        : string.Empty;

    /// <summary>Finish time as "HH:mm:ss", or blank when the readout carried none.</summary>
    public string FinishTimeText => _row.FinishTime is { } t ? t.ToString("HH:mm:ss") : string.Empty;

    /// <summary>Result (finish − start) as "H:mm:ss", or blank when either time is unknown.</summary>
    public string ElapsedText => _row.Elapsed is { } e && e >= TimeSpan.Zero
        ? e.ToString("h\\:mm\\:ss")
        : string.Empty;

    public bool IsKnown => _row.IsKnown;

    /// <summary>True for an unrecognised chip — drives the row's red highlight.</summary>
    public bool IsUnknown => !_row.IsKnown;

    /// <summary>Participant bib number when known; otherwise blank.</summary>
    public string ParticipantNumber => _row.ParticipantNumber;

    /// <summary>Full name when known; otherwise the localized "unknown chip" marker.</summary>
    public string FullName => _row.IsKnown ? _row.FullName : _localization.Get("FinishRead.Unknown");

    /// <summary>Group when known; otherwise blank.</summary>
    public string GroupName => _row.GroupName;

    /// <summary>Collected «Бали» for a point-scoring day; blank when the discipline doesn't score points.</summary>
    public string ScoreText => _row.Score is { } s ? s.ToString() : string.Empty;

    /// <summary>The participant's 1-based place within their group on the day; blank when no place could be
    /// assigned (unknown chip, non-OK status, out-of-competition runner).</summary>
    public string PlaceText => _row.Place is { } p ? p.ToString() : string.Empty;

    /// <summary>The medal rank of a podium place — 1/2/3 → gold/silver/bronze — or 0 for any other place
    /// (or none). Drives the place cell's highlight badge.</summary>
    public int PlaceMedal => _row.Place is 1 or 2 or 3 ? _row.Place.Value : 0;

    /// <summary>Loss to the group leader as "+H:mm:ss" for a placed runner behind the leader; blank for the
    /// leader themselves, a non-placed row, or a scoring day (no result time).</summary>
    public string GapText => _row.Gap is { } g && g > TimeSpan.Zero
        ? "+" + g.ToString("h\\:mm\\:ss")
        : string.Empty;

    /// <summary>
    /// Short status code shown in the status column (OK / MP / OVT / DNF / DNS / DSQ). "OK" shows ONLY for a
    /// manual "cleared to OK" ruling — a computed (automatic) OK stays blank, since the plain result already
    /// conveys a clean finish. Blank too when there is no status (unknown chip, or a discipline that doesn't
    /// evaluate finishes yet).
    /// </summary>
    public string StatusText => _row.Status switch
    {
        FinishStatus.Ok => _row.IsManualStatus ? "OK" : string.Empty,
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

    /// <summary>
    /// True when the row carries a non-OK status (MP / OVT / DNF / DNS / DSQ) — drives the red tint on
    /// the status cell. A missing status (<see cref="FinishStatus.None"/>) and OK both read false.
    /// </summary>
    public bool StatusIsBad => _row.Status is not (FinishStatus.None or FinishStatus.Ok);

    /// <summary>
    /// True when the shown status is a judge's manual override rather than the discipline's computed value —
    /// drives the status cell's bold weight so a hand-set status stands out.
    /// </summary>
    public bool StatusIsManual => _row.IsManualStatus;

    /// <summary>
    /// True when the status is a manual override to OK — drives the status cell's green colour (a manual
    /// "cleared to OK" ruling), while any other manual status stays red like the computed bad statuses.
    /// </summary>
    public bool StatusIsManualOk => _row.IsManualStatus && _row.Status == FinishStatus.Ok;
}
