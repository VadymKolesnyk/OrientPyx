using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Localization;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The plugin API 2 extensions, through the standard «Зважена естафета» plugin: a declared participant field is filled
/// by the participant import — from an XML element / attribute by name (UOF <c>&lt;Pol&gt;</c>, IOF <c>sex</c>) or a
/// CSV column — with the plugin's value translations (Р-59…Р-62); and a plugin report is built from the computed
/// results, laid out like a protocol and written to Word (Р-63…Р-66).
/// </summary>
[Collection(AppCollection.Name)]
public sealed class PluginExtensionsTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    private static async Task<IDisposable> PluginAsync(string id, bool enabled)
    {
        var catalog = Sp.GetRequiredService<IPluginCatalog>();
        var before = catalog.IsEnabled(id);
        await catalog.SetEnabledAsync(id, enabled);
        return new Restore(() => catalog.SetEnabledAsync(id, before));
    }

    private sealed class Restore(Func<Task> restore) : IDisposable
    {
        public void Dispose() => restore().GetAwaiter().GetResult();
    }

    private static string Sportsman(string name, string code, string pol) =>
        $"<Sportsman><FIO>{name}</FIO><FOUCode>{code}</FOUCode><Birthday>01.01.1990</Birthday>"
        + $"<Group>Ж21</Group><ProgEvent>1</ProgEvent>{(pol.Length > 0 ? $"<Pol>{pol}</Pol>" : "")}</Sportsman>";

    private static async Task<Dictionary<string, string>> FieldValuesAsync(CompetitionBuilder c, string key)
    {
        var store = Sp.GetRequiredService<IEventStore>();
        var names = (await store.GetParticipantsAsync(c.Folder)).ToDictionary(p => p.Id, p => p.FullName);
        return (await store.GetParticipantFieldValuesAsync(c.Folder))
            .Where(v => v.Key == key)
            .ToDictionary(v => names[v.ParticipantId], v => v.Value);
    }

    [Fact]
    public async Task Uof_PolFillsThePluginsGenderField_WhenChosen()
    {
        using var _ = await PluginAsync("weighted", true);
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("pfimp"));
        await c.SelectAsync();
        var data = new UofXmlParser().Parse("<UOFData>"
            + Sportsman("Іван Чоловік", "1", "1") + Sportsman("Марія Жінка", "2", "0") + Sportsman("Без Статі", "3", "")
            + "</UOFData>");
        Assert.Equal("0", data.Participants[1].Raw["Pol"]);

        // The competition runs no weighted discipline, so the field is not there yet — the file can still bring it.
        var found = await Editor.GetImportFileFieldsAsync(data);
        var gender = Assert.Single(found, f => f.Key == "gender");
        Assert.Equal("Pol", gender.SourceName);
        Assert.Equal(2, gender.Count);
        Assert.False(gender.Exists);
        Assert.Equal("Зважена естафета", gender.PluginName);

        // Not chosen in the import window: nothing is created.
        await Editor.ImportParticipantsAsync(data, clearFirst: false, ParticipantImportScope.AllDays);
        Assert.DoesNotContain(await Editor.GetCustomFieldsAsync(), f => f.Key == "gender");

        // Chosen: the plugin's field is created and «1»/«0» land as «Ч»/«Ж».
        await Editor.ImportParticipantsAsync(data with { FileFieldKeys = ["gender"] }, false, ParticipantImportScope.AllDays);
        var field = Assert.Single(await Editor.GetCustomFieldsAsync(), f => f.Key == "gender");
        Assert.Equal("weighted", field.PluginId);
        Assert.Equal(CustomFieldKind.Choice, field.Kind);
        var values = await FieldValuesAsync(c, "gender");
        Assert.Equal("Ч", values["Іван Чоловік"]);
        Assert.Equal("Ж", values["Марія Жінка"]);
        Assert.False(values.ContainsKey("Без Статі"));
        Assert.True((await Editor.GetImportFileFieldsAsync(data)).Single(f => f.Key == "gender").Exists);
    }

    [Fact]
    public async Task IofEntryList_SexAttributeFillsTheField()
    {
        using var _ = await PluginAsync("weighted", true);
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("pfiof"));
        await c.SelectAsync();
        var data = new UofXmlParser().Parse("""
            <EntryList xmlns="http://www.orienteering.org/datastandard/3.0">
              <PersonEntry><Person sex="F"><Name><Family>Коваль</Family><Given>Ольга</Given></Name></Person>
                <Class><Name>Ж21</Name></Class></PersonEntry>
              <PersonEntry><Person sex="M"><Name><Family>Коваль</Family><Given>Петро</Given></Name></Person>
                <Class><Name>Ч21</Name></Class></PersonEntry>
            </EntryList>
            """);

        var gender = Assert.Single(await Editor.GetImportFileFieldsAsync(data), f => f.Key == "gender");
        Assert.Equal("sex", gender.SourceName);
        await Editor.ImportParticipantsAsync(data with { FileFieldKeys = ["gender"] }, false, ParticipantImportScope.AllDays);

        var values = await FieldValuesAsync(c, "gender");
        Assert.Equal("Ж", values["Коваль Ольга"]);
        Assert.Equal("Ч", values["Коваль Петро"]);
    }

    [Fact]
    public async Task Csv_PluginFieldIsATargetAndItsValuesAreTranslated()
    {
        using var _ = await PluginAsync("weighted", true);
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("pfcsv"));
        await c.SelectAsync();

        // The mapping window lists the plugin's field (not created yet) and finds its column by any of its names.
        var gender = Assert.Single(await Editor.GetImportFieldsAsync(), f => f.Key == "gender");
        Assert.False(gender.Exists);
        Assert.True(gender.Matches("стать"));
        Assert.True(gender.Matches("POL"));
        Assert.True(gender.Matches("Gender"));
        Assert.False(gender.Matches("Група"));

        UofParticipant Row(string name, string code, string value) => new()
        {
            FullName = name, FsouCode = code, Group = "Ж21", DayNumbers = [1],
            Fields = new Dictionary<string, string> { ["gender"] = value }
        };
        var data = new UofParticipantData
        {
            Participants = [Row("Один", "11", "1"), Row("Два", "12", "female"), Row("Три", "13", "ж"), Row("Чотири", "14", "X"), Row("П'ять", "15", "")]
        };

        var preview = await Editor.PreviewParticipantImportAsync(data, ParticipantImportScope.AllDays);
        Assert.Equal("Стать", preview.FieldLabels["gender"]);

        await Editor.ImportParticipantsAsync(data, false, ParticipantImportScope.AllDays);
        var values = await FieldValuesAsync(c, "gender");
        Assert.Equal("Ч", values["Один"]);
        Assert.Equal("Ж", values["Два"]);
        Assert.Equal("Ж", values["Три"]);       // an option typed in another case — folded as on manual entry
        Assert.Equal("X", values["Чотири"]);    // unknown: kept as written (red in the tables, Р-18)
        Assert.False(values.ContainsKey("П'ять"));
    }

    [Fact]
    public async Task SwitchedOffPlugin_OffersNoFieldAndNoReport()
    {
        using var _ = await PluginAsync("weighted", false);
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("pfoff"));
        await c.SelectAsync();

        Assert.DoesNotContain(await Editor.GetImportFieldsAsync(), f => f.Key == "gender");
        Assert.DoesNotContain(PluginReports.Enabled(Sp.GetRequiredService<IPluginCatalog>()), r => r.Id == "weighted.check");
        var output = await Editor.BuildPluginReportAsync("weighted.check", c.Day(1).Id, Labels());
        Assert.Null(output.Document);
        Assert.Null(output.Error);
    }

    [Fact]
    public async Task UserField_IsFilledFromAnXmlElementOfItsName()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("pfuser"));
        await c.SelectAsync();
        var field = await Editor.AddCustomFieldAsync("Pol", CustomFieldKind.Text, []);
        Assert.NotNull(field);
        var data = new UofXmlParser().Parse("<UOFData>" + Sportsman("Хтось", "1", "1") + "</UOFData>");

        var found = Assert.Single(await Editor.GetImportFileFieldsAsync(data), f => f.Key == field.Key);
        Assert.True(found.Exists);
        Assert.Null(found.PluginName);
        await Editor.ImportParticipantsAsync(data with { FileFieldKeys = [field.Key] }, false, ParticipantImportScope.AllDays);
        Assert.Equal("1", (await FieldValuesAsync(c, field.Key))["Хтось"]);
    }

    private static PluginReportLabels Labels()
    {
        var localization = Sp.GetRequiredService<ILocalizationService>();
        return new PluginReportLabels("Головний суддя", "Головний секретар", "Журі", "П/З: OrientPyx", "Згенеровано",
            "Сторінка", localization.Get);
    }

    [Fact]
    public async Task WeightedCheckReport_ListsTheRunnersWhoseKCannotBeFound()
    {
        using var _ = await PluginAsync("weighted", true);
        var catalog = Sp.GetRequiredService<IPluginCatalog>();
        var localization = Sp.GetRequiredService<ILocalizationService>();
        foreach (var (culture, strings) in catalog.Plugins.Single(p => p.Info.Id == "weighted").Strings)
            localization.AddOverlay(culture, "weighted", strings);

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("wrcheck"), discipline: new DisciplineId("weighted.relay"),
            date: new DateTimeOffset(2026, 6, 6, 0, 0, 0, CompetitionBuilder.Offset));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("W", "31", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
        var born = new DateTimeOffset(1990, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var man = await c.AddRunnerAsync("Man", "", "6011", "W", configure: p => p.BirthDate = born);
        var odd = await c.AddRunnerAsync("Odd", "", "6012", "W", configure: p => p.BirthDate = born);
        var noYear = await c.AddRunnerAsync("NoYear", "", "6021", "W", configure: p => p.BirthDate = null);
        await c.AddRunnerAsync("NoGender", "", "6022", "W", configure: p => p.BirthDate = born);
        var fine1 = await c.AddRunnerAsync("Fine1", "", "6031", "W", configure: p => p.BirthDate = born);
        var fine2 = await c.AddRunnerAsync("Fine2", "", "6032", "W", configure: p => p.BirthDate = born);
        await c.AddRelayTeamAsync("W", 601, "Odd team", 1, ("Man", 1), ("Odd", 2));
        await c.AddRelayTeamAsync("W", 602, "Missing", 1, ("NoYear", 1), ("NoGender", 2));
        await c.AddRelayTeamAsync("W", 603, "Fine", 1, ("Fine1", 1), ("Fine2", 2));
        await c.AddReadoutAsync(1, "6031", "31", null, T(10, 20));

        await c.SelectAsync();
        await Editor.EnsurePluginFieldsAsync();
        await Editor.SetParticipantFieldAsync(man.Id, "gender", "Ч");
        await Editor.SetParticipantFieldAsync(odd.Id, "gender", "M");          // not an option — no K
        await Editor.SetParticipantFieldAsync(noYear.Id, "gender", "Ж");
        await Editor.SetParticipantFieldAsync(fine1.Id, "gender", "Ч");
        await Editor.SetParticipantFieldAsync(fine2.Id, "gender", "Ж");

        var report = Assert.Single(PluginReports.Enabled(catalog), r => r.Id == "weighted.check");
        Assert.Equal(ReportScope.Day, report.Scope);
        Assert.Equal("Перевірка K (стать і рік народження)", localization.Get(report.NameKey));

        var output = await Editor.BuildPluginReportAsync("weighted.check", c.Day(1).Id, Labels());
        Assert.Null(output.Error);
        var doc = Assert.IsType<ResultProtocolDocument>(output.Document);
        Assert.Equal("Перевірка K (стать і рік народження)", doc.Title);   // no title of its own — the report's name
        Assert.Equal(["№", "Команда / учасник", "Рік", "Стать", "Проблема"], doc.ColumnHeaders);
        Assert.Equal("06.06.2026", doc.DateText);
        Assert.Empty(doc.Officials);                                     // the report asks for none
        Assert.NotNull(doc.Footer);

        // «Лише команди з проблемами» (default): the two teams with a problem, a caption row over their legs.
        var section = Assert.Single(doc.Sections);
        Assert.Equal("W", section.GroupName);
        Assert.Equal(6, section.Rows.Count);
        Assert.True(section.Rows[0].IsTeamHeader);
        Assert.Equal(["601", "Odd team", "", "", ""], section.Rows[0].Cells);
        Assert.True(section.Rows[1].IsSubRow);
        Assert.Equal(["1", "Man", "1990", "Ч", ""], section.Rows[1].Cells);
        Assert.Contains("«M»", section.Rows[2].Cells[4]);
        Assert.Equal("немає року народження", section.Rows[4].Cells[4]);
        Assert.Equal("не вказано стать", section.Rows[5].Cells[4]);
        Assert.NotEmpty(Sp.GetRequiredService<IResultProtocolWriter>().Write(doc));

        // The report's setting, saved for the competition: every team.
        await Editor.SavePluginReportSettingsAsync("weighted.check", c.Day(1).Id,
            new Dictionary<string, object?> { ["onlyProblems"] = false });
        var settings = await Editor.GetPluginReportSettingsAsync("weighted.check", c.Day(1).Id);
        Assert.Equal(false, settings!.Values["onlyProblems"]);
        var all = await Editor.BuildPluginReportAsync("weighted.check", c.Day(1).Id, Labels());
        Assert.Equal(9, all.Document!.Sections.Single().Rows.Count);

        // Everyone fixed: no sections, the «all good» line.
        await Editor.SavePluginReportSettingsAsync("weighted.check", c.Day(1).Id,
            new Dictionary<string, object?> { ["onlyProblems"] = true });
        await Editor.SetParticipantFieldAsync(odd.Id, "gender", "ж");
        await Editor.SetParticipantFieldAsync((await c.GetLinkAsync("NoGender", 1)).ParticipantId, "gender", "Ч");
        var store = Sp.GetRequiredService<IEventStore>();
        var p = (await store.GetParticipantsAsync(c.Folder)).Single(x => x.FullName == "NoYear");
        p.BirthDate = born;
        await store.UpdateParticipantAsync(c.Folder, p);
        var good = await Editor.BuildPluginReportAsync("weighted.check", c.Day(1).Id, Labels());
        Assert.Empty(good.Document!.Sections);
        Assert.Equal("У всіх учасників є рік народження і стать «Ч» або «Ж».", good.Document.FilterSummary);
    }
}
