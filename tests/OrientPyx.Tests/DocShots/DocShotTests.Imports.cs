using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.DocShots.DemoCompetitions;

namespace OrientPyx.Tests.DocShots;

// Imports and other flows that start from a file or a popup: the archive conflict, the CSV column mapping and the
// import preview, the IOF XML group split, the manual start order, a column filter and the background activities.
// File imports run on a hidden scratch competition so the demo competitions stay as they are.
public sealed partial class DocShotTests
{
    private const string ImportDemo = "demo-import";

    private static readonly Lazy<Task<DemoBuilder>> ImportScratch = new(BuildImportScratchAsync);

    // Three runners already in the competition; the CSV below changes one, keeps one, adds one and leaves one out.
    private static async Task<DemoBuilder> BuildImportScratchAsync()
    {
        var b = await DemoBuilder.CreateAsync(TestEnvironment.Instance.Services, ImportDemo, "Відкрита першість клубу", "Київ",
            new DateTimeOffset(2026, 4, 18, 0, 0, 0, DemoBuilder.Kyiv), 1, DisciplineId.SetCourse, seed: 418);
        await b.UpdateInfoAsync(info => info.IsHidden = true);
        await b.AddDirectoriesAsync(4, 5);
        await b.AddGroupAsync("Ч21", _ => "", distanceFromCourse: false);
        await b.AddGroupAsync("Ж21", _ => "", distanceFromCourse: false);
        // The import matches people by their FSOU code.
        await b.AddRunnerAsync("Ч21", false, 1995, "1", "1050001", null, configure: p => p.FsouCode = "10234");
        await b.AddRunnerAsync("Ж21", true, 1998, "2", "1050002", null, configure: p => p.FsouCode = "10871");
        await b.AddRunnerAsync("Ч21", false, 1991, "3", "1050003", null, configure: p => p.FsouCode = "09912");
        return b;
    }

    private static async Task<string> ImportCsvAsync(DemoBuilder b)
    {
        var store = TestEnvironment.Instance.Services.GetRequiredService<IEventStore>();
        var people = (await store.GetParticipantsAsync(b.Folder)).OrderBy(p => p.Number).ToList();
        string Club(Guid? id) => b.Clubs.First(c => c.Id == id).Name;
        string Region(Guid? id) => b.Regions.First(r => r.Id == id).Name;
        var moved = b.Clubs.First(c => c.Id != people[0].ClubId).Name;           // the first runner changed club
        // Every column the people have: an all-days import overwrites a field the file lacks with a blank.
        string Row(int i, string group, string club) =>
            $"{people[i].FsouCode};{people[i].FullName};{people[i].BirthDate!.Value:dd.MM.yyyy};{people[i].Rank};{people[i].Coach};{group};{club};{Region(people[i].RegionId)};105000{i + 1}";
        return string.Join("\n",
            "Код ФСОУ;ПІБ;Дата народження;Розряд;Тренер;Група;Клуб;Регіон;Чіп",
            Row(0, "Ч21", moved),
            Row(1, "Ж21", Club(people[1].ClubId)),
            "11456;Новак Петро;14.02.2000;II;;Ч21;Маршрут;Львівська;1050004") + "\n";
    }

