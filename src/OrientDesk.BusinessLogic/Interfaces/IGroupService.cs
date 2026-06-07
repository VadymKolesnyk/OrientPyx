using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.BusinessLogic.Interfaces;

public interface IGroupService
{
    Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken = default);
}
