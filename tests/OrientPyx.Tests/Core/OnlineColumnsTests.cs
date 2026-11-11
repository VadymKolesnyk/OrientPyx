using System.Net;
using System.Text;
using System.Text.Json;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Online;

namespace OrientPyx.Tests.Core;

/// <summary>
/// Extra columns online (§10.2): the online layout offers the day's plugin columns and additional fields beside the
/// built-in ones, the publisher labels them in <c>display_config</c> and sends their values in <c>results.extra</c>,
/// and a project without that column keeps publishing everything else. «Наростаючий» joins an older layout visible.
/// </summary>
public sealed class OnlineColumnsTests
{
    private static readonly OnlineExtraColumn Time = new("weighted.time", "Час");
    private static readonly OnlineExtraColumn Gender = new("field.gender", "Стать", IsPlugin: false);

    [Fact]
    public void Resolve_PlacesNewColumnsAndKeepsExtrasTheDayLacks()
    {
        // A layout saved before «Наростаючий» and the extras existed (the whole catalogue of then, reordered: «Статус»
        // first), with an extra of another day.
        var older = OnlineDisplayConfig.Default.Columns.Where(c => c.Key != "cumulative").ToList();
        var saved = new OnlineDisplayConfig(
            [.. older.Where(c => c.Key == "status"), .. older.Where(c => c.Key != "status"), new("other.k", true, false)],
            SeparateDsqLg: true, SeparateDsqSm: false);

        var resolved = saved.Resolve([Time, Gender]);
        var keys = resolved.Select(c => c.Def.Key).ToList();
        Assert.Equal("status", keys[0]);
        Assert.Equal(keys.IndexOf("gap") + 1, keys.IndexOf("cumulative"));  // after the column it follows by default
        var cumulative = resolved.Single(c => c.Def.Key == "cumulative");
        Assert.True(cumulative.Lg && cumulative.Sm);                     // the default's visibility, not hidden
        Assert.False(resolved.Single(c => c.Def.Key == "birth").Visible); // an older catalogue column stays off
        var time = resolved.Single(c => c.Def.Key == "weighted.time");
        Assert.Equal(ResultColumn.Extra, time.Def.Column);
        Assert.Equal("Час", time.Def.LabelKey);
        Assert.True(time.Lg);                                             // a plugin column: shown on the large screen
        Assert.False(time.Sm);
        Assert.False(resolved.Single(c => c.Def.Key == "field.gender").Visible); // a field: off until switched on
        Assert.DoesNotContain("other.k", keys);                           // not offered today…
        Assert.Equal(["other.k"], saved.Unresolved([Time, Gender]).Select(c => c.Key)); // …but kept for saving
    }

    [Fact]
    public async Task Publisher_LabelsExtrasAndSendsTheirValues()
    {
        var handler = new RecordingHandler();
        using var publisher = new SupabaseResultPublisher(new HttpClient(handler));
        await publisher.PublishAsync(Publish(), Api, Snapshot(new Dictionary<string, string> { ["field.gender"] = "Ж" }));

        var events = handler.Bodies("events").Single();
        var columns = events[0].GetProperty("display_config").GetProperty("columns").EnumerateArray().ToList();
        var gender = columns.Single(c => c.GetProperty("key").GetString() == "field.gender");
        Assert.Equal("Стать", gender.GetProperty("label").GetString());
        Assert.False(columns.Single(c => c.GetProperty("key").GetString() == "rk").TryGetProperty("label", out _));

        var row = handler.Bodies("results").Single()[0];
        Assert.Equal("Ж", row.GetProperty("extra").GetProperty("field.gender").GetString());
        Assert.False(publisher.ExtraUnsupported);

        // The same layout next tick: the events row is not sent again.
        await publisher.PublishAsync(Publish(), Api, Snapshot(new Dictionary<string, string>()));
        Assert.Single(handler.Bodies("events"));
        Assert.Equal(JsonValueKind.Null, handler.Bodies("results")[1][0].GetProperty("extra").ValueKind); // cleared
    }

    [Fact]
    public async Task Publisher_WithoutTheExtraColumn_KeepsPublishingTheRest()
    {
        var handler = new RecordingHandler { RejectExtra = true };
        using var publisher = new SupabaseResultPublisher(new HttpClient(handler));
        await publisher.PublishAsync(Publish(), Api, Snapshot(new Dictionary<string, string> { ["field.gender"] = "Ж" }));

        Assert.True(publisher.ExtraUnsupported);
        var sent = handler.Bodies("results");
        Assert.Equal(2, sent.Count);                                     // rejected once, then without extra
        Assert.False(sent[1][0].TryGetProperty("extra", out _));
        Assert.Equal("Бігунка", sent[1][0].GetProperty("full_name").GetString());
    }

    private static readonly OnlineApiSettings Api = new("https://example.invalid", "key", "", 15);

    private static OnlinePublishSettings Publish() => OnlinePublishSettings.Default("cup", "Cup", "");

    private static OnlineResultsSnapshot Snapshot(IReadOnlyDictionary<string, string> extra) =>
        new([new OnlineDay(1, "")], 1, [new OnlineGroup("Ж21", null, null, 0)],
            [
                new OnlineResultRow(7, "Ж21", "Бігунка", "", "", "", "", "", 1, TimeSpan.FromMinutes(30), null, null,
                    null, null, FinishStatus.Ok, HasReadout: true, OutOfCompetition: false) { Extra = extra },
            ])
        {
            ExtraColumns = [Gender],
        };

    // Records every upsert body by table (the reads/deletes of stand-in bibs aside); optionally answers a results
    // upsert carrying "extra" like PostgREST does for a column the table lacks.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(string Table, string Body)> _requests = [];

        public bool RejectExtra { get; init; }

        public List<JsonElement[]> Bodies(string table) => _requests
            .Where(r => r.Table == table)
            .Select(r => JsonDocument.Parse(r.Body).RootElement.EnumerateArray().ToArray())
            .ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var table = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            var body = request.Content is null ? "[]" : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Add((table, body));
            if (RejectExtra && table == "results" && body.Contains("\"extra\"", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"code":"PGRST204","message":"Could not find the 'extra' column of 'results' in the schema cache"}""",
                        Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
