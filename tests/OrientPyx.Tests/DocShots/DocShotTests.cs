using Avalonia.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.DocShots.DemoCompetitions;

namespace OrientPyx.Tests.DocShots;

/// <summary>
/// The screenshots of the documentation site, one test per area (each opens what it needs, so the order does not
/// matter). Run only by <c>scripts/docs.ps1 -Shots</c> / the Docs workflow; the names are referenced from
/// <c>website/content</c> as <c>shots/&lt;name&gt;.png</c>.
/// </summary>
[Collection(AppCollection.Name)]
[Trait("Category", "DocShots")]
public sealed partial class DocShotTests
{
    private readonly DocShooter _s;

    public DocShotTests(HeadlessApp app) => _s = new DocShooter(app);

    [DocShotFact]
    public async Task Picker()
    {
        await _s.PrepareAsync();
        await _s.GoToPickerAsync();
        var selection = (EventSelectionViewModel)_s.Vm.CurrentView;
        await _s.OnUiAsync(() => selection.ShowHidden = false);
        await _s.WaitUntilAsync(() => All.All(id => selection.Events.Any(e => e.Identifier == id)), "demos listed");
        await _s.OnUiAsync(() => selection.SelectedEvent = selection.Events.First(e => e.Identifier == Cup));
        await _s.ShotAsync("picker", "Вікно вибору змагань: список демо-змагань, вибрано «Кубок міста».");

        await _s.OnUiAsync(() => selection.CreateCommand.Execute(null));
        await _s.SettleLongAsync();
        var create = (CreateEventViewModel)_s.Vm.CurrentView;
        await _s.OnUiAsync(() =>
        {
            create.Name = "Кубок міста з орієнтування 2027";
            create.Identifier = "kubok-2027";
            create.Venue = "Київ";
            create.DayCount = 2;
        });
        await _s.ShotAsync("create-competition", "Створення змагання: назва, ідентифікатор, місце, кількість днів.");
        await _s.OnUiAsync(() => create.CancelCommand.Execute(null));
        await _s.SettleLongAsync();
    }

