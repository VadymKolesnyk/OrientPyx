namespace OrientDesk.BusinessLogic.Enums;

/// <summary>
/// Kind of competition for a day (or, later, a group override). Stored as a string in the
/// event database. New values (e.g. rogaine, relay) can be appended without reworking storage.
/// </summary>
public enum DisciplineType
{
    /// <summary>Classic: competitor visits all control points in a prescribed order.</summary>
    SetCourse,

    /// <summary>Score: competitor picks any control points; ranked by count (then time).</summary>
    ScoreByCount
}
