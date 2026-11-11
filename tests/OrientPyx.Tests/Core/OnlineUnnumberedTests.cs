using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Online;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// Online results without start numbers: a runner with no number (or the second holder of a shared one) is published
/// under a stand-in negative bib, the same on every day, which the site doesn't show; once the runner gets a number the
/// stand-in row is deleted from the site.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class OnlineUnnumberedTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    [Fact]
    public async Task Snapshot_PublishesRunnersWithoutANumberUnderAStableStandIn()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("online-unnumbered"), dayCount: 2);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddControlsAsync(2, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddRunnerAsync("Номер", "5", "901", "Ч21");
        var blank = await c.AddRunnerAsync("БезНомера", "", "902", "Ч21");
        var twin = await c.AddRunnerAsync("Двійник", "5", "903", "Ч21");
        await c.SelectAsync();

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var day1 = await editor.GetOnlineResultsSnapshotAsync(c.Day(1).Id);
        Assert.Equal(3, day1.Rows.Count);                                    // nobody is left out
        Assert.Equal(1, day1.Unnumbered);                                    // the shared number is reported apart
        Assert.StartsWith("5 (", Assert.Single(day1.DuplicateNumbers!));

        var unnumbered = day1.Rows.Single(r => r.FullName == "БезНомера");
        Assert.Equal(OnlineResultRow.UnnumberedKey(blank.Id), unnumbered.Key);
        Assert.True(unnumbered.Key < 0);
        Assert.Null(unnumbered.Bib);

        // Of the two holders of 5, the first keeps it, the other goes under its own stand-in.
        var holders = day1.Rows.Where(r => r.FullName is "Номер" or "Двійник").ToList();
        var keeper = Assert.Single(holders, r => r.Key == 5);
        Assert.Equal(5, keeper.Bib);
        var other = holders.Single(r => r != keeper);
        var otherId = other.FullName == "Номер" ? c.Runners["Номер"].Id : twin.Id;
        Assert.Equal(OnlineResultRow.UnnumberedKey(otherId), other.Key);
        Assert.Null(other.Bib);

        // Day 2: the same stand-in, so the site's «Сума» joins the runner's days.
        var day2 = await editor.GetOnlineResultsSnapshotAsync(c.Day(2).Id);
        Assert.Equal(unnumbered.Key, day2.Rows.Single(r => r.FullName == "БезНомера").Key);
    }

    [Fact]
    public async Task Publisher_DeletesTheStandInOnceTheRunnerHasANumber()
    {
        var handler = new SiteHandler { OnlineStandIns = [-77] };            // left on the site by an earlier run
        using var publisher = new SupabaseResultPublisher(new HttpClient(handler));

        await publisher.PublishAsync(Publish(), Api, Snapshot(key: -5));
        Assert.Equal(-5, handler.Upserted.Last()[0].GetProperty("bib").GetInt32());
        Assert.Equal(1, handler.Reads);                                      // asks the site once for the day
        Assert.Equal(["-77"], handler.Deleted);

        // The runner got number 9: written under it, the stand-in row goes.
        await publisher.PublishAsync(Publish(), Api, Snapshot(key: 9));
        Assert.Equal(9, handler.Upserted.Last()[0].GetProperty("bib").GetInt32());
        Assert.Equal(1, handler.Reads);
        Assert.Equal(["-77", "-5"], handler.Deleted);

        // Nothing changed: no read, no delete.
        await publisher.PublishAsync(Publish(), Api, Snapshot(key: 9));
        Assert.Equal(1, handler.Reads);
        Assert.Equal(2, handler.Deleted.Count);
    }

    private static readonly OnlineApiSettings Api = new("https://example.invalid", "key", "", 15);

    private static OnlinePublishSettings Publish() => OnlinePublishSettings.Default("cup", "Cup", "");

    private static OnlineResultsSnapshot Snapshot(int key) =>
        new([new OnlineDay(1, "")], 1, [new OnlineGroup("Ч21", null, null, 0)],
            [
                new OnlineResultRow(key, "Ч21", "Бігун", "", "", "", "", "", null, null, null, null,
                    null, null, FinishStatus.None, HasReadout: false, OutOfCompetition: false),
            ],
            Unnumbered: key < 0 ? 1 : 0);

    // A PostgREST stand-in: records the results upserts, answers the read of stand-in bibs, and records which bibs
    // a delete names (from its `bib=in.(…)` filter).
    private sealed class SiteHandler : HttpMessageHandler
    {
        public int[] OnlineStandIns { get; init; } = [];

        public List<JsonElement[]> Upserted { get; } = [];

        public List<string> Deleted { get; } = [];

        public int Reads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var table = uri.AbsolutePath.Split('/').Last();
            var query = Uri.UnescapeDataString(uri.Query);
            if (table == "results" && request.Method == HttpMethod.Get)
            {
                Assert.Contains("bib=lt.0", query);
                Reads++;
                var rows = string.Join(",", OnlineStandIns.Select(b => $$"""{"bib":{{b}}}"""));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"[{rows}]") };
            }
            if (table == "results" && request.Method == HttpMethod.Delete)
            {
                var list = query[(query.IndexOf("bib=in.(", StringComparison.Ordinal) + 8)..].TrimEnd(')');
                Deleted.AddRange(list.Split(','));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (table == "results" && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Upserted.Add(JsonDocument.Parse(body).RootElement.EnumerateArray().ToArray());
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
