using System.Collections.Generic;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// Turns the roster's day set + field blocks into the flat leaf-column list and the top-level band
/// grouping the table renders. Replaces the per-day column building that used to live in
/// <c>ParticipantsView.axaml.cs</c>. Headers are resolved (localized) here, so a language change is
/// handled by rebuilding.
/// </summary>
public sealed class RosterColumnBuilder
{
    private readonly ILocalizationService _loc;

    public RosterColumnBuilder(ILocalizationService localization)
    {
        _loc = localization;
    }

    /// <summary>The fixed identity bands, in order, each a single column spanning both tiers. Number
    /// leads, then the merged ПІБ (full name) column.</summary>
    private static readonly (SheetCellKind Kind, string HeaderKey, string Path, double? FixedWidth)[] Identity =
    [
        (SheetCellKind.IdentityText, "Participants.Col.Number",    nameof(ParticipantRosterRowViewModel.Number),    70),
        (SheetCellKind.IdentityText, "Participants.Col.FullName",  nameof(ParticipantRosterRowViewModel.FullName),  220),
        (SheetCellKind.RowRank,      "Participants.Col.Rank",
            $"{nameof(ParticipantRosterRowViewModel.SelectedRank)}.{nameof(RankOption.Label)}", 110),
        (SheetCellKind.IdentityText, "Participants.Col.Coach",     nameof(ParticipantRosterRowViewModel.Coach),     130),
        (SheetCellKind.BirthDate,    "Participants.Col.BirthDate", nameof(ParticipantRosterRowViewModel.BirthDate), 130),
        // Region / Club (competition-level combos on the row). The path is the sort key (the combo
        // binds its own *Options/Selected* properties — RowRegion/RowClub ignore IdentityPath).
        (SheetCellKind.RowRegion,    "Participants.Col.Region",
            $"{nameof(ParticipantRosterRowViewModel.SelectedRegion)}.{nameof(RegionOption.Label)}", 150),
        (SheetCellKind.RowClub,      "Participants.Col.Club",
            $"{nameof(ParticipantRosterRowViewModel.SelectedClub)}.{nameof(ClubOption.Label)}", 150),
        (SheetCellKind.RowDussh,     "Participants.Col.Dussh",
            $"{nameof(ParticipantRosterRowViewModel.SelectedDussh)}.{nameof(DusshOption.Label)}", 150),
        // Competition-level text + boolean participant fields.
        (SheetCellKind.IdentityText, "Participants.Col.Representative", nameof(ParticipantRosterRowViewModel.Representative), 140),
        (SheetCellKind.IdentityText, "Participants.Col.FsouCode",      nameof(ParticipantRosterRowViewModel.FsouCode),      120),
        (SheetCellKind.IdentityBool, "Participants.Col.IsFsouMember",  nameof(ParticipantRosterRowViewModel.IsFsouMember),  110),
        (SheetCellKind.PaymentText,  "Participants.Col.Payment",       nameof(ParticipantRosterRowViewModel.Payment),       120),
        (SheetCellKind.IdentityText, "Participants.Col.Note",          nameof(ParticipantRosterRowViewModel.Note),          180),
    ];

    /// <summary>The team column, appended to the identity set only for team disciplines (rogaine).</summary>
    private static readonly (SheetCellKind Kind, string HeaderKey, string Path, double? FixedWidth) TeamColumn =
        (SheetCellKind.IdentityText, "Participants.Col.Team", nameof(ParticipantRosterRowViewModel.Team), 150);

