using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Online;

/// <summary>
/// Publishes live results to a Supabase project via its PostgREST API (upsert with
/// <c>Prefer: resolution=merge-duplicates</c>), matching the schema the spectator frontend reads
/// (<c>events</c> / <c>event_days</c> / <c>groups</c> / <c>results</c>). Ported from the standalone
/// "Orientir" publisher, but fed OrientPyx's already-computed snapshot instead of legacy DBF files.
///
/// Metadata (events / event_days / groups) changes rarely, so it is uploaded once per (slug, day) and the
/// result rows are re-sent each tick; <see cref="ResetMetadata"/> clears that memory after the options change.
/// One instance per running publish session — not registered as a shared singleton.
/// </summary>
public sealed class SupabaseResultPublisher : IResultPublisher, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    // Tracks what metadata has already been uploaded: the slug (events + event_days) and "slug:day" (groups).
    private readonly HashSet<string> _metaSent = new();

    // The extra result columns seen so far this session, across the days it published (a full reload goes through
    // every day): display_config labels them all, and the site hides one that has no value in a group.
    private readonly List<OnlineExtraColumn> _extras = [];

    // The display_config last uploaded with the events row; a new extra column changes it, which re-sends the row.
    private string? _sentDisplay;

    // "slug:day" -> the stand-in (negative) bibs online for runners without a number, as last sent. A runner who got a
    // number (or left the day) is removed from the site under the stand-in; a day not here yet is read from the site.
    private readonly Dictionary<string, HashSet<int>> _unnumbered = new();

    public SupabaseResultPublisher() : this(CreateHttpClient(), ownsHttp: true)
    {
    }

    // A field-day network drops in and out. SocketsHttpHandler caches DNS results for the lifetime of a
    // pooled connection, so after the Wi-Fi comes back a stale/failed resolution could otherwise be reused
    // for a long time; recycling connections every two minutes bounds how long a bad DNS answer sticks.
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public SupabaseResultPublisher(HttpClient http, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
    }

    public void ResetMetadata()
    {
        _metaSent.Clear();
        _sentDisplay = null;
        _unnumbered.Clear();
        TeamsUnsupported = false;
        ExtraUnsupported = false;
    }

    public bool TeamsUnsupported { get; private set; }

    public bool ExtraUnsupported { get; private set; }

    public async Task PublishAsync(
        OnlinePublishSettings publish,
        OnlineApiSettings api,
        OnlineResultsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!api.IsReadyToPublish)
            throw new InvalidOperationException("Online publish settings are incomplete (URL or service-role key missing).");

        var slug = publish.Slug;
        var dayKey = $"{slug}:{snapshot.PublishedDayNumber}";

        // A failed tick must not leave metadata half-uploaded and remembered as done: the frontend would then
        // never learn about a day or group added right when the network dropped, and the run would look healthy
        // ("results 25" every tick) while the site stayed stale until publishing was restarted. So each key is
        // recorded only after its own push succeeded, and any failure below un-remembers both keys so the next
        // successful tick re-sends the whole metadata set.
        foreach (var extra in snapshot.ExtraColumns)
        {
            var known = _extras.FindIndex(e => e.Key == extra.Key);
            if (known < 0)
                _extras.Add(extra);
            else
                _extras[known] = extra; // a renamed field / a language switch: the latest label
        }

        try
        {
            // 1) Competition + days metadata, once per slug — the events row again whenever its display_config
            // changes (an extra column appeared).
            var display = BuildDisplayConfig(publish, _extras);
            var displayJson = JsonSerializer.Serialize(display);
            var firstForSlug = !_metaSent.Contains(slug);
            if (firstForSlug || displayJson != _sentDisplay)
            {
                await PushAsync(api, "events", "id", [BuildEventRow(publish, snapshot.Days.Count, display)], cancellationToken);
                if (firstForSlug && snapshot.Days.Count > 0)
                    await PushAsync(api, "event_days", "event,day", BuildDayRows(slug, snapshot.Days), cancellationToken);
                _metaSent.Add(slug);
                _sentDisplay = displayJson;
            }

            // 2) Group metadata for the published day, once per (slug, day).
            if (snapshot.Groups.Count > 0 && !_metaSent.Contains(dayKey))
            {
                await PushAsync(api, "groups", "event,name,day",
                    BuildGroupRows(slug, snapshot.PublishedDayNumber, snapshot.Groups), cancellationToken);
                _metaSent.Add(dayKey);
            }

            // 3) Result rows — every tick. A project set up before the results.extra column answers 4xx naming
            // it: remember that and send the rows without it (the extra columns just stay off the site).
            if (snapshot.Rows.Count > 0)
            {
                try
                {
                    await PushAsync(api, "results", "event,bib,day",
                        BuildResultRows(publish, snapshot, withExtra: !ExtraUnsupported), cancellationToken);
                }
                catch (PublishException ex) when (!ExtraUnsupported && ex.Kind == PublishFailureKind.BadRequest
                                                  && ex.Message.Contains("extra", StringComparison.OrdinalIgnoreCase))
                {
                    ExtraUnsupported = true;
                    await PushAsync(api, "results", "event,bib,day",
                        BuildResultRows(publish, snapshot, withExtra: false), cancellationToken);
                }
            }

            // …and the stand-in rows of runners who have a number by now (or left the day) go.
            await PruneUnnumberedAsync(api, slug, snapshot, dayKey, cancellationToken);

            // 4) Relay teams (§11.1) — every tick, into their own table so the personal-results schema and the
            // frontend that reads it stay untouched. A project without that table (older frontend) answers 4xx:
            // remember it and keep publishing personal results rather than failing every tick.
            if (snapshot.Teams.Count > 0 && !TeamsUnsupported)
            {
                try
                {
                    await PushAsync(api, "relay_teams", "event,day,num",
                        BuildTeamRows(publish, snapshot), cancellationToken);
                }
                catch (PublishException ex) when (ex.Kind == PublishFailureKind.BadRequest)
                {
                    TeamsUnsupported = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // a stop/pause, not a failure — leave the metadata memory alone
        }
        catch
        {
            _metaSent.Remove(slug);
            _metaSent.Remove(dayKey);
            _sentDisplay = null;
            _unnumbered.Remove(dayKey);
            throw;
        }
    }

    // ── Row builders

    private static Dictionary<string, object?> BuildEventRow(
        OnlinePublishSettings p, int daysCount, Dictionary<string, object?> display) => new()
    {
        ["id"] = p.Slug,
        ["title"] = p.Title,
        ["subtitle"] = NullIfEmpty(p.Subtitle),
        ["days_count"] = daysCount,
        ["standings"] = p.Standings,
        ["points"] = p.Points,
        // The frontend reads its column layout from display_config (jsonb): the ordered list of visible
        // column keys. PostgREST serialises this nested object straight into the jsonb column.
        ["display_config"] = display,
        ["updated_at"] = DateTime.UtcNow.ToString("o"),
    };

    // The events.display_config payload the spectator frontend reads (1:1 with DisplayConfig in
    // web/src/types.ts): a version marker, the points/standings flags (duplicated so the frontend can read
    // everything from one place), the separate-DSQ-column toggles per screen, and the ordered columns each with
    // its large/small-screen visibility. An extra column (plugin column / additional field) also carries its
    // label — the site has no name of its own for it — and its values come in results.extra / legs[].extra.
    private static Dictionary<string, object?> BuildDisplayConfig(
        OnlinePublishSettings p, IReadOnlyList<OnlineExtraColumn> extras)
    {
        var display = p.EffectiveDisplay;
        return new()
        {
            ["version"] = 1,
            ["points"] = p.Points,
            ["standings"] = p.Standings,
            ["separateDsqLg"] = display.SeparateDsqLg,
            ["separateDsqSm"] = display.SeparateDsqSm,
            ["columns"] = display.Resolve(extras)
                .Select((c, i) =>
                {
                    var column = new Dictionary<string, object?>
                    {
                        ["key"] = c.Def.Key,
                        ["order"] = i,
                        ["lg"] = c.Lg,
                        ["sm"] = c.Sm,
                    };
                    if (c.Def.Column == ResultColumn.Extra)
                        column["label"] = c.Def.LabelKey;
                    return column;
                })
                .ToList(),
        };
    }

    private static List<Dictionary<string, object?>> BuildDayRows(string slug, IReadOnlyList<OnlineDay> days) =>
        days.Select((d, i) => new Dictionary<string, object?>
        {
            ["event"] = slug,
            ["day"] = d.Number,
            ["label"] = NullIfEmpty(d.Label),
            ["ord"] = i,
        }).ToList();

    private static List<Dictionary<string, object?>> BuildGroupRows(
        string slug, int day, IReadOnlyList<OnlineGroup> groups) =>
        groups.Select(g => new Dictionary<string, object?>
        {
            ["event"] = slug,
            ["name"] = g.Name,
            ["day"] = day,
            ["distance_km"] = g.DistanceKm,
            ["controls"] = g.ControlCount,
            ["ord"] = g.Order,
        }).ToList();

    private static List<Dictionary<string, object?>> BuildResultRows(
        OnlinePublishSettings p, OnlineResultsSnapshot snapshot, bool withExtra)
    {
        var rows = new List<Dictionary<string, object?>>(snapshot.Rows.Count);
        foreach (var r in snapshot.Rows)
        {
            var status = MapStatus(r);
            // The frontend's single "points" column is the ranking points («Очки») the group's points rule
            // awards (PointsRuleEvaluator — time/score ratio vs the leader, or a placement table). Prefer it;
            // only fall back to the raw rogaine score («Бали») when the group has no points rule, so a
            // rogaine day with no rule still publishes a number rather than a blank.
            decimal? points = r.Points ?? r.Score;

            var row = new Dictionary<string, object?>
            {
                ["event"] = p.Slug,
                ["bib"] = r.Key, // the number, or a runner's negative stand-in the site doesn't show
                ["day"] = snapshot.PublishedDayNumber,
                ["grp"] = r.GroupName,
                ["rk"] = r.OutOfCompetition ? null : r.Place,
                ["full_name"] = r.FullName,
                ["team"] = NullIfEmpty(r.Team),
                ["club"] = NullIfEmpty(r.Club),
                ["region"] = NullIfEmpty(r.Region),
                ["birth"] = NullIfEmpty(r.Birth),
                ["qual"] = NullIfEmpty(r.Qual),
                ["reason"] = status == "dsq" ? StatusReason(r.Status) : null,
                ["start_time"] = FormatTime(r.StartTime),
                ["finish_time"] = FormatTime(r.FinishTime),
                ["result_time"] = FormatTime(r.ResultTime),
                ["result_seconds"] = r.ResultTime is { } rt ? (int)rt.TotalSeconds : (int?)null,
                ["points"] = points,
                ["status"] = status,
                ["updated_at"] = DateTime.UtcNow.ToString("o"),
            };
            // Every row carries the key (PostgREST wants the same keys in one batch); null clears a stale value.
            if (withExtra)
                row["extra"] = r.Extra.Count > 0 ? r.Extra : null;
            rows.Add(row);
        }
        return rows;
    }

    // One relay_teams row per team: the team result plus its legs and the plugin column labels as jsonb, so the
    // frontend can render a team with its legs from one row (contract: docs/online-snapshot.md).
    private static List<Dictionary<string, object?>> BuildTeamRows(
        OnlinePublishSettings p, OnlineResultsSnapshot snapshot)
    {
        // The plugin columns, for a site that still lists them under the runner's name (the current one lays the
        // legs out by display_config instead).
        var columns = snapshot.ExtraColumns
            .Where(c => c.IsPlugin)
            .Select(c => new Dictionary<string, object?> { ["key"] = c.Key, ["label"] = c.Label })
            .ToList();
        return snapshot.Teams.Select(t =>
        {
            // A team with no status yet is still on the course («running»), however many legs are read — never
            // «finished_pending», which the site counts as finished. The site orders the running ones by legs done.
            var status = MapStatus(t.Status, t.Place, hasReadout: false);
            return new Dictionary<string, object?>
            {
                ["event"] = p.Slug,
                ["day"] = snapshot.PublishedDayNumber,
                ["num"] = t.Number,
                ["grp"] = t.GroupName,
                ["name"] = NullIfEmpty(t.Name),
                ["club"] = NullIfEmpty(t.Club),
                ["region"] = NullIfEmpty(t.Region),
                ["rk"] = t.OutOfCompetition ? null : t.Place,
                ["result_time"] = FormatTime(t.Time),
                ["result_seconds"] = t.Time is { } tt ? (int)tt.TotalSeconds : (int?)null,
                ["points"] = t.Points,
                ["status"] = status,
                ["reason"] = status == "dsq" ? StatusReason(t.Status) : null,
                ["out_of_competition"] = t.OutOfCompetition,
                ["restarted"] = t.Restarted,
                ["columns"] = columns,
                ["legs"] = t.Legs.Select(l => new Dictionary<string, object?>
                {
                    ["leg"] = l.Leg,
                    ["bib"] = l.Bib,
                    ["full_name"] = NullIfEmpty(l.FullName),
                    ["birth"] = NullIfEmpty(l.Birth),
                    ["qual"] = NullIfEmpty(l.Qual),
                    ["club"] = NullIfEmpty(l.Club),
                    ["region"] = NullIfEmpty(l.Region),
                    ["course"] = NullIfEmpty(l.Course),
                    ["start_time"] = FormatTime(l.StartTime),
                    ["leg_time"] = FormatTime(l.LegTime),
                    ["result_time"] = FormatTime(l.Result),
                    ["cumulative"] = FormatTime(l.Cumulative),
                    ["status"] = l.Pending ? "pending" : MapStatus(l.Status, place: 1, l.Status != FinishStatus.None),
                    ["reason"] = StatusReason(l.Status),
                    ["restarted"] = l.Restarted,
                    ["extra"] = l.Extra,
                }).ToList(),
                ["updated_at"] = DateTime.UtcNow.ToString("o"),
            };
        }).ToList();
    }

    private static string MapStatus(FinishStatus status, int? place, bool hasReadout) => status switch
    {
        FinishStatus.Ok when place is not null => "finished",
        FinishStatus.Ok => "finished_pending",
        FinishStatus.Mp or FinishStatus.Ovt or FinishStatus.Dnf or FinishStatus.Dsq => "dsq",
        FinishStatus.Dns => "dns",
        _ => hasReadout ? "finished_pending" : "running",
    };

    // Maps OrientPyx's FinishStatus + readout state onto the spectator status vocabulary the frontend
    // understands (finished / finished_pending / running / dsq / dns).
    private static string MapStatus(OnlineResultRow r) => MapStatus(r.Status, r.Place, r.HasReadout);

    // The short reason shown next to a DSQ result on the frontend ("MP", "OVT"…).
    private static string? StatusReason(FinishStatus status) => status switch
    {
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dsq => "DSQ",
        _ => null,
    };

    // A runner without a number is online under a stand-in negative bib (OnlineResultRow.UnnumberedKey). Once they
    // get a number — or leave the day — the upsert writes them elsewhere and the stand-in row would stay on the site
    // (twice the runner), so the stand-ins that are no longer in the snapshot are deleted. The first tick of a day
    // asks the site which ones it holds (left by an earlier run); later ticks compare with what they sent. Only
    // stand-ins are touched: a real number's row is left alone, as before.
    private async Task PruneUnnumberedAsync(
        OnlineApiSettings api, string slug, OnlineResultsSnapshot snapshot, string dayKey, CancellationToken cancellationToken)
    {
        var current = snapshot.Rows.Where(r => r.Key < 0).Select(r => r.Key).ToHashSet();
        var day = snapshot.PublishedDayNumber;
        var filter = $"event=eq.{Uri.EscapeDataString(slug)}&day=eq.{day}";

        if (!_unnumbered.TryGetValue(dayKey, out var online))
        {
            var url = $"{api.SupabaseUrl.TrimEnd('/')}/rest/v1/results?select=bib&{filter}&bib=lt.0";
            using var req = Request(HttpMethod.Get, url, api);
            var body = await SendAsync(req, "results", cancellationToken);
            online = ReadBibs(body);
        }

        // A few dozen per request keeps the URL short whatever the count.
        foreach (var gone in online.Where(k => !current.Contains(k)).Chunk(50))
        {
            var list = string.Join(",", gone.Select(k => k.ToString(CultureInfo.InvariantCulture)));
            var url = $"{api.SupabaseUrl.TrimEnd('/')}/rest/v1/results?{filter}&bib=in.({list})";
            using var req = Request(HttpMethod.Delete, url, api);
            req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
            await SendAsync(req, "results", cancellationToken);
        }

        _unnumbered[dayKey] = current;
    }

    // The bibs of a PostgREST `select=bib` answer ([{"bib":-5},…]); an empty or unexpected body reads as none.
    private static HashSet<int> ReadBibs(string body)
    {
        var bibs = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(body))
            return bibs;
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return bibs;
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("bib", out var bib) && bib.TryGetInt32(out var n))
                bibs.Add(n);
        return bibs;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, OnlineApiSettings api)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("apikey", api.ServiceRoleKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.ServiceRoleKey);
        return req;
    }

    // ── PostgREST upsert

    private async Task PushAsync(
        OnlineApiSettings api, string table, string onConflict,
        List<Dictionary<string, object?>> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return;

        var url = $"{api.SupabaseUrl.TrimEnd('/')}/rest/v1/{table}?on_conflict={onConflict}";
        var json = JsonSerializer.Serialize(rows);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("apikey", api.ServiceRoleKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.ServiceRoleKey);
        req.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        await SendAsync(req, table, cancellationToken);
    }

    public async Task ClearEventAsync(OnlineApiSettings api, string slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!api.IsReadyToPublish)
            throw new InvalidOperationException("Online publish settings are incomplete (URL or service-role key missing).");
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug is required.", nameof(slug));

        _metaSent.Clear();
        _unnumbered.Clear();

        // Relay teams (§11.1) live in an optional table — a project without it answers 4xx, which is fine here.
        if (!TeamsUnsupported)
        {
            var teamsUrl = $"{api.SupabaseUrl.TrimEnd('/')}/rest/v1/relay_teams?event=eq.{Uri.EscapeDataString(slug)}";
            using var teamsReq = new HttpRequestMessage(HttpMethod.Delete, teamsUrl);
            teamsReq.Headers.TryAddWithoutValidation("apikey", api.ServiceRoleKey);
            teamsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.ServiceRoleKey);
            teamsReq.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
            try
            {
                await SendAsync(teamsReq, "relay_teams", cancellationToken);
            }
            catch (PublishException ex) when (ex.Kind == PublishFailureKind.BadRequest)
            {
                TeamsUnsupported = true;
            }
        }

        // Children first: results and groups hang off (event, day), days off the event. The events row itself
        // stays — the next publish upserts it anyway, and the frontend keeps a valid competition meanwhile.
        foreach (var table in new[] { "results", "groups", "event_days" })
        {
            var url = $"{api.SupabaseUrl.TrimEnd('/')}/rest/v1/{table}?event=eq.{Uri.EscapeDataString(slug)}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.TryAddWithoutValidation("apikey", api.ServiceRoleKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.ServiceRoleKey);
            req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
            await SendAsync(req, table, cancellationToken);
        }
    }

    // Sends the request; returns the answer's body (empty for `return=minimal`) or throws a PublishException.
    private async Task<string> SendAsync(HttpRequestMessage req, string table, CancellationToken cancellationToken)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the user stopped publishing — not a network failure
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient surfaces its own timeout as a cancellation that the token didn't ask for.
            throw new PublishException(PublishFailureKind.Timeout, Describe(ex), ex);
        }
        catch (HttpRequestException ex)
        {
            cancellationToken.ThrowIfCancellationRequested(); // a stop mid-request isn't a network failure
            throw new PublishException(Classify(ex), Describe(ex), ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (resp.IsSuccessStatusCode)
                return body;

            var code = (int)resp.StatusCode;
            var kind = code switch
            {
                401 or 403 => PublishFailureKind.Unauthorized,
                >= 500 => PublishFailureKind.ServerError,
                >= 400 => PublishFailureKind.BadRequest,
                _ => PublishFailureKind.Unknown,
            };
            throw new PublishException(kind, $"HTTP {code} [{table}]: {Trim(body)}");
        }
    }

    // Maps a transport failure onto the coarse reason the UI explains in plain language. The useful signal is
    // in the SocketException nested inside HttpRequestException — the outer Message is the useless
    // "An error occurred while sending the request."
    private static PublishFailureKind Classify(HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                        => PublishFailureKind.NoDns,
                    SocketError.TimedOut => PublishFailureKind.Timeout,
                    _ => PublishFailureKind.NoConnection,
                };
            }

            if (e is System.Security.Authentication.AuthenticationException)
                return PublishFailureKind.NoConnection;
        }

        return PublishFailureKind.NoConnection;
    }

    // Flattens the exception chain into one line — the inner messages are where the real cause lives.
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message.Trim();
            if (m.Length > 0 && !parts.Contains(m))
                parts.Add(m);
        }
        return string.Join(" → ", parts);
    }

    private static string Trim(string body) =>
        body.Length <= 300 ? body.Trim() : body[..300].Trim() + "…";

    private static string? FormatTime(TimeSpan? t) =>
        t is { } v ? v.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
