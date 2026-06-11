namespace OrientDesk.BusinessLogic.Enums;

/// <summary>
/// Identifies a discipline-varying column in the participants grid. A discipline strategy declares
/// which of these it uses
/// (<see cref="OrientDesk.BusinessLogic.Disciplines.IDisciplineStrategy.UsesParticipantColumn"/>),
/// so the UI can show/hide cells without knowing the competition rules itself. Always-present
/// columns (surname, name, number, rank, coach, birth date, group, chip, actions) are intentionally
/// not listed here.
/// </summary>
public enum ParticipantColumn
{
    /// <summary>Team name (used by the rogaine discipline).</summary>
    Team
}
