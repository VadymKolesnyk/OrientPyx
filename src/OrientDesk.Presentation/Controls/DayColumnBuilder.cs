using System.Collections.Generic;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// Builds the flat (non-banded) column set for the day-mode participants table so it can reuse
/// <see cref="RosterTable"/> without the roster's per-day banding. Every column is a single-column
/// <see cref="RosterBand.BandKind.Identity"/> band, giving a plain one-tier header. Cells bind
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
    public IReadOnlyList<RosterBand> Build(bool showTeam, IReadOnlyList<RosterBand>? previous)
    {
        var bands = new List<RosterBand>();

        bands.Add(Identity(RosterCellKind.IdentityText, "Participants.Col.Number", nameof(ParticipantDayRowViewModel.Number)));
        bands.Add(Identity(RosterCellKind.IdentityText, "Participants.Col.FullName", nameof(ParticipantDayRowViewModel.FullName), fixedWidth: 220));
        bands.Add(Identity(RosterCellKind.IdentityText, "Participants.Col.Rank", nameof(ParticipantDayRowViewModel.Rank)));
        bands.Add(Identity(RosterCellKind.IdentityText, "Participants.Col.Coach", nameof(ParticipantDayRowViewModel.Coach)));
        bands.Add(Identity(RosterCellKind.BirthDate, "Participants.Col.BirthDate", nameof(ParticipantDayRowViewModel.BirthDate), fixedWidth: 160));

        // This day's group (combo bound directly on the row) and chip (free text, unique per day).
        bands.Add(Identity(RosterCellKind.RowGroup, "Participants.Col.Group", path: string.Empty,
            sortPath: $"{nameof(ParticipantDayRowViewModel.SelectedGroup)}.{nameof(GroupOption.Label)}"));
        bands.Add(Identity(RosterCellKind.ChipText, "Participants.Col.Chip", nameof(ParticipantDayRowViewModel.Chip)));

        if (showTeam)
            bands.Add(Identity(RosterCellKind.IdentityText, "Participants.Col.Team", nameof(ParticipantDayRowViewModel.Team)));

        // Trailing delete action.
        var actions = new RosterColumn(RosterCellKind.Actions) { Width = 48, WidthCapped = true, MinWidth = 48 };
        bands.Add(new RosterBand(RosterBand.BandKind.Identity, [actions]) { Header = string.Empty });

        CarryWidths(previous, bands);
        return bands;
    }

    private RosterBand Identity(RosterCellKind kind, string headerKey, string path, double? fixedWidth = null, string? sortPath = null)
    {
        var col = new RosterColumn(kind)
        {
            Header = _loc.Get(headerKey),
            IdentityPath = path,
            SortPath = sortPath ?? path,
        };
        if (fixedWidth is { } w)
        {
            col.Width = w;
            col.WidthCapped = true;
        }
        return new RosterBand(RosterBand.BandKind.Identity, [col]) { Header = col.Header };
    }

    // Carry widths forward by flat index where the kind lines up (best effort across rebuilds).
    private static void CarryWidths(IReadOnlyList<RosterBand>? previous, List<RosterBand> next)
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

    private static List<RosterColumn> Flatten(IReadOnlyList<RosterBand> bands)
    {
        var list = new List<RosterColumn>();
        foreach (var band in bands)
            list.AddRange(band.Columns);
        return list;
    }
}
