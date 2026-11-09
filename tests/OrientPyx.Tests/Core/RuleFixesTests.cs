using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The rules and data-safety fixes that came out of writing the user documentation (2026-11-09): disabled controls
/// and the required count, rogaine team names, the time sum with an out-of-competition day, imports that never clear
/// a field with a blank, day deletion, the finish auto-read writing into its own day, the lock of the link's own day,
/// taken start numbers and group-name clashes.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RuleFixesTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();

    private static IEventStore Store => Sp.GetRequiredService<IEventStore>();

    [Fact]
    public async Task ScoreByCount_ADisabledControlLowersTheRequiredCount()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("cntdis"), discipline: DisciplineId.ScoreByCount);
        var cps = await c.AddControlsAsync(1, "31", "32", "33", "34");
        await c.AddGroupAsync("Ч21", "31 32 33 34", s => s.RequiredControlCount = 3);
        await c.AddRunnerAsync("Two", "1", "1001", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 32", T(10, 0), T(10, 20));

        Assert.Equal(FinishStatus.Mp, (await c.ResultsAsync())["Two"].Status);  // 2 of 3

        var broken = cps.Single(p => p.Code == "34");
        broken.IsDisabled = true;                                              // «проблемний»: nobody can take it
        await Store.UpdateControlPointAsync(c.Folder, broken);
        Assert.Equal(FinishStatus.Ok, (await c.ResultsAsync())["Two"].Status);  // now 2 of 2
    }

    [Fact]
    public async Task Rogaine_TeamNamesMatchTheWayTheProtocolGroupsThem()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rogcase"), discipline: DisciplineId.Rogaine);
        var cps = await c.AddControlsAsync(1, "31", "42");
        foreach (var cp in cps) { cp.Points = int.Parse(cp.Code[..1]); await Store.UpdateControlPointAsync(c.Folder, cp); }
        await c.AddGroupAsync("Open", "31 42", s => s.TimeLimitSeconds = 60 * 60);
        await c.AddRunnerAsync("A1", "1", "1001", "Open", T(10, 0), p => p.Team = "Альфа");
        await c.AddRunnerAsync("A2", "2", "1002", "Open", T(10, 0), p => p.Team = " альфа ");
        await c.AddReadoutAsync(1, "1001", "31 42", T(10, 0), T(10, 40));
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 41));

        var r = await c.ResultsAsync();
        // One team: only the common control 31 counts, for both, and they share the place.
        Assert.Equal(3, r["A1"].Score);
        Assert.Equal(3, r["A2"].Score);
        Assert.Equal(1, r["A1"].Place);
        Assert.Equal(1, r["A2"].Place);
    }

    [Fact]
    public async Task SummaryByTime_ADayOutOfCompetitionKeepsTheTimeButNotThePlace()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("sumpk"), dayCount: 2);
        await c.AddControlsAsync(1, "31");
        await c.AddControlsAsync(2, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Fair", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Guest", "2", "1002", "Ч21", T(10, 0), configureDay: d => d.OutOfCompetition = d.EventDayId == c.Day(2).Id);
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 15));
        await c.AddReadoutAsync(2, "1001", "31", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(2, "1002", "31", T(10, 0), T(10, 15));

        await c.SelectAsync();
        var data = await Editor.GetSummaryProtocolDataAsync();
        var groups = Sp.GetRequiredService<ISummaryProtocolBuilder>()
            .RankGroups(data, new SummaryProtocolSettings { Mode = SummaryMode.ByTime });
        var entries = Assert.Single(groups).Entries.ToDictionary(e => e.Member.FullName);
        Assert.Equal(1, entries["Fair"].Place);
        Assert.Null(entries["Guest"].Place);                        // faster, but day 2 was поза конкурсом
        Assert.Equal("00:30:00", entries["Guest"].TotalText);       // the time still shows
    }

    [Fact]
    public async Task Import_ABlankFieldNeverClearsAStoredOne()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("impblank"));
        await c.AddGroupAsync("Ч21", "31");
        await c.SelectAsync();
        UofParticipantData File(string rank, string coach, string club, DateTimeOffset? birth, bool member) => new()
        {
            Participants =
            [
                new UofParticipant
                {
                    FullName = "Петренко Іван", FsouCode = "10234", Group = "Ч21", DayNumbers = [1],
                    Rank = rank, Coach = coach, Club = club, BirthDate = birth, IsFsouMember = member,
                },
            ],
        };

        var born = new DateTimeOffset(1995, 3, 1, 0, 0, 0, TimeSpan.FromHours(3));
        await Editor.ImportParticipantsAsync(File("КМСУ", "Довженко Т.", "Азимут", born, member: true), clearFirst: false, ParticipantImportScope.AllDays);
        // The same person from a file without those columns: nothing is cleared, membership stays.
        await Editor.ImportParticipantsAsync(File("", "", "", null, member: false), clearFirst: false, ParticipantImportScope.AllDays);

        var p = Assert.Single(await Store.GetParticipantsAsync(c.Folder));
        Assert.Equal("КМСУ", p.Rank);
        Assert.Equal("Довженко Т.", p.Coach);
        Assert.Equal(born.Date, p.BirthDate!.Value.Date);
        Assert.True(p.IsFsouMember);
        Assert.Equal("Азимут", (await Editor.GetClubsAsync()).Single(x => x.Id == p.ClubId).Name);

        // A value in the file still updates.
        await Editor.ImportParticipantsAsync(File("I", "", "Компас", null, member: false), clearFirst: false, ParticipantImportScope.AllDays);
        p = Assert.Single(await Store.GetParticipantsAsync(c.Folder));
        Assert.Equal("I", p.Rank);
        Assert.Equal("Компас", (await Editor.GetClubsAsync()).Single(x => x.Id == p.ClubId).Name);
    }

    [Fact]
    public async Task DeletingADayDeletesEverythingOfIt()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("delday"), dayCount: 2);
        await c.AddControlsAsync(2, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Both", "1", "1001", "Ч21", T(10, 0));
        await c.AddReadoutAsync(2, "1001", "31", T(10, 0), T(10, 20));
        var files = Sp.GetRequiredService<IEventFiles>();
        await files.EnsureDayFolderAsync(c.Folder, 2);
        var day2 = c.Day(2).Id;

        await c.SelectAsync(1);
        await Editor.DeleteDayAsync(day2);

        Assert.Empty(await Store.GetParticipantDaysAsync(c.Folder, day2));
        Assert.Empty(await Store.GetFinishReadoutsAsync(c.Folder, day2));
        Assert.Empty(await Store.GetControlPointsAsync(c.Folder, day2));
        Assert.Empty(await Store.GetGroupDaySettingsAsync(c.Folder, day2));
        Assert.False(Directory.Exists(Path.Combine(c.Folder, "day2")));
        Assert.Single(await Store.GetParticipantsAsync(c.Folder));             // the person stays (day 1)
    }

    [Fact]
    public async Task TheFinishImportWritesIntoTheDayItNames()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ownday"), dayCount: 2);
        await c.AddControlsAsync(2, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Runner", "1", "1001", "Ч21", T(10, 0));
        await c.SelectAsync(1);                                                 // the pages show day 1…

        var read = new ChipReadData
        {
            Records =
            [
                new ChipReadRecord
                {
                    ChipNumber = "1001",
                    StartTime = c.Date.AddDays(1) + T(10, 0),
                    FinishTime = c.Date.AddDays(1) + T(10, 20),
                    Punches = [new ChipPunch("31", c.Date.AddDays(1) + T(10, 10))],
                    ReadMark = "1",
                },
            ],
        };
        var result = await Editor.ImportFinishReadoutsAsync(read, forDayId: c.Day(2).Id);   // …the watch reads day 2

        Assert.Equal(1, result.Added);
        Assert.Empty(await Store.GetFinishReadoutsAsync(c.Folder, c.Day(1).Id));
        Assert.Single(await Store.GetFinishReadoutsAsync(c.Folder, c.Day(2).Id));
        var rows = await Editor.GetFinishReadoutRowsAsync(forDayId: c.Day(2).Id);
        Assert.Equal("Runner", Assert.Single(rows).FullName);
        Assert.Empty(await Editor.GetFinishReadoutRowsAsync());                 // the session's day 1 is untouched
    }

    [Fact]
    public async Task LeavingADayChecksThatDaysLockNotTheSessions()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("leavelock"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        var runner = await c.AddRunnerAsync("Runner", "1", "1001", "Ч21", T(10, 0));
        var day2 = c.Day(2);
        day2.IsLocked = true;
        await Store.UpdateDayAsync(c.Folder, day2);
        var link2 = await c.GetLinkAsync("Runner", 2);
        await c.SelectAsync(1);                                                 // an open day in the session

        await Assert.ThrowsAsync<DayLockedException>(() => Editor.RemoveParticipantFromDayAsync(link2.Id, runner.Id));
        Assert.Equal(2, await Editor.CountParticipantDaysAsync(runner.Id));

        var link1 = await c.GetLinkAsync("Runner", 1);
        await Editor.RemoveParticipantFromDayAsync(link1.Id, runner.Id);
        Assert.Equal(1, await Editor.CountParticipantDaysAsync(runner.Id));
        await Assert.ThrowsAsync<DayLockedException>(() => Editor.DeleteParticipantAsync(runner.Id));
    }

    [Fact]
    public async Task TakenNumbersAndGroupNamesAreFound()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("clash"));
        await c.AddGroupAsync("Ч21", "31");
        var m21b = await c.AddGroupAsync("M21B", "31");
        var a = await c.AddRunnerAsync("Перший Іван", "101", "1001", "Ч21", T(10, 0));
        var b = await c.AddRunnerAsync("Другий Петро", "102", "1002", "Ч21", T(10, 1));
        await c.SelectAsync();

        var conflict = await Editor.FindNumberConflictAsync(b.Id, " 101 ");
        Assert.Equal("Перший Іван", conflict!.HolderName);
        Assert.Equal("102", conflict.KeptNumber);
        Assert.Null(await Editor.FindNumberConflictAsync(a.Id, "101"));         // their own number
        Assert.Null(await Editor.FindNumberConflictAsync(b.Id, "103"));

        var clash = await Editor.FindGroupNameClashAsync(m21b.Id, "ч21");      // case and look-alikes count
        Assert.Equal("Ч21", clash!.ExistingName);
        Assert.Equal("M21B", clash.CurrentName);
        Assert.Null(await Editor.FindGroupNameClashAsync(m21b.Id, "Ч35"));
    }
}
