using OrientPyx.BusinessLogic.Disciplines;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A day a participant import put relay teams on while the day's own type is not the relay (only its relay groups run
/// it, by their own type) — offered to the user to make a relay day (<c>ICompetitionEditorService.SwitchDayToRelayAsync</c>).
/// </summary>
/// <param name="Current">The day's type now.</param>
/// <param name="KeptGroups">The day's groups that do not run the relay — they keep <paramref name="Current"/> as their own.</param>
public sealed record RelayDaySwitch(Guid DayId, int DayNumber, DisciplineId Current, IReadOnlyList<string> KeptGroups);
