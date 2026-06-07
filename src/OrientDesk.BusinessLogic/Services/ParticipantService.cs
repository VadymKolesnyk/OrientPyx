using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Placeholder participant service returning a few in-memory samples.</summary>
public sealed class ParticipantService : IParticipantService
{
    public Task<IReadOnlyList<Participant>> GetParticipantsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Participant> participants =
        [
            new() { Name = "Іван Петренко", ChipNumber = "8000123" },
            new() { Name = "Олена Коваль", ChipNumber = "8000124" },
            new() { Name = "Андрій Шевченко", ChipNumber = "8000125" }
        ];

        return Task.FromResult(participants);
    }
}
