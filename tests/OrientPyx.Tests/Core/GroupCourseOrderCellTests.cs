using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The «Порядок / список КП» cell on «Групи»: a group whose courses live in the bottom table (scatter variants, a relay
/// with leg courses) reads «(різні)», is not editable and counts the longest course; every other group edits its order.
/// </summary>
public sealed class GroupCourseOrderCellTests
{
    private static GroupDayRowViewModel Row(DisciplineId discipline, RelayCourseMode mode = RelayCourseMode.Group)
    {
        var sp = TestEnvironment.Instance.Services;
        var loc = sp.GetRequiredService<ILocalizationService>();
        var row = new GroupDayRow(
            Guid.NewGuid(), Guid.NewGuid(), 0, "Ч21", "31 32 33", null, null, discipline, null, null, null,
            ScatterVariantCount: 2, ScatterMaxControlCount: 5, CourseMode: mode);
        return new GroupDayRowViewModel(row, loc, sp.GetRequiredService<IDisciplineStrategyProvider>(),
            [PointsRuleOption.Default(loc, null)], string.Empty, string.Empty, (_, _) => { });
    }

    private static string Several =>
        TestEnvironment.Instance.Services.GetRequiredService<ILocalizationService>().Get("Groups.Col.CourseOrder.Several");

    [Theory]
    [InlineData("SetCourse", RelayCourseMode.Group)]
    [InlineData("Mixed", RelayCourseMode.Group)]
    [InlineData("Rogaine", RelayCourseMode.Group)]
    [InlineData("Relay", RelayCourseMode.Group)]
    public void SingleOrder_IsEditable(string discipline, RelayCourseMode mode)
    {
        var vm = Row(new DisciplineId(discipline), mode);

        Assert.True(vm.CanEditCourseOrderInline);
        Assert.Equal("31 32 33", vm.CourseOrderDisplay);
        Assert.Equal("3", vm.ControlCountText);
    }

    [Theory]
    [InlineData("Scatter", RelayCourseMode.Group)]
    [InlineData("Relay", RelayCourseMode.ByBib)]
    [InlineData("Relay", RelayCourseMode.TeamSet)]
    [InlineData("Relay", RelayCourseMode.PerLeg)]
    public void SeveralCourses_ReadVariousAndAreLocked(string discipline, RelayCourseMode mode)
    {
        var vm = Row(new DisciplineId(discipline), mode);

        Assert.False(vm.CanEditCourseOrderInline);
        Assert.Equal(Several, vm.CourseOrderDisplay);
        Assert.NotEqual("Groups.Col.CourseOrder.Several", vm.CourseOrderDisplay);
        Assert.Equal("5", vm.ControlCountText);
        Assert.False(vm.HasUnknownCodes);
    }

    [Fact]
    public void RelayModeSwitch_FollowsInPlace()
    {
        var vm = Row(DisciplineId.Relay);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ShowRelayCourseMode(RelayCourseMode.PerLeg);

        Assert.False(vm.CanEditCourseOrderInline);
        Assert.Equal(Several, vm.CourseOrderDisplay);
        Assert.Contains(nameof(GroupDayRowViewModel.CourseOrderDisplay), raised);
        Assert.Contains(nameof(GroupDayRowViewModel.CanEditCourseOrderInline), raised);

        vm.ShowRelayCourseMode(RelayCourseMode.Group);

        Assert.True(vm.CanEditCourseOrderInline);
        Assert.Equal("31 32 33", vm.CourseOrderDisplay);
    }
}
