using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Plugins.Sdk.Toolkit.Patterns;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// IOF 3.0 CourseData import fixes (docs/iof-xml-export-plan.md §12.4–12.5): the race of a multi-race file (C1), a
/// length written as a double (C2), crossing points (C3), a control with alternatives and a run in random order
/// becoming a «mixed» pattern (C4 / C5) — and the import making such a group «Змішаний».
/// </summary>
[Collection(AppCollection.Name)]
public sealed class IofCourseDataTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;
    private static IIofXmlParser Parser => Sp.GetRequiredService<IIofXmlParser>();

    private static string Doc(string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
          <Event><Name>Test</Name></Event>
          {body}
        </CourseData>
        """;

    private static string Race(int? number, string courseName, string codes) =>
        (number is { } n ? $"<RaceCourseData raceNumber=\"{n}\">" : "<RaceCourseData>")
        + "<Control><Id>S1</Id></Control><Control><Id>F1</Id></Control>"
        + string.Concat(codes.Split(' ').Select(c => $"<Control><Id>{c}</Id></Control>"))
        + $"<Course><Name>{courseName}</Name>"
        + "<CourseControl type=\"Start\"><Control>S1</Control></CourseControl>"
        + string.Concat(codes.Split(' ').Select(c => $"<CourseControl type=\"Control\"><Control>{c}</Control></CourseControl>"))
        + "<CourseControl type=\"Finish\"><Control>F1</Control></CourseControl></Course></RaceCourseData>";

    // ---- C1: several races

    [Fact]
    public void Races_TheRaceNumberedLikeTheDayIsRead()
    {
        var xml = Doc(Race(1, "Ч21", "31 32") + Race(2, "Ч21", "41 42"));
        var day2 = Parser.Parse(xml, raceNumber: 2);
        Assert.Equal(["S1", "41", "42", "F1"], day2.Courses.Single().ControlCodes);
        Assert.Equal([1, 2], day2.RaceNumbers);
        Assert.Equal(2, day2.RaceNumber);
        Assert.False(day2.RaceUnresolved);

        var day3 = Parser.Parse(xml, raceNumber: 3);
        Assert.True(day3.RaceUnresolved);                       // the import asks which race
        Assert.Equal(["S1", "31", "32", "F1"], day3.Courses.Single().ControlCodes);

        var single = Parser.Parse(Doc(Race(null, "Ч21", "31 32")), raceNumber: 5);
        Assert.False(single.RaceUnresolved);                    // one race — no question
        Assert.Empty(single.RaceNumbers);
    }

    // ---- C2: xsd:double lengths

    [Fact]
    public void Length_ADoubleIsRoundedToMetres()
    {
        var xml = Doc("<RaceCourseData><Control><Id>31</Id></Control>"
            + "<Course><Name>Ч21</Name><Length>3420.6</Length><Climb>4.5</Climb>"
            + "<CourseControl><Control>31</Control></CourseControl></Course></RaceCourseData>");
        var course = Parser.Parse(xml).Courses.Single();
        Assert.Equal(3421, course.Length);
        Assert.Equal(5, course.Climb);
    }

    // ---- C3: crossing points

    [Fact]
    public void CrossingPoints_AreNoControlsAndNoPartOfTheCourse()
    {
        var xml = Doc("<RaceCourseData>"
            + "<Control><Id>S1</Id></Control><Control><Id>31</Id></Control><Control type=\"CrossingPoint\"><Id>X1</Id></Control>"
            + "<Control><Id>32</Id></Control><Control><Id>F1</Id></Control>"
            + "<Course><Name>Ч21</Name>"
            + "<CourseControl type=\"Start\"><Control>S1</Control></CourseControl>"
            + "<CourseControl><Control>31</Control></CourseControl>"
            + "<CourseControl type=\"CrossingPoint\"><Control>X1</Control></CourseControl>"
            + "<CourseControl><Control>32</Control></CourseControl>"
            + "<CourseControl type=\"EndOfMarkedRoute\"><Control>32</Control></CourseControl>"
            + "<CourseControl type=\"Finish\"><Control>F1</Control></CourseControl></Course></RaceCourseData>");
        var data = Parser.Parse(xml);
        Assert.DoesNotContain(data.Controls, c => c.Code == "X1");
        Assert.Contains(data.Controls, c => c.Code == "32");     // also an ordinary control — kept
        Assert.Equal(["S1", "31", "32", "F1"], data.Courses.Single().ControlCodes);
    }

    // ---- C4 / C5: a choice → a «mixed» pattern

    private const string ChoiceCourse =
        "<RaceCourseData>"
        + "<Control><Id>S1</Id></Control><Control><Id>31</Id></Control><Control><Id>32</Id></Control>"
        + "<Control><Id>41</Id></Control><Control><Id>42</Id></Control><Control><Id>43</Id></Control>"
        + "<Control><Id>50</Id></Control><Control><Id>F1</Id></Control>"
        + "<Course><Name>Ч21</Name>"
        + "<CourseControl type=\"Start\"><Control>S1</Control></CourseControl>"
        + "<CourseControl><Control>31</Control><Control>32</Control></CourseControl>"
        + "<CourseControl randomOrder=\"true\"><Control>41</Control></CourseControl>"
        + "<CourseControl randomOrder=\"true\"><Control>42</Control></CourseControl>"
        + "<CourseControl randomOrder=\"true\"><Control>43</Control></CourseControl>"
        + "<CourseControl><Control>50</Control></CourseControl>"
        + "<CourseControl type=\"Finish\"><Control>F1</Control></CourseControl></Course></RaceCourseData>";

    [Fact]
    public void Choice_BecomesAMixedPattern()
    {
        var course = Parser.Parse(Doc(ChoiceCourse)).Courses.Single();
        Assert.Equal("[1: 31 32] [3: 41 42 43] 50", course.Pattern);
        Assert.Equal(["S1", "31", "41", "42", "43", "50", "F1"], course.ControlCodes); // the first alternatives, for the distance
        Assert.True(CoursePattern.Parse(course.Pattern!).Errors.Count == 0);

        var plain = Parser.Parse(Doc(Race(null, "Ч21", "31 32"))).Courses.Single();
        Assert.Null(plain.Pattern);
    }

    [Fact]
    public async Task Choice_ImportsTheGroupAsMixedAndJudgesEitherAlternative()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("iofmix"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = Parser.Parse(Doc(ChoiceCourse), raceNumber: 1);
        await editor.ImportControlPointsAsync(data, replaceAll: true);
        var result = await editor.ImportGroupsAsync(data, updateExisting: true);
        Assert.Equal(["Ч21"], result.MixedGroups);

        var settings = await c.GetGroupSettingsAsyncByName("Ч21");
        Assert.Equal(DisciplineId.Mixed, settings.DisciplineOverride);
        Assert.Equal("[1: 31 32] [3: 41 42 43] 50", settings.CourseOrder);

        await c.AddRunnerAsync("Перший", "1", "901", "Ч21", new TimeSpan(10, 0, 0));
        await c.AddRunnerAsync("Другий", "2", "902", "Ч21", new TimeSpan(10, 0, 0));
        await c.AddReadoutAsync(1, "901", "31 43 41 42 50", new TimeSpan(10, 0, 0), new TimeSpan(10, 30, 0));
        await c.AddReadoutAsync(1, "902", "32 41 42 43 50", new TimeSpan(10, 0, 0), new TimeSpan(10, 31, 0));
        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Перший"].Status);
        Assert.Equal(FinishStatus.Ok, r["Другий"].Status);
    }
}

internal static class CompetitionBuilderIofExtensions
{
    /// <summary>A group's day-1 settings by its name, for a group the builder did not add itself (an import made it); the group is adopted by the builder.</summary>
    public static async Task<BusinessLogic.Entities.GroupDaySettings> GetGroupSettingsAsyncByName(this CompetitionBuilder c, string name)
    {
        var store = TestEnvironment.Instance.Services.GetRequiredService<IEventStore>();
        var groups = await store.GetGroupsAsync(c.Folder);
        var group = groups.Single(g => g.Name == name);
        c.Groups[name] = group; // so the builder can add runners to it
        var all = await store.GetGroupDaySettingsAsync(c.Folder, c.Day(1).Id);
        return all.Single(s => s.GroupId == group.Id);
    }
}
