using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The relay part of a UOF participant file (the ФСОУ registration export): «3Е» in ProgEvent marks the runner's relay
/// day, <c>GrupRel</c> is the relay group there, <c>Prim</c> the team name and <c>PsRel</c> the leg. The file has no
/// team numbers — the teams are created without them and numbered later; more runners than legs leaves the extras on
/// the team «без етапу»; nobody is skipped.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UofRelayImportTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static string Runner(string name, string code, string group, string days,
        string relayGroup = "", string team = "", int leg = 0, string regionTeam = "0") => $"""
          <Sportsman><FIO>{name}</FIO><FOUCode>{code}</FOUCode><FSOU>1</FSOU><Birthday>01.01.2012</Birthday>
            <Group>{group}</Group><Region>Київ</Region>{(team.Length > 0 ? $"<Prim>{team}</Prim>" : "")}
            <RelayLeg>{regionTeam}</RelayLeg><ProgEvent>{days}</ProgEvent><Pol>0</Pol><PsRel>{leg}</PsRel>{(relayGroup.Length > 0 ? $"<GrupRel>{relayGroup}</GrupRel>" : "")}</Sportsman>
        """;

    private static readonly string File = "<UOFData>\n" + string.Concat(
        // A full team of four, one of them on the relay day only.
        Runner("Артюх Аніта", "1", "Ж12", "1,2,3Е", "МІКС12", "Легенда", 1, regionTeam: "2"),
        Runner("Осташов Артем", "2", "Ч12", "1,2,3Е", "МІКС12", "Легенда", 2),
        Runner("Хоменко Єгор", "3", "Ч12", "2,3Е", "МІКС12", "Легенда", 3),
        Runner("Михайленко Софія", "4", "Ж12", "3Е", "МІКС12", "Легенда", 4),
        // Five runners for a three-leg team (two teams under one name, legs repeated): two are left without a leg.
        Runner("Ветеран Один", "11", "Ч40", "1,3Е", "МІКС140", "Київ 140", 2),
        Runner("Ветеран Два", "12", "Ч40", "1,3Е", "МІКС140", "Київ 140", 2),
        Runner("Ветеран Три", "13", "Ж45", "1,3Е", "МІКС140", "Київ 140", 3),
        Runner("Ветеран Чотири", "14", "Ж45", "1,3Е", "МІКС140", "Київ 140", 1),
        Runner("Ветеран П'ять", "15", "Ч55", "1,3Е", "МІКС140", "Київ 140", 1),
        // No legs in the file: the team's next free legs, in file order.
        Runner("Бортник Антоніна", "21", "Ж12", "1,2,3Е", "МІКС12", "Олімп"),
        Runner("Синиця Лев", "22", "Ч12", "1,2,3Е", "МІКС12", "Олімп"),
        // The name is the team, exactly as written: «олімп» is another team than «Олімп».
        Runner("Ратуш Катерина", "23", "Ж12", "1,2,3Е", "МІКС12", "олімп"),
        // On the relay day but with no relay group / no team: entered there, without a team.
        Runner("Без Групи", "31", "Ж14", "1,2,3Е"),
        Runner("Без Команди", "32", "Ч14", "1,2,3Е", "МІКС14"),
        // A relay group but no relay day: the relay fields are ignored.
        Runner("Не Біжить", "33", "Ч12", "1,2", "МІКС12", "Легенда", 1)) + "</UOFData>";

    [Fact]
    public void Parser_ReadsRelayDaysGroupTeamAndLeg()
    {
        var data = new UofXmlParser().Parse(File);
        var anita = data.Participants.Single(p => p.FullName == "Артюх Аніта");
        Assert.Equal([1, 2, 3], anita.DayNumbers);
        Assert.Equal([3], anita.RelayDayNumbers);
        Assert.Equal("МІКС12", anita.RelayGroup);
        Assert.Equal("Легенда", anita.RelayTeamName);
        Assert.Equal(1, anita.RelayLeg);                     // PsRel — <RelayLeg> (the region's team) is not the leg
        Assert.Null(anita.RelayTeamNumber);
        Assert.Equal("МІКС12", anita.GroupOn(3));
        Assert.Equal("Ж12", anita.GroupOn(1));

        Assert.Equal([3], data.Participants.Single(p => p.FullName == "Михайленко Софія").DayNumbers);
        Assert.Null(data.Participants.Single(p => p.FullName == "Бортник Антоніна").RelayLeg);   // PsRel 0
        var noRelayDay = data.Participants.Single(p => p.FullName == "Не Біжить");
        Assert.Empty(noRelayDay.RelayDayNumbers);
        Assert.Equal("", noRelayDay.RelayGroup);
        Assert.Equal("", noRelayDay.RelayTeamName);
        Assert.Equal("", data.Participants.Single(p => p.FullName == "Без Групи").GroupOn(3));
    }

    [Fact]
    public async Task Import_CreatesRelayDayGroupsAndTeamsWithoutNumbers()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("uofrelay"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var store = Sp.GetRequiredService<IEventStore>();
        var data = new UofXmlParser().Parse(File);

        var result = await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.Equal(data.Participants.Count, result.Added);  // nobody is skipped
        Assert.Equal(2, result.DaysCreated);
        Assert.Equal(4, result.RelayTeamsCreated);            // Легенда, Київ 140, Олімп, олімп
        Assert.Equal(2, result.RelayUnplaced);
        Assert.Equal(2, result.RelayWithoutTeam);

        // Day 3 — every runner on it runs the relay — is a relay day; only the relay groups are on it.
        var days = (await store.GetDaysAsync(c.Folder)).ToDictionary(d => d.Number);
        Assert.Equal(DisciplineId.Relay, days[3].DefaultDiscipline);
        Assert.NotEqual(DisciplineId.Relay, days[1].DefaultDiscipline);
        var groups = (await store.GetGroupsAsync(c.Folder)).ToDictionary(g => g.Id, g => g.Name);
        var day3 = await store.GetGroupDaySettingsAsync(c.Folder, days[3].Id);
        Assert.Equal(["МІКС12", "МІКС14", "МІКС140"], day3.Select(s => groups[s.GroupId]).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(4, day3.Single(s => groups[s.GroupId] == "МІКС12").LegCount);    // the highest leg in the file
        Assert.Equal(3, day3.Single(s => groups[s.GroupId] == "МІКС140").LegCount);
        Assert.DoesNotContain(await store.GetGroupDaySettingsAsync(c.Folder, days[1].Id), s => groups[s.GroupId].StartsWith("МІКС"));

        // The relay-only runner is on day 3 only; the runner with no relay group is on day 3 without a group.
        var roster = await editor.GetParticipantRosterAsync();
        Assert.Equal(1, roster.Single(r => r.FullName == "Михайленко Софія").Days.Count(d => d.IsMember));
        var links3 = (await store.GetParticipantDaysAsync(c.Folder, days[3].Id))
            .ToDictionary(l => roster.Single(r => r.ParticipantId == l.ParticipantId).FullName);
        Assert.Null(links3["Без Групи"].GroupId);
        Assert.Null(links3["Без Команди"].RelayTeamId);
        Assert.False(links3.ContainsKey("Не Біжить"));

        await Sp.GetRequiredService<ISessionService>().SetCurrentDayAsync(days[3]);
        var teams = (await editor.GetRelayTeamsDataAsync()).Teams;
        Assert.Equal(4, teams.Count);
        Assert.All(teams, t => Assert.Null(t.Number));

        var legenda = teams.Single(t => t.Name == "Легенда");
        Assert.Equal(["Артюх Аніта", "Осташов Артем", "Хоменко Єгор", "Михайленко Софія"],
            legenda.Legs.OrderBy(l => l.Leg).Select(l => l.FullName));
        Assert.Empty(legenda.Unplaced);
        Assert.Equal("", legenda.Result!.Bib(1));            // no number — no leg number yet

        var veterans = teams.Single(t => t.Name == "Київ 140");
        Assert.Equal(3, veterans.Legs.Count);
        Assert.Equal(["Ветеран Чотири", "Ветеран Один", "Ветеран Три"], veterans.Legs.OrderBy(l => l.Leg).Select(l => l.FullName));
        Assert.Equal(["Ветеран Два", "Ветеран П'ять"], veterans.Unplaced.Select(u => u.FullName));

        var olimp = teams.Single(t => t.Name == "Олімп");
        Assert.Equal(["Бортник Антоніна", "Синиця Лев"], olimp.Legs.Where(l => l.LinkId is not null).OrderBy(l => l.Leg).Select(l => l.FullName));
        Assert.Equal("Ратуш Катерина", teams.Single(t => t.Name == "олімп").Legs.Single(l => l.Leg == 1).FullName);

        // A runner without a leg is evaluated alone and never placed (Р-22).
        var results = await editor.GetDayResultsByParticipantAsync(days[3].Id);
        var extra = roster.Single(r => r.FullName == "Ветеран Два").ParticipantId;
        Assert.Null(results[extra].Place);
    }

    [Fact]
    public async Task Reimport_KeepsTheTeamsAndTheNumbersGivenLater()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("uofrelay2"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var store = Sp.GetRequiredService<IEventStore>();
        var data = new UofXmlParser().Parse(File);
        await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);

        var day3 = (await store.GetDaysAsync(c.Folder)).Single(d => d.Number == 3);
        await Sp.GetRequiredService<ISessionService>().SetCurrentDayAsync(day3);
        var legenda = (await editor.GetRelayTeamsDataAsync()).Teams.Single(t => t.Name == "Легенда");

        // The number is given by hand (in step with OCAD); a taken number is refused, a blank one clears it again.
        Assert.True(await editor.UpdateRelayTeamAsync(Edit(legenda, 101)));
        var other = (await editor.GetRelayTeamsDataAsync()).Teams.First(t => t.Name == "Олімп");
        Assert.False(await editor.UpdateRelayTeamAsync(Edit(other, 101)));
        Assert.True(await editor.UpdateRelayTeamAsync(Edit(other, null)));

        var again = await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.RelayTeamsCreated);
        Assert.Equal(2, again.RelayUnplaced);

        var teams = (await editor.GetRelayTeamsDataAsync()).Teams;
        Assert.Equal(4, teams.Count);
        var numbered = teams.Single(t => t.Name == "Легенда");
        Assert.Equal(101, numbered.Number);
        Assert.Equal(legenda.Id, numbered.Id);
        Assert.Equal("101.4", numbered.Result!.Bib(4));
        Assert.Equal("Михайленко Софія", numbered.Legs.Single(l => l.Leg == 4).FullName);
        Assert.Equal(teams[0].Id, numbered.Id);              // numbered teams come first

        static RelayTeamEdit Edit(RelayTeamRow t, int? number) =>
            new(t.Id, t.GroupId, number, t.Name, t.ClubId, t.RegionId, t.OutOfCompetition, t.StatusOverride, t.CourseSet);
    }

    // A relay event of its own (the ФСОУ export of «2-х етапні естафети»): no ProgEvent, no GrupRel — the relay group is
    // <Group>, the team <Prim>, the leg <RelayLeg>; PsRel is 0 everywhere. A runner with no <Prim> runs individually.
    private static string EventRunner(string name, string code, string group, string team, int leg) => $"""
          <Sportsman><FIO>{name}</FIO><FOUCode>{code}</FOUCode><FSOU>0</FSOU><Birthday>01.01.1980</Birthday>
            <Group>{group}</Group><Region>Одеська</Region>{(team.Length > 0 ? $"<Prim>{team}</Prim>" : "")}
            <RelayLeg>{leg}</RelayLeg><Pol>1</Pol><PsRel>0</PsRel></Sportsman>
        """;

    private static readonly string RelayEventFile = "<UOFData>\n<Events>1</Events>\n" + string.Concat(
        EventRunner("Бриз Другий", "101", "МіхА", "Бриз", 2),
        EventRunner("Бриз Перший", "102", "МіхА", "Бриз", 1),
        EventRunner("Хвиля Перша", "103", "МіхА", "Хвиля", 1),
        EventRunner("Хвиля Друга", "104", "МіхА", "Хвиля", 2),
        EventRunner("Лис Перший", "105", "Міх50", "Лис", 1),
        EventRunner("Лис Другий", "106", "Міх50", "Лис", 2),
        // A team with no legs in the file: the next free ones.
        EventRunner("Горіх Один", "107", "Міх50", "Горіх", 0),
        EventRunner("Горіх Два", "108", "Міх50", "Горіх", 0),
        // No team: an individual runner in an individual group.
        EventRunner("Відкритий Бігун", "109", "ОПЕН", "", 0)) + "</UOFData>";

    [Fact]
    public void Parser_ReadsARelayEventOfItsOwn()
    {
        var data = new UofXmlParser().Parse(RelayEventFile);
        var first = data.Participants.Single(p => p.FullName == "Бриз Перший");
        Assert.True(first.RelayOnImportedDays);
        Assert.Empty(first.DayNumbers);
        Assert.Empty(first.RelayDayNumbers);                 // the import decides the day
        Assert.Equal("МіхА", first.RelayGroup);
        Assert.Equal("Бриз", first.RelayTeamName);
        Assert.Equal(1, first.RelayLeg);                     // <RelayLeg> — PsRel is 0 in such a file
        Assert.Equal(2, data.Participants.Single(p => p.FullName == "Бриз Другий").RelayLeg);
        Assert.Null(data.Participants.Single(p => p.FullName == "Горіх Один").RelayLeg);

        var open = data.Participants.Single(p => p.FullName == "Відкритий Бігун");
        Assert.False(open.RelayOnImportedDays);
        Assert.Equal("", open.RelayTeamName);

        // A file with a relay day («3Е») keeps reading the leg from PsRel — <RelayLeg> is the region's team there.
        Assert.All(new UofXmlParser().Parse(File).Participants, p => Assert.False(p.RelayOnImportedDays));
    }

    [Fact]
    public async Task Import_RelayEventOfItsOwn_OnAOneDayCompetition_MakesTheTeams()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("uofrelayevt"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var store = Sp.GetRequiredService<IEventStore>();
        var data = new UofXmlParser().Parse(RelayEventFile);

        var result = await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.Equal(data.Participants.Count, result.Added);
        Assert.Equal(4, result.RelayTeamsCreated);
        Assert.Equal(0, result.RelayUnplaced);
        Assert.Equal(0, result.RelayWithoutTeam);

        // The team groups run the relay on the day; the individual group does not.
        var groups = (await store.GetGroupsAsync(c.Folder)).ToDictionary(g => g.Id, g => g.Name);
        var settings = (await store.GetGroupDaySettingsAsync(c.Folder, c.Day(1).Id)).ToDictionary(s => groups[s.GroupId]);
        Assert.Equal(DisciplineId.Relay, settings["МіхА"].DisciplineOverride);
        Assert.Equal(DisciplineId.Relay, settings["Міх50"].DisciplineOverride);
        Assert.NotEqual(DisciplineId.Relay, settings["ОПЕН"].DisciplineOverride);
        Assert.Equal(2, settings["МіхА"].LegCount);

        var teams = (await editor.GetRelayTeamsDataAsync()).Teams;
        Assert.Equal(["Бриз", "Горіх", "Лис", "Хвиля"], teams.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(teams, t => Assert.Null(t.Number));
        Assert.Equal(["Бриз Перший", "Бриз Другий"],
            teams.Single(t => t.Name == "Бриз").Legs.OrderBy(l => l.Leg).Select(l => l.FullName));
        Assert.Equal(["Горіх Один", "Горіх Два"],
            teams.Single(t => t.Name == "Горіх").Legs.Where(l => l.LinkId is not null).OrderBy(l => l.Leg).Select(l => l.FullName));

        // The day itself is not a relay day yet — offered to switch; the group without teams keeps its type.
        var former = c.Day(1).DefaultDiscipline;
        var offer = Assert.Single(await editor.GetRelayDaySwitchesAsync(data, ParticipantImportScope.AllDays));
        Assert.Equal(1, offer.DayNumber);
        Assert.Equal(former, offer.Current);
        Assert.Equal(["ОПЕН"], offer.KeptGroups);

        var day = await editor.SwitchDayToRelayAsync(offer.DayId);
        Assert.Equal(DisciplineId.Relay, day!.DefaultDiscipline);
        Assert.Equal(DisciplineId.Relay, (await store.GetDaysAsync(c.Folder)).Single().DefaultDiscipline);
        settings = (await store.GetGroupDaySettingsAsync(c.Folder, c.Day(1).Id)).ToDictionary(s => groups[s.GroupId]);
        Assert.Null(settings["МіхА"].DisciplineOverride);    // follows the day now
        Assert.Null(settings["Міх50"].DisciplineOverride);
        Assert.Equal(former, settings["ОПЕН"].DisciplineOverride);
        Assert.Equal(2, settings["МіхА"].LegCount);
        await Sp.GetRequiredService<ISessionService>().SetCurrentDayAsync(day);
        Assert.Equal(4, (await editor.GetRelayTeamsDataAsync()).Teams.Count);
        Assert.Empty(await editor.GetRelayDaySwitchesAsync(data, ParticipantImportScope.AllDays));
    }

    [Fact]
    public async Task Import_RelayEventOfItsOwn_GoesOnTheRelayDayOrTheTargetDay()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("uofrelayevt2"), dayCount: 3);
        var store = Sp.GetRequiredService<IEventStore>();
        var day3 = c.Day(3);
        day3.DefaultDiscipline = DisciplineId.Relay;
        await store.UpdateDayAsync(c.Folder, day3);
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = new UofXmlParser().Parse(RelayEventFile);

        // All days: the team members go on the competition's relay day only; the individual runner on every day.
        await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        var roster = await editor.GetParticipantRosterAsync();
        Assert.Equal([3], roster.Single(r => r.FullName == "Лис Перший").Days.Where(d => d.IsMember).Select(d => d.DayNumber));
        Assert.Equal(3, roster.Single(r => r.FullName == "Відкритий Бігун").Days.Count(d => d.IsMember));
        await c.SelectAsync(3);
        Assert.Equal(4, (await editor.GetRelayTeamsDataAsync()).Teams.Count);
        Assert.Empty(await editor.GetRelayDaySwitchesAsync(data, ParticipantImportScope.AllDays));   // a relay day already

        // Current day only: the target day is the relay day.
        await c.SelectAsync(2);
        var scope = ParticipantImportScope.CurrentDay(2, ParticipantLinkField.FsouCode);
        var result = await editor.ImportParticipantsAsync(data, clearFirst: false, scope);
        Assert.Equal(4, result.RelayTeamsCreated);
        Assert.True(await editor.CurrentDayHasRelayAsync());
        Assert.Equal(4, (await editor.GetRelayTeamsDataAsync()).Teams.Count);
        Assert.Equal([2], (await editor.GetRelayDaySwitchesAsync(data, scope)).Select(s => s.DayNumber));
    }

    [Fact]
    public async Task Import_OnAnExistingIndividualDay_MakesOnlyTheRelayGroupsRelay()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("uofrelay3"), dayCount: 3);
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var store = Sp.GetRequiredService<IEventStore>();
        var data = new UofXmlParser().Parse(File);
        var result = await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.Equal(0, result.DaysCreated);

        var day3 = c.Day(3);
        Assert.NotEqual(DisciplineId.Relay, (await store.GetDaysAsync(c.Folder)).Single(d => d.Number == 3).DefaultDiscipline);
        var groups = (await store.GetGroupsAsync(c.Folder)).ToDictionary(g => g.Id, g => g.Name);
        var settings = await store.GetGroupDaySettingsAsync(c.Folder, day3.Id);
        Assert.All(settings, s => Assert.Equal(DisciplineId.Relay, s.DisciplineOverride));

        await c.SelectAsync(3);
        Assert.True(await editor.CurrentDayHasRelayAsync());
        Assert.Equal(4, (await editor.GetRelayTeamsDataAsync()).Teams.Count);
        Assert.Contains("МІКС140", settings.Select(s => groups[s.GroupId]));

        // Only day 3 got teams; every group there runs the relay, so none keeps the former type.
        var offer = Assert.Single(await editor.GetRelayDaySwitchesAsync(data, ParticipantImportScope.AllDays));
        Assert.Equal(3, offer.DayNumber);
        Assert.Empty(offer.KeptGroups);
        await editor.SwitchDayToRelayAsync(offer.DayId);
        Assert.All(await store.GetGroupDaySettingsAsync(c.Folder, day3.Id), s => Assert.Null(s.DisciplineOverride));
        Assert.Equal(4, (await editor.GetRelayTeamsDataAsync()).Teams.Count);
    }
}
