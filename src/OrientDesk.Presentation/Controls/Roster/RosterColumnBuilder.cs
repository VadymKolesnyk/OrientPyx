using System.Collections.Generic;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

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
        (SheetCellKind.IdentityText, "Participants.Col.Payment",       nameof(ParticipantRosterRowViewModel.Payment),       120),
    ];

    /// <summary>
    /// Builds the bands (and, via them, the flat column list) for the given days and blocks. Existing
    /// <paramref name="previous"/> columns are reused by identity to preserve user-set widths across
    /// rebuilds (collapse/expand, language change) where the column still exists.
    /// </summary>
    public IReadOnlyList<SheetBand> Build(
        IReadOnlyList<EventDay> days,
        IReadOnlyList<RosterFieldBlockViewModel> blocks,
        IReadOnlyList<SheetBand>? previous)
    {
        var bands = new List<SheetBand>(Identity.Length + blocks.Count + 1);

        // Identity: one single-column band each, spanning both header tiers.
        foreach (var (kind, headerKey, path, fixedWidth) in Identity)
        {
            var col = new SheetColumn(kind)
            {
                Header = _loc.Get(headerKey),
                IdentityPath = path,
                SortPath = path,
            };
            if (fixedWidth is { } w)
            {
                col.Width = w;
                col.WidthCapped = true; // explicit width is never auto-capped
            }
            bands.Add(new SheetBand(SheetBand.BandKind.Identity, [col]) { Header = col.Header });
        }

        // Field blocks: collapsed ⇒ one merged column; expanded ⇒ one column per day.
        foreach (var block in blocks)
        {
            var cols = new List<SheetColumn>();
            const double fieldWidth = 110.0;
            if (block.IsCollapsed)
            {
                cols.Add(new SheetColumn(MergedKind(block.Field))
                {
                    Header = string.Empty,
                    Width = fieldWidth,
                    WidthCapped = true,
                    // A collapsed block is one sortable column: sort by the row's merged aggregate.
                    SortPath = block.Field == RosterField.Groups
                        ? $"{nameof(ParticipantRosterRowViewModel.CollapsedGroupValue)}.{nameof(GroupOption.Label)}"
                        : nameof(ParticipantRosterRowViewModel.CollapsedChipValue),
                });
            }
            else
            {
                for (var i = 0; i < days.Count; i++)
                {
                    cols.Add(new SheetColumn(LeafKind(block.Field))
                    {
                        Header = $"{_loc.Get("Header.Day")} {days[i].Number}",
                        DayIndex = i,
                        Width = fieldWidth,
                        WidthCapped = true,
                        SortPath = block.Field == RosterField.Groups
                            ? $"Days[{i}].{nameof(RosterDayCellViewModel.SelectedGroup)}.{nameof(GroupOption.Label)}"
                            : $"Days[{i}].{nameof(RosterDayCellViewModel.Chip)}",
                    });
                }
            }

            bands.Add(new SheetBand(SheetBand.BandKind.FieldBlock, cols)
            {
                Header = _loc.Get(block.LabelKey),
                Block = block,
            });
        }

        // Trailing actions column (delete), its own single-column band.
        var actions = new SheetColumn(SheetCellKind.Actions) { Width = 48, WidthCapped = true, MinWidth = 48 };
        bands.Add(new SheetBand(SheetBand.BandKind.Identity, [actions]) { Header = string.Empty });

        // Carry user-set widths forward where a column at the same position/kind still exists.
        CarryWidths(previous, bands);
        return bands;
    }

    private static SheetCellKind LeafKind(RosterField field) =>
        field == RosterField.Groups ? SheetCellKind.Group : SheetCellKind.Chip;

    private static SheetCellKind MergedKind(RosterField field) =>
        field == RosterField.Groups ? SheetCellKind.CollapsedGroup : SheetCellKind.CollapsedChip;

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
