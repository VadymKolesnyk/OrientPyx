namespace OrientDesk.BusinessLogic.Enums;

/// <summary>Kind of a control point. Stored as a string in the event database.</summary>
public enum ControlPointType
{
    /// <summary>An ordinary control point on the course.</summary>
    Regular,

    /// <summary>A start point (the start itself).</summary>
    Start,

    /// <summary>A finish point (the finish itself).</summary>
    Finish
}
