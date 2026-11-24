namespace OrientPyx.BusinessLogic.Models;

// A values-only tree in the shape of IOF Data Standard 3.0 (ResultList / StartList). The names follow the standard's
// elements; the writer (DataAccess) only serialises it, in the element order of IOF.xsd. Times are whole seconds.

/// <summary>The message header: who made the file and when (<c>creator</c> / <c>createTime</c>).</summary>
public sealed record IofDocumentHeader(string Creator, DateTimeOffset CreateTime);

/// <summary><c>&lt;Event&gt;</c>: name, start date (and end date for several races), organiser, the races of a
/// multi-day file (empty for one day).</summary>
public sealed record IofEvent(string Name, DateOnly StartDate, DateOnly? EndDate, string Organiser, IReadOnlyList<IofRace> Races);

/// <summary><c>&lt;Race&gt;</c> of a multi-race event: its number (= the day number), name and date.</summary>
public sealed record IofRace(int Number, string Name, DateOnly Date);

/// <summary><c>ResultList/@status</c>: <c>Complete</c> once every day of the file is closed, else <c>Snapshot</c>.</summary>
public enum IofResultListStatus
{
    Complete,
    Snapshot
}

/// <summary>IOF <c>ResultStatus</c> tokens the export uses (the names are the tokens).</summary>
public enum IofResultStatus
{
    OK,
    Finished,
    MissingPunch,
    Disqualified,
    DidNotFinish,
    OverTime,
    NotCompeting,
    DidNotStart,
    Active,
    Inactive
}

/// <summary><c>Organisation/@type</c> values the export writes.</summary>
public enum IofOrganisationType
{
    Club,
    NationalRegion
}

/// <summary><c>SplitTime/@status</c> — exactly the three values of the schema.</summary>
public enum IofSplitStatus
{
    OK,
    Missing,
    Additional
}

/// <summary><c>&lt;Person&gt;</c>: family + given name (both required by the schema), birth date, the FSOU code as
/// <c>&lt;Id type="FSOU"&gt;</c>.</summary>
public sealed record IofPerson(string Family, string Given, DateOnly? BirthDate, string? FsouId);

/// <summary><c>&lt;Organisation&gt;</c> with its name and type.</summary>
public sealed record IofOrganisation(string Name, IofOrganisationType Type);

/// <summary>A course (<c>SimpleCourse</c> / <c>SimpleRaceCourse</c>): race number (class course of a multi-day file
/// only), name, family, length in metres, number of controls. Every part is optional.</summary>
public sealed record IofSimpleCourse(int? RaceNumber, string? Name, string? Family, int? LengthMetres, int? NumberOfControls);

/// <summary><c>&lt;SplitTime&gt;</c>: the control code, the seconds since the start (absent for a missing control) and
/// the status.</summary>
public sealed record IofSplit(string Code, int? TimeSeconds, IofSplitStatus Status);

/// <summary><c>&lt;Score type="…"&gt;</c>.</summary>
public sealed record IofScore(string Type, decimal Value);

/// <summary><c>&lt;OverallResult&gt;</c>: a team's result (relay, rogaine) or the multi-day total.</summary>
public sealed record IofOverallResult(
    int? TimeSeconds, int? TimeBehindSeconds, int? Position, IofResultStatus Status, IReadOnlyList<IofScore> Scores);

/// <summary>
/// One race result (<c>PersonRaceResult</c> / <c>TeamMemberRaceResult</c>). <see cref="Leg"/> / <see cref="LegOrder"/>
/// only for a team member; <see cref="RaceNumber"/> only in a multi-day file.
/// </summary>
public sealed record IofRaceResult
{
    public int? RaceNumber { get; init; }
    public int? Leg { get; init; }
    public int? LegOrder { get; init; }
    public string? Bib { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? Finish { get; init; }
    public int? TimeSeconds { get; init; }
    public int? TimeBehindSeconds { get; init; }
    public int? Position { get; init; }
    public IofResultStatus Status { get; init; }
    public IReadOnlyList<IofScore> Scores { get; init; } = [];
    public IofOverallResult? Overall { get; init; }
    public IofSimpleCourse? Course { get; init; }
    public IReadOnlyList<IofSplit> Splits { get; init; } = [];
    public string? Chip { get; init; }
}

/// <summary><c>&lt;ResultList&gt;</c>.</summary>
public sealed record IofResultList(
    IofDocumentHeader Header, IofEvent Event, IofResultListStatus Status, IReadOnlyList<IofClassResult> Classes);

/// <summary><c>&lt;ClassResult&gt;</c>: the class name, its course per race, the people and the teams.</summary>
public sealed record IofClassResult(
    string ClassName,
    IReadOnlyList<IofSimpleCourse> Courses,
    IReadOnlyList<IofPersonResult> Persons,
    IReadOnlyList<IofTeamResult> Teams);

/// <summary><c>&lt;PersonResult&gt;</c>: one person with a result per race of the file.</summary>
public sealed record IofPersonResult(IofPerson Person, IofOrganisation? Organisation, IReadOnlyList<IofRaceResult> Results);

/// <summary><c>&lt;TeamResult&gt;</c>: name (required, may be empty), organisation, team number, members.</summary>
public sealed record IofTeamResult(
    string Name, IofOrganisation? Organisation, string? Bib, IReadOnlyList<IofTeamMemberResult> Members);

/// <summary><c>&lt;TeamMemberResult&gt;</c>.</summary>
public sealed record IofTeamMemberResult(IofPerson Person, IofOrganisation? Organisation, IofRaceResult Result);

/// <summary>One race start (<c>PersonRaceStart</c> / <c>TeamMemberRaceStart</c>).</summary>
public sealed record IofRaceStart
{
    public int? RaceNumber { get; init; }
    public int? Leg { get; init; }
    public int? LegOrder { get; init; }
    public string? Bib { get; init; }
    public DateTimeOffset? Start { get; init; }
    public IofSimpleCourse? Course { get; init; }
    public string? Chip { get; init; }
}

/// <summary><c>&lt;StartList&gt;</c>.</summary>
public sealed record IofStartList(IofDocumentHeader Header, IofEvent Event, IReadOnlyList<IofClassStart> Classes);

/// <summary><c>&lt;ClassStart&gt;</c>.</summary>
public sealed record IofClassStart(
    string ClassName,
    IReadOnlyList<IofSimpleCourse> Courses,
    IReadOnlyList<IofPersonStart> Persons,
    IReadOnlyList<IofTeamStart> Teams);

/// <summary><c>&lt;PersonStart&gt;</c>: one person with a start per race of the file (the schema needs at least one).</summary>
public sealed record IofPersonStart(IofPerson Person, IofOrganisation? Organisation, IReadOnlyList<IofRaceStart> Starts);

/// <summary><c>&lt;TeamStart&gt;</c>.</summary>
public sealed record IofTeamStart(
    string? Name, IofOrganisation? Organisation, string? Bib, IReadOnlyList<IofTeamMemberStart> Members);

/// <summary><c>&lt;TeamMemberStart&gt;</c>.</summary>
public sealed record IofTeamMemberStart(IofPerson Person, IofOrganisation? Organisation, IofRaceStart Start);
