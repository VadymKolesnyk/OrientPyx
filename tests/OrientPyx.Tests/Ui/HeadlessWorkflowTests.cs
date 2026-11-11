using System.Reflection;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Judge's workflows driven through the real view models on the headless app: creating a competition from the
/// picker, adding and locking days, adding groups / controls / chips / participants, auto-reading a chip file on the
/// finish page, generating the protocol documents, creating relay teams, switching the read-out type.
/// Each test opens the competition it needs, so the order of tests does not matter.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class HeadlessWorkflowTests
{
    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public HeadlessWorkflowTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task CreateCompetitionFromThePicker()
    {
        var id = TestIds.Next("ui_create");
        await GoToPickerAsync();
        var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
        await _app.OnUiAsync(() => selection.CreateCommand.Execute(null));
        await _app.SettleAsync();
        var create = Assert.IsType<CreateEventViewModel>(_app.Vm.CurrentView);
        await _app.ScreenshotAsync("10-create-empty");

        await _app.OnUiAsync(async () =>
        {
            create.Name = "Кубок тесту";
            create.Identifier = id;
            create.Venue = "Ліс";
            create.DayCount = 2;
            await create.CreateCommand.ExecuteAsync(null);
        });
        // The new competition opens straight away on its first day.
        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected && Session().CurrentEvent?.Identifier == id,
            what: "the new competition opened");
        await _app.SettleAsync();
        Assert.Equal("Кубок тесту", Session().CurrentEvent!.Name);
        Assert.Equal(1, Session().CurrentDay!.Number);
        Assert.IsType<ShellViewModel>(_app.Vm.CurrentView);
        await _app.ScreenshotAsync("11-create-opened");

        var summary = await TestEnvironment.Instance.GetEventAsync(id);
        Assert.Equal(2, (await Core.GetRequiredService<IEventStore>().GetDaysAsync(summary.FolderPath)).Count);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task CreateRefusesABlankOrTakenIdentifier()
    {
        await GoToPickerAsync();
        var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
        await _app.OnUiAsync(() => selection.CreateCommand.Execute(null));
        await _app.SettleAsync();
        var create = (CreateEventViewModel)_app.Vm.CurrentView;
        await _app.OnUiAsync(async () =>
        {
            create.Name = "x";
            create.Identifier = "";
            await create.CreateCommand.ExecuteAsync(null);
        });
        Assert.False(string.IsNullOrEmpty(create.ErrorMessage));
        await _app.OnUiAsync(() => create.Identifier = TestEnvironment.Instance.SyntheticId);
        await _app.WaitUntilAsync(() => create.IdentifierError is not null, what: "duplicate identifier flagged");
        await _app.OnUiAsync(() => create.CancelCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.CurrentView is EventSelectionViewModel, what: "cancel returns to the picker");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task DaysPageAddsAndLocksADay()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_days"));
        await OpenCompetitionAsync(c.Summary.Identifier);
        await _app.OnUiAsync(() => _app.Vm.OpenCompetitionDaysCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<CompetitionDaysViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Days.Count == 1, what: "one day listed");

        await _app.OnUiAsync(() => page.AddDayCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.Days.Count == 2, what: "second day added");
        var day2 = page.Days.Single(d => d.Number == 2);
        await _app.OnUiAsync(() => page.ToggleDayLockCommand.ExecuteAsync(day2));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.Days.Single(d => d.Number == 2).IsLocked, what: "day 2 locked");
        await _app.ScreenshotAsync("12-days-locked");

        var store = Core.GetRequiredService<IEventStore>();
        var days = await store.GetDaysAsync(c.Folder);
        Assert.True(days.Single(d => d.Number == 2).IsLocked);
        Assert.False(days.Single(d => d.Number == 1).IsLocked);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task GroupsControlsChipsAndParticipantsPagesAddRows()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_rows"));
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenGroupsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var groups = Assert.IsType<GroupsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => groups.AddGroupCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => groups.Groups.Count == 1, what: "group row added");
        await _app.OnUiAsync(() => { groups.Groups[0].Name = "Ч21"; groups.Groups[0].CourseOrder = "31 32"; });
        await Task.Delay(900);                                             // debounced save
        await _app.SettleAsync();
        await _app.ScreenshotAsync("13-groups-row");

        await _app.OnUiAsync(() => _app.Vm.OpenControlPointsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var points = Assert.IsType<ControlPointsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => points.AddPointCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => points.Points.Count == 1, what: "control point added");
        await _app.OnUiAsync(() => points.Points[0].Code = "31");
        await Task.Delay(900);
        await _app.SettleAsync();

        await _app.OnUiAsync(() => _app.Vm.OpenChipsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var chips = Assert.IsType<ChipsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => chips.AddChipCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => chips.Chips.Count == 1, what: "rental chip added");
        await _app.OnUiAsync(() => chips.Chips[0].Number = "7000");
        await Task.Delay(900);
        await _app.SettleAsync();

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (participants.IsRosterMode)
        {
            // The page opens on the roster; pick the real day in the day dropdown to edit the day grid.
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => !d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsDayMode, what: "participants page in day mode");
        await _app.OnUiAsync(() => participants.AddParticipantCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => participants.Participants.Count == 1, what: "participant row added");
        await _app.OnUiAsync(() =>
        {
            participants.Participants[0].FullName = "Тестовий Учасник";
            participants.Participants[0].Number = "77";
            participants.Participants[0].Chip = "7000";
        });
        await Task.Delay(900);
        await _app.SettleAsync();
        await _app.ScreenshotAsync("14-participant-row");

        // What the pages wrote, read back through the core services.
        await c.SelectAsync();
        var editor = Core.GetRequiredService<ICompetitionEditorService>();
        var groupRows = await editor.GetGroupDayRowsAsync();
        Assert.Equal("Ч21", Assert.Single(groupRows).Name);
        Assert.Equal("31 32", groupRows[0].CourseOrder);
        Assert.Equal("31", Assert.Single(await editor.GetControlPointsAsync()).Code);
        Assert.Equal("7000", Assert.Single(await editor.GetRentalChipsAsync()).Number);
        var row = Assert.Single(await editor.GetParticipantDayRowsAsync());
        Assert.Equal("Тестовий Учасник", row.FullName);
        Assert.Equal("77", row.Number);
        Assert.Equal("7000", row.Chip);
        Assert.Equal("Ч21", row.GroupName);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task ParticipantsPageScansChipsOntoRowsAndOpensTheStatement()
    {
        // Two chip-less runners; the dialog is opened on the day grid, a chip is "scanned" through the manual
        // box, applied, and the statement opens on the same rows. The rental base gains the scanned chip.
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_scan"));
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Сканований Перший", "1", "", "Ч21");
        await c.AddRunnerAsync("Сканований Другий", "2", "", "Ч21");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => !d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsDayMode && participants.Participants.Count == 2, what: "two day rows");
        await _app.SettleAsync();

        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 2, what: "day table shows both rows");

        // A taller window so the whole dialog (set-up, file row, counters, table, footer) is in the screenshots.
        var (width, height) = await _app.OnUiAsync(() => (_app.Window.Width, _app.Window.Height));
        await _app.OnUiAsync(() => { _app.Window.Width = 1100; _app.Window.Height = 900; });
        await _app.SettleAsync();

        // The command awaits the dialog, so it runs detached; the dialog itself is reached through the overlay.
        _ = _app.OnUiAsync(() => participants.ScanChipsAsync(table));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is ChipScanViewModel, what: "chip-scan dialog open");
        await _app.SettleAsync();
        var scan = (ChipScanViewModel)_app.Vm.Dialogs.Current!;
        Assert.Equal(2, scan.Rows.Count);
        await _app.ScreenshotAsync("15-chip-scan-open");

        await _app.OnUiAsync(() =>
        {
            scan.ManualNumber = "7101";
            scan.SubmitManualCommand.Execute(null);
        });
        await _app.SettleAsync();
        Assert.Equal("7101", scan.Rows[0].Chip);
        Assert.True(scan.Rows[0].AddToBase);
        Assert.Same(scan.Rows[1], scan.SelectedRow);
        await _app.ScreenshotAsync("16-chip-scan-one-assigned");

        await _app.OnUiAsync(() => scan.ApplyCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is StatementViewModel, what: "statement opened after apply");
        await _app.SettleAsync();
        await _app.ScreenshotAsync("17-chip-scan-statement");
        var statement = (StatementViewModel)_app.Vm.Dialogs.Current!;
        await _app.OnUiAsync(() => statement.CloseCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "statement closed");
        await _app.OnUiAsync(() => { _app.Window.Width = width; _app.Window.Height = height; });
        await _app.SettleAsync();

        await c.SelectAsync();
        var editor = Core.GetRequiredService<ICompetitionEditorService>();
        Assert.Equal("7101", Assert.Single(await editor.GetRentalChipsAsync()).Number);
        var rows = await editor.GetParticipantDayRowsAsync();
        Assert.Equal("7101", rows.Single(r => r.FullName == "Сканований Перший").Chip);
        Assert.Equal("", rows.Single(r => r.FullName == "Сканований Другий").Chip);
        Assert.NotNull(await editor.GetChipScanSettingsAsync());   // the set-up is saved on apply
        AssertNoUiErrors();
    }

    [Fact]
    public async Task ImportPreviewDialogRendersThreeBlocksAndReturnsTheDecision()
    {
        // The dialog alone, on a synthetic preview: the three blocks, select-all / none, and the decision.
        await EnsureAnyCompetitionOpenAsync();
        var localization = Core.GetRequiredService<ILocalizationService>();
        var kovalId = Guid.NewGuid();
        var preview = new ImportPreview(
            [new ImportPreviewAdded(0, "Новий Учасник", "Ч21", 2008, "Орієнтир", "Київ", [1])],
            [new ImportPreviewUpdated(1, kovalId, "Коваль Олег", "Ч21",
                [new ImportPreviewChange("Club", null, "", "Орієнтир"), new ImportPreviewChange("Chip", 1, "", "8001"),
                 new ImportPreviewChange("Day", 2, "", ""), new ImportPreviewChange("Payment", null, "100", "250")])],
            [new ImportPreviewMissing(Guid.NewGuid(), "Петренко Іван", "Ч21", 2001, "", "Львів", [1, 2])]);

        var vm = new ImportPreviewViewModel(localization, preview);
        var shown = _app.OnUiAsync(() => _app.Vm.Dialogs.ShowImportPreviewAsync(vm));
        await _app.WaitUntilAsync(() => ReferenceEquals(_app.Vm.Dialogs.Current, vm), what: "preview dialog open");
        await _app.SettleAsync();
        await _app.ScreenshotAsync("18-import-preview");

        await _app.OnUiAsync(() =>
        {
            vm.DeselectAllAddedCommand.Execute(null);
            vm.SelectAllMissingCommand.Execute(null);
            Assert.True(vm.HasPaymentChanges);
            vm.UpdatePayment = false;
            Assert.DoesNotContain(vm.Updated.Single().Changes, line => line.Contains("100"));
            vm.ConfirmCommand.Execute(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "preview dialog closed");
        var decision = await vm.Completion;
        Assert.NotNull(decision);
        Assert.Equal([0], decision.ExcludedRows.OrderBy(x => x));
        Assert.Single(decision.DeleteParticipantIds);
        Assert.False(decision.UpdatePayment);
        await shown;
        AssertNoUiErrors();
    }

    [Fact]
    public async Task FinishPageAutoReadsAChipFile()
    {
        var env = TestEnvironment.Instance;
        await OpenCompetitionAsync(env.SyntheticId);
        await _app.OnUiAsync(() => _app.Vm.OpenFinishReadCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<FinishReadViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Readouts.Count > 0, what: "existing read-outs listed");
        var before = page.Readouts.Count;

        var file = Path.Combine(TestEnvironment.ResultsDir, "autoread.csv");
        await File.WriteAllTextAsync(file,
            "No.;Read on;SIID;Start DOW;Start time;Finish DOW;Finish time;No. of records;\n" +
            "1;2026-05-01 11:20:03;8002003;Fr;10:05:00;Fr;10:40:00;3;31;Fr;10:15:00;33;Fr;10:25:00;34;Fr;10:35:00;\n");
        await _app.OnUiAsync(() =>
        {
            page.AutoReadFilePath = file;
            page.AutoReadIntervalSeconds = 1;
            page.AutoReadEnabled = true;
        });
        try
        {
            await _app.WaitUntilAsync(() => page.Readouts.Count == before + 1, TimeSpan.FromSeconds(30), "the file's read-out appears in the log");
            await _app.SettleAsync();
            var added = page.Readouts.Single(r => r.ChipNumber == "8002003");
            Assert.True(added.IsKnown);
            Assert.Equal("Мельник Софія", added.FullName);
            Assert.Equal("", added.StatusText);                         // an automatic OK shows no code
            Assert.False(added.StatusIsBad);
            await _app.ScreenshotAsync("15-finish-autoread");
            Assert.False(page.HasAutoReadError, page.AutoReadError);
        }
        finally
        {
            await _app.OnUiAsync(() => page.AutoReadEnabled = false);
            await _app.SettleAsync();
        }
        AssertNoUiErrors();
    }

    [Fact]
    public async Task ProtocolPagesGenerateWordDocuments()
    {
        var env = TestEnvironment.Instance;
        await OpenCompetitionAsync(env.SyntheticId);

        await _app.OnUiAsync(() => _app.Vm.OpenProtocolsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var protocols = Assert.IsType<ProtocolsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => !protocols.Preview.IsEmpty, what: "protocol preview filled");
        var result = await await _app.OnUiAsync(() => protocols.GenerateAsync());
        Assert.NotNull(result);
        Checks.IsDocx(result.Bytes);
        Assert.EndsWith(".docx", result.SuggestedFileName);
        _output.WriteLine($"result protocol: {result.SuggestedFileName}, {result.Bytes.Length} bytes");

        await _app.OnUiAsync(() => _app.Vm.OpenStartProtocolCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var start = Assert.IsType<StartProtocolsViewModel>(Shell().SelectedPage);
        var startResult = await await _app.OnUiAsync(() => start.GenerateAsync());
        Assert.NotNull(startResult);
        Checks.IsDocx(startResult.Bytes);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task GroupsPageEditsRelayLegsInTheGrid()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_legs"), discipline: DisciplineId.Relay);
        await c.AddGroupAsync("R1", "31", s => s.LegCount = 3);
        await c.AddGroupAsync("R2", "31", s => s.LegCount = 2);
        var store = Core.GetRequiredService<IEventStore>();
        await OpenCompetitionAsync(c.Summary.Identifier);
        await _app.OnUiAsync(() => _app.Vm.OpenGroupsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<GroupsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Groups.Count == 2, what: "two relay groups listed");
        Assert.True(page.ShowLegCountColumn);
        var r1 = page.Groups.Single(g => g.Name == "R1");
        var r2 = page.Groups.Single(g => g.Name == "R2");
        Assert.Equal("3", r1.LegCountDisplay);

        // The selected group: the relay settings under the grid take the count (and save it).
        await _app.OnUiAsync(() => page.SelectedGroup = r1);
        await _app.WaitUntilAsync(() => page.IsRelayEditorVisible, what: "relay settings shown");
        await _app.OnUiAsync(() => r1.LegCountText = "4");
        Assert.Equal("4", page.RelayLegCountText);

        // Another group: saved in the background, the settings strip of the selected one stays.
        await _app.OnUiAsync(() => r2.LegCountText = "5");
        await WaitForLegsAsync("R1", 4);
        await WaitForLegsAsync("R2", 5);
        Assert.Equal("4", page.RelayLegCountText);

        // «Старт → Масовий старт» sets the mass start and restarts while the strip is open; the strip's own save
        // must not put back the values it was shown with.
        await c.SetRelaySettingsAsync("R1", 1, 4, new TimeSpan(10, 0, 0), """{"2":"11:00:00","4":"12:00:00"}""");

        // The strip's box edits the cell back.
        await _app.OnUiAsync(() => page.RelayLegCountText = "2");
        Assert.Equal("2", r1.LegCountText);
        await WaitForLegsAsync("R1", 2);
        var saved = await c.GetGroupSettingsAsync("R1", 1);
        Assert.Equal(new TimeSpan(10, 0, 0), saved.MassStartTime);
        Assert.Equal("""{"2":"11:00:00"}""", saved.RestartTimesJson);   // leg 4 is gone with the leg
        AssertNoUiErrors();

        // Polled off the UI thread: blocking it on a store call would deadlock.
        async Task WaitForLegsAsync(string group, int legs)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await store.GetGroupDaySettingsAsync(c.Folder, c.Day(1).Id))
                   .Single(s => s.GroupId == c.Groups[group].Id).LegCount != legs)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"{group}: {legs} legs not saved in time");
                await Task.Delay(100);
            }
        }
    }

    [Fact]
    public async Task RelayTeamsPageAssignsTeamNumbers()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_relay"), discipline: DisciplineId.Relay);
        await c.AddGroupAsync("R", "31", s => { s.LegCount = 3; s.MassStartTime = new TimeSpan(10, 0, 0); });
        var store = Core.GetRequiredService<IEventStore>();
        var groupId = c.Groups["R"].Id;
        // Three teams without a number (as a UOF import makes them) and one already numbered.
        await store.AddRelayTeamsAsync(c.Folder,
        [
            new() { EventDayId = c.Day(1).Id, GroupId = groupId, Name = "Гамма" },
            new() { EventDayId = c.Day(1).Id, GroupId = groupId, Name = "Альфа" },
            new() { EventDayId = c.Day(1).Id, GroupId = groupId, Name = "Бета", Number = 101 },
            new() { EventDayId = c.Day(1).Id, GroupId = groupId, Name = "Дельта" },
        ]);
        await OpenCompetitionAsync(c.Summary.Identifier);
        await _app.OnUiAsync(() => _app.Vm.OpenRelayTeamsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<RelayTeamsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Teams.Count == 4, what: "four teams listed");

        // By name, from 100, keeping existing numbers: «Бета» keeps 101, which the counter steps over.
        await AssignAsync(page.Teams.OrderBy(t => t.Name), start: 100, reassign: false);
        Assert.Equal(new Dictionary<string, int?> { ["Альфа"] = 100, ["Бета"] = 101, ["Гамма"] = 102, ["Дельта"] = 103 },
            await NumbersAsync());

        // Reversed, from 100, renumbering all: every number passes to another team (one write, past the unique index).
        await AssignAsync(page.Teams.OrderByDescending(t => t.Name), start: 100, reassign: true);
        Assert.Equal(new Dictionary<string, int?> { ["Альфа"] = 103, ["Бета"] = 102, ["Гамма"] = 101, ["Дельта"] = 100 },
            await NumbersAsync());
        await _app.WaitUntilAsync(() => page.Teams.Single(t => t.Name == "Дельта").NumberText == "100", what: "table refreshed");
        await _app.ScreenshotAsync("16-relay-teams");
        AssertNoUiErrors();

        async Task AssignAsync(IEnumerable<RelayTeamRowViewModel> shown, int start, bool reassign)
        {
            var order = shown.Cast<object?>().ToList();
            var run = _app.OnUiAsync(() => page.AssignNumbersCommand.ExecuteAsync(order));
            await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is AssignNumbersViewModel, what: "assign-numbers dialog");
            var dialog = (AssignNumbersViewModel)_app.Vm.Dialogs.Current!;
            await _app.OnUiAsync(() =>
            {
                dialog.StartNumber = start;
                dialog.ReassignExisting = reassign;
                dialog.ConfirmCommand.Execute(null);
            });
            await run;
            await _app.SettleAsync();
        }

        async Task<Dictionary<string, int?>> NumbersAsync() =>
            (await store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id)).ToDictionary(t => t.Name, t => t.Number);
    }

    [Fact]
    public async Task SettingsSwitchTheReadoutType()
    {
        await EnsureAnyCompetitionOpenAsync();
        var settings = _app.Vm.Settings;
        var app = Core.GetRequiredService<IAppSettingsService>();
        var original = await app.GetReadoutTypeAsync();
        try
        {
            await _app.OnUiAsync(() => settings.ReadoutType = ReadoutType.SportTime);
            await _app.WaitUntilAsync(() => app.GetReadoutTypeAsync().GetAwaiter().GetResult() == ReadoutType.SportTime, what: "readout type saved");
            Assert.True(settings.IsSportTime);
        }
        finally
        {
            await _app.OnUiAsync(() => settings.ReadoutType = original);
            await _app.WaitUntilAsync(() => app.GetReadoutTypeAsync().GetAwaiter().GetResult() == original, what: "readout type restored");
        }
        AssertNoUiErrors();
    }

    [Fact]
    public async Task DashboardShowsTheSyntheticCounts()
    {
        var env = TestEnvironment.Instance;
        await OpenCompetitionAsync(env.SyntheticId);
        await _app.OnUiAsync(() => _app.Vm.OpenDashboardCommand.Execute(null));
        await _app.SettleAsync();
        var page = Assert.IsType<DashboardViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Info.HasSelection && page.Info.ParticipantTotal == 6, what: "dashboard loaded");
        Assert.Equal(2, page.Info.GroupsToday);
        Assert.Equal(6, page.Info.StartsAssigned);
        await _app.ScreenshotAsync("17-dashboard-synthetic");
        AssertNoUiErrors();
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private async Task GoToPickerAsync()
    {
        await _app.SettleAsync();
        if (_app.Vm.IsEventSelected)
        {
            await _app.OnUiAsync(() => _app.Vm.ChangeEventCommand.ExecuteAsync(null));
            await _app.SettleAsync();
        }
        else if (_app.Vm.CurrentView is CreateEventViewModel create)
        {
            await _app.OnUiAsync(() => create.CancelCommand.Execute(null));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => _app.Vm.CurrentView is EventSelectionViewModel, what: "picker shown");
    }

    private async Task OpenCompetitionAsync(string identifier)
    {
        await GoToPickerAsync();
        var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
        if (!selection.Events.Any(e => e.Identifier == identifier))
        {
            // The picker lists what it scanned when it was shown; a competition built after that needs a rescan
            // (the app itself rescans whenever it returns to the picker).
            var items = await selection.FetchEventsAsync();
            await _app.OnUiAsync(() => selection.Populate(items));
        }
        await _app.WaitUntilAsync(() => selection.Events.Any(e => e.Identifier == identifier), what: $"{identifier} listed");
        await _app.OnUiAsync(async () =>
        {
            selection.SelectedEvent = selection.Events.First(e => e.Identifier == identifier);
            await selection.OpenCommand.ExecuteAsync(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected, what: $"{identifier} opened");
        await _app.SettleAsync();
    }

    private async Task EnsureAnyCompetitionOpenAsync()
    {
        if (!_app.Vm.IsEventSelected)
            await OpenCompetitionAsync(TestEnvironment.Instance.SyntheticId);
    }

    private ShellViewModel Shell()
    {
        var field = typeof(MainWindowViewModel).GetField("_shell", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ShellViewModel)field.GetValue(_app.Vm)!;
    }

    private ISessionService Session()
    {
        var field = typeof(MainWindowViewModel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ISessionService)field.GetValue(_app.Vm)!;
    }

    private void AssertNoUiErrors()
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail("The UI thread reported an exception:" + Environment.NewLine + text);
    }
}
