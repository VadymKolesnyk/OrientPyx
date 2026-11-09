using Avalonia.Controls;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using static OrientPyx.Tests.DocShots.DemoCompetitions;

namespace OrientPyx.Tests.DocShots;

// The city cup in detail: menus, the participants page's menus and dialogs, the finish desk with the splits panel,
// the protocol preview, the «?» help; then the overlays (settings, about, network) and the plugins pages.
public sealed partial class DocShotTests
{
    private string L(string key) => _s.Vm.Localization[key];

    [DocShotFact]
    public async Task CupDetails()
    {
        await _s.PrepareAsync();
        await _s.OpenAsync(Cup, day: 1);

        // The top menu «Старт».
        await _s.PageAsync<DashboardViewModel>(vm => vm.OpenDashboardCommand);
        await _s.OpenMenuAsync(L("Menu.Start"));
        await _s.ShotAsync("menu-start", "Відкрите меню «Старт»: жеребкування, стартові протоколи, кількість карт.");
        await _s.CloseMenusAsync();

        // A group selected on «Групи»: its course editor below the table.
        var groups = await _s.PageAsync<GroupsViewModel>(vm => vm.OpenGroupsCommand);
        await _s.OnUiAsync(() => groups.SelectedGroup = groups.Groups.First(g => g.Name == "Ч21"));
        await _s.SettleLongAsync();
        await _s.ShotAsync("groups-selected", "«Групи» з вибраною групою Ч21: порядок КП редагується під таблицею.");

        // The participants page: «?» help, the «Імпорт ▾» and «Дії ▾» menus.
        var participants = await _s.PageAsync<ParticipantsViewModel>(vm => vm.OpenParticipantsCommand);
        await SelectParticipantsDayAsync(participants, roster: false);
        await _s.OnUiAsync(() => participants.ShowHelpCommand.Execute(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is ScreenHelpViewModel, "help dialog");
        await _s.SettleLongAsync();
        await _s.ShotAsync("help-dialog", "Довідка кнопки «?» на сторінці «Учасники»: що це, для чого, як користуватися.");
        await _s.OnUiAsync(() => ((ScreenHelpViewModel)_s.Vm.Dialogs.Current!).CloseCommand.Execute(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is null, "help closed");

        var import = await _s.FindTextAsync(L("Participants.ImportMenu"), c => c is TextBlock);
        await _s.OpenFlyoutAsync((Button)import);
        await _s.ShotAsync("participants-import", "Меню «Імпорт ▾» на сторінці «Учасники».");
        await _s.HideFlyoutAsync((Button)import);

        var actions = await _s.FindTextAsync(L("Participants.ActionsMenu"), c => c is TextBlock);
        await _s.OpenFlyoutAsync((Button)actions);
        await _s.ShotAsync("participants-actions", "Меню «Дії ▾» на сторінці «Учасники».");
        await _s.HideFlyoutAsync((Button)actions);

        // «Швидке зняття» (Ctrl+W).
        _ = _s.OnUiAsync(() => participants.QuickWithdrawalCommand.ExecuteAsync(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is QuickWithdrawalViewModel, "quick withdrawal open");
        await _s.SettleLongAsync();
        await _s.ShotAsync("quick-withdrawal", "Діалог «Швидке зняття»: номер учасника → статус.");
        await _s.OnUiAsync(() => ((QuickWithdrawalViewModel)_s.Vm.Dialogs.Current!).CancelCommand.Execute(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is null, "quick withdrawal closed");

        // The finish desk: a clean run and an MP run selected, with the splits panel.
        var finish = await _s.PageAsync<FinishReadViewModel>(vm => vm.OpenFinishReadCommand);
        await _s.OnUiAsync(() => finish.AutoReadFilePath = @"D:\Змагання\Кубок міста\День 1\зчитка.csv");
        await _s.SettleLongAsync();
        await _s.ShotAsync("finish-read", "«Фініш → Зчитка на фініші», день 1: журнал зчитаних чіпів і лічильники.");
        await _s.ResizeAsync(DocShooter.Width, 1100);
        await SelectReadoutAsync(finish, finish.Readouts.First(r => r.GroupName == "Ч21" && !r.StatusIsBad));
        await _s.ShotAsync("finish-read-splits", "Вибраний рядок зчитки (Ч21, OK) і панель сплітів.");
        await SelectReadoutAsync(finish, finish.Readouts.First(r => r.StatusIsBad && r.StatusDetail.Length > 0));
        await _s.ShotAsync("finish-read-mp", "Вибраний рядок зі статусом MP: панель показує пропущений КП.");
        await _s.ResizeAsync(DocShooter.Width, DocShooter.Height);

        // The pencil of the MP row: «Редагування зчитки».
        var mpRow = finish.Readouts.First(r => r.StatusIsBad && r.StatusDetail.Length > 0);
        _ = _s.OnUiAsync(() => finish.EditRowCommand.ExecuteAsync(mpRow));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is not null, "read-out edit open");
        await _s.SettleLongAsync();
        await _s.ShotAsync("finish-read-edit", "«Редагування зчитки»: учасник, час старту і фінішу, статус, відмітки.");
        await _s.DismissDialogAsync();

        // The window auto-read opens for a chip nobody holds (the same edit window with its own title).
        var unknown = finish.Readouts.First(r => r.ChipNumber == "1099999");
        _ = _s.OnUiAsync(() => (Task)typeof(FinishReadViewModel)
            .GetMethod("PromptUnknownReadAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(finish, [unknown.Id])!);
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is not null, "unknown chip window open");
        await _s.SettleLongAsync();
        await _s.ShotAsync("finish-unknown-chip", "«Невідомий чіп — присвоєння учасника»: кому належить зчитаний чіп.");
        await _s.DismissDialogAsync();

        // The result protocol's live preview, alone.
        await ProtocolPreviewShotAsync("protocols-preview", "Попередній перегляд протоколу результатів (день 1).");

        // Day 2: the late entries without a chip — «Сканування чіпів» and «Відомість».
        await _s.SetDayAsync(2);
        participants = await _s.PageAsync<ParticipantsViewModel>(vm => vm.OpenParticipantsCommand);
        await SelectParticipantsDayAsync(participants, roster: false);
        var table = await _s.FindAsync<SheetTable>("DayTable");
        _ = _s.OnUiAsync(() => participants.ScanChipsAsync(table));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is ChipScanViewModel, "chip scan open");
        await _s.SettleLongAsync();
        var scan = (ChipScanViewModel)_s.Vm.Dialogs.Current!;
        await _s.OnUiAsync(() =>
        {
            scan.FilePath = @"D:\Змагання\Кубок міста\День 2\видача.csv";    // not the test machine's temp path
            scan.ManualNumber = "7200113";
            scan.SubmitManualCommand.Execute(null);
        });
        await _s.SettleLongAsync();
        await _s.ShotAsync("chip-scan", "«Сканування чіпів»: учасники без чіпа, перший уже отримав прокатний чіп.");
        await _s.OnUiAsync(() => scan.CancelCommand.Execute(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is null, "chip scan closed");

        _ = _s.OnUiAsync(() => participants.OpenStatementAsync(table));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is StatementViewModel, "statement open");
        await _s.SettleLongAsync();
        await _s.ShotAsync("statement", "«Відомість»: список учасників з чіпами для видачі й підпису.");
        await _s.OnUiAsync(() => ((StatementViewModel)_s.Vm.Dialogs.Current!).CloseCommand.Execute(null));
        await _s.WaitUntilAsync(() => _s.Vm.Dialogs.Current is null, "statement closed");
        await _s.SetDayAsync(1);
    }

    [DocShotFact]
    public async Task OverlaysAndPlugins()
    {
        await _s.PrepareAsync();
        await _s.OpenAsync(Cup, day: 1);

        // Settings: a neutral events path instead of the test machine's temp folder (shown, never saved).
        await _s.OnUiAsync(() => _s.Vm.OpenSettingsCommand.Execute(null));
        await _s.SettleLongAsync();
        await _s.OnUiAsync(() => _s.Vm.Settings.EventsPath = @"C:\Users\Суддя\AppData\Local\OrientPyx\data-root\events");
        await _s.SettleLongAsync();
        await _s.ShotAsync("settings", "«Файл → Налаштування»: вигляд, шляхи, онлайн-ключі, тип відмітки, оновлення.");
        var readout = await _s.FindTextAsync(L("Settings.Readout.Title"), c => c is TextBlock);
        await _s.OnUiAsync(() => readout.BringIntoView());
        await _s.SettleLongAsync();
        await _s.ShotAsync("settings-readout", "Блок «Тип відмітки» в налаштуваннях: SportIdent або Sport Time.",
            crop: await _s.CardOfAsync(readout));
        await _s.OnUiAsync(() => _s.Vm.CloseSettingsCommand.Execute(null));
        await _s.SettleLongAsync();

        await _s.OnUiAsync(() => _s.Vm.OpenAboutCommand.Execute(null));
        await _s.SettleLongAsync();
        await _s.ShotAsync("about", "«Довідка → Про програму»: версія і оновлення.");
        await _s.OnUiAsync(() => _s.Vm.CloseAboutCommand.Execute(null));
        await _s.SettleLongAsync();

        await _s.OnUiAsync(() => _s.Vm.OpenNetworkCommand.Execute(null));
        await _s.SettleLongAsync();
        await _s.OnUiAsync(() => _s.Vm.Network.HostName = _s.Vm.Network.StationName = "FINISH-PC");   // not this machine's name
        await _s.SettleLongAsync();
        await _s.ShotAsync("network", "«Файл → Спільна робота…»: цей комп'ютер як хост або підключення до хоста.");
        await _s.OnUiAsync(() => _s.Vm.CloseNetworkCommand.Execute(null));
        await _s.SettleLongAsync();

        var plugins = await _s.PageAsync<PluginsViewModel>(vm => vm.OpenPluginsCommand);
        await _s.WaitUntilAsync(() => plugins.Plugins.Count >= 2, "standard plugins listed");
        var penalty = plugins.Plugins.Single(p => p.Id == "missed-penalty");
        await _s.OnUiAsync(() => penalty.Enabled = true);
        await _s.SettleLongAsync();
        await _s.ShotAsync("plugins", "«Плагіни → Керування плагінами…»: стандартні плагіни, «Штраф замість зняття» увімкнено.");
        await _s.OnUiAsync(() => _s.Vm.OpenPluginSettingsCommand.ExecuteAsync("missed-penalty"));
        await _s.SettleLongAsync();
        await _s.ShotAsync("plugin-settings", "Сторінка налаштувань плагіна «Штраф замість зняття» (меню «Плагіни»).");
        plugins = await _s.PageAsync<PluginsViewModel>(vm => vm.OpenPluginsCommand);
        await _s.OnUiAsync(() => plugins.Plugins.Single(p => p.Id == "missed-penalty").Enabled = false);
        await _s.SettleLongAsync();
    }

    private async Task SelectReadoutAsync(FinishReadViewModel finish, FinishReadRowViewModel row)
    {
        await _s.OnUiAsync(() => finish.SelectedReadout = row);
        await _s.SettleLongAsync();
        await _s.SetBottomPaneHeightAsync(430);   // the splits panel as tall as a judge drags it
        await _s.ScrollTableToAsync("Sheet", row);
    }

    /// <summary>The «Протоколи результатів» preview card alone, from a tall window so the document fits.</summary>
    private async Task ProtocolPreviewShotAsync(string name, string description)
    {
        await _s.ResizeAsync(DocShooter.Width, 1500);
        var protocols = await _s.PageAsync<ProtocolsViewModel>(vm => vm.OpenProtocolsCommand);
        await _s.OnUiAsync(() => protocols.IsLandscape = true);       // the default portrait page is too narrow to read
        await _s.SettleLongAsync();
        var title = await _s.FindTextAsync(L("Protocols.Preview.Title"), c => c is TextBlock);
        await _s.ShotAsync(name, description, crop: await _s.CardOfAsync(title));
        await _s.ResizeAsync(DocShooter.Width, DocShooter.Height);
    }
}
