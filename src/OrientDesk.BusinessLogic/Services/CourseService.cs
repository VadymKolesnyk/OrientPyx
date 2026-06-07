using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Placeholder course service returning a few in-memory samples.</summary>
public sealed class CourseService : ICourseService
{
    public Task<IReadOnlyList<Course>> GetCoursesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Course> courses =
        [
            new() { Name = "Коротка", Code = "A" },
            new() { Name = "Довга", Code = "B" }
        ];

        return Task.FromResult(courses);
    }
}
