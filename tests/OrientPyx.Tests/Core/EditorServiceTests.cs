using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>The editing service behind the pages: participants and their day links, chips and collisions, days
/// and locks, groups, rental chips, copying between days, bulk numbers, quick withdrawal data, the dashboard,
/// the session and the competition catalog.</summary>
[Collection(AppCollection.Name)]
public sealed class EditorServiceTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;
    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();
    private static IEventStore Store => Sp.GetRequiredService<IEventStore>();

    [Fact]
    public async Task Participants_AddEditRemoveAndDeleteCascade()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("part"), dayCount: 2);
        await c.SelectAsync();
        Assert.Null(await Editor.AddParticipantToDayAsync());          // no group on the day yet
        var group = await Editor.AddGroupToDayAsync("Ч21");
        var row = await Editor.AddParticipantToDayAsync();
        Assert.NotNull(row);
        Assert.Equal(group.GroupId, row.GroupId);

        await Editor.UpdateParticipantDayRowAsync(row with { FullName = "Коваль Олег", Number = "15", Chip = "4001", Rank = "I" });
        var rows = await Editor.GetParticipantDayRowsAsync();
        var saved = Assert.Single(rows);
        Assert.Equal("Коваль Олег", saved.FullName);
        Assert.Equal("15", saved.Number);
        Assert.Equal("4001", saved.Chip);

        // Membership on day 2 through the group setter, then the roster shows both days.
        var linkId2 = await Editor.SetParticipantDayGroupAsync(row.ParticipantId, c.Day(2).Id, null);
        Assert.NotEqual(Guid.Empty, linkId2);
        var roster = await Editor.GetParticipantRosterAsync();
        Assert.Equal(2, roster.Single().Days.Count(d => d.IsMember));

        // Leaving day 2 keeps the participant; leaving the last day deletes them.
        await Editor.RemoveParticipantFromDayAsync(linkId2, row.ParticipantId);
        Assert.Single(await Editor.GetParticipantRosterAsync());
        await Editor.RemoveParticipantFromDayAsync(row.LinkId, row.ParticipantId);
        Assert.Empty(await Editor.GetParticipantRosterAsync());
        Assert.Empty(await Store.GetAllParticipantDaysAsync(c.Folder));
    }

    [Fact]
    public async Task Chips_CollisionsAreIgnoredReassignedOrSwapped()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("chips"));
        await c.AddGroupAsync("Ч21", "31");
        var a = await c.AddRunnerAsync("A", "1", "4001", "Ч21");
        var b = await c.AddRunnerAsync("B", "2", "4002", "Ч21");
        var d = await c.AddRunnerAsync("C", "3", "", "Ч21");
        await c.SelectAsync();
        var day = c.Day(1).Id;

        await Editor.SetParticipantDayChipAsync(d.Id, day, "4001");          // held by A → ignored
        Assert.Equal("", (await c.GetLinkAsync("C", 1)).Chip);
        Assert.Equal("A", await Editor.FindChipHolderAsync(day, "4001", d.Id));
        Assert.Null(await Editor.FindChipHolderAsync(day, "4001", a.Id));

        var previous = await Editor.ReassignParticipantDayChipAsync(d.Id, day, "4001");
        Assert.Equal(a.Id, previous);
        Assert.Equal("4001", (await c.GetLinkAsync("C", 1)).Chip);
        Assert.Equal("", (await c.GetLinkAsync("A", 1)).Chip);

        await Editor.SetParticipantDayChipsBatchAsync([(a.Id, day, "4005")]);
        Assert.Equal(b.Id, await Editor.SwapParticipantDayChipsAsync(a.Id, day, "4002", "4005")); // A takes B's chip, B gets A's
        Assert.Equal("4002", (await c.GetLinkAsync("A", 1)).Chip);
        Assert.Equal("4005", (await c.GetLinkAsync("B", 1)).Chip);

        Assert.Equal(2, await Editor.SetParticipantDayChipsBatchAsync([(b.Id, day, "4003"), (a.Id, day, "4004")]));
        Assert.Equal("4003", (await c.GetLinkAsync("B", 1)).Chip);
    }

    [Fact]
    public async Task RentalChips_HoldersAndDashboardCounts()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rent"));
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("A", "1", "5001", "Ч21");
        await c.AddRunnerAsync("B", "2", "", "Ч21");
        await c.SelectAsync();
        var chip = await Editor.AddRentalChipAsync();
        chip.Number = "5001";
        chip.Note = "SIAC";
        await Editor.UpdateRentalChipAsync(chip);
        var free = await Editor.AddRentalChipAsync();
        free.Number = "5002";
        await Editor.UpdateRentalChipAsync(free);

        var holders = await Editor.GetRentalChipHoldersAsync();
        Assert.Equal("A", holders["5001"]);
        Assert.False(holders.ContainsKey("5002"));
        var dashboard = await Editor.GetDashboardAsync();
        Assert.Equal(2, dashboard.ChipsTotal);
        Assert.Equal(1, dashboard.ChipsHandedOut);
        Assert.Equal(1, dashboard.ChipsFree);
        Assert.Equal(1, dashboard.WithoutChip);
        Assert.Equal(2, dashboard.ParticipantsToday);
        Assert.Equal(1, dashboard.GroupsToday);

        await Editor.DeleteRentalChipAsync(free.Id);
        Assert.Single(await Editor.GetRentalChipsAsync());
    }

    [Fact]
    public async Task Days_AddRenumberLockAndDelete()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("days"));
        await c.SelectAsync();
        var day2 = await Editor.AddDayAsync();
        Assert.Equal(2, day2.Number);
        Assert.True(Directory.Exists(Path.Combine(c.Folder, "day2")) || !Directory.Exists(Path.Combine(c.Folder, "day1")));

        var renumbered = await Editor.ChangeDayNumberAsync(day2.Id, 5);
        Assert.NotNull(renumbered);
        Assert.Equal(5, renumbered.Number);
        Assert.Equal([1, 5], (await Editor.GetDaysAsync()).Select(d => d.Number).OrderBy(x => x));

        var locked = await Editor.SetDayLockedAsync(c.Day(1).Id, true);
        Assert.True(locked!.IsLocked);
        await Sp.GetRequiredService<ISessionService>().SetCurrentDayAsync(locked);
        var ex = await Assert.ThrowsAsync<DayLockedException>(() => Editor.AddGroupToDayAsync("Ч21"));
        Assert.Equal(1, ex.DayNumber);
        Assert.Empty(await Editor.GetGroupDayRowsAsync());                   // reads still work
        var unlocked = await Editor.ToggleDayLockedAsync(c.Day(1).Id);
        Assert.False(unlocked!.IsLocked);
        await Sp.GetRequiredService<ISessionService>().SetCurrentDayAsync(unlocked);
        Assert.NotNull(await Editor.AddGroupToDayAsync("Ч21"));

        await Editor.DeleteDayAsync(day2.Id);
        Assert.Single(await Editor.GetDaysAsync());
    }

    [Fact]
    public async Task Groups_DayMembershipAndCascadeDelete()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("grp"), dayCount: 2);
        await c.SelectAsync();
        var row = await Editor.AddGroupToDayAsync("Ч21");
        Assert.Single(await Editor.GetGroupDayRowsAsync());
        Assert.Single(await Editor.GetGroupsAsync());

        Assert.False(await Editor.UpdateGroupDayRowAsync(row with { CourseOrder = "31 32", DistanceKm = 2.5m, TimeLimitSeconds = 3600 })); // true only on a merge
        var updated = (await Editor.GetGroupDayRowsAsync()).Single();
        Assert.Equal("31 32", updated.CourseOrder);
        Assert.Equal(2.5m, updated.DistanceKm);

        await Sp.GetRequiredService<ISessionService>().SetCurrentDayAsync(c.Day(2));
        Assert.Empty(await Editor.GetGroupDayRowsAsync());
        var pulled = await Editor.PullAllGroupsIntoDayAsync();
        Assert.Single(pulled);
        Assert.Equal(row.GroupId, pulled[0].GroupId);

        await Editor.RemoveGroupFromDayAsync(pulled[0].SettingsId, pulled[0].GroupId);
        Assert.Single(await Editor.GetGroupsAsync());                        // still on day 1
        await Sp.GetRequiredService<ISessionService>().SetCurrentDayAsync(c.Day(1));
        await Editor.RemoveGroupFromDayAsync(row.SettingsId, row.GroupId);
        Assert.Empty(await Editor.GetGroupsAsync());                         // on no day → deleted
    }

    [Fact]
    public async Task CopyParticipantsBetweenDays_CreatesLinksAndSkipsHeldChips()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("copy"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("A", "1", "6001", "Ч21", CompetitionBuilder.At(10, 0), days: [1]);
        await c.AddRunnerAsync("B", "2", "6002", "Ч21", days: [1]);
        await c.AddRunnerAsync("Holder", "3", "6001", "Ч21", days: [2]);
        await c.SelectAsync();
        var result = await Editor.CopyParticipantsBetweenDaysAsync(new CopyParticipantsRequest(c.Day(1).Id, c.Day(2).Id,
            new CopyParticipantsOptions(Chip: true, StartTime: true)));
        Assert.Equal(2, result.Copied);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(1, result.ChipsSkipped);                                // 6001 is held on day 2 already
        Assert.Equal("", (await c.GetLinkAsync("A", 2)).Chip);
        Assert.Equal("6002", (await c.GetLinkAsync("B", 2)).Chip);
        Assert.Equal(CompetitionBuilder.At(10, 0), (await c.GetLinkAsync("A", 2)).StartTime);

        var again = await Editor.CopyParticipantsBetweenDaysAsync(new CopyParticipantsRequest(c.Day(1).Id, c.Day(2).Id, new CopyParticipantsOptions()));
        Assert.Equal(0, again.Copied);
        Assert.Equal(2, again.AlreadyPresent);
    }

    [Fact]
    public async Task NumbersStartTimesStatusAndBonusSetters()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("setters"));
        await c.AddGroupAsync("Ч21", "31");
        var a = await c.AddRunnerAsync("A", "", "", "Ч21");
        var b = await c.AddRunnerAsync("B", "", "", "Ч21");
        await c.SelectAsync();
        var day = c.Day(1).Id;
        Assert.Equal(2, await Editor.SetParticipantNumbersBatchAsync([(a.Id, "101"), (b.Id, "102")]));
        await Editor.SetParticipantDayStartTimeAsync(a.Id, day, new TimeSpan(12, 30, 0));
        await Editor.SetParticipantDayResultStatusAsync(b.Id, day, FinishStatus.Dns);
        await Editor.SetParticipantDayBonusAsync(a.Id, day, 3);
        await Editor.SetParticipantDayOutOfCompetitionAsync(a.Id, day, true);

        var rows = await Editor.GetParticipantDayRowsAsync();
        var ra = rows.Single(r => r.FullName == "A");
        var rb = rows.Single(r => r.FullName == "B");
        Assert.Equal("101", ra.Number);
        Assert.Equal(new TimeSpan(12, 30, 0), ra.StartTime);
        Assert.Equal(3, ra.Bonus);
        Assert.True(ra.OutOfCompetition);
        Assert.Equal(FinishStatus.Dns, rb.Result.Status);

        var qw = await Editor.GetQuickWithdrawalDataAsync(day);
        var member = qw.Members.Single(m => m.Number == "102");
        Assert.Equal(FinishStatus.Dns, member.CurrentStatus);
        Assert.False(member.HasReadout);
        await Editor.SetParticipantDayResultStatusAsync(b.Id, day, null);
        Assert.Null((await Editor.GetQuickWithdrawalDataAsync(day)).Members.Single(m => m.Number == "102").CurrentStatus);
    }

    [Fact]
    public async Task CheckedIn_BatchToggleSurvivesTheRowSave()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("checkin"));
        await c.AddGroupAsync("Ч21", "31");
        var a = await c.AddRunnerAsync("A", "1", "", "Ч21");
        var b = await c.AddRunnerAsync("B", "2", "", "Ч21");
        var d = await c.AddRunnerAsync("C", "3", "", "Ч21");
        await c.SelectAsync();

        Assert.Equal(0, await Editor.SetParticipantsCheckedInAsync([], true));
        Assert.Equal(2, await Editor.SetParticipantsCheckedInAsync([a.Id, b.Id, Guid.NewGuid()], true)); // unknown id skipped
        Assert.Equal(0, await Editor.SetParticipantsCheckedInAsync([a.Id, b.Id], true));                  // already on → nothing changed
        var roster = await Editor.GetParticipantRosterAsync();
        Assert.True(roster.Single(r => r.FullName == "A").CheckedIn);
        Assert.True(roster.Single(r => r.FullName == "B").CheckedIn);
        Assert.False(roster.Single(r => r.FullName == "C").CheckedIn);

        // The debounced identity save rewrites every identity column but leaves the flag alone.
        var rowA = roster.Single(r => r.FullName == "A");
        await Editor.UpdateParticipantIdentityAsync(rowA with { FullName = "A2", CheckedIn = false });
        roster = await Editor.GetParticipantRosterAsync();
        Assert.True(roster.Single(r => r.FullName == "A2").CheckedIn);

        Assert.Equal(2, await Editor.SetParticipantsCheckedInAsync([a.Id, b.Id, d.Id], false));
        Assert.All(await Editor.GetParticipantRosterAsync(), r => Assert.False(r.CheckedIn));
    }

    [Fact]
    public async Task PaymentPerDay_SplitsAndSumsStoredPayments()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("pay"), dayCount: 2);
        var group = await c.AddGroupAsync("Ч21", "31");
        await Store.UpdateGroupEntryFeeAsync(c.Folder, group.Id, 100m);
        await c.AddRunnerAsync("A", "1", "", "Ч21", configure: p => p.Payment = "200");
        await c.SelectAsync();

        await Editor.SetPaymentPerDayAsync(true);
        var link1 = await c.GetLinkAsync("A", 1);
        var link2 = await c.GetLinkAsync("A", 2);
        Assert.Equal(200m, decimal.Parse(link1.Payment, System.Globalization.CultureInfo.InvariantCulture) + decimal.Parse(link2.Payment, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True((await Editor.GetParticipantDayRowsAsync()).Single().PaymentPerDay);

        await Editor.SetPaymentPerDayAsync(false);
        var roster = (await Editor.GetParticipantRosterAsync()).Single();
        Assert.Equal("200", roster.Payment);
        Assert.False(roster.PaymentPerDay);
    }

    [Fact]
    public async Task RegionsClubsAndInfo()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("info"));
        await c.AddGroupAsync("Ч21", "31");
        var a = await c.AddRunnerAsync("A", "1", "", "Ч21");
        await c.SelectAsync();
        var region = await Editor.AddRegionAsync("Київ");
        Assert.NotNull(region);
        Assert.Equal(region.Id, (await Editor.AddRegionAsync("київ"))!.Id);  // same name under TextKey rules → the existing one
        Assert.Single(await Editor.GetRegionsAsync());
        var club = await Editor.AddClubAsync("Клуб А");
        Assert.NotNull(club);
        await Editor.SetParticipantRegionAsync(a.Id, region.Id);
        await Editor.SetParticipantClubAsync(a.Id, club.Id);
        var row = (await Editor.GetParticipantDayRowsAsync()).Single();
        Assert.Equal("Київ", row.RegionName);
        Assert.Equal("Клуб А", row.ClubName);
        Assert.Equal(1, (await Editor.GetRegionParticipantCountsAsync())[region.Id]);

        var info = await Editor.GetInfoAsync();
        Assert.NotNull(info);
        info.ChiefJudge = "Суддя";
        info.ChiefJudgeCategory = "НК";
        await Editor.SaveInfoAsync(info);
        var data = await c.ProtocolDataAsync();
        Assert.Equal("Суддя", data.Officials.ChiefJudge);
        Assert.Equal("НК", data.Officials.ChiefJudgeCategory);
    }

    [Fact]
    public async Task SessionRestoreHiddenFlagAndRename()
    {
        var catalog = Sp.GetRequiredService<IEventCatalogService>();
        var session = Sp.GetRequiredService<ISessionService>();
        var id = TestIds.Next("sess");
        var c = await CompetitionBuilder.CreateAsync(Sp, id, dayCount: 2);
        await c.SelectAsync(2);
        Assert.True(session.HasSelection);
        Assert.Equal(2, session.CurrentDay!.Number);

        session.Clear();
        Assert.False(session.HasSelection);
        Assert.True(await session.TryRestoreLastAsync());
        Assert.Equal(id, session.CurrentEvent!.Identifier);
        Assert.Equal(2, session.CurrentDay!.Number);

        var summary = await catalog.FindByIdentifierAsync(id);
        Assert.NotNull(summary);
        Assert.False(await catalog.IsIdentifierAvailableAsync(id));
        await catalog.SetHiddenAsync(summary, true);
        Assert.True((await catalog.FindByIdentifierAsync(id))!.IsHidden);

        var newId = id + "_renamed";
        var renamed = await catalog.RenameIdentifierAsync(summary, newId);
        Assert.Equal(newId, renamed.Identifier);
        Assert.True(Directory.Exists(renamed.FolderPath));
        Assert.False(Directory.Exists(summary.FolderPath));
        Assert.Null(await catalog.FindByIdentifierAsync(id));
        Assert.NotNull(await catalog.FindByIdentifierAsync(newId));
        Assert.True(await catalog.IsIdentifierAvailableAsync(id));
        await Assert.ThrowsAnyAsync<Exception>(() => catalog.RenameIdentifierAsync(renamed, "bad/name"));
    }

    [Fact]
    public async Task Archive_ListsRequiredPluginsAndWarnsWhenMissing()
    {
        var plugins = Sp.GetRequiredService<IPluginCatalog>();
        var wasOn = plugins.IsEnabled("weighted");
        await plugins.SetEnabledAsync("weighted", true);
        try
        {
            var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("arcp"), discipline: new BusinessLogic.Disciplines.DisciplineId("weighted.relay"));
            await c.AddGroupAsync("W", "31", s => s.LegCount = 2);
            var archive = Sp.GetRequiredService<IEventArchiveService>();
            var path = Path.Combine(TestEnvironment.ResultsDir, $"{c.Summary.Identifier}.opyx");
            await Store.ReleaseAsync(c.Folder);
            await archive.ExportAsync(c.Summary.Identifier, path);

            var preview = await archive.PreviewImportAsync(path);
            Assert.Empty(preview.MissingPlugins);

            await plugins.SetEnabledAsync("weighted", false);
            var missing = await archive.PreviewImportAsync(path);
            var required = Assert.Single(missing.MissingPlugins);
            Assert.Equal("weighted", required.Id);
            Assert.Contains("weighted.relay", required.Disciplines);

            var imported = await archive.ImportAsync(path, c.Summary.Identifier + "_copy", overwrite: false);   // proceeds anyway
            Assert.Equal(c.Summary.Identifier + "_copy", imported.Competition.Identifier);
            await Assert.ThrowsAsync<InvalidOperationException>(() => archive.ImportAsync(path, c.Summary.Identifier + "_copy", overwrite: false));
            await Assert.ThrowsAsync<ArgumentException>(() => archive.ImportAsync(path, "bad/id", overwrite: true));
        }
        finally
        {
            await plugins.SetEnabledAsync("weighted", wasOn);
        }
    }

    [Fact]
    public async Task UnknownDisciplineWhenThePluginIsOff()
    {
        var plugins = Sp.GetRequiredService<IPluginCatalog>();
        var wasOn = plugins.IsEnabled("missed-penalty");
        await plugins.SetEnabledAsync("missed-penalty", false);
        try
        {
            var id = new BusinessLogic.Disciplines.DisciplineId("missed-penalty.set-course");
            var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("unk"), discipline: id);
            await c.AddControlsAsync(1, "31", "32");
            await c.AddGroupAsync("Ч21", "31 32");
            await c.AddRunnerAsync("A", "1", "1001", "Ч21", CompetitionBuilder.At(10, 0));
            await c.AddReadoutAsync(1, "1001", "31 32", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 20));
            var r = await c.ResultsAsync();
            Assert.Equal(FinishStatus.Ok, r["A"].Status);                   // judged like a set course
            Assert.Null(r["A"].Place);                                       // but never placed
            var provider = Sp.GetRequiredService<BusinessLogic.Disciplines.IDisciplineStrategyProvider>();
            Assert.True(provider.For(id).IsUnknown);
            Assert.Contains(id, await Editor.GetDayDisciplinesAsync(c.Day(1).Id));
        }
        finally
        {
            await plugins.SetEnabledAsync("missed-penalty", wasOn);
        }
    }

    [Fact]
    public async Task MapCounts_PerGroupPerDayWithNoGroupRow()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("maps"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddGroupAsync("Ж21", "31");
        await c.AddRunnerAsync("A", "1", "4001", "Ч21");                       // both days
        await c.AddRunnerAsync("B", "2", "4002", "Ч21", days: [1]);            // day 1 only
        await c.AddRunnerAsync("C", "3", "4003", "Ж21", days: [2]);            // day 2 only
        await c.AddRunnerAsync("D", "4", "4004", null, days: [1]);             // no group on day 1
        // Ж21 does not run on day 1 at all → a null (not a zero) there.
        var zh1 = await c.GetGroupSettingsAsync("Ж21", 1);
        await Store.DeleteGroupDaySettingsAsync(c.Folder, zh1.Id);
        await c.SelectAsync();

        var data = await Editor.GetMapCountsAsync();
        Assert.Equal([1, 2], data.Days.Select(d => d.Number));
        Assert.Equal(3, data.Rows.Count);

        var ch = data.Rows[0];
        Assert.Equal("Ч21", ch.GroupName);
        Assert.Equal([2, 1], ch.Counts);

        var zh = data.Rows[1];
        Assert.Equal("Ж21", zh.GroupName);
        Assert.Equal([null, 1], zh.Counts);

        var noGroup = data.Rows[2];
        Assert.Null(noGroup.GroupId);
        Assert.Equal([1, null], noGroup.Counts);
    }
}