    /// <summary>
    /// Builds the bands (and, via them, the flat column list) for the given days and blocks. Existing
    /// <paramref name="previous"/> columns are reused by identity to preserve user-set widths across
    /// rebuilds (collapse/expand, language change) where the column still exists.
    /// </summary>
    public IReadOnlyList<SheetBand> Build(
        IReadOnlyList<EventDay> days,
        IReadOnlyList<RosterFieldBlockViewModel> blocks,
        IReadOnlyList<EntryFeeDiscount> discounts,
        bool raisedFeeEnabled,
        bool showTeam,
        bool showScore,
        IReadOnlyList<SheetBand>? previous)
    {
        var bands = new List<SheetBand>(Identity.Length + blocks.Count + 2);

        // Identity columns, plus the team column when a team discipline is in play (rogaine). Team is
        // competition-level (one value per participant, shared across days), so it is a plain identity
        // column like Representative — not a per-day block.
        var identity = showTeam
            ? [.. Identity, TeamColumn]
            : Identity;

        // Identity: one single-column band each, spanning both header tiers.
        foreach (var (kind, headerKey, path, fixedWidth) in identity)
        {
            var col = new SheetColumn(kind)
            {
                Header = _loc.Get(headerKey),
                IdentityPath = path,
                SortPath = path,
                // A stable key (the header KEY, not its localized text) so a hidden column survives a
                // language change; the picker shows the localized header.
                Key = $"id:{headerKey}",
                PickerLabel = _loc.Get(headerKey),
                // Plain editable text columns (number, name, coach, payment, …) support fill-down paste
                // straight to their bound property. Combos/dates/bools keep single-cell paste.
                PastePath = kind is SheetCellKind.IdentityText or SheetCellKind.PaymentText ? path : null,
            };
            if (fixedWidth is { } w)
            {
                col.Width = w;
                col.WidthCapped = true; // explicit width is never auto-capped
            }
            // The status bar shows the row count under «Номер» (the leading column).
            if (headerKey == "Participants.Col.Number")
                col.ShowCount = true;
            // The payment column tints by status, offers the "by status" filter mode, and is summed in
            // the status bar.
            if (kind == SheetCellKind.PaymentText)
                DayColumnBuilder.ConfigurePaymentColumn(col, nameof(ParticipantRosterRowViewModel.PaymentStatusKey),
                    nameof(ParticipantRosterRowViewModel.Payment));
            // Row-level combos (rank/region/club/ДЮСШ) resolve a paste to an option by exact label match.
            DayColumnBuilder.ConfigureComboPaste(col);
            bands.Add(new SheetBand(SheetBand.BandKind.Identity, [col]) { Header = col.Header });
        }

        // With a single day there is nothing to group: a "block" would be one "День 1" column under a
        // band label with a pointless collapse toggle. Render each field as a plain identity column
        // instead — single-tier header (the block label), per-day leaf cell on day 0, fully editable.
        var singleDay = days.Count == 1;

        // Field blocks: collapsed ⇒ one merged column; expanded ⇒ one column per day.
        foreach (var block in blocks)
        {
            // The «Бали» (score) and editable «Бонус» blocks only appear on point-scoring days.
            if (block.Field is RosterField.Score or RosterField.Bonus && !showScore)
                continue;

            var cols = new List<SheetColumn>();
            var fieldWidth = ResultWidth(block.Field);
            var blockLabel = _loc.Get(block.LabelKey);
            var isChips = block.Field == RosterField.Chips;

            if (singleDay)
            {
                // One plain column for this field, bound to the only day (index 0). No band, no toggle.
                var col = new SheetColumn(LeafKind(block.Field))
                {
                    Header = blockLabel,
                    DayIndex = 0,
                    // Result-text leaves carry the day-cell property to bind under Days[0].; other leaves ignore it.
                    IdentityPath = ResultTextPath(block.Field),
                    Width = fieldWidth,
                    WidthCapped = true,
                    SortPath = LeafSortPath(block.Field, 0),
                    // Key by the field only (no day suffix) so a hidden field survives a rebuild and lines
                    // up with the multi-day "block:<field>" merged key for the hidden-set carry-over.
                    Key = $"block:{block.Field}",
                    PickerLabel = blockLabel,
                    PastePath = LeafPastePath(block.Field, 0),
                    RentalChipColumn = isChips,
                    ToolTipPath = ResultTooltipPath(block.Field),
                };
                // Single-day group/status cell binds under Days[0]. too — wire combo-paste the same way.
                ConfigureDayComboPaste(col, block.Field, 0);
                bands.Add(new SheetBand(SheetBand.BandKind.Identity, [col]) { Header = blockLabel });
                continue;
            }
            if (block.IsCollapsed)
            {
                cols.Add(new SheetColumn(MergedKind(block.Field))
                {
                    Header = string.Empty,
                    // Merged read-only result cells carry the row's merged-value property to bind.
                    IdentityPath = CollapsedResultPath(block.Field),
                    Width = fieldWidth,
                    WidthCapped = true,
                    // A collapsed block is one sortable column: sort by the row's merged aggregate.
                    SortPath = CollapsedSortPath(block.Field),
                    // Copy reads the merged display value for off-screen rows (the group sort key isn't
                    // the displayed label). On-screen rows copy their rendered cell, so the "різні" /
                    // "<group> (n днів)" states are exact there; off-screen falls back to this value.
                    CopyPath = CollapsedCopyPath(block.Field),
                    // Key by the field only (not collapse state / day) so hiding the block survives a
                    // collapse/expand toggle. The merged column hidden ⇒ all its day columns hidden.
                    Key = $"block:{block.Field}",
                    PickerLabel = blockLabel,
                    // The chip block's right-click menu offers the rental toggle (a collapsed all-days
                    // edit, like an expanded one, is still a chip value).
                    RentalChipColumn = isChips,
                });
            }
            else
            {
                for (var i = 0; i < days.Count; i++)
                {
                    var leaf = new SheetColumn(LeafKind(block.Field))
                    {
                        Header = $"{_loc.Get("Header.Day")} {days[i].Number}",
                        DayIndex = i,
                        IdentityPath = ResultTextPath(block.Field),
                        Width = fieldWidth,
                        WidthCapped = true,
                        SortPath = LeafSortPath(block.Field, i),
                        Key = $"block:{block.Field}:day{days[i].Number}",
                        PickerLabel = $"{blockLabel} — {_loc.Get("Header.Day")} {days[i].Number}",
                        // Per-day text leaves (chip / start time) support fill-down paste straight to
                        // the day cell's bound property; group/out-of-competition leaves do not.
                        PastePath = LeafPastePath(block.Field, i),
                        // Per-day chip cells get the rental toggle in their right-click menu.
                        RentalChipColumn = isChips,
                        ToolTipPath = ResultTooltipPath(block.Field),
                    };
                    // Per-day combo leaves (group / status) resolve a paste to an option on that day by
                    // exact label match — bound under Days[i]. like the cell factory binds them.
                    ConfigureDayComboPaste(leaf, block.Field, i);
                    cols.Add(leaf);
                }
            }

            bands.Add(new SheetBand(SheetBand.BandKind.FieldBlock, cols)
            {
                Header = _loc.Get(block.LabelKey),
                Block = block,
            });
        }

        // Entry-fee tail: raised-fee flag (when enabled), one column per discount, then the total.
        EntryFeeColumns.Append(bands, _loc, discounts, raisedFeeEnabled);

        // Trailing actions column (delete), its own single-column band. A PickerLabel + stable Key make
        // it hideable from the columns picker like any other column.
        var actions = new SheetColumn(SheetCellKind.Actions)
        {
            Width = 48,
            WidthCapped = true,
            MinWidth = 48,
            Key = "actions",
            PickerLabel = _loc.Get("Participants.Col.Actions"),
        };
        bands.Add(new SheetBand(SheetBand.BandKind.Identity, [actions]) { Header = string.Empty });

        // Carry user-set widths forward where a column at the same position/kind still exists.
        CarryWidths(previous, bands);
        return bands;
    }

