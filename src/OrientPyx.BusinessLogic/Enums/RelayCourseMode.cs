namespace OrientPyx.BusinessLogic.Enums;

/// <summary>How a relay group gives each leg its course (Р-41).</summary>
public enum RelayCourseMode
{
    /// <summary>Every leg runs the group's own course order (no named courses).</summary>
    Group,

    /// <summary>Leg i runs the named course set for that leg (<c>GroupDaySettings.LegCoursesJson</c>).</summary>
    ByLeg,

    /// <summary>Each leg number («100.2») has its own course (<c>LegCourseAssignment</c> rows).</summary>
    ByBib,

    /// <summary>The team has a set of courses, each run exactly as many times as listed, in any order.</summary>
    TeamSet
}
