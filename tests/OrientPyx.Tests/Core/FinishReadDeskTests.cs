using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
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

    [Fact]
    public async Task EditModal_BonusInScoringGroups_AndTheLogSpellsTheSumOut()
    {
        // A score-by-time day: «Ч21» scores points, «Ж21» runs a set course (no bonus there).
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rbonus"), discipline: DisciplineId.ScoreByTime);
        var cps = await c.AddControlsAsync(1, "31", "32");
        var store = Sp.GetRequiredService<IEventStore>();
        foreach (var cp in cps) { cp.Points = 5; await store.UpdateControlPointAsync(c.Folder, cp); }
        var men = await c.AddGroupAsync("Ч21", "31 32", s => s.TimeLimitSeconds = 60 * 60);
        var women = await c.AddGroupAsync("Ж21", "31 32", s => s.DisciplineOverride = DisciplineId.SetCourse);
        var holder = await c.AddRunnerAsync("Holder", "1", "7001", "Ч21", CompetitionBuilder.At(10, 0));
        var other = await c.AddRunnerAsync("Other", "2", "", "Ж21", CompetitionBuilder.At(10, 0), configureDay: d => d.Bonus = 7);
        var readout = await c.AddReadoutAsync(1, "7001", "31 32", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 20));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var localization = Sp.GetRequiredService<ILocalizationService>();

        var data = await editor.GetFinishReadoutEditAsync(readout.Id);
        Assert.NotNull(data);
        Assert.Contains(men.Id, data.BonusGroupIds);
        Assert.DoesNotContain(women.Id, data.BonusGroupIds);
        Assert.Equal(7, data.Participants.Single(p => p.ParticipantId == other.Id).Bonus);

        // The field follows the holder and their group; an untouched field sends nothing.
        var vm = new FinishReadoutEditViewModel(localization, data);
        Assert.True(vm.ShowBonus);
        Assert.Equal("", vm.BonusText);
        vm.SelectedParticipant = vm.Participants.Single(o => o.ParticipantId == other.Id);
        Assert.False(vm.ShowBonus);                       // «Ж21» scores no points
        Assert.Equal("7", vm.BonusText);
        vm.HolderGroup = vm.Groups.Single(g => g.Id == men.Id);
        Assert.True(vm.ShowBonus);                        // moved into a scoring group
        vm.SelectedParticipant = vm.Participants.Single(o => o.ParticipantId == holder.Id);
        vm.ConfirmCommand.Execute(null);
        Assert.False((await vm.Completion)!.SetsBonus);

        // A typed bonus lands on the holder: the results add it, the log shows «X (Y − Z)».
        vm = new FinishReadoutEditViewModel(localization, data) { BonusText = "-3" };
        vm.ConfirmCommand.Execute(null);
        var edit = (await vm.Completion)!;
        Assert.True(edit.SetsBonus);
        Assert.Equal(-3, edit.Bonus);
        await editor.UpdateFinishReadoutAsync(edit);

        Assert.Equal(-3, (await c.GetLinkAsync("Holder", 1)).Bonus);
        Assert.Equal(7, (await c.ResultsAsync())["Holder"].Score);
        var row = (await editor.GetFinishReadoutRowsAsync()).Single(r => r.Id == readout.Id);
        Assert.Equal(10, row.Score);
        Assert.Equal(-3, row.Bonus);
        var cell = new FinishReadRowViewModel(row, localization);
        Assert.Equal("7 (10 − 3)", cell.ScoreText);
        Assert.Equal(7, cell.ScoreSort);
        Assert.Equal("12 (10 + 2)", new FinishReadRowViewModel(row with { Bonus = 2 }, localization).ScoreText);
        // No bonus (or a zero one) — just the total.
        Assert.Equal("10", new FinishReadRowViewModel(row with { Bonus = null }, localization).ScoreText);
        Assert.Equal("10", new FinishReadRowViewModel(row with { Bonus = 0 }, localization).ScoreText);
    }
}
