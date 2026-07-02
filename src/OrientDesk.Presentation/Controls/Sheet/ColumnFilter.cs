using System;
using System.Collections.Generic;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.Controls;

/// <summary>The Google-Sheets-style filter modes a column can use.</summary>
public enum SheetFilterMode
{
    /// <summary>Keep rows whose cell value is one of an explicitly selected set of values.</summary>
    Values,

    /// <summary>Keep rows whose cell value satisfies a text condition (contains, equals, …).</summary>
    Condition,

    /// <summary>
    /// Keep rows whose cell value (a status token) is one of an explicitly selected set of categories.
    /// Opt-in per column (<see cref="SheetColumn.StatusFilter"/>); used by the participant payment
    /// column (empty / over / under / equal / not-a-number).
    /// </summary>
    Status
}

/// <summary>The text conditions offered in <see cref="SheetFilterMode.Condition"/> mode.</summary>
public enum SheetFilterCondition
{
    Contains,
    DoesNotContain,
    Equals,
    StartsWith,
    EndsWith,
    IsEmpty,
    IsNotEmpty
}

/// <summary>
/// One active per-column filter on a <see cref="SheetTable"/>. Keyed by <see cref="SheetColumn.Key"/>
/// so it survives a header rebuild (language change, collapse/expand, day-set change). Pure
/// presentation state: filtering is display-only and never mutates the bound row collection.
/// </summary>
public sealed class SheetFilter
{
    /// <summary>The <see cref="SheetColumn.Key"/> this filter applies to.</summary>
    public required string ColumnKey { get; init; }

    /// <summary>Localized column header, re-set on each rebuild so the chip label re-localizes.</summary>
    public string Header { get; set; } = string.Empty;

    public SheetFilterMode Mode { get; set; } = SheetFilterMode.Condition;

    // ── Condition mode ──
    public SheetFilterCondition Condition { get; set; } = SheetFilterCondition.Contains;
    public string Text { get; set; } = string.Empty;

    // ── Values mode ──
    /// <summary>The set of cell values (as displayed text) to keep. Null ⇒ every value passes.</summary>
    public HashSet<string>? AllowedValues { get; set; }

    // ── Status mode ──
    /// <summary>The set of status tokens (category names) to keep. Null ⇒ every status passes.</summary>
    public HashSet<string>? AllowedStatuses { get; set; }

    /// <summary>True when this filter would actually exclude rows (so it is worth keeping/showing).</summary>
    public bool IsActive => Mode switch
    {
        SheetFilterMode.Values => AllowedValues is not null,
        SheetFilterMode.Status => AllowedStatuses is not null,
        SheetFilterMode.Condition => Condition is SheetFilterCondition.IsEmpty or SheetFilterCondition.IsNotEmpty
            || !string.IsNullOrEmpty(Text),
        _ => false
    };

    /// <summary>Whether a row whose this-column cell renders as <paramref name="cellText"/> passes.</summary>
    public bool Matches(string? cellText)
    {
        var value = cellText ?? string.Empty;
        return Mode switch
        {
            SheetFilterMode.Values => MatchesValues(value),
            SheetFilterMode.Status => AllowedStatuses is null || AllowedStatuses.Contains(value),
            _ => MatchesCondition(value)
        };
    }

    private bool MatchesValues(string value)
        => AllowedValues is null || AllowedValues.Contains(value);

    private bool MatchesCondition(string value)
    {
        const StringComparison ci = StringComparison.CurrentCultureIgnoreCase;
        // Text conditions are layout-tolerant: the typed term is expanded (wrong keyboard layout, s/i→ы)
        // and the condition holds if it holds for ANY variant (for the negative DoesNotContain, ALL).
        var variants = TextSearch.Variants(Text);
        return Condition switch
        {
            SheetFilterCondition.IsEmpty => string.IsNullOrWhiteSpace(value),
            SheetFilterCondition.IsNotEmpty => !string.IsNullOrWhiteSpace(value),
            SheetFilterCondition.Contains => AnyVariant(variants, v => value.Contains(v, ci)),
            SheetFilterCondition.DoesNotContain => !AnyVariant(variants, v => value.Contains(v, ci)),
            SheetFilterCondition.Equals => AnyVariant(variants, v => value.Equals(v, ci)),
            SheetFilterCondition.StartsWith => AnyVariant(variants, v => value.StartsWith(v, ci)),
            SheetFilterCondition.EndsWith => AnyVariant(variants, v => value.EndsWith(v, ci)),
            _ => true
        };
    }

    // True when any variant satisfies the predicate. An empty variant set means the term was blank, in
    // which case the original substring behaviour (empty term matches everything) is preserved.
    private bool AnyVariant(IReadOnlyList<string> variants, Func<string, bool> predicate)
    {
        if (variants.Count == 0)
            return predicate(Text);
        foreach (var v in variants)
            if (predicate(v))
                return true;
        return false;
    }

    /// <summary>The chip text shown above the table, e.g. «Прізвище: містить «іван»».</summary>
    public string Describe(ILocalizationService loc)
    {
        var head = string.IsNullOrEmpty(Header) ? loc.Get("Sheet.Filter.Column") : Header;
        if (Mode == SheetFilterMode.Values)
        {
            var count = AllowedValues?.Count ?? 0;
            return $"{head}: {loc.Get("Sheet.Filter.ValuesSummary")} ({count})";
        }
        if (Mode == SheetFilterMode.Status)
        {
            var count = AllowedStatuses?.Count ?? 0;
            return $"{head}: {loc.Get("Sheet.Filter.StatusSummary")} ({count})";
        }

        var cond = loc.Get(ConditionKey(Condition));
        return Condition is SheetFilterCondition.IsEmpty or SheetFilterCondition.IsNotEmpty
            ? $"{head}: {cond}"
            : $"{head}: {cond} «{Text}»";
    }

    /// <summary>Localization key for a condition's display name.</summary>
    public static string ConditionKey(SheetFilterCondition condition) => condition switch
    {
        SheetFilterCondition.Contains => "Sheet.Filter.Cond.Contains",
        SheetFilterCondition.DoesNotContain => "Sheet.Filter.Cond.DoesNotContain",
        SheetFilterCondition.Equals => "Sheet.Filter.Cond.Equals",
        SheetFilterCondition.StartsWith => "Sheet.Filter.Cond.StartsWith",
        SheetFilterCondition.EndsWith => "Sheet.Filter.Cond.EndsWith",
        SheetFilterCondition.IsEmpty => "Sheet.Filter.Cond.IsEmpty",
        SheetFilterCondition.IsNotEmpty => "Sheet.Filter.Cond.IsNotEmpty",
        _ => "Sheet.Filter.Cond.Contains"
    };

    /// <summary>All conditions in display order, for the popup's condition dropdown.</summary>
    public static IReadOnlyList<SheetFilterCondition> AllConditions { get; } =
    [
        SheetFilterCondition.Contains,
        SheetFilterCondition.DoesNotContain,
        SheetFilterCondition.Equals,
        SheetFilterCondition.StartsWith,
        SheetFilterCondition.EndsWith,
        SheetFilterCondition.IsEmpty,
        SheetFilterCondition.IsNotEmpty
    ];
}
