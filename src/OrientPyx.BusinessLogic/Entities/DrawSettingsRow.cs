namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Per-day, per-kind start-draw settings, stored in the event database (one row per <see cref="EventDay"/>
/// + <see cref="OrientPyx.BusinessLogic.Models.DrawSettingsKind"/>). Holds whatever the draw page had
/// entered — start time, interval, separation, and the lane arrangement (or the per-group rows of the
/// classic page) — serialised as JSON, so reopening the page restores the last state. A day with no row
/// falls back to the page's defaults. Mirrors <see cref="StartProtocolSettingsRow"/>.
/// </summary>
public class DrawSettingsRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The day these settings belong to.</summary>
    public Guid EventDayId { get; set; }

    /// <summary>Which draw page (lane-based vs classic) these settings are for.</summary>
    public Models.DrawSettingsKind Kind { get; set; }

    /// <summary>The settings record (<see cref="Models.DrawSettings"/>) serialised as JSON.</summary>
    public string Json { get; set; } = string.Empty;
}
