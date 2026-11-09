using Avalonia.Controls;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using static OrientPyx.Tests.DocShots.DemoCompetitions;

namespace OrientPyx.Tests.DocShots;

// One set per format, on its own demo competition: the pages where the format differs, a selected read-out with its
// splits, and the result protocol.
public sealed partial class DocShotTests
{
    [DocShotFact]
    public async Task GuideScoreFormats()
    {
        await _s.PrepareAsync();

        await _s.OpenAsync(ScoreCount);
        await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.ShotAsync("score-count-groups", "«Групи» змагань «За вибором по кількості КП»: обов'язкова кількість КП у кожної групи.");
        await FinishShotAsync("score-count-finish", "Зчитка «За вибором по кількості КП»: вибраний учасник і його відмітки.", "Ч21");
        await ProtocolPreviewShotAsync("score-count-protocol", "Протокол «За вибором по кількості КП».");

        await _s.OpenAsync(ScoreTime);
        await _s.PageAsync<ControlPointsViewModel>(vm => vm.OpenControlPointsCommand);
        await _s.ShotAsync("score-time-control-points", "«КП» змагань «За вибором за часом»: бали кожного КП.");
        await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.ShotAsync("score-time-groups", "«Групи» «За вибором за часом»: контрольний час і штраф за хвилину.");
        await FinishShotAsync("score-time-finish", "Зчитка «За вибором за часом»: бали, штраф і результат вибраного учасника.", "Ч21");
        await ProtocolPreviewShotAsync("score-time-protocol", "Протокол «За вибором за часом».");
    }

    [DocShotFact]
    public async Task GuideRogaine()
    {
        await _s.PrepareAsync();
        await _s.OpenAsync(Rogaine);
        await _s.PageAsync<ControlPointsViewModel>(vm => vm.OpenControlPointsCommand);
        await _s.ShotAsync("rogaine-control-points", "«КП» рогейну: бали КП (перша цифра коду).");
        await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.ShotAsync("rogaine-groups", "«Групи» рогейну: список КП і контрольний час 3 год.");
        var participants = await _s.PageAsync<ParticipantsViewModel>(vm => vm.OpenParticipantsCommand);
        await SelectParticipantsDayAsync(participants, roster: false);
        // Only the columns that tell the story, so «Команда» is next to the names (the view is reset afterwards).
        var table = await _s.FindAsync<SheetTable>("DayTable");
        var keep = new[] { "Participants.Col.Number", "Participants.Col.FullName", "Participants.Col.Team", "Participants.Col.Group",
            "Participants.Col.Chip", "Participants.Col.Club" }.Select(L).ToHashSet();
        await _s.OnUiAsync(() =>
        {
            foreach (var column in table.Bands!.SelectMany(b => b.Columns).Where(c => c.Key.Length > 0 && !keep.Contains(c.Header)).ToList())
                table.SetColumnHidden(column.Key, true);
        });
        await _s.SettleLongAsync();
        await _s.ShotAsync("rogaine-participants", "Учасники рогейну: колонка «Команда» об'єднує двох учасників у команду.");
        await _s.OnUiAsync(() => table.ResetLayout());
        await _s.SettleLongAsync();
        await FinishShotAsync("rogaine-finish", "Зчитка рогейну: відмітки учасника команди.", "Змішані");
        await ProtocolPreviewShotAsync("rogaine-protocol", "Протокол рогейну: команди, бали, штраф.");
    }

