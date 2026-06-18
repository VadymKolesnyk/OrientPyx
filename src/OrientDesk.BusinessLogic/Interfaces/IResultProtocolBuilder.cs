using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Turns raw day results (<see cref="ResultProtocolData"/>) plus the user's <see cref="ResultProtocolSettings"/>
/// and the localized <see cref="ProtocolLabels"/> into a renderable <see cref="ResultProtocolDocument"/>:
/// resolves the header text (settings override, else the competition fields), maps each visible column to a
/// formatted cell, and orders each group's rows (placed finishers first by place, then the rest by name).
/// Layer-neutral — no UI, no document library — so it lives in BusinessLogic and is unit-testable.
/// </summary>
public interface IResultProtocolBuilder
{
    ResultProtocolDocument Build(ResultProtocolData data, ResultProtocolSettings settings, ProtocolLabels labels);
}

/// <summary>
/// Localized captions the protocol needs that don't come from competition data: the default title, each
/// column's header text, and the section sub-caption labels (length / control-count / time-limit). Supplied
/// by the Presentation layer from <c>ILocalizationService</c> so the builder stays localization-free, and
/// the column headers honour the user's chosen column order (the builder picks the ones it emits).
/// </summary>
public sealed record ProtocolLabels(
    string DefaultTitle,
    IReadOnlyDictionary<ProtocolColumn, string> ColumnHeaders,
    string DistanceLabel,
    string ControlCountLabel,
    string TimeLimitLabel);
