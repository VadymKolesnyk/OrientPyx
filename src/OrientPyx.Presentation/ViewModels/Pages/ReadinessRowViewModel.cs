using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One problem on «Перевірка готовності дня». Read-only: the texts are formatted from the issue's keys when it is applied
/// (and again by the page on a language switch — a row does not listen to the localization service itself).
/// </summary>
public sealed partial class ReadinessRowViewModel : ObservableObject
{
    public ReadinessRowViewModel(ReadinessIssue issue, ILocalizationService localization, int dayNumber)
    {
        _issue = issue;
        Refresh(localization, dayNumber);
    }

    private ReadinessIssue _issue;

    public ReadinessIssue Issue => _issue;

    /// <summary>The row identity across reloads (<see cref="ReadinessIssue.Key"/>).</summary>
    public string Key => _issue.Key;

    public bool IsError => _issue.Severity == ReadinessSeverity.Error;

    /// <summary>Errors sort before warnings.</summary>
    public int SeveritySort => (int)_issue.Severity;

    [ObservableProperty]
    private string _severityText = string.Empty;

    public string Number => _issue.Number;

    [ObservableProperty]
    private string _subject = string.Empty;

    public string GroupName => _issue.GroupName;

    [ObservableProperty]
    private string _problem = string.Empty;

    /// <summary>The page the problem is fixed on — named as in the menu.</summary>
    [ObservableProperty]
    private string _where = string.Empty;

    /// <summary>A fresh issue for the same row (a live reload): only the cells that changed raise.</summary>
    public void Apply(ReadinessIssue issue, ILocalizationService localization, int dayNumber)
    {
        var old = _issue;
        _issue = issue;
        if (old.Severity != issue.Severity)
        {
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(SeveritySort));
        }
        if (old.Number != issue.Number)
            OnPropertyChanged(nameof(Number));
        if (old.GroupName != issue.GroupName)
            OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(Issue));
        Refresh(localization, dayNumber);
    }

    /// <summary>Re-formats the texts (after a language switch).</summary>
    public void Refresh(ILocalizationService localization, int dayNumber)
    {
        SeverityText = localization.Get(IsError ? "DayReadiness.Severity.Error" : "DayReadiness.Severity.Warning");
        Subject = _issue.Subject.Length > 0
            ? _issue.Subject
            : string.Format(CultureInfo.CurrentCulture, localization.Get("DayReadiness.Subject.Day"), dayNumber);
        Problem = Format(localization.Get(_issue.MessageKey), _issue.Args);
        Where = localization.Get(WhereKey(_issue.Target));
    }

    // A translation with a placeholder the issue has no value for shows its template rather than throwing.
    private static string Format(string template, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return template;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args.Cast<object>().ToArray());
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static string WhereKey(ReadinessTarget target) => target switch
    {
        ReadinessTarget.Day => "Menu.Competition.Days",
        ReadinessTarget.Draw => "Menu.Start.Draw",
        ReadinessTarget.MassStart => "Menu.Start.MassStartDraw",
        ReadinessTarget.Group => "Menu.Competition.Groups",
        ReadinessTarget.RelayTeam => "Menu.Competition.RelayTeams",
        ReadinessTarget.NumberVariants => "Menu.Competition.NumberVariants",
        _ => "Menu.Participants"
    };
}
