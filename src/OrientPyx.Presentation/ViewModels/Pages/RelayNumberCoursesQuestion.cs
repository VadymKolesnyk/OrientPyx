using System.Globalization;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Дистанції за номером не діятимуть»: asked before a relay team moves to another group while its leg numbers hold
/// courses of its group («Варіанти за номерами», Р-41) — the results take only the rows of the team's group, so there
/// they stop applying. Confirm moves the team anyway; cancel keeps it. Shared by the participant tables and «Команди».
/// </summary>
internal static class RelayNumberCoursesQuestion
{
    private const int Shown = 12;

    public static ConfirmDialogViewModel Build(
        ILocalizationService localization, IEnumerable<RelayNumberCourse> courses, string targetGroup)
    {
        var all = courses.OrderBy(c => c.TeamNumber).ThenBy(c => c.Leg).ToList();
        var list = string.Join(", ", all.Take(Shown).Select(c => string.Format(CultureInfo.CurrentCulture,
            localization.Get("Relay.NumberCourses.Item"), c.Bib, c.Course)));
        if (all.Count > Shown)
            list += " " + string.Format(CultureInfo.CurrentCulture,
                localization.Get("Relay.NumberCourses.More"), all.Count - Shown);
        return new ConfirmDialogViewModel(localization, "Relay.NumberCourses.Title", "Relay.NumberCourses.Message",
            confirmKey: "Relay.NumberCourses.Move")
        {
            MessageArgs = [list, targetGroup]
        };
    }
}
