using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.Services;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>Start draw, entry fees, custom field normalisation, layout-tolerant search, identifiers and table
/// view persistence.</summary>
[Collection(AppCollection.Name)]
public sealed class DrawFeesAndHelpersTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static DrawGroup Group(string name, params (string Name, string Club)[] members) =>
        new(Guid.NewGuid(), name, "31", ["31"],
            members.Select(m => new DrawParticipant(Guid.NewGuid(), Guid.NewGuid(), m.Name, "", "", m.Club, "")).ToList());

    // ---- Draw ----------------------------------------------------------------------------------------------

    [Fact]
    public void StartGroupOrder_EliteThenVeteransThenKidsThenRestUnchanged()
    {
        var names = new[] { "Ч75", "Ж12", "OPEN", "Ж21Е", "Ч20", "Ж75", "Ч12", "Ч35", "Ж16", "Ч21Е", "Ч55", "Ж21А", "Ч14", "Ч10" };
        var sorted = StartGroupOrder.Sort(names, n => n);
        Assert.Equal(
            ["Ж21Е", "Ч21Е", "Ж75", "Ч75", "Ч55", "Ж12", "Ч12", "Ч14", "Ж16", "OPEN", "Ч20", "Ч35", "Ж21А", "Ч10"],
            sorted);
    }


    [Fact]
    public void Draw_AssignsOneMinutePerSlotPerLaneAndIsSeededDeterministic()
    {
        var draw = Sp.GetRequiredService<IStartDrawService>();
        var lane1 = new[] { Group("Ч21", ("a", "X"), ("b", "Y"), ("c", "X")), Group("Ч35", ("d", "X"), ("e", "Y")) };
        var lane2 = new[] { Group("Ж21", ("f", "X"), ("g", "Y")) };
        var start = new TimeSpan(11, 0, 0);
        var interval = TimeSpan.FromMinutes(1);

        var first = draw.Draw([lane1, lane2], start, interval, DrawSeparationField.Club, groupGap: 1, seed: 42);
        var again = draw.Draw([lane1, lane2], start, interval, DrawSeparationField.Club, groupGap: 1, seed: 42);
        Assert.Equal(first, again);
        Assert.Equal(7, first.Count);
        Assert.Equal(first.Select(a => a.LinkId).Distinct().Count(), first.Count);

        // Lane 1: Ч21 takes 11:00–11:02, a gap slot, then Ч35 11:04–11:05. Lane 2 starts again at 11:00.
        var lane1Times = first.Take(5).Select(a => a.StartTime).ToList();
        Assert.Equal([start, start + interval, start + 2 * interval, start + 4 * interval, start + 5 * interval], lane1Times);
        Assert.Equal(start, first[5].StartTime);
        Assert.Equal(start + interval, first[6].StartTime);

        // Separation inside Ч21: a clash (X after X) is accepted only when nobody of another club is left.
        var byLink = lane1[0].Members.ToDictionary(m => m.LinkId, m => m.ClubName);
        var ch21 = first.Where(a => byLink.ContainsKey(a.LinkId)).OrderBy(a => a.StartTime).Select(a => byLink[a.LinkId]).ToList();
        Assert.Equal(3, ch21.Count);
        Assert.True(ch21 is ["X", "Y", "X"] or ["Y", "X", "X"]);
    }

    [Fact]
    public void Draw_SeparationOnlyClashesWhenEveryoneLeftShares()
    {
        var draw = Sp.GetRequiredService<IStartDrawService>();
        var group = Group("G", ("a", "A"), ("b", "B"), ("c", "C"), ("d", "C"), ("e", "C"));
        for (var seed = 0; seed < 20; seed++)
        {
            var result = draw.Draw([[group]], TimeSpan.Zero, TimeSpan.FromMinutes(1), DrawSeparationField.Club, seed: seed);
            var clubs = result.Select(a => group.Members.Single(m => m.LinkId == a.LinkId).ClubName).ToList();
            for (var i = 1; i < clubs.Count; i++)
                if (clubs[i] == clubs[i - 1])
                    Assert.All(clubs.Skip(i), x => Assert.Equal(clubs[i], x));
        }
    }

    [Fact]
    public void ClassicDraw_EachGroupHasItsOwnStartAndInterval()
    {
        var draw = Sp.GetRequiredService<IStartDrawService>();
        var g1 = Group("Ч21", ("a", ""), ("b", ""));
        var g2 = Group("Ж21", ("c", ""), ("d", ""), ("e", ""));
        var result = draw.DrawClassic(
        [
            new ClassicDrawGroup(g1, new TimeSpan(10, 0, 0), TimeSpan.FromMinutes(2)),
            new ClassicDrawGroup(g2, new TimeSpan(12, 0, 0), TimeSpan.FromMinutes(1)),
        ], DrawSeparationField.None, seed: 1);
        Assert.Equal(5, result.Count);
        Assert.Equal([new TimeSpan(10, 0, 0), new TimeSpan(10, 2, 0)], result.Take(2).Select(a => a.StartTime));
        Assert.Equal([new TimeSpan(12, 0, 0), new TimeSpan(12, 1, 0), new TimeSpan(12, 2, 0)], result.Skip(2).Select(a => a.StartTime));
    }

    [Fact]
    public async Task Draw_PrepDataAndSavedStartTimesReachTheStartOrderPage()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("drawdb"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddRunnerAsync("A", "1", "", "Ч21");
        await c.AddRunnerAsync("B", "2", "", "Ч21");
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var prep = await editor.GetDrawPrepDataAsync(c.Day(1).Id);
        var group = Assert.Single(prep.Groups);
        Assert.Equal("31", group.FirstControl);
        Assert.Equal(2, group.Members.Count);

        var draw = Sp.GetRequiredService<IStartDrawService>();
        var assignments = draw.Draw([[group]], new TimeSpan(11, 0, 0), TimeSpan.FromMinutes(1), DrawSeparationField.None, seed: 7);
        Assert.Equal(2, await editor.SaveDrawStartTimesAsync(assignments));

        var order = await editor.GetStartOrderDataAsync(c.Day(1).Id);
        var members = Assert.Single(order.Groups).Members;
        Assert.Equal([new TimeSpan(11, 0, 0), new TimeSpan(11, 1, 0)], members.Select(m => m.StartTime));
        var dashboard = await editor.GetDashboardAsync();
        Assert.Equal(2, dashboard.StartsAssigned);
        Assert.Equal(new TimeSpan(11, 0, 0), dashboard.FirstStart);
    }

    // ---- Entry fees ----------------------------------------------------------------------------------------

    [Fact]
    public void EntryFeeCalculator_AppliesOnlyTheLargestDiscount()
    {
        var calc = new EntryFeeCalculator();
        var input = new EntryFeeComputation
        {
            Days = [new EntryFeeDayInput(100m, 20m), new EntryFeeDayInput(100m, 0m)],
            SelectedEntryPercents = [10m, 25m],
            SelectedChipPercents = [50m],
        };
        Assert.Equal(160m, calc.Compute(input));   // 200 × 0.75 + 20 × 0.5
        Assert.Equal(220m, calc.Compute(new EntryFeeComputation { Days = input.Days, SelectedEntryPercents = [], SelectedChipPercents = [] }));
        Assert.Equal(20m, calc.Compute(new EntryFeeComputation { Days = input.Days, SelectedEntryPercents = [100m], SelectedChipPercents = [] }));
    }

    [Fact]
    public void EntryFeeContext_PricesChipsAndRaisedFees()
    {
        var group = new Group { Name = "Ч21", EntryFee = 150m };
        var dayId = Guid.NewGuid();
        var fsou = new EntryFeeDiscount { Name = "ФСОУ", Percent = 20m, IsFsouMemberDiscount = true };
        var student = new EntryFeeDiscount { Name = "Студент", Percent = 50m, AppliesToChipRental = true };
        var info = new CompetitionInfo { RaisedFeeEnabled = true, RaisedFeeAmount = 200m, ChipRentalPricePerDay = 30m };
        var ctx = new EntryFeeContext(new EntryFeeCalculator(), info, [group],
            [new ChipPriceOverride { Note = "SIAC", PricePerDay = 60m }],
            [fsou, student],
            [new RentalChip { Number = "5000" }, new RentalChip { Number = "5001", Note = "siac" }]);

        decimal Total(string chip, bool raised = false, bool fsouMember = false, params Guid[] discounts) =>
            ctx.Total(raised, fsouMember, discounts, [new EntryFeeDay(dayId, group.Id, chip, raised)]);

        Assert.Equal(180m, Total(""));                               // no chip: fee + rental price
        Assert.Equal(150m, Total("9999"));                           // own chip
        Assert.Equal(180m, Total("5000"));                           // rental chip
        Assert.Equal(210m, Total("5001"));                           // rental chip with a price override by note
        Assert.Equal(230m, Total("5000", raised: true));             // raised fee replaces the group fee
        Assert.Equal(150m, Total("5000", fsouMember: true));         // 150 × 0.8 + 30 (FSOU never discounts chips)
        Assert.Equal(90m, Total("5000", discounts: student.Id));     // 75 + 15
        Assert.Equal(90m, Total("5000", fsouMember: true, discounts: student.Id)); // the larger discount wins

        var breakdown = ctx.Describe(false, false, [], [new EntryFeeDay(dayId, group.Id, "", false)]);
        Assert.Equal(ChipRentalReason.NoChipCharged, breakdown.Days[0].ChipReason);
        Assert.Equal(180m, breakdown.Total);
    }

    [Fact]
    public async Task EntryFee_ReachesTheParticipantRowsAndFsouMembersGetTheSeededDiscount()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("fee"));
        var store = Sp.GetRequiredService<IEventStore>();
        var group = await c.AddGroupAsync("Ч21", "31");
        await store.UpdateGroupEntryFeeAsync(c.Folder, group.Id, 100m);
        await c.AddRunnerAsync("Member", "1", "", "Ч21", configure: p => p.IsFsouMember = true);
        await c.AddRunnerAsync("Guest", "2", "", "Ч21");
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var discounts = await editor.GetEntryFeeDiscountsAsync();
        var fsou = discounts.FirstOrDefault(d => d.IsFsouMemberDiscount);
        var rows = await editor.GetParticipantDayRowsAsync();
        var guest = rows.Single(r => r.FullName == "Guest");
        var member = rows.Single(r => r.FullName == "Member");
        Assert.Equal(100m, guest.TotalEntryFee);
        if (fsou is { Percent: > 0 })
            Assert.Equal(100m * (1 - fsou.Percent / 100m), member.TotalEntryFee);
        else
            Assert.Equal(100m, member.TotalEntryFee);
    }

    // ---- Custom fields -------------------------------------------------------------------------------------

    [Fact]
    public void CustomField_NormalisesChoiceNumberAndBool()
    {
        var choice = new CustomField("gender", "Стать", CustomFieldKind.Choice, [new ChoiceOption("Ч"), new ChoiceOption("Ж")], CustomField.UserSource, 0);
        Assert.Equal("Ч", choice.Normalize(" ч "));
        Assert.Equal("Ж", choice.Normalize("ж"));
        Assert.Equal("M", choice.Normalize("M"));            // no synonyms: another spelling is kept as typed
        Assert.True(choice.IsInvalid("M"));
        // An event.db written while options still had synonyms reads fine — the synonyms are ignored.
        Assert.Equal(["Ч", "Ж"], CustomField.ParseOptions("""[{"Value":"Ч","Aliases":["M","Чол"]},{"Value":"Ж","Aliases":null}]""")
            .Select(o => o.Value));
        var stored = CustomField.SerializeOptions(choice.Options);
        Assert.DoesNotContain("Aliases", stored);
        Assert.Equal(["Ч", "Ж"], CustomField.ParseOptions(stored).Select(o => o.Value));
        Assert.Equal("x", choice.Normalize("x"));
        Assert.True(choice.IsInvalid("x"));
        Assert.False(choice.IsInvalid(""));

        var number = new CustomField("w", "Вага", CustomFieldKind.Number, [], CustomField.UserSource, 1);
        Assert.Equal("1.5", number.Normalize("1,5"));
        Assert.Equal("1000.5", number.Normalize("1 000,5"));
        Assert.True(number.IsInvalid("abc"));

        var flag = new CustomField("f", "Прапор", CustomFieldKind.Bool, [], CustomField.UserSource, 2);
        Assert.Equal("1", flag.Normalize("так"));
        Assert.Equal("0", flag.Normalize("ні"));
        Assert.Equal("1", flag.Normalize("YES"));
        Assert.False(flag.IsInvalid("1"));
        Assert.True(flag.IsInvalid("maybe"));

        Assert.Equal("stat", CustomField.KeyFromLabel("Стать"));
        Assert.Equal("field", CustomField.KeyFromLabel("!!!"));
    }

    [Fact]
    public void CustomFieldEditor_ReadsChoiceOptionsAsACommaList()
    {
        var loc = Sp.GetRequiredService<OrientPyx.Localization.ILocalizationService>();
        var kinds = Enum.GetValues<CustomFieldKind>()
            .Select(k => new OrientPyx.Presentation.ViewModels.Dialogs.FieldKindOption(k, loc)).ToList();
        var field = new CustomField("stat", "Стать", CustomFieldKind.Choice,
            [new ChoiceOption("Ч"), new ChoiceOption("Ж")], "plugin:x", 0);
        var row = new OrientPyx.Presentation.ViewModels.Dialogs.CustomFieldRowViewModel(field, false, kinds, loc, _ => { });
        Assert.Equal("Ч, Ж", row.OptionsText);

        row.OptionsText = " Ч ,Ж; Інше,, ч ";
        var options = row.ToField().Options;
        Assert.Equal(["Ч", "Ж", "Інше"], options.Select(o => o.Value));   // separators trimmed, the repeat «ч» dropped
        Assert.Equal("Ч", row.ToField().Normalize("ч"));
    }

    [Fact]
    public async Task CustomField_RoundTripsThroughTheServiceAndImportTargets()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("cf"));
        await c.AddGroupAsync("Ч21", "31");
        var runner = await c.AddRunnerAsync("A", "1", "", "Ч21");
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var field = await editor.AddCustomFieldAsync("Стать", CustomFieldKind.Choice, [new ChoiceOption("Ч"), new ChoiceOption("Ж")]);
        Assert.NotNull(field);
        Assert.Equal("stat", field.Key);
        Assert.Equal("Ч", await editor.SetParticipantFieldAsync(runner.Id, field.Key, "ч"));
        var rows = await editor.GetParticipantDayRowsAsync();
        Assert.Equal("Ч", rows.Single().Fields[field.Key]);
        var roster = await editor.GetParticipantRosterAsync();
        Assert.Equal("Ч", roster.Single().Fields[field.Key]);
        Assert.Equal("", await editor.SetParticipantFieldAsync(runner.Id, field.Key, " "));
        Assert.False((await editor.GetParticipantDayRowsAsync()).Single().Fields.ContainsKey(field.Key));
        Assert.Null(await editor.AddCustomFieldAsync("   ", CustomFieldKind.Text, []));
        Assert.True(await editor.DeleteCustomFieldAsync(field.Key));
        Assert.Empty(await editor.GetCustomFieldsAsync());
    }

    // ---- Search, identifiers, layouts ----------------------------------------------------------------------

    [Fact]
    public void TextSearch_ToleratesTheWrongKeyboardLayout()
    {
        Assert.Equal(["Rjktc", "Колес"], TextSearch.Variants("Rjktc"));
        Assert.Contains("Быков", TextSearch.Variants("Bikov"));
        Assert.True(TextSearch.Matches("Колесник Іван", "Rjktc"));
        Assert.True(TextSearch.Matches("Коваль", "коваль"));
        Assert.True(TextSearch.Matches("М12", "M12"));
        Assert.True(TextSearch.Matches("anything", ""));
        Assert.False(TextSearch.Matches("anything", "   "));
        Assert.False(TextSearch.Matches("Коваль", "Бондар"));
    }

    [Theory]
    [InlineData("M21E", "Ч21Е")]
    [InlineData("W21A", "Ж21А")]
    [InlineData("МЖ55", "Ч55|Ж55")]
    [InlineData("MW12; Ж14", "Ч12|Ж12|Ж14")]
    [InlineData("МІКС20", "МІКС20")]
    [InlineData("МАЛЮКИ", "МАЛЮКИ")]
    [InlineData("OPEN", "OPEN")]
    public void CourseNameSplitter_MapsGenderLettersOnlyInGroupCodes(string course, string expected)
    {
        var groups = new CourseNameSplitter().Split(course);
        Assert.Equal(expected.Split('|'), groups);
    }

    [Theory]
    [InlineData("kubok-2026", true)]
    [InlineData("Кубок міста", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a:b", false)]
    public void EventIdentifier_Validates(string id, bool valid)
    {
        Assert.Equal(valid, EventIdentifier.IsValid(id));
    }

    [Fact]
    public async Task TableLayoutStore_PersistsPerCompetitionAndSanitisesOnRead()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("layout"));
        await c.SelectAsync();
        var store = new TableLayoutStore(Sp.GetRequiredService<ISessionService>(), Sp.GetRequiredService<IWorkspaceMode>());
        Assert.Equal(c.Folder, store.CurrentScopeId);
        Assert.Null(store.Load("participants.day"));

        store.Save("participants.day", new TableLayout
        {
            Columns = { ["name"] = new ColumnLayout { Width = 120 }, ["bad"] = new ColumnLayout { Width = 5 }, [""] = new ColumnLayout { Width = 100 } },
            Order = ["a", "b", "a", ""],
            Hidden = ["x", "x"],
        });
        var loaded = store.Load("participants.day");
        Assert.NotNull(loaded);
        Assert.Equal(120, loaded.Columns["name"].Width);
        Assert.False(loaded.Columns.ContainsKey("bad"));
        Assert.False(loaded.Columns.ContainsKey(""));
        Assert.Equal(["a", "b"], loaded.Order);
        Assert.Equal(["x"], loaded.Hidden);
        Assert.True(File.Exists(Path.Combine(c.Folder, "views.json")));

        store.SaveDefault("participants.day", new TableLayout { Hidden = ["club"] });
        Assert.True(store.HasDefault("participants.day"));
        store.ClearLayout("participants.day");
        Assert.Null(store.Load("participants.day"));
        Assert.False(store.HasDefault("participants.day"));

        // A corrupt file reads as "no layout" instead of breaking the table.
        await File.WriteAllTextAsync(Path.Combine(c.Folder, "views.json"), "{ not json");
        Assert.Null(store.Load("participants.day"));
    }
}
