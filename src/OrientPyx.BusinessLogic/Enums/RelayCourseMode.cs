namespace OrientPyx.BusinessLogic.Enums;

/// <summary>How a relay group gives each leg its course (Р-41).</summary>
public enum RelayCourseMode
{
    /// <summary>Every leg runs the group's own course order (no named courses).</summary>
    Group,

    /// <summary>Each leg number («100.2») has its own course (<c>LegCourseAssignment</c> rows). (A former «ByLeg» —
    /// leg i runs course X in every team — is stored as this mode with a row per team since migration
    /// <c>DropRelayByLeg</c>.)</summary>
    ByBib,

    /// <summary>The team has a set of courses, each run exactly as many times as listed, in any order: the team's own
    /// set, else the courses assigned to its leg numbers, else the group's.</summary>
    TeamSet
}
