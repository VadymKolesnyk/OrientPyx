using System.Collections.Generic;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// Builds the flat (non-banded) column set for the day-mode participants table so it can reuse
/// <see cref="SheetTable"/> without the roster's per-day banding. Every column is a single-column
/// <see cref="SheetBand.BandKind.Identity"/> band, giving a plain one-tier header. Cells bind
/// directly on <see cref="ParticipantDayRowViewModel"/> (each row already represents one day).
/// </summary>
public sealed class DayColumnBuilder
{
    private readonly ILocalizationService _loc;

    public DayColumnBuilder(ILocalizationService localization)
    {
        _loc = localization;
    }

    /// <summary>
    /// Builds the day-grid bands. The Team column is included only for disciplines that use teams
    /// (rogaine) — the caller passes <paramref name="showTeam"/>; rebuild when discipline changes.
    /// Existing <paramref name="previous"/> bands carry user-set widths forward.
    /// </summary>
    public IReadOnlyList<SheetBand> Build(
        bool showTeam,
        IReadOnlyList<EntryFeeDiscount> discounts,
        bool raisedFeeEnabled,
        IReadOnlyList<SheetBand>? previous)
    {
        var bands = new List<SheetBand>();

        bands.Add(Identity(SheetCellKind.IdentityText, "Participants.Col.Number", nameof(ParticipantDayRowViewModel.Number)));
        bands.Add(Identity(SheetCellKind.IdentityText, "Participants.Col.FullName", nameof(ParticipantDayRowViewModel.FullName), fixedWidth: 220));
        // Rank: a combo bound on the row (RankOptions/SelectedRank); sorted by the selected label.
        bands.Add(Identity(SheetCellKind.RowRank, "Participants.Col.Rank", path: string.Empty,
            sortPath: $"{nameof(ParticipantDayRowViewModel.SelectedRank)}.{nameof(RankOption.Label)}"));
        bands.Add(Identity(SheetCellKind.IdentityText, "Participants.Col.Coach", nameof(ParticipantDayRowViewModel.Coach)));
        bands.Add(Identity(SheetCellKind.BirthDate, "Participants.Col.BirthDate", nameof(ParticipantDayRowViewModel.BirthDate), fixedWidth: 160));

        // Region / Club (competition-level combos bound on the row), sorted by their labels.
        bands.Add(Identity(SheetCellKind.RowRegion, "Participants.Col.Region", path: string.Empty,
            sortPath: $"{nameof(ParticipantDayRowViewModel.SelectedRegion)}.{nameof(RegionOption.Label)}"));
        bands.Add(Identity(SheetCellKind.RowClub, "Participants.Col.Club", path: string.Empty,
            sortPath: $"{nameof(ParticipantDayRowViewModel.SelectedClub)}.{nameof(ClubOption.Label)}"));
        bands.Add(Identity(SheetCellKind.RowDussh, "Participants.Col.Dussh", path: string.Empty,
            sortPath: $"{nameof(ParticipantDayRowViewModel.SelectedDussh)}.{nameof(DusshOption.Label)}"));

        // Competition-level text + boolean participant fields.
        bands.Add(Identity(SheetCellKind.IdentityText, "Participants.Col.Representative", nameof(ParticipantDayRowViewModel.Representative)));
        bands.Add(Identity(SheetCellKind.IdentityText, "Participants.Col.FsouCode", nameof(ParticipantDayRowViewModel.FsouCode)));
        bands.Add(Identity(SheetCellKind.IdentityBool, "Participants.Col.IsFsouMember", nameof(ParticipantDayRowViewModel.IsFsouMember), fixedWidth: 110));
        var paymentBand = Identity(SheetCellKind.PaymentText, "Participants.Col.Payment", nameof(ParticipantDayRowViewModel.Payment));
        ConfigurePaymentColumn(paymentBand.Columns[0], nameof(ParticipantDayRowViewModel.PaymentStatusKey));
        bands.Add(paymentBand);

        // This day's group (combo bound directly on the row) and chip (free text, unique per day).
        bands.Add(Identity(SheetCellKind.RowGroup, "Participants.Col.Group", path: string.Empty,
            sortPath: $"{nameof(ParticipantDayRowViewModel.SelectedGroup)}.{nameof(GroupOption.Label)}"));
        var chipBand = Identity(SheetCellKind.ChipText, "Participants.Col.Chip", nameof(ParticipantDayRowViewModel.Chip));
        chipBand.Columns[0].RentalChipColumn = true; // right-click offers the rental toggle
        bands.Add(chipBand);
        // This day's start time (HH:mm text) and out-of-competition flag.
        bands.Add(Identity(SheetCellKind.StartTimeText, "Participants.Col.StartTime", nameof(ParticipantDayRowViewModel.StartTimeText),
            sortPath: nameof(ParticipantDayRowViewModel.StartTime)));
        bands.Add(Identity(SheetCellKind.IdentityBool, "Participants.Col.OutOfCompetition", nameof(ParticipantDayRowViewModel.OutOfCompetition), fixedWidth: 110));

        if (showTeam)
            bands.Add(Identity(SheetCellKind.IdentityText, "Participants.Col.Team", nameof(ParticipantDayRowViewModel.Team)));

        // Entry-fee tail: raised-fee flag (when enabled), one column per discount, then the total.
        EntryFeeColumns.Append(bands, _loc, discounts, raisedFeeEnabled);

        // Trailing delete action.
        var actions = new SheetColumn(SheetCellKind.Actions) { Width = 48, WidthCapped = true, MinWidth = 48 };
        bands.Add(new SheetBand(SheetBand.BandKind.Identity, [actions]) { Header = string.Empty });

        CarryWidths(previous, bands);
        return bands;
    }

    /// <summary>
    /// Points the payment column's filter at the row's payment-status token and enables the "by status"
    /// filter mode (empty / over / under / equal / not-a-number). Sort stays on the raw payment text.
    /// Shared by the day grid and the roster.
    /// </summary>
    internal static void ConfigurePaymentColumn(SheetColumn col, string statusKeyPath)
    {
        col.FilterPath = statusKeyPath;
        col.StatusFilter = true;
    }

    private SheetBand Identity(SheetCellKind kind, string headerKey, string path, double? fixedWidth = null, string? sortPath = null)
    {
        var header = _loc.Get(headerKey);
        var col = new SheetColumn(kind)
        {
            Header = header,
            IdentityPath = path,
            SortPath = sortPath ?? path,
            // Key off the header KEY (stable across languages) so a hidden column survives a language
            // change; the picker shows the localized header.
            Key = $"id:{headerKey}",
            PickerLabel = header,
        };
        if (fixedWidth is { } w)
        {
            col.Width = w;
            col.WidthCapped = true;
        }
        return new SheetBand(SheetBand.BandKind.Identity, [col]) { Header = col.Header };
    }

    // Carry widths forward by flat index where the kind lines up (best effort across rebuilds).
    private static void CarryWidths(IReadOnlyList<SheetBand>? previous, List<SheetBand> next)
    {
        if (previous is null)
            return;
        var oldCols = Flatten(previous);
        var newCols = Flatten(next);
        var count = oldCols.Count < newCols.Count ? oldCols.Count : newCols.Count;
        for (var i = 0; i < count; i++)
            if (oldCols[i].Kind == newCols[i].Kind)
                newCols[i].Width = oldCols[i].Width;
    }

    private static List<SheetColumn> Flatten(IReadOnlyList<SheetBand> bands)
    {
        var list = new List<SheetColumn>();
        foreach (var band in bands)
            list.AddRange(band.Columns);
        return list;
    }
}
