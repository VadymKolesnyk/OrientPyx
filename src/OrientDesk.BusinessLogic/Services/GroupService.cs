using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Placeholder group service returning a few in-memory samples.</summary>
public sealed class GroupService : IGroupService
{
    public Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Group> groups =
        [
            new() { Name = "Чоловіки 21", Code = "M21" },
            new() { Name = "Жінки 21", Code = "W21" }
        ];

        return Task.FromResult(groups);
    }
}
