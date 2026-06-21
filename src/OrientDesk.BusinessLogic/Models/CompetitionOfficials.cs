namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// The competition's officials as entered on the create / settings form: a course-setter, chief judge and
/// chief secretary (each a name + optional judge category) plus the jury (free multi-line text). A simple
/// carrier so the create flow can seed them onto the new <c>CompetitionInfo</c> without a long parameter list.
/// </summary>
public sealed record CompetitionOfficials(
    string CourseSetter,
    string CourseSetterCategory,
    string ChiefJudge,
    string ChiefJudgeCategory,
    string ChiefSecretary,
    string ChiefSecretaryCategory,
    string Jury)
{
    public static readonly CompetitionOfficials None =
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}