    [DocShotFact]
    public async Task CupPages()
    {
        await _s.PrepareAsync();
        await _s.OpenAsync(Cup, day: 1);

        await _s.PageAsync<DashboardViewModel>(vm => vm.OpenDashboardCommand);
        await _s.ShotAsync("dashboard", "«Панель» Кубка міста, день 1.");

        await _s.PageAsync<CompetitionInfoViewModel>(vm => vm.OpenCompetitionInfoCommand);
        await _s.ShotAsync("competition-info", "«Змагання → Інформація»: назва, місце, організатор, суддівська колегія.");

        await _s.PageAsync<CompetitionDaysViewModel>(vm => vm.OpenCompetitionDaysCommand);
        await _s.ShotAsync("competition-days", "«Змагання → Дні»: два дні з датою, місцем і типом змагань.");

        await _s.PageAsync<ControlPointsViewModel>(vm => vm.OpenControlPointsCommand);
        await _s.ShotAsync("control-points", "«Змагання → КП»: контрольні пункти дня з координатами.");

        var groups = await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.ShotAsync("groups", "«Змагання → Групи»: групи дня з дистанцією, довжиною і кількістю КП.");

        await _s.PageAsync<ChipsViewModel>(vm => vm.OpenChipsCommand);
        await _s.ShotAsync("chips", "«Змагання → Чіпи»: прокатні чіпи з типом картки.");

        await _s.PageAsync<RegionsViewModel>(vm => vm.OpenRegionsCommand);
        await _s.ShotAsync("regions", "«Змагання → Довідники → Регіони».");

        await _s.PageAsync<ClubsViewModel>(vm => vm.OpenClubsCommand);
        await _s.ShotAsync("clubs", "«Змагання → Довідники → Клуби».");

        var participants = await _s.PageAsync<ParticipantsViewModel>(vm => vm.OpenParticipantsCommand);
        await SelectParticipantsDayAsync(participants, roster: true);
        await _s.ShotAsync("participants-roster", "«Учасники», режим «Мандатка»: усі учасники з блоками днів.");
        await SelectParticipantsDayAsync(participants, roster: false);
        await _s.ShotAsync("participants-day", "«Учасники», таблиця дня 1 з колонками результатів.");

        await _s.PageAsync<EntryFeesViewModel>(vm => vm.OpenEntryFeesCommand);
        await _s.ShotAsync("entry-fees", "«Змагання → Довідники → Стартові внески».");

        await _s.PageAsync<DrawViewModel>(vm => vm.OpenDrawCommand);
        await _s.ShotAsync("draw", "«Старт → Жеребкування».");

        await _s.PageAsync<ClassicDrawViewModel>(vm => vm.OpenClassicDrawCommand);
        await _s.ShotAsync("classic-draw", "«Старт → Класичне жеребкування».");

        await _s.PageAsync<StartProtocolsViewModel>(vm => vm.OpenStartProtocolCommand);
        await _s.ShotAsync("start-protocol", "«Старт → Стартовий протокол»: налаштування і попередній перегляд.");

        await _s.PageAsync<StartProtocolsViewModel>(vm => vm.OpenStartProtocolJudgesCommand);
        await _s.ShotAsync("start-protocol-judges", "«Старт → Стартовий суддівський протокол».");

        await _s.PageAsync<MapCountsViewModel>(vm => vm.OpenMapCountsCommand);
        await _s.ShotAsync("map-counts", "«Старт → Кількість карт»: група × день.");

        await _s.PageAsync<ProtocolsViewModel>(vm => vm.OpenProtocolsCommand);
        await _s.ShotAsync("protocols", "«Результати → Протоколи результатів»: шаблон і попередній перегляд.");

        await _s.PageAsync<SummaryProtocolsViewModel>(vm => vm.OpenSummaryProtocolCommand);
        await _s.ShotAsync("summary-protocol", "«Результати → Протокол по сумі днів».");

        await _s.PageAsync<SplitsExportViewModel>(vm => vm.OpenSplitsExportCommand);
        await _s.ShotAsync("splits-export", "«Результати → Спліти (HTML)».");

        var monitor = await _s.PageAsync<MonitorResultsViewModel>(vm => vm.OpenMonitorResultsCommand);
        await _s.ShotAsync("monitor", "«Результати → Результати на монітор».");
        await _s.OnUiAsync(() => monitor.AddFileCommand.Execute(null));
        await _s.SettleLongAsync();
        await _s.OnUiAsync(() =>
        {
            var file = monitor.Files.Last();
            monitor.SelectedFile = file;
            file.Title = "Діти: Ч12, Ж12";
            foreach (var group in file.Groups.Where(g => g.Name is "Ч12" or "Ж12"))
                group.IsSelected = true;
        });
        await _s.SettleLongAsync();
        await _s.ShotAsync("monitor-file", "Файл монітора: заголовок, групи, оновлення й прокрутка.");

        await _s.PageAsync<OnlineResultsViewModel>(vm => vm.OpenOnlineResultsCommand);
        await _s.ShotAsync("online-results", "«Результати → Онлайн-результати».");

        await _s.PageAsync<PointsViewModel>(vm => vm.OpenPointsCommand);
        await _s.ShotAsync("points", "«Змагання → Глобальні довідники → Очки».");

        await _s.PageAsync<RanksViewModel>(vm => vm.OpenRanksCommand);
        await _s.ShotAsync("ranks", "«Змагання → Глобальні довідники → Розряди».");

        await _s.PageAsync<DusshViewModel>(vm => vm.OpenDusshCommand);
        await _s.ShotAsync("dussh", "«Змагання → Довідники → ДЮСШ».");
    }

    private async Task SelectParticipantsDayAsync(ParticipantsViewModel page, bool roster)
    {
        var day = _s.Session.CurrentDay?.Number;
        await _s.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => roster ? d.IsRoster : !d.IsRoster && d.Day?.Number == day));
        await _s.SettleLongAsync();
        await _s.WaitUntilAsync(() => roster ? page.Roster.Count > 0 : page.Participants.Count > 0, "participant rows");
        await _s.SettleLongAsync();
    }
}
