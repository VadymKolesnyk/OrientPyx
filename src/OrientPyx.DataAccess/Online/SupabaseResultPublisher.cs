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

    public void ResetMetadata() => _metaSent.Clear();

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
        try
        {
            // 1) Competition + days metadata, once per slug.
            if (!_metaSent.Contains(slug))
            {
                await PushAsync(api, "events", "id", [BuildEventRow(publish, snapshot.Days.Count)], cancellationToken);
                if (snapshot.Days.Count > 0)
                    await PushAsync(api, "event_days", "event,day", BuildDayRows(slug, snapshot.Days), cancellationToken);
                _metaSent.Add(slug);
            }

            // 2) Group metadata for the published day, once per (slug, day).
            if (snapshot.Groups.Count > 0 && !_metaSent.Contains(dayKey))
            {
                await PushAsync(api, "groups", "event,name,day",
                    BuildGroupRows(slug, snapshot.PublishedDayNumber, snapshot.Groups), cancellationToken);
                _metaSent.Add(dayKey);
            }

            // 3) Result rows — every tick.
            if (snapshot.Rows.Count > 0)
            {
                await PushAsync(api, "results", "event,bib,day",
                    BuildResultRows(publish, snapshot), cancellationToken);
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
            throw;
        }
    }

    // ── Row builders

    private static Dictionary<string, object?> BuildEventRow(OnlinePublishSettings p, int daysCount) => new()
    {
        ["id"] = p.Slug,
        ["title"] = p.Title,
        ["subtitle"] = NullIfEmpty(p.Subtitle),
        ["days_count"] = daysCount,
        ["standings"] = p.Standings,
        ["points"] = p.Points,
        // The frontend reads its column layout from display_config (jsonb): the ordered list of visible
        // column keys. PostgREST serialises this nested object straight into the jsonb column.
        ["display_config"] = BuildDisplayConfig(p),
        ["updated_at"] = DateTime.UtcNow.ToString("o"),
    };

    // The events.display_config payload the spectator frontend reads (1:1 with DisplayConfig in
    // web/src/types.ts): a version marker, the points/standings flags (duplicated so the frontend can read
    // everything from one place), the separate-DSQ-column toggles per screen, and the ordered columns each with
    // its large/small-screen visibility.
    private static Dictionary<string, object?> BuildDisplayConfig(OnlinePublishSettings p)
    {
        var display = p.EffectiveDisplay;
        return new()
        {
            ["version"] = 1,
            ["points"] = p.Points,
            ["standings"] = p.Standings,
            ["separateDsqLg"] = display.SeparateDsqLg,
            ["separateDsqSm"] = display.SeparateDsqSm,
            ["columns"] = display.Resolve()
                .Select((c, i) => new Dictionary<string, object?>
                {
                    ["key"] = c.Def.Key,
                    ["order"] = i,
                    ["lg"] = c.Lg,
                    ["sm"] = c.Sm,
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
        OnlinePublishSettings p, OnlineResultsSnapshot snapshot)
    {
        var rows = new List<Dictionary<string, object?>>(snapshot.Rows.Count);
        foreach (var r in snapshot.Rows)
        {
            // The frontend keys results by (event, bib, day) — a row with no number can't be addressed, skip it.
            if (r.Bib is not { } bib)
                continue;

            var status = MapStatus(r);
            // The frontend's single "points" column is the ranking points («Очки») the group's points rule
            // awards (PointsRuleEvaluator — time/score ratio vs the leader, or a placement table). Prefer it;
            // only fall back to the raw rogaine score («Бали») when the group has no points rule, so a
            // rogaine day with no rule still publishes a number rather than a blank.
            decimal? points = r.Points ?? r.Score;

            rows.Add(new Dictionary<string, object?>
            {
                ["event"] = p.Slug,
                ["bib"] = bib,
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
            });
        }
        return rows;
    }

    // Maps OrientPyx's FinishStatus + readout state onto the spectator status vocabulary the frontend
    // understands (finished / finished_pending / running / dsq / dns).
    private static string MapStatus(OnlineResultRow r) => r.Status switch
    {
        FinishStatus.Ok when r.Place is not null => "finished",
        FinishStatus.Ok => "finished_pending",     // valid run, place not assigned yet
        FinishStatus.Mp or FinishStatus.Ovt or FinishStatus.Dnf or FinishStatus.Dsq => "dsq",
        FinishStatus.Dns => "dns",
        _ => r.HasReadout ? "finished_pending" : "running",
    };

    // The short reason shown next to a DSQ result on the frontend ("MP", "OVT"…).
    private static string? StatusReason(FinishStatus status) => status switch
    {
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dsq => "DSQ",
        _ => null,
    };

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
            if (resp.IsSuccessStatusCode)
                return;

            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
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
