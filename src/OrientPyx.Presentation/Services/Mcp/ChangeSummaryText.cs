using System.Globalization;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services.Mcp;

/// <summary>
/// A change of the assistant in the window's language: the summary's <c>Mcp.Change.*</c> template with its arguments
/// (an argument that is itself a known word — add / update / delete, a directory kind — is translated through
/// <c>Mcp.Arg.*</c>); the English text when the template is missing.
/// </summary>
public static class ChangeSummaryText
{
    public static string Format(ChangeSummary summary, ILocalizationService localization)
    {
        var template = localization.Get(summary.Key);
        if (string.Equals(template, summary.Key, StringComparison.Ordinal))
            return summary.Text;
        var words = summary.Args.Select(a => Word(a, localization));
        // undo / redo name the change they act on: in its own words, as {0}.
        if (summary.Inner is { } inner)
            words = words.Skip(1).Prepend(Format(inner, localization));
        var args = words.Cast<object>().ToArray();
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return summary.Text;
        }
    }

    /// <summary>A field name of «Деталі» in the window's language (<c>Mcp.Field.*</c>), else as is.</summary>
    public static string Field(string field, ILocalizationService localization)
    {
        var key = "Mcp.Field." + field.Split(' ')[0];
        var text = localization.Get(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? field : text + field[field.Split(' ')[0].Length..];
    }

    private static string Word(string arg, ILocalizationService localization)
    {
        var key = "Mcp.Arg." + arg;
        var text = localization.Get(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? arg : text;
    }
}
