namespace OrientPyx.BusinessLogic.Models;

/// <summary>How bad a readiness problem is: an error makes a result wrong (or there is none), a warning may be fine.</summary>
public enum ReadinessSeverity
{
    Error,
    Warning
}

/// <summary>What a readiness check found (one value per check; <see cref="ReadinessRules.SeverityOf"/> gives its level).</summary>
public enum ReadinessKind
{
    NoGroup,
    NoChip,
    DuplicateChip,
    ChipTooSmall,
    BirthYearOutOfGroup,
    RequiredFieldEmpty,
    InvalidFieldValue,
    DuplicateNumber,
    NoStartTime,
    GroupWithoutStarts,
    DayWithoutStarts,
    RelayNoMassStart,
    GroupWithoutCourse,
    UnknownCourseCodes,
    InvalidCoursePattern,
    RelayLegWithoutVariant,
    UnknownDiscipline,
    RelayLegCourse,
    RelayRunnerWithoutLeg,
    RelayTeamIncomplete,
    RelayTeamWithoutNumber,
    NumberVariantWrongGroup,
    NumberWithoutVariant
}

/// <summary>Where a problem is fixed — the page «Перейти» opens. Declared in the order the list shows them.</summary>
public enum ReadinessTarget
{
    Day,
    Draw,
    MassStart,
    Group,
    Participant,
    RelayTeam,
    NumberVariants
}

/// <summary>Which cell of the target row holds the problem (an intent — the page maps it onto its column).</summary>
public enum ReadinessField
{
    None,
    Number,
    Group,
    Chip,
    BirthDate,
    StartTime,
    CustomField,
    CourseOrder,
    Discipline
}

/// <summary>
/// One problem of the day's data. BusinessLogic does not localize: the text is <see cref="MessageKey"/> formatted with
/// <see cref="Args"/> (strings, numbers in the invariant culture) — where possible the key a cell's tooltip already uses.
/// The ids say which row to show (<see cref="Target"/> picks the page).
/// </summary>
public sealed record ReadinessIssue(ReadinessKind Kind, ReadinessTarget Target, string MessageKey, IReadOnlyList<string> Args)
{
    public ReadinessSeverity Severity => ReadinessRules.SeverityOf(Kind);

    /// <summary>Who / what: the participant's name, the group («Ч21», «Ч21 · A» for one of its courses), the team; blank
    /// for the day itself.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The participant's / team's number; blank when none.</summary>
    public string Number { get; init; } = string.Empty;

    public string GroupName { get; init; } = string.Empty;

    /// <summary>The group's place on the day (<c>GroupDaySettings.Order</c>), for sorting; <see cref="int.MaxValue"/>
    /// without a group.</summary>
    public int GroupOrder { get; init; } = int.MaxValue;

    public Guid? DayId { get; init; }
    public Guid? LinkId { get; init; }
    public Guid? ParticipantId { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? GroupSettingsId { get; init; }
    public Guid? RelayTeamId { get; init; }

    /// <summary>«Варіанти за номерами»: the row's number and leg.</summary>
    public int? TeamNumber { get; init; }
    public int? Leg { get; init; }

    public ReadinessField Field { get; init; }

    /// <summary>The additional field's key (<see cref="ReadinessField.CustomField"/>); blank otherwise.</summary>
    public string FieldKey { get; init; } = string.Empty;

    /// <summary>A key stable across reloads (the list keeps its selection by it).</summary>
    public string Key => string.Join('|', Kind, LinkId ?? GroupSettingsId ?? RelayTeamId ?? DayId, FieldKey, TeamNumber, Leg, Subject);
}

/// <summary>Every problem of one day, errors first (<see cref="Errors"/> / <see cref="Warnings"/> count them).</summary>
public sealed record DayReadiness(Guid DayId, int DayNumber, IReadOnlyList<ReadinessIssue> Issues)
{
    public static DayReadiness Empty { get; } = new(Guid.Empty, 0, []);

    public int Errors => Issues.Count(i => i.Severity == ReadinessSeverity.Error);

    public int Warnings => Issues.Count(i => i.Severity == ReadinessSeverity.Warning);
}

/// <summary>The level of each check, in one place.</summary>
public static class ReadinessRules
{
    public static ReadinessSeverity SeverityOf(ReadinessKind kind) => kind switch
    {
        ReadinessKind.NoGroup
            or ReadinessKind.DuplicateChip
            or ReadinessKind.RequiredFieldEmpty
            or ReadinessKind.GroupWithoutCourse
            or ReadinessKind.UnknownCourseCodes
            or ReadinessKind.InvalidCoursePattern
            or ReadinessKind.RelayLegWithoutVariant
            or ReadinessKind.UnknownDiscipline
            or ReadinessKind.RelayLegCourse
            or ReadinessKind.RelayRunnerWithoutLeg => ReadinessSeverity.Error,
        _ => ReadinessSeverity.Warning
    };
}