    private static SheetCellKind LeafKind(RosterField field) => field switch
    {
        RosterField.Groups => SheetCellKind.Group,
        RosterField.Chips => SheetCellKind.Chip,
        RosterField.StartTimes => SheetCellKind.StartTime,
        RosterField.OutOfCompetition => SheetCellKind.OutOfCompetition,
        // Result blocks: the status one is an editable combo, «бонус» an editable signed-integer cell, the
        // rest read-only text. The leaf's IdentityPath carries the day-cell property the factory binds to.
        RosterField.ResultStatus => SheetCellKind.Status,
        RosterField.Bonus => SheetCellKind.Bonus,
        _ => SheetCellKind.ResultText,
    };

    // Wire combo-paste for a per-day combo leaf (group / status), bound under Days[i]. like the cell
    // factory binds it. A paste resolves to an option on that day by exact label match (both option
    // types expose Label); other per-day leaves (chip/start-time/out-of-competition/read-only result)
    // get no descriptor and keep their existing paste behaviour.
    private static void ConfigureDayComboPaste(SheetColumn col, RosterField field, int dayIndex)
    {
        var prefix = $"Days[{dayIndex}].";
        switch (field)
        {
            case RosterField.Groups:
                col.ComboItemsPath = $"{prefix}{nameof(RosterDayCellViewModel.GroupOptions)}";
                col.ComboSelectedPath = $"{prefix}{nameof(RosterDayCellViewModel.SelectedGroup)}";
                col.ComboLabelPath = nameof(GroupOption.Label);
                break;
            case RosterField.ResultStatus:
                col.ComboItemsPath = $"{prefix}{nameof(RosterDayCellViewModel.StatusOptions)}";
                col.ComboSelectedPath = $"{prefix}{nameof(RosterDayCellViewModel.SelectedStatus)}";
                col.ComboLabelPath = nameof(GroupOption.Label);
                break;
        }
    }

