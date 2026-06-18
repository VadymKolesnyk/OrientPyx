namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// The chosen split-printout target: which installed printer to print to and the thermal-roll width.
/// Width is in millimetres (56 or 80); the printer name is an installed Windows printer ("просто
/// вибрав принтер — і друкує"). Blank printer = not configured yet.
/// </summary>
public sealed record PrintSettings(string PrinterName, int WidthMm)
{
    /// <summary>True once a printer has actually been chosen.</summary>
    public bool HasPrinter => !string.IsNullOrWhiteSpace(PrinterName);
}

/// <summary>
/// A ready-to-render split printout for one finish read-out: a header identifying the runner and the
/// course passage in order. Built in the BusinessLogic layer (layer-neutral, no printer/UI refs) from a
/// <see cref="SplitsView"/> + the resolved row metadata; the DataAccess printer renders it to paper.
/// The passage is always printed in order; <see cref="HasPoints"/> tells the renderer to add the бал
/// column for scored disciplines.
/// </summary>
public sealed class SplitPrintDocument
{
    // ── Header ────────────────────────────────────────────────────────────────────────────────────
    public string FullName { get; init; } = string.Empty;
    public string Number { get; init; } = string.Empty;
    public string ChipNumber { get; init; } = string.Empty;

    /// <summary>True when the chip is one of the competition's rental chips ("орендований чіп").</summary>
    public bool IsRentalChip { get; init; }

    public string GroupName { get; init; } = string.Empty;

    /// <summary>Result time (finish − start) as "H:mm:ss", or blank when not resolvable.</summary>
    public string ResultText { get; init; } = string.Empty;

    /// <summary>Status code (OK / MP / …); blank for an unknown chip.</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>Status detail value (e.g. the first missing/out-of-order control for MP); blank when none.
    /// The renderer appends the localized explanation after the status code.</summary>
    public string StatusDetail { get; init; } = string.Empty;

    /// <summary>Total result points scored (rogaine), as plain digits — the net after any over-time penalty;
    /// blank for a non-scoring discipline. Printed on its own header line ("Сума балів: 12"), mirroring the
    /// status line. When <see cref="PenaltyText"/> is set the renderer prints the breakdown "X − Y = Z" instead.</summary>
    public string TotalPointsText { get; init; } = string.Empty;

    /// <summary>Gross points before the over-time penalty (the "X" in "X − Y = Z"); blank when there is no
    /// penalty. Printed only alongside <see cref="PenaltyText"/>.</summary>
    public string GrossPointsText { get; init; } = string.Empty;

    /// <summary>Over-time penalty deducted (the "Y" in "X − Y = Z"), as plain digits; blank when none. Drives
    /// both the breakdown on the points line and the "−Y" shown beside the finish row.</summary>
    public string PenaltyText { get; init; } = string.Empty;

    /// <summary>Start punch wall-clock time ("HH:mm:ss"), blank when unknown — printed on the СТАРТ/ФІНІШ line.</summary>
    public string StartClock { get; init; } = string.Empty;

    /// <summary>Finish punch wall-clock time ("HH:mm:ss.f"), blank when no finish — printed on the СТАРТ/ФІНІШ line.</summary>
    public string FinishClock { get; init; } = string.Empty;

    /// <summary>Course total straight-line distance in km ("0.000"), blank when no coordinates — the ДЛ. value.</summary>
    public string TotalDistanceText { get; init; } = string.Empty;

    /// <summary>Average pace over the whole result (result time ÷ total distance) as "m:ss" — the СК. value.</summary>
    public string AvgPaceText { get; init; } = string.Empty;

    public DateTimeOffset PrintedAt { get; init; } = DateTimeOffset.Now;

    // ── Body ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The course passage in order (every punch in chip order), already formatted.</summary>
    public IReadOnlyList<SplitPrintRow> Rows { get; init; } = [];

    /// <summary>True when any row carries points — the renderer then shows the бал column (scored disciplines).</summary>
    public bool HasPoints => Rows.Any(r => r.PointsText is not null);

    /// <summary>
    /// True for a point-scoring discipline that still has leg geometry (rogaine, an Ordered passage). The
    /// renderer keeps the full set-course columns — leg/cumulative time, distance, pace — and inserts a бал
    /// column, rather than the compact №ПП КП БАЛ ЧАС layout the geometry-less choice/score formats use.
    /// </summary>
    public bool HasGeometry { get; init; }

    /// <summary>
    /// The prescribed (correct) control order, populated <b>only</b> when a set-course passage broke the
    /// order (MP) — so the slip shows what the runner should have done, with the first missing/out-of-order
    /// control flagged. Empty when the order was correct or the discipline isn't a set course; the renderer
    /// then prints nothing extra.
    /// </summary>
    public IReadOnlyList<SplitPrintCorrectRow> CorrectOrder { get; init; } = [];
}

/// <summary>
/// Localized captions the printer draws around the data (the document itself is values-only, since it is
/// built in the layer-neutral BusinessLogic layer; the Presentation layer supplies these from
/// <c>ILocalizationService</c>). Column headers and field labels for the receipt.
/// </summary>
public sealed record SplitPrintLabels(
    string ChipLabel,
    string RentalChipLabel,
    string StartLabel,
    string FinishLabel,
    string ResultLabel,
    string DistanceLabel,
    string AvgPaceLabel,
    string StatusLabel,
    string MpDetailLabel,
    string TotalPointsLabel,
    string ColSeq,
    string ColCode,
    string ColElapsed,
    string ColLeg,
    string ColDistance,
    string ColPace,
    string ColPoints,
    string CorrectOrderTitle);

/// <summary>
/// One printed passage row: the 1-based index (blank for start/finish markers), the control code, the
/// straight-line leg distance (metres), the leg/elapsed times and the leg pace — all pre-formatted as the
/// panel shows them. <see cref="PointsText"/> is non-null only for scored disciplines (drives the бал
/// column). <see cref="OnCourse"/> marks an on-course punch so the renderer can flag off-course ones.
/// <see cref="CountsForTeam"/> is true for a rogaine scoring punch on a control the whole team also took
/// (so the slip can mark which controls count toward the team); false outside a team context.
/// </summary>
public sealed record SplitPrintRow(
    string Index,
    string Code,
    string DistanceText,
    string LegText,
    string ElapsedText,
    string PaceText,
    string? PointsText,
    bool OnCourse,
    bool CountsForTeam = false);

/// <summary>
/// One control of the prescribed (correct) order, printed on the compact bottom line when a set-course
/// passage broke the order. <see cref="Code"/> is the control code; <see cref="Taken"/> is false for a
/// control the runner did not visit in order, which the renderer emphasises (bold) so the missing controls
/// stand out on the single correct-order line.
/// </summary>
public sealed record SplitPrintCorrectRow(string Code, bool Taken);
