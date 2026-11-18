using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Undo;
using OrientPyx.DataAccess.Persistence;
using OrientPyx.DataAccess.Persistence.Undo;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.Core.Undo.UndoKit;

namespace OrientPyx.Tests.Core.Undo;

/// <summary>
/// What the change recorder takes into a step and what it keeps out (§6.2…§6.5): only committed saves, nothing of a
/// bulk statement but the «not undoable» mark, no read (not even one that seeds a row), no background work, nothing
/// written after the step ended or was reverted; the journal's answers for an empty, unknown and evicted step, its
/// limits on a journal of the test's own, and the completeness of the reference rules over the model.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UndoRecorderTests
{
    private static async Task<CompetitionBuilder> BuildAsync()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("undo_rec"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Іваненко Іван", "1", "1001", "Ч21");
        await c.SelectAsync();
        return c;
    }

    private static UndoStepRecord? Record(CompetitionBuilder c, Guid step)
        => UndoJournal.Shared.Find(c.Folder, step, null, out var record) == UndoLookup.Found ? record : null;

    private static IReadOnlyList<UndoEntityChange> ChangesOf(CompetitionBuilder c, Guid step)
        => Record(c, step) is { } record ? UndoJournal.Shared.SetsOf(record).SelectMany(s => s.Changes).ToList() : [];

    private static async Task<IReadOnlyList<string>> RegionNamesAsync(CompetitionBuilder c)
        => (await Store.GetRegionsAsync(c.Folder)).Select(r => r.Name).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task OnlyCommittedSavesAreJournalled()
    {
        var c = await BuildAsync();
        await Store.AddRegionAsync(c.Folder, new Region { Name = "Є" });

        var step = await StepAsync(async () =>
        {
            // Rolled back explicitly.
            await using (var db = EventDbContextFactory.Create(c.Folder))
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                db.Regions.Add(new Region { Name = "Відкочено" });
                await db.SaveChangesAsync();
                await tx.RollbackAsync();
            }
            // Never committed (the transaction is disposed, which rolls back without telling anyone).
            await using (var db = EventDbContextFactory.Create(c.Folder))
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                db.Regions.Add(new Region { Name = "Без коміту" });
                await db.SaveChangesAsync();
            }
            // A save the unique index refuses.
            await using (var db = EventDbContextFactory.Create(c.Folder))
            {
                db.Regions.Add(new Region { Name = "Є" });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
            // Committed: the only change of the step.
            await using (var db = EventDbContextFactory.Create(c.Folder))
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                db.Regions.Add(new Region { Name = "Записано" });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
        });

        var change = Assert.Single(ChangesOf(c, step));
        Assert.Equal(typeof(Region), change.EntityType);
        Assert.Equal(UndoChangeKind.Added, change.Kind);
        Assert.Equal("Записано", change.After[nameof(Region.Name)]);
        Assert.Equal(["Є", "Записано"], await RegionNamesAsync(c));

        await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Equal(["Є"], await RegionNamesAsync(c));
    }

    [Fact]
    public async Task ABulkWriteMakesTheWholeStepNotUndoable()
    {
        var c = await BuildAsync();
        await c.AddReadoutAsync(1, "1001", "31", null, CompetitionBuilder.At(10, 30));

        var step = await StepAsync(async () =>
        {
            await Editor.SetParticipantDayChipAsync(c.Runners["Іваненко Іван"].Id, c.Day(1).Id, "2001");   // tracked
            await Editor.ClearFinishReadoutsAsync();                                                        // ExecuteDelete
        });

        Assert.True(Record(c, step)!.NotUndoable);
        Assert.Empty(ChangesOf(c, step));
        var before = await EventDbDump.TakeAsync(c.Folder);
        await RevertExpectAsync(step, UndoRevertStatus.NotUndoable);
        Assert.Equal(before, await EventDbDump.TakeAsync(c.Folder));
    }

    [Fact]
    public async Task TheFsouDiscountSeededByAReadInsideAStepIsNotPartOfIt()
    {
        var c = await BuildAsync();
        // Make sure the read has something to seed (an earlier page load may have seeded it already).
        await ExecuteAsync(c, "DELETE FROM EntryFeeDiscounts WHERE IsFsouMemberDiscount = 1");

        EntryFeeDiscount? added = null;
        var step = await StepAsync(async () =>
        {
            Assert.Contains(await Editor.GetEntryFeeDiscountsAsync(), d => d.IsFsouMemberDiscount);   // seeds it
            added = await Editor.AddEntryFeeDiscountRowAsync();
        });

        var change = Assert.Single(ChangesOf(c, step));
        Assert.Equal(added!.Id, change.Key[0]);

        await RevertExpectAsync(step, UndoRevertStatus.Done);
        var discounts = await Store.GetEntryFeeDiscountsAsync(c.Folder);
        Assert.Contains(discounts, d => d.IsFsouMemberDiscount);
        Assert.DoesNotContain(discounts, d => d.Id == added.Id);
    }

    [Fact]
    public async Task BackgroundWorkStartedUnderSuppressIsNotPartOfTheStep()
    {
        var c = await BuildAsync();
        Task? background = null;
        var step = await StepAsync(async () =>
        {
            await Store.AddRegionAsync(c.Folder, new Region { Name = "Крок" });
            using (UndoScope.Suppress())
            {
                await Store.AddRegionAsync(c.Folder, new Region { Name = "Система" });
                // A loop started here (the finish auto-read, the monitor) keeps no step for its whole life.
                background = Task.Run(() => Store.AddRegionAsync(c.Folder, new Region { Name = "Фон" }));
            }
            await background;
        });

        Assert.Equal("Крок", Assert.Single(ChangesOf(c, step)).After[nameof(Region.Name)]);
        await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Equal(["Система", "Фон"], await RegionNamesAsync(c));
    }

    [Fact]
    public async Task ATaskThatOutlivesItsStepIsKeptOutOfItAndLogged()
    {
        var c = await BuildAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? late = null;
        var lines = new List<string>();
        var previousLog = UndoRecorder.Log;
        UndoRecorder.Log = line =>
        {
            lock (lines)
                lines.Add(line);
            previousLog?.Invoke(line);
        };
        try
        {
            var step = await StepAsync(async () =>
            {
                await Store.AddRegionAsync(c.Folder, new Region { Name = "Крок" });
                // Fire-and-forget: inherits the step, but writes after the step's last entry was given back.
                late = Task.Run(async () =>
                {
                    await gate.Task;
                    await Store.AddRegionAsync(c.Folder, new Region { Name = "Запізно" });
                });
            });
            Assert.False(UndoScope.IsActive(step));
            gate.SetResult();
            await late!;

            Assert.Equal("Крок", Assert.Single(ChangesOf(c, step)).After[nameof(Region.Name)]);
            lock (lines)
                Assert.Contains(lines, l => l.Contains(step.ToString(), StringComparison.Ordinal));
            await RevertExpectAsync(step, UndoRevertStatus.Done);
            Assert.Equal(["Запізно"], await RegionNamesAsync(c));
        }
        finally
        {
            UndoRecorder.Log = previousLog;
        }
    }

    [Fact]
    public async Task AWriteUnderAnAlreadyRevertedStepIsKeptOutOfIt()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => Store.AddRegionAsync(c.Folder, new Region { Name = "Перший" }));
        var redo = await RevertExpectAsync(step, UndoRevertStatus.Done);

        // The same cell session writes again after Ctrl+Z (the UI gives it a new step; the journal guards it anyway).
        using (UndoScope.Enter(step))
            await Store.AddRegionAsync(c.Folder, new Region { Name = "Після відкату" });

        Assert.True(Record(c, step)!.Reverted);
        Assert.Equal("Перший", Assert.Single(ChangesOf(c, step)).After[nameof(Region.Name)]);
        await RevertExpectAsync(step, UndoRevertStatus.NotFound, "a reverted step again");
        await RevertExpectAsync(redo, UndoRevertStatus.Done, "redo");
        Assert.Equal(["Перший", "Після відкату"], await RegionNamesAsync(c));
    }

    [Fact]
    public async Task AWriteThatChangedNothingIsAnEmptyStep()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => Editor.SetParticipantsCheckedInAsync([c.Runners["Іваненко Іван"].Id], false));

        Assert.NotNull(Record(c, step));
        await RevertExpectAsync(step, UndoRevertStatus.Empty);
    }

    [Fact]
    public async Task AStepNoWriteReachedIsNotFound()
    {
        var c = await BuildAsync();
        var reads = await StepAsync(async () =>
        {
            await Editor.GetParticipantDayRowsAsync();
            await Editor.GetEntryFeeDiscountsAsync();
        });
        // The service refuses before the store: the day is closed.
        await Editor.SetDayLockedAsync(c.Day(1).Id, true);
        await c.SelectAsync();
        var refused = await StepAsync(async () => await Assert.ThrowsAsync<DayLockedException>(
            () => Editor.SetParticipantDayChipAsync(c.Runners["Іваненко Іван"].Id, c.Day(1).Id, "2001")));
        await Editor.SetDayLockedAsync(c.Day(1).Id, false);

        await RevertExpectAsync(reads, UndoRevertStatus.NotFound, "reads only");
        await RevertExpectAsync(refused, UndoRevertStatus.NotFound, "refused by the service");
        await RevertExpectAsync(Guid.NewGuid(), UndoRevertStatus.NotFound, "never used");
    }

    [Fact]
    public async Task ReleasingTheCompetitionFolderForgetsItsSteps()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => Store.AddRegionAsync(c.Folder, new Region { Name = "Крок" }));

        // Renaming the identifier, deleting the competition or importing an archive over it release the folder first.
        await Store.ReleaseAsync(c.Folder);

        await RevertExpectAsync(step, UndoRevertStatus.NotFound);
        Assert.Equal(["Крок"], await RegionNamesAsync(c));
    }

    [Fact]
    public async Task AnEvictedStepIsReportedAsEvicted()
    {
        var c = await BuildAsync();
        var journal = new UndoJournal { MaxStepsPerOwner = 1 };
        var first = Guid.NewGuid();
        journal.Append(c.Folder, first, null, Set(1));
        journal.Append(c.Folder, Guid.NewGuid(), null, Set(1));

        var result = await UndoReverter.RevertAsync(c.Folder, first, null, journal, null, CancellationToken.None);

        Assert.Equal(UndoRevertStatus.Evicted, result.Status);
    }

    [Fact]
    public void JournalLimitsAndEviction()
    {
        const string path = "undo-journal-limits";
        var journal = new UndoJournal { MaxStepsPerOwner = 3, MaxChanges = 8, MaxChangesPerStep = 4, MaxEvictedRemembered = 2 };
        UndoLookup Find(Guid step, string? owner = null) => journal.Find(path, step, owner, out _);

        // An empty step (registered, nothing written) goes first: it held nothing, so it reads as never seen.
        var empty = Guid.NewGuid();
        journal.Touch(path, empty, null);
        var s1 = Append(journal, path, 1);
        var s2 = Append(journal, path, 1);
        var s3 = Append(journal, path, 1);
        Assert.Equal(UndoLookup.NotFound, Find(empty));
        Assert.Equal(UndoLookup.Found, Find(s1));
        Assert.Equal(3, journal.StepCount(path, null));

        // Without empty steps the oldest one goes, and the journal tells it was evicted.
        var s4 = Append(journal, path, 1);
        Assert.Equal(UndoLookup.Evicted, Find(s1));
        Assert.Equal(UndoLookup.Found, Find(s4));

        // Another owner's steps neither count against this owner nor can be found by it.
        var station = Append(journal, path, 1, "station-token");
        Assert.Equal(3, journal.StepCount(path, null));
        Assert.Equal(1, journal.StepCount(path, "station-token"));
        Assert.Equal(UndoLookup.NotFound, Find(station));
        Assert.Equal(UndoLookup.Found, Find(station, "station-token"));
        Assert.Equal(UndoLookup.NotFound, Find(s2, "station-token"));

        // A step past its own limit keeps nothing and is not undoable.
        var big = Append(journal, path, 5);
        journal.Find(path, big, null, out var record);
        Assert.True(record!.NotUndoable);
        Assert.Equal(0, record.ChangeCount);
        Assert.Equal(UndoLookup.Evicted, Find(s2));

        // Past the limit over all steps the oldest steps of any owner go.
        var s5 = Append(journal, path, 4);
        var s6 = Append(journal, path, 4);
        Assert.True(journal.TotalChanges <= 8, $"{journal.TotalChanges} changes kept");
        Assert.Equal(UndoLookup.Found, Find(s6));
        Assert.NotEqual(UndoLookup.Found, Find(s3));

        // Only the last evicted ids are remembered.
        Assert.Equal(UndoLookup.NotFound, Find(s1));

        // Forgetting a competition drops all of it.
        journal.Forget(path);
        Assert.Equal(0, journal.StepCount(path, null));
        Assert.Equal(UndoLookup.NotFound, Find(s6));
        Assert.Equal(UndoLookup.NotFound, Find(s5));
        Assert.Equal(0, journal.TotalChanges);
    }

    [Fact]
    public void EveryReferenceOfTheModelIsAReferenceRuleOrExcluded()
    {
        using var db = EventDbContextFactory.Create(Path.Combine(Path.GetTempPath(), "undo-model-only"));
        var model = db.Model;
        var listed = UndoReferenceRules.All.Select(r => (r.Entity, r.Property)).ToHashSet();
        var missing = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            var key = entity.FindPrimaryKey();
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType != typeof(Guid) && property.ClrType != typeof(Guid?))
                    continue;
                if (!property.Name.EndsWith("Id", StringComparison.Ordinal))
                    continue;
                if (key is { Properties.Count: 1 } && key.Properties[0] == property)
                    continue;
                var pair = (entity.ClrType, property.Name);
                if (!listed.Contains(pair) && !UndoReferenceRules.NotReferences.Contains(pair))
                    missing.Add($"{entity.ClrType.Name}.{property.Name}");
            }
        }
        Assert.True(missing.Count == 0, "Not in UndoReferenceRules (nor NotReferences): " + string.Join(", ", missing));

        // And every rule names a real property and a real target table.
        foreach (var rule in UndoReferenceRules.All)
        {
            Assert.True(model.FindEntityType(rule.Entity)?.FindProperty(rule.Property) is not null, $"{rule.Entity.Name}.{rule.Property}");
            Assert.NotNull(model.FindEntityType(rule.Target));
        }
    }

    private static Guid Append(UndoJournal journal, string path, int changes, string? owner = null)
    {
        var step = Guid.NewGuid();
        journal.Append(path, step, owner, Set(changes));
        return step;
    }

    private static UndoChangeSet Set(int changes) => new(Enumerable.Range(0, changes)
        .Select(_ => new UndoEntityChange { EntityType = typeof(Region), Key = [Guid.NewGuid()], Kind = UndoChangeKind.Added })
        .ToList());

    private static async Task ExecuteAsync(CompetitionBuilder c, string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = Path.Combine(c.Folder, "event.db"), Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