    [DocShotFact]
    public async Task GuideMixedAndScatter()
    {
        await _s.PrepareAsync();

        await _s.OpenAsync(Mixed);
        var groups = await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.OnUiAsync(() => groups.SelectedGroup = groups.Groups.First(g => g.Name == "Ч21"));
        await _s.SettleLongAsync();
        await _s.ShotAsync("mixed-groups", "«Групи» змішаного напрямку з вибраною групою: шаблон дистанції і його розбір.");
        _ = _s.OnUiAsync(() => groups.ShowCoursePatternHelpCommand.ExecuteAsync(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is CoursePatternHelpViewModel, "pattern help open");
        await _s.SettleLongAsync();
        await _s.ShotAsync("mixed-pattern-help", "Довідка про синтаксис шаблону змішаного напрямку.");
        await _s.OnUiAsync(() => ((CoursePatternHelpViewModel)_s.Vm.Dialogs.Current!).CloseCommand.Execute(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is null, "pattern help closed");
        await FinishShotAsync("mixed-finish", "Зчитка змішаного напрямку: відмітки учасника, перевірені за шаблоном.", "Ч21");
        await ProtocolPreviewShotAsync("mixed-protocol", "Протокол змішаного напрямку.");

        await _s.OpenAsync(Scatter);
        await _s.ResizeAsync(DocShooter.Width, 1000);           // room for the variants table under the grid
        groups = await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.OnUiAsync(() => groups.SelectedGroup = groups.Groups.First(g => g.Name == "Ч21"));
        await _s.SettleLongAsync();
        await _s.SetBottomPaneHeightAsync(400);
        await _s.ShotAsync("scatter-groups", "«Групи» розсіювання з вибраною групою Ч21: варіанти A, B, C.");
        await _s.ResizeAsync(DocShooter.Width, DocShooter.Height);
        await FinishShotAsync("scatter-finish", "Зчитка розсіювання: у сплітах видно визначений варіант.", "Ч21", index: 1);
        await ProtocolPreviewShotAsync("scatter-protocol", "Протокол розсіювання.");
    }

    [DocShotFact]
    public async Task GuideRelay()
    {
        await _s.PrepareAsync();
        await _s.OpenAsync(Relay);
        await _s.ResizeAsync(DocShooter.Width, 1000);           // room for the relay settings under the grid
        var groups = await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.OnUiAsync(() => groups.SelectedGroup = groups.Groups.First(g => g.Name == "Ч21"));
        await _s.SettleLongAsync();
        await _s.SetBottomPaneHeightAsync(400);
        await _s.ShotAsync("relay-groups", "«Групи» естафети з вибраною групою: кількість етапів, масовий старт, призначення дистанцій.");
        await _s.ResizeAsync(DocShooter.Width, DocShooter.Height);
        var teams = await _s.PageAsync<RelayTeamsViewModel>(vm => vm.OpenRelayTeamsCommand);
        await _s.WaitUntilAsync(() => teams.Teams.Count > 0, "relay teams listed");
        await _s.OnUiAsync(() => teams.SelectedTeam = teams.Teams[0]);
        await _s.SettleLongAsync();
        await _s.ShotAsync("relay-teams", "«Змагання → Команди (естафета)»: команди і їхні етапи.");
        await FinishShotAsync("relay-finish", "Зчитка естафети: етап учасника і номер на кшталт «101.2».", null, index: 1);
        await ProtocolPreviewShotAsync("relay-protocol", "Протокол естафети: команди з результатами етапів.");
    }

    [DocShotFact]
    public async Task DaysDiscipline()
    {
        await _s.PrepareAsync();
        await _s.OpenAsync(Cup);
        await _s.PageAsync<CompetitionDaysViewModel>(vm => vm.OpenCompetitionDaysCommand);
        // The type cell is a label until it is entered (LazyComboCell, internal): enter it the way a click does and
        // let it drop its list open.
        var label = await _s.FindTextAsync(L("Discipline.Type.SetCourse"), c => c is TextBlock);
        await _s.OnUiAsync(() =>
        {
            var cell = label.GetVisualAncestors().First(a => a.GetType().Name == "LazyComboCell");
            cell.GetType().GetMethod("BeginEdit")!.Invoke(cell, [true, null]);
        });
        await _s.SettleLongAsync();
        await _s.ShotAsync("days-discipline", "«Змагання → Дні»: тип змагань вибирається для кожного дня.");
        await _s.CloseDropDownsAsync();
    }

    /// <summary>The finish page with the <paramref name="index"/>-th read-out of a group (any group when null) selected.</summary>
    private async Task FinishShotAsync(string name, string description, string? group, int index = 0)
    {
        var finish = await _s.PageAsync<FinishReadViewModel>(vm => vm.OpenFinishReadCommand);
        await _s.WaitUntilAsync(() => finish.Readouts.Count > 0, "read-outs listed");
        await _s.OnUiAsync(() => finish.AutoReadFilePath = @"D:\Змагання\зчитка.csv");
        await _s.ResizeAsync(DocShooter.Width, 960);
        await SelectReadoutAsync(finish, finish.Readouts.Where(r => group is null || r.GroupName == group).Skip(index).First());
        await _s.ShotAsync(name, description);
        await _s.ResizeAsync(DocShooter.Width, DocShooter.Height);
    }
}