    private const string SplitCoursesXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
          <Event><Name>Відкрита першість клубу</Name></Event>
          <RaceCourseData>
            <Map><Scale>10000</Scale></Map>
            <Control><Id>S1</Id><Position lng="30.500" lat="50.450"/></Control>
            <Control><Id>31</Id><Position lng="30.504" lat="50.452"/></Control>
            <Control><Id>32</Id><Position lng="30.509" lat="50.451"/></Control>
            <Control><Id>33</Id><Position lng="30.506" lat="50.447"/></Control>
            <Control><Id>F1</Id><Position lng="30.501" lat="50.449"/></Control>
            <Course><Name>Ч21;Ж21</Name><Length>3200</Length>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Control"><Control>32</Control></CourseControl>
              <CourseControl type="Control"><Control>33</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl>
            </Course>
            <Course><Name>ЧЖ55</Name><Length>2100</Length>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Control"><Control>33</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl>
            </Course>
          </RaceCourseData>
        </CourseData>
        """;

    [DocShotFact]
    public async Task ImportArchiveConflict()
    {
        await _s.PrepareAsync();
        await _s.GoToPickerAsync();
        var archive = Path.Combine(DocShotOutput.DemoDir, $"{Cup}.opyx");
        _ = _s.OnUiAsync(() => _s.Vm.ImportCompetitionFileAsync(archive));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is not null, "archive import dialog");
        await _s.SettleLongAsync();
        await _s.ShotAsync("import-event-conflict", "«Файл → Імпорт змагань…» з ідентифікатором, що вже є: перезаписати чи імпортувати як нове.");
        await _s.DismissDialogAsync();
    }

    [DocShotFact]
    public async Task ImportParticipantsCsv()
    {
        await _s.PrepareAsync();
        var b = await ImportScratch.Value;
        var csv = await ImportCsvAsync(b);
        await _s.OpenAsync(ImportDemo);
        var participants = await _s.PageAsync<ParticipantsViewModel>(vm => vm.OpenParticipantsCommand);
        _ = _s.OnUiAsync(() => participants.ImportFromCsvAsync(csv));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is CsvMappingViewModel, "CSV mapping open");
        await _s.SettleLongAsync();
        await _s.ShotAsync("csv-mapping", "«Імпорт учасників із CSV/Excel»: яке поле з якої колонки файлу.");
        await _s.OnUiAsync(() => ((CsvMappingViewModel)_s.Vm.Dialogs.Current!).ConfirmCommand.Execute(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is ImportPreviewViewModel, "import preview open");
        await _s.SettleLongAsync();
        await _s.ShotAsync("import-preview", "«Що зробить імпорт»: нові, змінені й відсутні у файлі учасники.");
        await _s.DismissDialogAsync();
    }

    [DocShotFact]
    public async Task ImportCoursesXmlSplit()
    {
        await _s.PrepareAsync();
        await ImportScratch.Value;
        await _s.OpenAsync(ImportDemo);
        var groups = await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        _ = _s.OnUiAsync(() => groups.ImportFromXmlAsync(SplitCoursesXml, "courses.xml"));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is not null, "XML import dialog");
        await _s.SettleLongAsync();
        if (_s.Vm.Dialogs.Current is ImportOptionsViewModel options)
        {
            await _s.ShotAsync("xml-import-options", "Параметри імпорту КП і дистанцій з IOF XML.");
            await _s.OnUiAsync(() => options.ConfirmCommand.Execute(null));
            await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is SplitGroupsViewModel, "group split open");
            await _s.SettleLongAsync();
        }
        Assert.IsType<SplitGroupsViewModel>(_s.Vm.Dialogs.Current);
        await _s.ShotAsync("xml-import-split", "«Розділення груп»: дистанція «Ч21;Ж21» і «ЧЖ55» розкладаються на окремі групи.");
        await _s.DismissDialogAsync();
    }

    [DocShotFact]
    public async Task StartOrderFilterAndActivities()
    {
        await _s.PrepareAsync();
        await _s.OpenAsync(Cup, day: 1);
        var participants = await _s.PageAsync<ParticipantsViewModel>(vm => vm.OpenParticipantsCommand);
        await SelectParticipantsDayAsync(participants, roster: false);

        // «Дії → Ручне редагування порядку старту в групі».
        _ = _s.OnUiAsync(() => participants.EditStartOrderCommand.ExecuteAsync(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is StartOrderViewModel, "start order open");
        var order = (StartOrderViewModel)_s.Vm.Dialogs.Current!;
        await _s.OnUiAsync(() => order.SelectedGroup = order.Groups.FirstOrDefault(g => g.Name == "Ч21") ?? order.Groups[0]);
        await _s.SettleLongAsync();
        await _s.ShotAsync("start-order", "«Порядок старту в групі»: учасників перетягують, хвилини лишаються на місцях.");
        await _s.DismissDialogAsync();

        // A column filter: what a right-click on the «Група» header opens.
        var table = await _s.FindAsync<SheetTable>("DayTable");
        var header = await _s.FindTextAsync(L("Participants.Col.Group"),
            c => c is TextBlock && c.GetVisualAncestors().Any(a => a.GetType().Name == "SheetHeaderPanel"));
        await _s.OnUiAsync(() =>
        {
            var column = table.Bands!.SelectMany(b => b.Columns).First(c => c.Header == L("Participants.Col.Group"));
            typeof(SheetTable).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .First(m => m.Name == "ShowColumnFilter" && m.GetParameters() is [{ ParameterType.Name: "SheetColumn" }, { ParameterType.Name: "Control" }])
                .Invoke(table, [column, header]);
        });
        await _s.SettleLongAsync();
        await _s.ShotAsync("sheet-filter", "Фільтр колонки «Група» (правий клік по заголовку): відбір за значеннями.");
        await _s.OnUiAsync(() =>
        {
            foreach (var popup in _s.Window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Where(p => p.IsOpen))
                popup.IsOpen = false;
        });
        await _s.SettleLongAsync();

        // The background activities: finish auto-read watching a file, its card in the top-bar flyout.
        var finish = await _s.PageAsync<FinishReadViewModel>(vm => vm.OpenFinishReadCommand);
        var watched = Path.Combine(Path.GetTempPath(), "OrientPyx.DocShots", "зчитка.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(watched)!);
        await File.WriteAllTextAsync(watched, "");
        await _s.OnUiAsync(() =>
        {
            finish.AutoReadFilePath = watched;
            finish.AutoReadEnabled = true;
        });
        await _s.SettleLongAsync(1500);
        var activities = await _s.FindAsync<Button>(where: b => b.Flyout is not null && ToolTip.GetTip(b) as string == L("Activity.Title"));
        await _s.OpenFlyoutAsync(activities);
        await _s.SettleLongAsync(800);
        // Cropped to the flyout: the page behind shows the watched file's real (temp) path.
        var flyoutTitle = await _s.FindTextAsync(L("Activity.Title"), c => c is TextBlock && c.GetVisualAncestors().OfType<FlyoutPresenter>().Any());
        var presenter = await _s.OnUiAsync(() => (Control)flyoutTitle.GetVisualAncestors().OfType<FlyoutPresenter>().First());
        await _s.ShotAsync("activities", "Фонові процеси у верхній панелі: зчитка на фініші стежить за файлом.", crop: presenter, pad: 16);
        await _s.HideFlyoutAsync(activities);
        await _s.OnUiAsync(() =>
        {
            finish.AutoReadEnabled = false;
            finish.AutoReadFilePath = @"D:\Змагання\Кубок міста\День 1\зчитка.csv";
        });
        await _s.SettleLongAsync();
    }
}