    // The read-only result-text property on RosterDayCellViewModel for a given result field. (The status
    // block uses the Status leaf kind, which binds StatusOptions/SelectedStatus directly, not a path.)
    private static string ResultTextPath(RosterField field) => field switch
    {
        RosterField.ActualStart => nameof(RosterDayCellViewModel.ActualStartText),
        RosterField.Finish => nameof(RosterDayCellViewModel.FinishText),
        RosterField.Result => nameof(RosterDayCellViewModel.ResultText_),
        RosterField.Place => nameof(RosterDayCellViewModel.PlaceText),
        RosterField.Score => nameof(RosterDayCellViewModel.ScoreText),
        RosterField.Points => nameof(RosterDayCellViewModel.PointsText),
        RosterField.AwardedRank => nameof(RosterDayCellViewModel.AwardedRankText),
        RosterField.Bonus => nameof(RosterDayCellViewModel.BonusText),
        _ => string.Empty,
    };

    // The per-day-cell property carrying the hover tooltip for a result leaf (only «Бали» has one — the
    // per-control score breakdown). Bound under Days[i]. by the cell factory. Empty ⇒ no tooltip.
    private static string ResultTooltipPath(RosterField field) => field == RosterField.Score
        ? nameof(RosterDayCellViewModel.ScoreTooltip)
        : string.Empty;

    // Column width per field: the editable per-day fields keep the original 110; result fields are sized
    // to their content (times wider, place/score narrow).
    private static double ResultWidth(RosterField field) => field switch
    {
        RosterField.ActualStart or RosterField.Finish or RosterField.Result => 100.0,
        RosterField.ResultStatus => 90.0,
        RosterField.Place or RosterField.Score or RosterField.Points => 70.0,
        RosterField.AwardedRank => 120.0,
        RosterField.Bonus => 80.0,
        _ => 110.0, // groups / chips / start times / out-of-competition
    };

    private static SheetCellKind MergedKind(RosterField field) => field switch
    {
        RosterField.Groups => SheetCellKind.CollapsedGroup,
        RosterField.Chips => SheetCellKind.CollapsedChip,
        RosterField.StartTimes => SheetCellKind.CollapsedStartTime,
        RosterField.OutOfCompetition => SheetCellKind.CollapsedOutOfCompetition,
        RosterField.ResultStatus => SheetCellKind.CollapsedStatus,
        _ => SheetCellKind.CollapsedResultText,
    };

    // The row property a collapsed (merged) read-only result cell binds its label to (shared value or "різні").
    private static string CollapsedResultPath(RosterField field) => field switch
    {
        RosterField.ActualStart => nameof(ParticipantRosterRowViewModel.CollapsedActualStart),
        RosterField.Finish => nameof(ParticipantRosterRowViewModel.CollapsedFinish),
        RosterField.ResultStatus => nameof(ParticipantRosterRowViewModel.CollapsedResultStatus),
        RosterField.Result => nameof(ParticipantRosterRowViewModel.CollapsedResult),
        RosterField.Place => nameof(ParticipantRosterRowViewModel.CollapsedPlace),
        RosterField.Score => nameof(ParticipantRosterRowViewModel.CollapsedScore),
        RosterField.Points => nameof(ParticipantRosterRowViewModel.CollapsedPoints),
        RosterField.AwardedRank => nameof(ParticipantRosterRowViewModel.CollapsedAwardedRank),
        RosterField.Bonus => nameof(ParticipantRosterRowViewModel.CollapsedBonus),
        _ => string.Empty,
    };

