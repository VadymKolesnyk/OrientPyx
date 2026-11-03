using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Presentation.Services;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>Participant and course imports: the CSV parser, IOF XML 3.0 course data and entry lists, UOF XML,
/// and the service methods that apply them to a competition.</summary>
[Collection(AppCollection.Name)]
public sealed class ImportTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private const string IofCourses = """
        <?xml version="1.0" encoding="UTF-8"?>
        <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
          <Event><Name>Test</Name></Event>
          <RaceCourseData>
            <Map><Scale>10000</Scale></Map>
            <Control><Id>S1</Id><Position lng="30.500" lat="50.450"/><MapPosition x="0" y="0" unit="mm"/></Control>
            <Control><Id>31</Id><Position lng="30.501" lat="50.450"/><MapPosition x="10" y="0" unit="mm"/></Control>
            <Control><Id>32</Id><Position lng="30.501" lat="50.451"/><MapPosition x="10" y="10" unit="mm"/></Control>
            <Control><Id>F1</Id><Position lng="30.500" lat="50.451"/><MapPosition x="0" y="10" unit="mm"/></Control>
            <Course><Name>Ч18</Name><Length>400</Length><Climb>10</Climb>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Control"><Control>32</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl>
            </Course>
            <Course><Name>Ж16_A</Name><CourseFamily>Ж16</CourseFamily><Length>500</Length>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Control"><Control>32</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl>
            </Course>
            <Course><Name>Ж16_B</Name><CourseFamily>Ж16</CourseFamily><Length>520</Length>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>32</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl>
            </Course>
          </RaceCourseData>
        </CourseData>
        """;

    private const string IofEntries = """
        <?xml version="1.0" encoding="UTF-8"?>
        <EntryList xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
          <Event><Name>Test</Name></Event>
          <TeamEntry>
            <Id>7</Id><Name>Team A</Name>
            <Organisation><Name>Клуб А</Name></Organisation>
            <TeamEntryPerson><Person><Name><Family>Коваль</Family><Given>Олег</Given></Name></Person><Leg>1</Leg><ControlCard>8002001</ControlCard></TeamEntryPerson>
            <TeamEntryPerson><Person><Name><Family>Бондар</Family><Given>Ігор</Given></Name></Person><Leg>2</Leg><ControlCard>8002002</ControlCard></TeamEntryPerson>
            <Class><Name>Ч21Е</Name></Class>
          </TeamEntry>
          <PersonEntry>
            <Person><Name><Family>Петренко</Family><Given>Іван</Given></Name><BirthDate>2008-03-05</BirthDate></Person>
            <Organisation><Name>Клуб А</Name></Organisation>
            <ControlCard>8001234</ControlCard>
            <Class><Name>Ч18</Name></Class>
          </PersonEntry>
          <PersonEntry>
            <Person><Name><Family>Шевченко</Family><Given>Марія</Given></Name><BirthDate>2009-07-01</BirthDate></Person>
            <ControlCard>0</ControlCard>
            <Class><Name>Ж16</Name></Class>
          </PersonEntry>
        </EntryList>
        """;

    private const string Uof = """
        <?xml version="1.0" encoding="windows-1251"?>
        <UOFData>
          <Orgs>ФСОУ</Orgs>
          <Sportsman><FIO>Петренко Іван</FIO><Predst>Київ</Predst><FOUCode>12345</FOUCode><FSOU>1</FSOU>
            <Birthday>05.03.2008</Birthday><Qualification>КМС</Qualification><PAY>200</PAY>
            <Trener>Коваль О.</Trener><Trener>Бондар І.</Trener><Region>Київ</Region><Club>Клуб А</Club>
            <DUSSH>ДЮСШ 1</DUSSH><Group>Ч18</Group><Chip>8001234</Chip><ProgEvent>1,2</ProgEvent></Sportsman>
          <Sportsman><FIO>Шевченко Марія</FIO><FOUCode>12346</FOUCode><FSOU>0</FSOU><Birthday>01.07.2009</Birthday>
            <Region>Львів</Region><Group>Ж16</Group><Chip>0</Chip><ProgEvent>1</ProgEvent></Sportsman>
        </UOFData>
        """;

    [Fact]
    public void CsvParser_DetectsSeparatorHandlesQuotesAndPadsRows()
    {
        var parser = new CsvParser();
        var semi = parser.Parse("ПІБ;Номер;Група\n\"Коваль, Олег\";12;Ч21\nБондар Ігор;13\n\n");
        Assert.Equal(["ПІБ", "Номер", "Група"], semi.Header);
        Assert.Equal(2, semi.Rows.Count);
        Assert.Equal(["Коваль, Олег", "12", "Ч21"], semi.Rows[0]);
        Assert.Equal(["Бондар Ігор", "13", ""], semi.Rows[1]);

        var comma = parser.Parse("name,club\n\"He said \"\"hi\"\"\",X\n");
        Assert.Equal(["He said \"hi\"", "X"], comma.Rows[0]);
    }

    [Fact]
    public void ImportTarget_KeysAreStable()
    {
        Assert.Equal("field.gender", ImportTarget.Field("gender").Key);
        Assert.Equal("gender", ImportTarget.Field("gender").FieldKey);
        Assert.Contains(ImportTarget.Builtins, t => t.Key == "FullName");
        Assert.True(new ImportTarget("Chip").IsBuiltin);
        Assert.Equal("relay.leg", ImportTarget.RelayLeg.Key);
    }

    [Fact]
    public void IofXml_ParsesControlsCoursesAndVariantFamilies()
    {
        var data = new IofXmlParser().Parse(IofCourses);
        Assert.Equal("3.0", data.Version);
        Assert.Equal(10000, data.MapScale);
        Assert.Equal(4, data.Controls.Count);
        Assert.Equal(ControlPointType.Start, data.Controls.Single(x => x.Code == "S1").Type);
        Assert.Equal(ControlPointType.Finish, data.Controls.Single(x => x.Code == "F1").Type);
        Assert.Equal(ControlPointType.Regular, data.Controls.Single(x => x.Code == "31").Type);
        Assert.Equal(10, data.Controls.Single(x => x.Code == "31").MapX);

        Assert.Equal(["Ч18", "Ж16"], data.Courses.Select(x => x.Name));
        var plain = data.Courses[0];
        Assert.Equal(400, plain.Length);
        Assert.Empty(plain.Variants);
        var family = data.Courses[1];
        Assert.Equal(["A", "B"], family.Variants.Select(v => v.Code));
        Assert.Equal(["S1", "32", "31", "F1"], family.Variants[1].ControlCodes);
        Assert.Throws<IofXmlFormatException>(() => new IofXmlParser().Parse("<Nope/>"));
    }

    [Fact]
    public async Task IofXml_ImportsControlPointsAndGroups()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("iof"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = new IofXmlParser().Parse(IofCourses);

        var points = await editor.ImportControlPointsAsync(data, replaceAll: true);
        Assert.Equal(4, points.Imported);
        Assert.True(points.Replaced);
        var stored = await editor.GetControlPointsAsync();
        Assert.Equal(4, stored.Count);
        Assert.Equal(ControlPointType.Start, stored.Single(p => p.Code == "S1").Type);

        var groups = await editor.ImportGroupsAsync(data, updateExisting: true);
        Assert.Equal(2, groups.Added);
        var rows = await editor.GetGroupDayRowsAsync();
        var ch18 = rows.Single(g => g.Name == "Ч18");
        Assert.Equal("S1 31 32 F1", ch18.CourseOrder);
        Assert.Equal(0.300m, ch18.DistanceKm);              // from map millimetres × scale, not the file's Length
        Assert.Null(ch18.DisciplineOverride);
        var zh16 = rows.Single(g => g.Name == "Ж16");
        Assert.Equal(DisciplineId.Scatter, zh16.DisciplineOverride);
        var variants = await editor.GetCourseVariantsAsync(zh16.GroupId);
        Assert.Equal(["A", "B"], variants.Select(v => v.Code));

        // Re-importing with updateExisting=false leaves the groups alone; appending controls adds nothing new.
        var again = await editor.ImportGroupsAsync(data, updateExisting: false);
        Assert.Equal(0, again.Added);
        var appended = await editor.ImportControlPointsAsync(data, replaceAll: false);
        Assert.Equal(0, appended.Added);
    }

    [Fact]
    public async Task UofXml_ParsesAndImportsCreatingLookupsAndDays()
    {
        XmlEncodingReader.EnsureCodePagesRegistered();
        var bytes = Encoding.GetEncoding(1251).GetBytes(Uof);
        var xml = XmlEncodingReader.DecodeXml(bytes);
        var data = new UofXmlParser().Parse(xml);
        Assert.Equal("ФСОУ", data.Organisation);
        Assert.Equal(2, data.Participants.Count);
        var ivan = data.Participants[0];
        Assert.Equal("Петренко Іван", ivan.FullName);
        Assert.True(ivan.IsFsouMember);
        Assert.Equal("Коваль О., Бондар І.", ivan.Coach);
        Assert.Equal(new DateTime(2008, 3, 5), ivan.BirthDate!.Value.DateTime);
        Assert.Equal([1, 2], ivan.DayNumbers);
        Assert.Equal("", data.Participants[1].Chip);          // "0" means no chip

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("uof"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var result = await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.DaysCreated);                   // ProgEvent 1,2 on a one-day competition

        var store = Sp.GetRequiredService<IEventStore>();
        Assert.Equal(2, (await store.GetDaysAsync(c.Folder)).Count);
        Assert.Equal(["Київ", "Львів"], (await editor.GetRegionsAsync()).Select(r => r.Name).OrderBy(x => x));
        Assert.Single(await editor.GetClubsAsync());
        Assert.Single(await editor.GetDusshesAsync());
        var roster = await editor.GetParticipantRosterAsync();
        Assert.Equal(2, roster.Count);
        var row = roster.Single(r => r.FullName == "Петренко Іван");
        Assert.Equal("Київ", row.RegionName);
        Assert.Equal("КМС", row.Rank);
        Assert.Equal(2, row.Days.Count(d => d.IsMember));
        Assert.Equal("8001234", row.Days[0].Chip);
        Assert.Equal("Ч18", row.Days[0].GroupName);

        // Second import of the same file updates by FSOU code instead of duplicating.
        var second = await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.Updated);
        Assert.Equal(2, (await editor.GetParticipantRosterAsync()).Count);

        // Clear-first replaces everything.
        var third = await editor.ImportParticipantsAsync(data, clearFirst: true, ParticipantImportScope.AllDays);
        Assert.Equal(2, third.Added);
        Assert.Equal(2, (await editor.GetParticipantRosterAsync()).Count);
        Assert.Equal(2, (await editor.GetGroupsAsync()).Count);
    }

    [Fact]
    public async Task UofXml_CurrentDayOnlyScopeLinksByNameAndNeverCreatesDays()
    {
        var data = new UofXmlParser().Parse(Uof);
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("uofd"));
        await c.AddGroupAsync("Ч18", "31");
        await c.AddRunnerAsync("Петренко Іван", "7", "", "Ч18", configure: p => p.Rank = "I");
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        var scope = ParticipantImportScope.CurrentDay(1, ParticipantLinkField.FullName, ParticipantUpdateFields.Rank);
        var result = await editor.ImportParticipantsAsync(data, clearFirst: false, scope);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.DaysCreated);
        Assert.Single(await Sp.GetRequiredService<IEventStore>().GetDaysAsync(c.Folder));
        var roster = await editor.GetParticipantRosterAsync();
        var ivan = roster.Single(r => r.FullName == "Петренко Іван");
        Assert.Equal("КМС", ivan.Rank);                        // the one field allowed to update
        Assert.Equal("7", ivan.Number);                        // the number was not in UpdateFields
        Assert.Equal("8001234", ivan.Days[0].Chip);            // the day link is always written
    }

    [Fact]
    public async Task IofEntryList_ReadsPersonsAndTeamsAndImportsRelayLegs()
    {
        var data = new UofXmlParser().Parse(IofEntries);
        Assert.Equal(4, data.Participants.Count);
        var member = data.Participants[0];                     // team members first
        Assert.Equal("Коваль Олег", member.FullName);
        Assert.Equal(7, member.RelayTeamNumber);
        Assert.Equal("Team A", member.RelayTeamName);
        Assert.Equal(1, member.RelayLeg);
        Assert.Equal("Клуб А", member.Club);                   // the team's organisation
        Assert.Equal("Ч21Е", member.Group);
        var person = data.Participants[2];
        Assert.Equal("Петренко Іван", person.FullName);
        Assert.Equal(new DateTime(2008, 3, 5), person.BirthDate!.Value.DateTime);
        Assert.Equal("", data.Participants[3].Chip);

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("iofe"), discipline: DisciplineId.Relay);
        await c.AddGroupAsync("Ч21Е", "31", s => s.LegCount = 2);
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var result = await editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.Equal(4, result.Added);

        var teams = await editor.GetRelayTeamsDataAsync();
        var team = Assert.Single(teams.Teams.Where(t => t.GroupId == c.Groups["Ч21Е"].Id));
        Assert.Equal(7, team.Number);
        Assert.Equal("Team A", team.Name);
        Assert.Equal("Коваль Олег", team.Legs.Single(l => l.Leg == 1).FullName);
        Assert.Equal("Бондар Ігор", team.Legs.Single(l => l.Leg == 2).FullName);
    }

    [Fact]
    public async Task LegCoursesImport_AcceptsPlainLines()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("legc"), discipline: DisciplineId.Relay);
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        Assert.Equal(3, await editor.ImportLegCoursesAsync("100.1;A\n100;2;B\n101-1;B\n"));
        var stored = await Sp.GetRequiredService<IEventStore>().GetLegCourseAssignmentsAsync(c.Folder, c.Day(1).Id);
        Assert.Equal("B", stored.Single(a => a.TeamNumber == 100 && a.Leg == 2).CourseKey);
        Assert.Equal(3, stored.Count);
    }
}
