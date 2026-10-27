using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.Plugins;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A result column beyond the built-in ones (§10.2): a plugin discipline's own column (key
/// <c>"&lt;pluginId&gt;.&lt;key&gt;"</c>, value from the result's <see cref="ParticipantDayResult.Extra"/>) or an
/// additional participant field (key <c>"field.&lt;key&gt;"</c>). The key is what protocol / monitor templates store; a
/// stored key whose column is not available on a day is simply not shown. <see cref="LabelKey"/> is a localization
/// key (a plugin's keys resolve through its strings) or, for a field, the field's plain label.
/// </summary>
public sealed record ExtraResultColumn(string Key, string LabelKey, ColumnKind Kind, ResultColumnDef? Plugin, string? FieldKey)
{
    /// <summary>True for a time / number column (centred, kept on one line); false for text.</summary>
    public bool IsNumeric => Kind is ColumnKind.Number or ColumnKind.Time;

    /// <summary>The cell text for one result: the plugin's formatted value, or the participant's field value.</summary>
    public string Format(ParticipantDayResult result, IReadOnlyDictionary<string, string>? fields)
    {
        if (FieldKey is { } key)
            return fields is not null && fields.TryGetValue(key, out var value) ? value : string.Empty;
        if (Plugin is null || result.Extra.Count == 0)
            return string.Empty;
        return Plugin.FormatValue(new ResultRow(new Dictionary<string, object?>(result.Extra)));
    }

    /// <summary>True when the plugin flags the result's value (shown in red); false for a field.</summary>
    public bool IsProblem(ParticipantDayResult result) =>
        Plugin is not null && result.Extra.Count > 0
        && Plugin.IsProblem(new ResultRow(new Dictionary<string, object?>(result.Extra)));

    /// <summary>The extra columns of a day: the plugin columns of <paramref name="disciplines"/> (once per key), then
    /// one per additional participant field, in field order.</summary>
    public static IReadOnlyList<ExtraResultColumn> ForDay(
        IDisciplineStrategyProvider strategies, IEnumerable<DisciplineId> disciplines, IEnumerable<CustomField> fields)
    {
        var columns = new List<ExtraResultColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in disciplines.Distinct())
        {
            var strategy = strategies.For(id);
            if (strategy.PluginId is not { } pluginId)
                continue;
            foreach (var column in strategy.ResultColumns)
            {
                var key = PluginStrings.Key(pluginId, column.Key);
                if (seen.Add(key))
                    columns.Add(new ExtraResultColumn(key, column.Label, column.Kind, column, null));
            }
        }

        foreach (var field in fields.OrderBy(f => f.Order))
        {
            var key = CustomField.ColumnPrefix + field.Key;
            if (seen.Add(key))
                columns.Add(new ExtraResultColumn(key, field.Label,
                    field.Kind == CustomFieldKind.Number ? ColumnKind.Number : ColumnKind.Text, null, field.Key));
        }
        return columns;
    }
}
