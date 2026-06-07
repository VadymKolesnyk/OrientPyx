using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.BusinessLogic.Interfaces;

public interface ICourseService
{
    Task<IReadOnlyList<Course>> GetCoursesAsync(CancellationToken cancellationToken = default);
}
