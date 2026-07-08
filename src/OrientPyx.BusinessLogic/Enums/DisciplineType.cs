namespace OrientPyx.BusinessLogic.Enums;

/// <summary>
/// Kind of competition for a day (or, later, a group override). Stored as a string in the
/// event database. New values (e.g. rogaine, relay) can be appended without reworking storage.
/// </summary>
public enum DisciplineType
{
    /// <summary>Classic: competitor visits all control points in a prescribed order.</summary>
    SetCourse,

    /// <summary>Score: competitor picks any control points; ranked by count (then time).</summary>
    ScoreByCount,

    /// <summary>
    /// Score by time: competitor picks control points within a time limit; ranked by points
    /// collected, with a penalty deducted per minute of finishing late.
    /// </summary>
    ScoreByTime,

    /// <summary>
    /// Rogaine: long score format; competitor picks control points within a (usually long) time
    /// limit and is ranked by the points collected.
    /// </summary>
    Rogaine,

    /// <summary>
    /// Mixed (змішаний): the course order is a pattern that mixes prescribed order with free-choice
    /// sections — an ordered run <c>&lt;41 42&gt;</c>, an "any N of" block <c>[2 45 46 47]</c>, and nesting.
    /// Judged like a set course but against the pattern (see the «змішаний» course-pattern help).
    /// </summary>
    Mixed,

    /// <summary>
    /// Scatter / butterfly (розсіювання): the course is defined by <b>several</b> valid orders (variants),
    /// each a prescribed sequence of controls; every runner runs one of them. The variant a runner took is
    /// auto-detected from their read-out (the best-matching order), then judged like a set course against
    /// that variant — MP against the closest one when none fully matches. Variants are stored per (day,
    /// group) in a dedicated event-database table, not in the group's course-order string.
    /// </summary>
    Scatter
}
