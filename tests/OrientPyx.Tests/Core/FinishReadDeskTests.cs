using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// What the finish desk sees around the read-out log: the head count under the table (read out / still on course /
/// unknown chips) and each runner's start by the start protocol in the read-out edit modal.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class FinishReadDeskTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    [Fact]
    public async Task Log_CountsRunnersReadOnCourseAndUnknownChips()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rcount"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Read", "1", "7001", "Ч21", CompetitionBuilder.At(10, 0));
        await c.AddRunnerAsync("Out", "2", "7002", "Ч21", CompetitionBuilder.At(10, 1));
        await c.AddRunnerAsync("Withdrawn", "3", "7003", "Ч21", CompetitionBuilder.At(10, 2),
            configureDay: l => l.ResultStatusOverride = FinishStatus.Dns);
        // A runner who reads twice counts once; so does an unknown chip read twice.
        await c.AddReadoutAsync(1, "7001", "31", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 20));
        await c.AddReadoutAsync(1, "7001", "31", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 21));
        await c.AddReadoutAsync(1, "7999", "31", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 25));
        await c.AddReadoutAsync(1, "7999", "31", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 26));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        var log = await editor.GetFinishReadoutLogAsync();

        Assert.Equal(4, log.Rows.Count);
        Assert.Equal(new FinishReadoutCounts(ReadOut: 1, OnCourse: 1, Unknown: 1), log.Counts);
        // The withdrawn runner is not «on course»; the figure matches the dashboard tile.
        Assert.Equal(log.Counts.OnCourse, (await editor.GetDashboardAsync()).OnCourse);
    }

    [Fact]
    public async Task EditModal_ShowsTheHoldersProtocolStart_AndFollowsThePickedHolder()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rpstart"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        var holder = await c.AddRunnerAsync("Holder", "1", "7001", "Ч21", CompetitionBuilder.At(10, 24));
        var undrawn = await c.AddRunnerAsync("Undrawn", "2", "", "Ч21");
        var readout = await c.AddReadoutAsync(1, "7001", "31", CompetitionBuilder.At(10, 25), CompetitionBuilder.At(10, 50));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var localization = Sp.GetRequiredService<ILocalizationService>();

        var data = await editor.GetFinishReadoutEditAsync(readout.Id);
        Assert.NotNull(data);
        Assert.Equal(CompetitionBuilder.At(10, 24), data.Participants.Single(p => p.ParticipantId == holder.Id).StartTime);
        Assert.Null(data.Participants.Single(p => p.ParticipantId == undrawn.Id).StartTime);

        var vm = new FinishReadoutEditViewModel(localization, data);
        Assert.True(vm.ShowProtocolStart);
        Assert.Equal(string.Format(localization.Get("FinishRead.Edit.ProtocolStart"), "10:24:00"), vm.ProtocolStartText);

        vm.SelectedParticipant = vm.Participants.Single(o => o.ParticipantId == undrawn.Id);
        Assert.Equal(
            string.Format(localization.Get("FinishRead.Edit.ProtocolStart"), localization.Get("FinishRead.Edit.ProtocolStartNone")),
            vm.ProtocolStartText);
    }
}
