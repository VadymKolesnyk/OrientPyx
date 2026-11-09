using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Importing a competition archive (a double-clicked <c>.opyx</c> or «Файл → Імпорт змагань…») opens the imported
/// competition straight away instead of leaving the user on the picker.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ArchiveImportTests
{
    private readonly HeadlessApp _app;

    public ArchiveImportTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task ImportedCompetitionOpensRightAway()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_imp"));
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун 01", "1", "", "Ч21");
        var archive = Path.Combine(TestEnvironment.ResultsDir, $"{c.Summary.Identifier}.opyx");
        await Core.GetRequiredService<IEventArchiveService>().ExportAsync(c.Summary.Identifier, archive);

        // The archive's identifier is taken (the source competition), so import it under a new one.
        var copy = TestIds.Next("ui_imp_copy");
        await _app.SettleAsync();
        _ = _app.OnUiAsync(() => _app.Vm.ImportCompetitionFileAsync(archive));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is ImportEventViewModel, what: "import dialog");
        var dialog = (ImportEventViewModel)_app.Vm.Dialogs.Current!;
        await _app.OnUiAsync(() => dialog.NewIdentifier = copy);
        await _app.WaitUntilAsync(() => dialog.IsNewIdentifierAvailable, what: "new identifier accepted");
        await _app.OnUiAsync(() => dialog.ConfirmCommand.Execute(null));

        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected && CurrentIdentifier() == copy,
            what: $"{copy} opened after the import");
        await _app.SettleAsync();
        Assert.IsType<ShellViewModel>(_app.Vm.CurrentView);
    }

    private string? CurrentIdentifier()
    {
        var field = typeof(MainWindowViewModel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((ISessionService)field.GetValue(_app.Vm)!).CurrentEvent?.Identifier;
    }
}
