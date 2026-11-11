using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using OrientPyx.Presentation.Converters;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The relay columns of both participant tables (Р-21…Р-23), bound to a row's <see cref="RelayCellsViewModel"/> —
/// <c>Relay.</c> on a day-grid row, <c>Days[i].Relay.</c> on a roster row: «Номер етапу» «100.2» (read-only, it follows
/// the team's number and the leg), «Команда» (the team's name, typed like a rogaine team; an unnamed team shows a
/// greyed «№101»), «Номер команди» and «Етап» (tinted amber while some leg of the team has nobody — the team runs
/// «поза конкурсом» then). The editable cells commit when the cell is left (one edit = one save, never one per
/// keystroke) and take a fill-down paste.
/// </summary>
internal static class RelayColumns
{
    private static readonly BoolToOpacityConverter DimWhenNotMember = new();

    /// <summary>The fields a relay column can show, in the roster's field terms.</summary>
    public static bool IsRelay(RosterField field) =>
        field is RosterField.Relay or RosterField.RelayTeam or RosterField.RelayTeamNumber or RosterField.RelayLeg;

    /// <summary>A comfortable width for the field's column (its header with the sort handle fits).</summary>
    public static double Width(RosterField field) => field switch
    {
        RosterField.Relay => 130,
        RosterField.RelayTeam => 170,
        RosterField.RelayTeamNumber => 155,
        _ => 80,
    };

    /// <param name="relayPrefix">The path to the row's relay cells: <c>Relay.</c> or <c>Days[i].Relay.</c>.</param>
    /// <param name="dayPrefix">The roster's day cell (<c>Days[i].</c>) whose lock / membership the cell follows; null on
    /// the day grid, where the table's own lock applies.</param>
    public static SheetColumn Column(
        RosterField field, string header, string key, string pickerLabel, string relayPrefix, string? dayPrefix, double width)
    {
        var value = relayPrefix + field switch
        {
            RosterField.Relay => nameof(RelayCellsViewModel.BibText),
            RosterField.RelayTeam => nameof(RelayCellsViewModel.TeamName),
            RosterField.RelayTeamNumber => nameof(RelayCellsViewModel.TeamText),
            _ => nameof(RelayCellsViewModel.LegText),
        };
        var sort = relayPrefix + field switch
        {
            RosterField.Relay => nameof(RelayCellsViewModel.BibSort),
            RosterField.RelayTeam => nameof(RelayCellsViewModel.TeamName),
            RosterField.RelayTeamNumber => nameof(RelayCellsViewModel.TeamSort),
            _ => nameof(RelayCellsViewModel.LegSort),
        };
        var column = new SheetColumn(SheetCellKind.Custom)
        {
            Header = header,
            PickerLabel = pickerLabel,
            Key = key,
            SortPath = sort,
            FilterPath = value,
            CopyPath = value,
            PastePath = field == RosterField.Relay ? null : value,
            Width = width,
            WidthCapped = true,
            CellBuilder = () => Cell(field, value, relayPrefix, dayPrefix),
        };
        if (field == RosterField.RelayLeg)
        {
            column.CellBackgroundPath = relayPrefix + nameof(RelayCellsViewModel.IsIncomplete);
            column.CellBackgroundBrush = DayColumnBuilder.ChipCapacityBrush;   // the shared amber «check this» tint
            column.CellBackgroundTooltipPath = relayPrefix + nameof(RelayCellsViewModel.IncompleteTooltip);
        }
        return column;
    }

    private static Control Cell(RosterField field, string value, string relayPrefix, string? dayPrefix)
    {
        var tooltip = relayPrefix + nameof(RelayCellsViewModel.Tooltip);
        if (field == RosterField.Relay)
        {
            var label = RosterCellFactory.BuildResultLabel(value, tooltip);
            if (dayPrefix is not null)
                label[!Visual.OpacityProperty] =
                    new Binding(dayPrefix + nameof(RosterDayCellViewModel.IsMember)) { Converter = DimWhenNotMember };
            return label;
        }

        var cell = new LazyTextCell(value, value, new SheetTextOptions
        {
            Mask = field == RosterField.RelayTeam ? SheetColumnBuilder.NumericMask.None : SheetColumnBuilder.NumericMask.Digits,
            CommitOnLostFocus = true,
            PlaceholderPath = field == RosterField.RelayTeam ? relayPrefix + nameof(RelayCellsViewModel.TeamNamePlaceholder) : null,
        });
        // The leg needs a team; on the roster every cell also needs an open relay day.
        var enabled = dayPrefix is not null
            ? dayPrefix + (field == RosterField.RelayLeg
                ? nameof(RosterDayCellViewModel.CanEditRelayLeg)
                : nameof(RosterDayCellViewModel.CanEditRelay))
            : field == RosterField.RelayLeg ? relayPrefix + nameof(RelayCellsViewModel.HasTeam) : null;
        if (enabled is not null)
            cell[!InputElement.IsEnabledProperty] = new Binding(enabled) { FallbackValue = true };
        cell[!ToolTip.TipProperty] = new Binding(tooltip);
        if (dayPrefix is not null)
        {
            cell[!LazyEditCell.LockedDayNumberProperty] =
                new Binding(dayPrefix + nameof(RosterDayCellViewModel.LockedDayNumber));
            cell[!Visual.OpacityProperty] =
                new Binding(dayPrefix + nameof(RosterDayCellViewModel.IsMember)) { Converter = DimWhenNotMember };
        }
        return cell;
    }
}
