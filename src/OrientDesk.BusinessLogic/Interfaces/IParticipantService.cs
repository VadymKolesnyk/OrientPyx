using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.BusinessLogic.Interfaces;

public interface IParticipantService
{
    Task<IReadOnlyList<Participant>> GetParticipantsAsync(CancellationToken cancellationToken = default);
}
