using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>«Сканування чіпів»: the dialog's routing of scanned chips (rental note by the chip-type field, own-chip
/// rows skipped, the held-back mismatches, repeat and foreign chips, the file-watch priming) and the editing
/// service behind it (adding scanned rental chips to the base, the saved set-up round trip).</summary>
[Collection(AppCollection.Name)]
public sealed class ChipScanTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;
    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();
    private static ILocalizationService Loc => Sp.GetRequiredService<ILocalizationService>();

    private static readonly CustomField ChipType = new(
        "chip-type", "Тип чіпа", CustomFieldKind.Choice,
        [new ChoiceOption("AIR", []), new ChoiceOption("власний", ["свій"])],
        CustomField.UserSource, 0);

    private static ChipScanCandidate Candidate(string name, string chipType) =>
        new(Guid.NewGuid(), name.Length.ToString(), name, "Ч21",
            new Dictionary<string, string> { ["chip-type"] = chipType }, Row: name);

    private static ChipScanViewModel Dialog(
        IReadOnlyList<ChipScanCandidate> candidates,
        IReadOnlyList<RentalChip>? pool = null,
        IReadOnlyDictionary<string, string>? holders = null,
        ChipScanSettings? settings = null)
        => new(Loc, candidates, [ChipType], pool ?? [],
            holders ?? new Dictionary<string, string>(TextKey.Comparer), settings,
            defaultFilePath: "C:\\x\\rentchip.csv", layoutStore: new NoLayoutStore());

    [Fact]
    public void Rules_DefaultFromFieldAndRoutesScans()
    {
        // Three kinds in table order: plain rental (blank), AIR rental, an own chip (spelled via a synonym).
        var rows = new[] { Candidate("Плоский", ""), Candidate("Аер", "AIR"), Candidate("Свій", "свій") };
        var vm = Dialog(rows, pool: [new RentalChip { Number = "7001", Note = "AIR" }]);

        Assert.Equal("chip-type", vm.SelectedField.Key);          // picked by its label («чіп»)
        Assert.Equal(3, vm.Rules.Count);                          // blank + the two options
        var own = vm.Rules.Single(r => r.Value == "власний");
        Assert.True(own.IsOwn);                                   // «власний» reads as an own chip by default
        Assert.Equal("AIR", vm.Rules.Single(r => r.Value == "AIR").Note);
        Assert.Equal(3, vm.Counters.Count);                       // plain, AIR, and the «skipped» line
        Assert.True(vm.Rows[2].IsSkipped);
        Assert.Same(vm.Rows[0], vm.Target);
        Assert.True(vm.HasNext);
        Assert.Contains("Плоский", vm.NextPrompt);

        // A fresh chip for the first row: rental, filed in the base with the blank rule's note.
        vm.HandleScan("5001");
        Assert.Contains("Аер", vm.NextPrompt);
        Assert.Contains("AIR", vm.NextPrompt);
        Assert.Equal("5001", vm.Rows[0].Chip);
        Assert.True(vm.Rows[0].AddToBase);
        Assert.False(vm.Rows[0].IsSkipped);
        Assert.Same(vm.Rows[1], vm.Target);

        // A base chip already noted AIR for the AIR row: assigned, not re-added.
        vm.HandleScan("7001");
        Assert.Equal("7001", vm.Rows[1].Chip);
        Assert.False(vm.Rows[1].AddToBase);

        // The own-chip row is skipped: nobody is waiting any more, a further scan is refused.
        Assert.Null(vm.Target);
        Assert.False(vm.HasNext);
        vm.HandleScan("123456");
        Assert.True(vm.IsMessageWarning);
        Assert.False(vm.Rows[2].HasChip);
        Assert.True(vm.HasAssignments);
        Assert.All(vm.Counters, c => Assert.Equal(0, c.Remaining));

        vm.ApplyCommand.Execute(null);
        var result = vm.Completion.Result;
        Assert.NotNull(result);
        Assert.Equal(2, result.Assignments.Count);
        var added = Assert.Single(result.NewRentalChips);
        Assert.Equal(("5001", ""), added);
    }

    [Fact]
    public void Mismatch_IsHeldBackUntilForced_RepeatsAndForeignRefused()
    {
        var rows = new[] { Candidate("Аер", "AIR"), Candidate("Свій", "власний") };
        var pool = new[] { new RentalChip { Number = "7001", Note = "" }, new RentalChip { Number = "7002", Note = "AIR" } };
        var holders = new Dictionary<string, string>(TextKey.Comparer) { ["9000"] = "Коваль" };
        var vm = Dialog(rows, pool, holders);

        // A plain base chip for someone who needs AIR: held, nothing assigned.
        vm.HandleScan("7001");
        Assert.True(vm.HasPending);
        Assert.Equal("7001", vm.PendingNumber);
        Assert.False(vm.Rows[0].HasChip);

        // «Присвоїти все одно» takes it (and it stays filed as it was — not re-added).
        vm.ForceAssignCommand.Execute(null);
        Assert.False(vm.HasPending);
        Assert.Equal("7001", vm.Rows[0].Chip);
        Assert.False(vm.Rows[0].AddToBase);

        // The own-chip row is skipped, so with the AIR row served nobody waits: the next chip is refused.
        vm.HandleScan("7002");
        Assert.False(vm.HasPending);
        Assert.True(vm.IsMessageWarning);
        Assert.False(vm.Rows[1].HasChip);

        // A base chip whose note matches is simply taken; a held one can be dismissed, or forced.
        vm.ClearRow(vm.Rows[0]);
        vm.HandleScan("7002");
        Assert.Equal("7002", vm.Rows[0].Chip);
        Assert.False(vm.Rows[0].AddToBase);
        vm.ClearRow(vm.Rows[0]);
        vm.HandleScan("7001");
        Assert.True(vm.HasPending);
        vm.DismissPendingCommand.Execute(null);
        Assert.False(vm.HasPending);
        Assert.False(vm.Rows[0].HasChip);
        vm.HandleScan("7001");
        vm.ForceAssignCommand.Execute(null);

        // Someone else's chip and a chip already scanned here are refused outright.
        vm.HandleScan("9000");
        Assert.True(vm.IsMessageWarning);
        Assert.False(vm.Rows[1].HasChip);
        vm.HandleScan("7001");
        Assert.True(vm.IsMessageWarning);
        Assert.False(vm.Rows[1].HasChip);

        // Taking a chip back frees the row for a new scan; the set-up is what the page saves.
        vm.ClearRow(vm.Rows[0]);
        Assert.False(vm.HasAssignments);
        var settings = vm.BuildSettings();
        Assert.Equal("chip-type", settings.FieldKey);
        Assert.Equal(string.Empty, settings.ReadoutFilePath);   // the default path is stored as blank
        Assert.True(settings.Rules.Single(r => r.Value == "власний").IsOwn);

        vm.CancelCommand.Execute(null);
        Assert.Null(vm.Completion.Result);
    }

    [Fact]
    public void SavedSettings_OverrideDefaults_AndSelectionDrivesTarget()
    {
        var saved = new ChipScanSettings
        {
            FieldKey = "chip-type",
            ReadoutFilePath = "D:\\station\\scan.csv",
            Rules =
            [
                new ChipScanTypeRule { Value = "", IsOwn = false, Note = "std" },
                new ChipScanTypeRule { Value = "AIR", IsOwn = true },      // deliberately odd: AIR = own
            ]
        };
        var rows = new[] { Candidate("А", ""), Candidate("Б", ""), Candidate("В", "AIR") };
        var vm = Dialog(rows, settings: saved);

        Assert.Equal("D:\\station\\scan.csv", vm.FilePath);
        Assert.False(vm.IsSetupExpanded);                          // set up before → collapsed
        Assert.Equal("std", vm.Rules.Single(r => r.Value == "").Note);
        Assert.True(vm.Rules.Single(r => r.Value == "AIR").IsOwn);

        // Clicking a row makes it the target; after it is served the selection moves to the next waiting row —
        // the AIR row is an own-chip (skipped) one here, so it wraps round to the first.
        Assert.True(vm.Rows[2].IsSkipped);
        vm.SelectedRow = vm.Rows[1];
        vm.HandleScan("5002");
        Assert.Equal("5002", vm.Rows[1].Chip);
        Assert.Equal("std", vm.Rows[1].Note);
        Assert.Same(vm.Rows[0], vm.SelectedRow);
        vm.HandleScan("111");
        Assert.Equal("111", vm.Rows[0].Chip);
        Assert.Null(vm.SelectedRow);                               // nobody waits any more
        Assert.False(vm.HasNext);
    }

    [Fact]
    public void RowType_CanBeChangedInTheDialog_AndIsWrittenBackOnApply()
    {
        var rows = new[] { Candidate("А", ""), Candidate("Б", "AIR"), Candidate("В", "власний") };
        var vm = Dialog(rows);
        var air = vm.Rules.Single(r => r.Value == "AIR");
        var own = vm.Rules.Single(r => r.Value == "власний");
        Assert.Equal("AIR", vm.Rows[1].SelectedRule!.Value);
        Assert.Contains("AIR", air.Caption);

        // А needs AIR after all: the row's dropdown changes its requirement and the counters at once.
        vm.Rows[0].SelectedRule = air;
        Assert.True(vm.Rows[0].Requirement.Note == "AIR");
        Assert.Equal(2, vm.Counters.Single(c => c.Label == air.Caption).Needed);

        // В brings a chip after all → scanned like anyone else; Б switched to own after a scan loses the chip.
        vm.Rows[2].SelectedRule = vm.Rules.Single(r => r.Value == "");
        Assert.True(vm.Rows[2].IsWaiting);
        vm.HandleScan("5001");                         // → А (first waiting), filed as AIR
        Assert.Equal("AIR", vm.Rows[0].Note);
        vm.HandleScan("5002");                         // → Б
        Assert.Equal("5002", vm.Rows[1].Chip);
        vm.Rows[1].SelectedRule = own;
        Assert.False(vm.Rows[1].HasChip);
        Assert.True(vm.Rows[1].IsSkipped);

        vm.ApplyCommand.Execute(null);
        var result = vm.Completion.Result!;
        Assert.Single(result.Assignments);
        var updates = result.FieldUpdates.OrderBy(u => u.Candidate.FullName).ToList();
        Assert.Equal(3, updates.Count);
        Assert.Equal(("А", "AIR"), (updates[0].Candidate.FullName, updates[0].Value));
        Assert.Equal(("Б", "власний"), (updates[1].Candidate.FullName, updates[1].Value));
        Assert.Equal(("В", ""), (updates[2].Candidate.FullName, updates[2].Value));
        Assert.All(updates, u => Assert.Equal("chip-type", u.Key));
    }

    [Fact]
    public async Task Editor_MarksStatementPrinted_CreatingTheFlagFieldOnce()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("printed"));
        await c.AddGroupAsync("Ч21", "31");
        var a = await c.AddRunnerAsync("A", "1", "", "Ч21");
        var b = await c.AddRunnerAsync("B", "2", "", "Ч21");
        await c.SelectAsync();

        var first = await Editor.MarkStatementPrintedAsync([a.Id], "Відомість надрукована");
        Assert.True(first.FieldCreated);
        Assert.Equal(CustomField.StatementPrintedKey, first.Key);
        var field = Assert.Single(await Editor.GetCustomFieldsAsync());
        Assert.Equal(CustomFieldKind.Bool, field.Kind);
        Assert.Equal("Відомість надрукована", field.Label);

        var again = await Editor.MarkStatementPrintedAsync([a.Id, b.Id], "ignored");
        Assert.False(again.FieldCreated);
        Assert.Single(await Editor.GetCustomFieldsAsync());
        var roster = await Editor.GetParticipantRosterAsync();
        Assert.All(roster, r => Assert.Equal("1", r.Fields[CustomField.StatementPrintedKey]));
    }

    [Fact]
    public void ExtractNewScans_PrimesThenReportsOnlyNewRows()
    {
        var vm = Dialog([Candidate("А", "")]);
        ChipReadData Data(params (string Chip, string Mark)[] rows) => new()
        {
            Records = rows.Select(r => new ChipReadRecord { ChipNumber = r.Chip, ReadMark = r.Mark }).ToList()
        };

        // The first tick is history, however many rows the file already has.
        Assert.Empty(vm.ExtractNewScans(Data(("1", "a"), ("2", "b"))));
        // Same file again: nothing new. A new row, a re-read (new mark) and a second identical row all count.
        Assert.Empty(vm.ExtractNewScans(Data(("1", "a"), ("2", "b"))));
        Assert.Equal(["3", "1", "2"], vm.ExtractNewScans(Data(("1", "a"), ("2", "b"), ("3", "c"), ("1", "d"), ("2", "b"))));
        Assert.Empty(vm.ExtractNewScans(Data(("1", "a"), ("2", "b"), ("3", "c"), ("1", "d"), ("2", "b"))));
    }

    [Fact]
    public async Task Editor_AddsScannedRentalChips_AndRoundTripsSettings()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("scan"));
        await c.SelectAsync();
        Assert.Null(await Editor.GetChipScanSettingsAsync());

        var existing = await Editor.AddRentalChipAsync();
        existing.Number = "7001";
        existing.Note = "AIR";
        await Editor.UpdateRentalChipAsync(existing);

        // Existing numbers keep their note; blanks and repeats collapse.
        var added = await Editor.AddRentalChipsAsync([("5001", ""), ("5002", "AIR"), ("7001", ""), (" ", "x"), ("5001", "AIR")]);
        Assert.Equal(2, added.Added);
        Assert.Equal(2, added.Skipped);
        var chips = (await Editor.GetRentalChipsAsync()).ToDictionary(x => x.Number, x => x.Note);
        Assert.Equal(3, chips.Count);
        Assert.Equal("AIR", chips["7001"]);
        Assert.Equal("", chips["5001"]);
        Assert.Equal("AIR", chips["5002"]);

        await Editor.SaveChipScanSettingsAsync(new ChipScanSettings
        {
            FieldKey = "chip-type",
            Rules = [new ChipScanTypeRule { Value = "AIR", Note = "AIR" }, new ChipScanTypeRule { Value = "власний", IsOwn = true }]
        });
        var loaded = await Editor.GetChipScanSettingsAsync();
        Assert.NotNull(loaded);
        Assert.Equal("chip-type", loaded.FieldKey);
        Assert.Equal(2, loaded.Rules.Count);
        Assert.True(loaded.Rules[1].IsOwn);

        // Saving again updates the single row rather than adding another.
        await Editor.SaveChipScanSettingsAsync(new ChipScanSettings { FieldKey = "other" });
        Assert.Equal("other", (await Editor.GetChipScanSettingsAsync())!.FieldKey);
    }

    [Fact]
    public void TooSmallCard_IsHeldBackUntilForced_UnknownOrFittingCardsPass()
    {
        // A 40-control course: an SI-Card 5 (30 punches) can't hold it, an SI-Card 9 (50) and an unknown number can.
        var rows = new[]
        {
            Candidate("Довга", "") with { NeededPunches = 40 },
            Candidate("Коротка", "") with { NeededPunches = 12 },
        };
        var vm = Dialog(rows);

        vm.HandleScan("123456");
        Assert.True(vm.HasPending);
        Assert.Equal("123456", vm.PendingNumber);
        Assert.False(vm.Rows[0].HasChip);

        // «Пропустити» drops it so another chip can be scanned; a card that fits is taken straight away.
        vm.DismissPendingCommand.Execute(null);
        Assert.False(vm.HasPending);
        vm.HandleScan("1234567");
        Assert.Equal("1234567", vm.Rows[0].Chip);
        Assert.Equal("SI-Card 9 · 50", vm.Rows[0].CardText);

        // The same small card fits the short course; an unknown number is never judged.
        vm.HandleScan("123456");
        Assert.Equal("123456", vm.Rows[1].Chip);
        vm.ClearRow(vm.Rows[1]);
        vm.HandleScan("3000000");
        Assert.Equal("3000000", vm.Rows[1].Chip);
        Assert.Equal("", vm.Rows[1].CardText);

        // «Присвоїти все одно» takes a too-small card.
        vm.ClearRow(vm.Rows[0]);
        vm.HandleScan("123456");
        Assert.True(vm.HasPending);
        vm.ForceAssignCommand.Execute(null);
        Assert.Equal("123456", vm.Rows[0].Chip);
        Assert.False(vm.HasPending);

        // With another punching system (Sport Time) the numbers say nothing about the card: no hold, no card text.
        var other = new ChipScanViewModel(Loc, rows, [ChipType], [], new Dictionary<string, string>(TextKey.Comparer),
            null, "C:\\x\\rentchip.csv", new NoLayoutStore(), sportIdentCards: false);
        Assert.False(other.ShowCardColumn);
        other.HandleScan("123456");
        Assert.False(other.HasPending);
        Assert.Equal("123456", other.Rows[0].Chip);
        Assert.Equal("", other.Rows[0].CardText);
    }

    private sealed class NoLayoutStore : ITableLayoutStore
    {
        public string? CurrentScopeId => null;
        public TableLayout? Load(string tableKey) => null;
        public void Save(string tableKey, TableLayout layout) { }
        public void SaveDefault(string tableKey, TableLayout layout) { }
        public void ClearLayout(string tableKey) { }
        public bool HasDefault(string tableKey) => false;
    }
}
