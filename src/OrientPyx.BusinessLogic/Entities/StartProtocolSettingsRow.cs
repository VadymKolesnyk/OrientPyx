using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Per-day, per-kind start-protocol template, stored in the event database (one row per
/// <see cref="EventDay"/> + <see cref="StartProtocolKind"/>). Holds the protocol layout (orientation,
/// ordered/visible columns, header text) serialised as JSON. A day with no row falls back to the kind's
/// built-in default at load time. Mirrors <see cref="ResultProtocolSettingsRow"/> but adds the kind
/// discriminator because the regular and judges' protocols keep separate templates.
/// </summary>
public class StartProtocolSettingsRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The day this template belongs to.</summary>
    public Guid EventDayId { get; set; }

    /// <summary>Which start protocol (regular vs judges) this template is for.</summary>
    public StartProtocolKind Kind { get; set; }

    /// <summary>The <see cref="StartProtocolSettings"/> serialised as JSON.</summary>
    public string Json { get; set; } = string.Empty;
}
