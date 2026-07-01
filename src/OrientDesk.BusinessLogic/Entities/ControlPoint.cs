using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A single control point (контрольний пункт / КП) belonging to one competition day.
/// Stored in the event database; scoped to a day via <see cref="EventDayId"/>.
/// </summary>
public class ControlPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    /// <summary>Stable display/sort order within the day's grid. Kept because Code is free-text.</summary>
    public int Order { get; set; }

    /// <summary>Control-point code, e.g. "31", "S1". Free-text string (not a number).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>WGS-84 latitude; optional.</summary>
    public double? Latitude { get; set; }

    /// <summary>WGS-84 longitude; optional.</summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// Paper-map X position in millimetres (IOF <c>&lt;MapPosition&gt;</c>); optional. With
    /// <see cref="MapY"/> and <see cref="MapScale"/> this gives undistorted on-the-ground leg distances
    /// (map mm × scale) — the distance orienteering software prints. Preferred over the geographic
    /// coordinates, which the Web Mercator export stretches by 1/cos(latitude).
    /// </summary>
    public double? MapX { get; set; }

    /// <summary>Paper-map Y position in millimetres (IOF <c>&lt;MapPosition&gt;</c>); optional.</summary>
    public double? MapY { get; set; }

    /// <summary>Map scale denominator captured at import (e.g. 4000 for 1:4000); optional. Combined
    /// with <see cref="MapX"/>/<see cref="MapY"/> to turn map millimetres into ground metres.</summary>
    public int? MapScale { get; set; }

    /// <summary>Kind of point. Persisted as a string in the database.</summary>
    public ControlPointType Type { get; set; } = ControlPointType.Regular;

    /// <summary>
    /// Score value awarded when this control point is taken on a score/rogaine day; optional
    /// (null = not scored).
    /// </summary>
    public int? Points { get; set; }

    /// <summary>
    /// True when this control stopped working during the day («проблемний КП»). A disabled control is
    /// dropped from the prescribed/allowed course everywhere it is required: a set-course runner who
    /// missed it is not penalised (no MP), and a scored control no longer counts toward points. Edited
    /// from the «Проблемні КП» modal on the read-out (зчитка) page.
    /// </summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