    private static string CollapsedSortPath(RosterField field) => field switch
    {
        RosterField.Groups => nameof(ParticipantRosterRowViewModel.CollapsedGroupSortKey),
        RosterField.Chips => nameof(ParticipantRosterRowViewModel.CollapsedChipValue),
        RosterField.StartTimes => nameof(ParticipantRosterRowViewModel.CollapsedStartTimeText),
        RosterField.OutOfCompetition => nameof(ParticipantRosterRowViewModel.CollapsedOutOfCompetition),
        // Result blocks: sort the collapsed column by its merged display value.
        _ => CollapsedResultPath(field),
    };

    // The merged display value COPY reads for an off-screen collapsed cell. For groups this is the
    // selected option's label (the all-days-same case); chips/start-times already display their value;
    // out-of-competition is the bool (copied as a flag mark); result blocks copy their merged value.
    private static string CollapsedCopyPath(RosterField field) => field switch
    {
        RosterField.Groups =>
            $"{nameof(ParticipantRosterRowViewModel.CollapsedGroupValue)}.{nameof(GroupOption.Label)}",
        RosterField.Chips => nameof(ParticipantRosterRowViewModel.CollapsedChipValue),
        RosterField.StartTimes => nameof(ParticipantRosterRowViewModel.CollapsedStartTimeText),
        RosterField.OutOfCompetition => nameof(ParticipantRosterRowViewModel.CollapsedOutOfCompetition),
        _ => CollapsedResultPath(field),
    };

    // The two-way text property a per-day leaf cell edits, for fill-down paste. Only the plain text
    // fields (chip, start time) qualify; group is a combo and out-of-competition is a checkbox.
    private static string? LeafPastePath(RosterField field, int i) => field switch
    {
        RosterField.Chips => $"Days[{i}].{nameof(RosterDayCellViewModel.Chip)}",
        RosterField.StartTimes => $"Days[{i}].{nameof(RosterDayCellViewModel.StartTime)}",
        RosterField.Bonus => $"Days[{i}].{nameof(RosterDayCellViewModel.BonusText)}",
        _ => null,
    };

    private static string LeafSortPath(RosterField field, int i) => field switch
    {
        RosterField.Groups => $"Days[{i}].{nameof(RosterDayCellViewModel.SelectedGroup)}.{nameof(GroupOption.Label)}",
        RosterField.Chips => $"Days[{i}].{nameof(RosterDayCellViewModel.Chip)}",
        RosterField.StartTimes => $"Days[{i}].{nameof(RosterDayCellViewModel.StartTime)}",
        RosterField.OutOfCompetition => $"Days[{i}].{nameof(RosterDayCellViewModel.OutOfCompetition)}",
        // Result blocks sort by the cell's display text (status sorts by the selected option's label).
        RosterField.ResultStatus => $"Days[{i}].{nameof(RosterDayCellViewModel.SelectedStatus)}.{nameof(FinishStatusOption.Label)}",
        _ => $"Days[{i}].{ResultTextPath(field)}",
    };

    // Preserve widths the user dragged: match old→new columns by their flat index where the kind
    // lines up. A best-effort heuristic; on shape change (collapse/expand) mismatches just re-auto-size.
    private static void CarryWidths(IReadOnlyList<SheetBand>? previous, List<SheetBand> next)
    {
        if (previous is null)
            return;

        var oldCols = Flatten(previous);
        var newCols = Flatten(next);
        var count = oldCols.Count < newCols.Count ? oldCols.Count : newCols.Count;
        for (var i = 0; i < count; i++)
        {
            if (oldCols[i].Kind == newCols[i].Kind)
                newCols[i].Width = oldCols[i].Width;
        }
    }

    private static List<SheetColumn> Flatten(IReadOnlyList<SheetBand> bands)
    {
        var list = new List<SheetColumn>();
        foreach (var band in bands)
            list.AddRange(band.Columns);
        return list;
    }
}
