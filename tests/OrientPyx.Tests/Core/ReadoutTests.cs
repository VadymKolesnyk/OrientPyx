using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Presentation.Services;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>The chip read-out parsers (SportIdent and Sport Time CSV) and the read-out import into the finish log.</summary>
[Collection(AppCollection.Name)]
public sealed class ReadoutTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private const string SportIdentFile =
        "No.;Read on;SIID;Start DOW;Start time;Finish DOW;Finish time;No. of records;\n" +
        "1;2024-06-15 11:20:03;8001234;Sa;10:00:00;Sa;10:25:30;3;31;Sa;10:05:00;32;Sa;10:12:00;33;Sa;10:20:00;\n" +
        "2;2024-06-15 11:30:10;8001235;Sa;10:02:00;Sa;10:31:00;2;31;Sa;10:08:00;;;;33;Sa;10:26:00;\n" +
        "\n" +
        "3;2024-06-15 11:40:00;8001236;Fr;23:50:00;Sa;00:20:00;1;31;Sa;00:05:00;\n";

    private const string SportTimeFile =
        "No.;read at;SI-Card;start time;Finish time;No. of punches;1.CN;1.DOW;1.Time;2.CN;2.DOW;2.Time;3.CN;3.DOW;3.Time\n" +
        "1;11:30:00;2101234;10:00:00 (Сб);10:25:30 (Сб);3;31;;10:05:00 (Сб);32;;10:12:00;33;;10:20:00\n" +
        "2;11:35:00;2101235;10:02:00;10:31:00;2;31;;10:08:00;;;;33;;10:26:00\n";

    [Fact]
    public void SportIdent_NewFileHeader_IsTheConfigPlusLayout_AndParsesAsEmpty()
    {
        var parser = new SportIdentCsvReadoutParser();
        var header = parser.NewFileHeader!;
        Assert.True(parser.CanParse(header));
        Assert.Empty(parser.Parse(header).Records);
        // A ReaderUI data row appended under that header reads with the chip, finish and punches in place.
        var row = "2;2026-11-05 16:09:10;2771457;;1;Th; 21:33:59;;;;1;Th; 21:33:59;;;;;;;4;Fr; 15:58:29;;;;;club;\"Sever\";" +
                  ";;;;;;;;;;;;;;;;2;42;Fr; 13:11:36;76;Fr; 13:17:50;\r\n";
        var record = Assert.Single(parser.Parse(header + row).Records);
        Assert.Equal("2771457", record.ChipNumber);
        Assert.Equal(new TimeSpan(15, 58, 29), record.FinishTime!.Value.TimeOfDay);
        Assert.Equal(["42", "76"], record.Punches.Select(p => p.ControlCode));
        Assert.Null(((IReadoutParser)new SportTimeCsvReadoutParser()).NewFileHeader);
    }

    [Fact]
    public void Poller_SeedsANewOrEmptyFileWithTheHeader_AndLeavesContentAlone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "opyx-poller-" + Guid.NewGuid().ToString("N"));
        try
        {
            var header = new SportIdentCsvReadoutParser().NewFileHeader;
            var fresh = Path.Combine(dir, "sub", "new.csv");
            FileReadoutPoller.EnsureFileExists(fresh, header);
            Assert.Equal(header, File.ReadAllText(fresh));

            var empty = Path.Combine(dir, "empty.csv");
            File.WriteAllText(empty, string.Empty);
            FileReadoutPoller.EnsureFileExists(empty, header);
            Assert.Equal(header, File.ReadAllText(empty));

            var full = Path.Combine(dir, "full.csv");
            File.WriteAllText(full, SportIdentFile);
            FileReadoutPoller.EnsureFileExists(full, header);
            Assert.Equal(SportIdentFile, File.ReadAllText(full));

            var plain = Path.Combine(dir, "plain.csv");
            FileReadoutPoller.EnsureFileExists(plain);
            Assert.Equal(string.Empty, File.ReadAllText(plain));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SportIdent_ParsesRecordsPunchesAndDates()
    {
        var parser = new SportIdentCsvReadoutParser();
        Assert.True(parser.CanParse(SportIdentFile));
        var data = parser.Parse(SportIdentFile);
        Assert.Equal("SportIdent-CSV", data.SourceFormat);
        Assert.Equal(3, data.Records.Count);

        var first = data.Records[0];
        Assert.Equal("8001234", first.ChipNumber);
        Assert.Equal(["31", "32", "33"], first.Punches.Select(p => p.ControlCode));
        Assert.Equal(new DateTime(2024, 6, 15, 10, 0, 0), first.StartTime!.Value.DateTime);
        Assert.Equal(new DateTime(2024, 6, 15, 10, 25, 30), first.FinishTime!.Value.DateTime);
        Assert.Equal(new DateTime(2024, 6, 15, 10, 12, 0), first.Punches[1].Time!.Value.DateTime);
        Assert.Equal("2024-06-15 11:20:03", first.ReadMark);

        var second = data.Records[1];
        Assert.Equal(["31", "33"], second.Punches.Select(p => p.ControlCode)); // the blank triplet is a missed punch

        var midnight = data.Records[2];
        Assert.Equal(new DateTime(2024, 6, 14, 23, 50, 0), midnight.StartTime!.Value.DateTime); // Friday start
        Assert.Equal(new DateTime(2024, 6, 15, 0, 20, 0), midnight.FinishTime!.Value.DateTime);
    }

    [Fact]
    public void SportIdent_RejectsAFileWithoutTheHeader()
    {
        var parser = new SportIdentCsvReadoutParser();
        const string headerless = "1;2024-06-15 11:20:03;8001234;Sa;10:00:00;Sa;10:25:30;0;\n";
        Assert.False(parser.CanParse(headerless));
        Assert.Throws<ReadoutFormatException>(() => parser.Parse(headerless));
        Assert.False(parser.CanParse(SportTimeFile));
    }

    [Fact]
    public void SportTime_ParsesUkrainianWeekdaysAndTimes()
    {
        var parser = new SportTimeCsvReadoutParser();
        Assert.True(parser.CanParse(SportTimeFile));
        var data = parser.Parse(SportTimeFile);
        Assert.Equal("SportTime-CSV", data.SourceFormat);
        Assert.Equal(2, data.Records.Count);
        var first = data.Records[0];
        Assert.Equal("2101234", first.ChipNumber);
        Assert.Equal(["31", "32", "33"], first.Punches.Select(p => p.ControlCode));
        Assert.Equal(TimeSpan.FromMinutes(25.5), first.FinishTime - first.StartTime);
        Assert.Equal(TimeSpan.FromMinutes(5), first.Punches[0].Time - first.StartTime);
        Assert.Equal("11:30:00", first.ReadMark);
        Assert.Equal(["31", "33"], data.Records[1].Punches.Select(p => p.ControlCode));
    }

    [Fact]
    public void ParserSelector_FollowsTheReadoutType()
    {
        var selector = Sp.GetRequiredService<IReadoutParserSelector>();
        Assert.IsType<SportIdentCsvReadoutParser>(selector.For(ReadoutType.SportIdent));
        Assert.IsType<SportTimeCsvReadoutParser>(selector.For(ReadoutType.SportTime));
    }

    [Fact]
    public void CsvEncodingReader_PrefersUtf8ThenFallsBackTo1251()
    {
        XmlEncodingReader.EnsureCodePagesRegistered();
        const string text = "1;11:30:00;2101234;10:00:00 (Сб)";
        Assert.Equal(text, CsvEncodingReader.Decode(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, CsvEncodingReader.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()));
        Assert.Equal(text, CsvEncodingReader.Decode(Encoding.GetEncoding(1251).GetBytes(text)));
    }

    [Fact]
    public async Task ImportFinishReadouts_AddsResolvesDeduplicatesAndClearsADnsOverride()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rimp"), date: new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.FromHours(3)));
        await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("Ч21", "31 32 33");
        var known = await c.AddRunnerAsync("Known", "1", "8001234", "Ч21", configureDay: d => d.ResultStatusOverride = FinishStatus.Dns);
        await c.AddRunnerAsync("Missing", "2", "8001235", "Ч21");
        await c.SelectAsync();

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = new SportIdentCsvReadoutParser().Parse(SportIdentFile);
        var first = await editor.ImportFinishReadoutsAsync(data);
        Assert.Equal(3, first.Added);
        Assert.Equal(0, first.Skipped);
        Assert.Equal(3, first.AddedReads.Count);

        var again = await editor.ImportFinishReadoutsAsync(data);
        Assert.Equal(0, again.Added);
        Assert.Equal(3, again.Skipped);
        Assert.Equal(3, again.Duplicates.Count);

        var rows = (await editor.GetFinishReadoutRowsAsync()).OrderBy(r => r.Order).ToList();
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].IsKnown);
        Assert.Equal("Known", rows[0].FullName);
        Assert.Equal("Ч21", rows[0].GroupName);
        Assert.Equal(FinishStatus.Ok, rows[0].Status);
        Assert.Equal(TimeSpan.FromMinutes(25.5), rows[0].Elapsed);
        Assert.Equal(FinishStatus.Mp, rows[1].Status);
        Assert.Equal("32", rows[1].StatusDetail);
        Assert.False(rows[2].IsKnown);                         // chip 8001236 belongs to nobody
        Assert.Equal("", rows[2].FullName);

        var results = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, results["Known"].Status); // the read cleared the DNS override
        Assert.Null(results["Known"].Override);
        var link = await c.GetLinkAsync("Known", 1);
        Assert.Null(link.ResultStatusOverride);
        Assert.Equal(known.Id, link.ParticipantId);

        Assert.Equal(3, await editor.ClearFinishReadoutsAsync());
        Assert.Empty(await editor.GetFinishReadoutRowsAsync());
    }

    [Fact]
    public async Task EditReadout_ManualStatusReassignmentAndNewParticipant()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("redit"));
        await c.AddControlsAsync(1, "31");
        var group = await c.AddGroupAsync("Ч21", "31");
        var holder = await c.AddRunnerAsync("Holder", "1", "7001", "Ч21", CompetitionBuilder.At(10, 0));
        var other = await c.AddRunnerAsync("Other", "2", "", "Ч21", CompetitionBuilder.At(10, 0));
        var readout = await c.AddReadoutAsync(1, "7001", "31", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 20));
        var unknown = await c.AddReadoutAsync(1, "7999", "31", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 25));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        var edit = await editor.GetFinishReadoutEditAsync(readout.Id);
        Assert.NotNull(edit);
        Assert.Equal(holder.Id, edit.CurrentHolderId);
        Assert.Equal(FinishStatus.Ok, edit.Status);
        Assert.Contains(edit.Participants, p => p.ParticipantId == other.Id);

        // 1. A manual DSQ on the holder.
        await editor.UpdateFinishReadoutAsync(new FinishReadoutEdit
        {
            Id = readout.Id, ChipNumber = "7001", StartTime = readout.StartTime, FinishTime = readout.FinishTime,
            Punches = [new ChipPunch("31", null)], ManualStatus = FinishStatus.Dsq,
        });
        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Dsq, r["Holder"].Status);
        Assert.True((await editor.GetFinishReadoutRowsAsync()).Single(x => x.Id == readout.Id).IsManualStatus);

        // 1b. Clearing the manual status clears the override again.
        await editor.UpdateFinishReadoutAsync(new FinishReadoutEdit
        {
            Id = readout.Id, ChipNumber = "7001", StartTime = readout.StartTime, FinishTime = readout.FinishTime,
            Punches = [new ChipPunch("31", null)], ManualStatus = null,
        });
        Assert.Equal(FinishStatus.Ok, (await c.ResultsAsync())["Holder"].Status);

        // 2. Reassign the chip to the other runner: the previous holder loses it.
        await editor.UpdateFinishReadoutAsync(new FinishReadoutEdit
        {
            Id = readout.Id, ChipNumber = "7001", StartTime = readout.StartTime, FinishTime = readout.FinishTime,
            Punches = [new ChipPunch("31", null)], ManualStatus = null, ReassignToParticipantId = other.Id,
        });
        Assert.Equal("", (await c.GetLinkAsync("Holder", 1)).Chip);
        Assert.Equal("7001", (await c.GetLinkAsync("Other", 1)).Chip);
        r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Other"].Status);
        Assert.Equal(FinishStatus.None, r["Holder"].Status);

        // 3. An unknown chip gets a brand-new participant.
        await editor.UpdateFinishReadoutAsync(new FinishReadoutEdit
        {
            Id = unknown.Id, ChipNumber = "7999", StartTime = unknown.StartTime, FinishTime = unknown.FinishTime,
            Punches = [new ChipPunch("31", null)],
            NewParticipant = new NewParticipantData { FullName = "Новий Учасник", GroupId = group.Id, Number = "99" },
        });
        var rows = await editor.GetFinishReadoutRowsAsync();
        var created = rows.Single(x => x.Id == unknown.Id);
        Assert.True(created.IsKnown);
        Assert.Equal("Новий Учасник", created.FullName);
        Assert.Equal("99", created.ParticipantNumber);
        Assert.Equal(FinishStatus.Ok, created.Status);
    }

    [Fact]
    public async Task EditReadout_MovesHolderToAnotherGroup()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rgroup"));
        await c.AddControlsAsync(1, "31");
        var men = await c.AddGroupAsync("Ч21", "31");
        var women = await c.AddGroupAsync("Ж21", "31");
        var holder = await c.AddRunnerAsync("Holder", "1", "7001", "Ч21", CompetitionBuilder.At(10, 0));
        var readout = await c.AddReadoutAsync(1, "7001", "31", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 20));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        // The modal is told each member's group, so it can open the group picker on it.
        var data = await editor.GetFinishReadoutEditAsync(readout.Id);
        Assert.NotNull(data);
        Assert.Equal(men.Id, data.Participants.Single(p => p.ParticipantId == holder.Id).GroupId);

        await editor.UpdateFinishReadoutAsync(new FinishReadoutEdit
        {
            Id = readout.Id, ChipNumber = "7001", StartTime = readout.StartTime, FinishTime = readout.FinishTime,
            Punches = [new ChipPunch("31", null)], GroupId = women.Id,
        });

        var link = await c.GetLinkAsync("Holder", 1);
        Assert.Equal(women.Id, link.GroupId);
        Assert.Equal("7001", link.Chip);
        Assert.Equal("Ж21", (await editor.GetFinishReadoutRowsAsync()).Single(x => x.Id == readout.Id).GroupName);
    }
}
