using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.DataAccess.Online;

/// <summary>
/// Publishes live results to a Supabase project via its PostgREST API (upsert with
/// <c>Prefer: resolution=merge-duplicates</c>), matching the schema the spectator frontend reads
/// (<c>events</c> / <c>event_days</c> / <c>groups</c> / <c>results</c>). Ported from the standalone
/// "Orientir" publisher, but fed OrientDesk's already-computed snapshot instead of legacy DBF files.
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

    public SupabaseResultPublisher() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ownsHttp: true)
    {
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

        // 1) Competition + days metadata, once per slug.
        if (!_metaSent.Contains(slug))
        {
            await PushAsync(api, "events", "id", [BuildEventRow(publish, snapshot.Days.Count)], cancellationToken);
            if (snapshot.Days.Count > 0)
                await PushAsync(api, "event_days", "event,day", BuildDayRows(slug, snapshot.Days), cancellationToken);
            _metaSent.Add(slug);
        }

        // 2) Group metadata for the published day, once per (slug, day).
        var dayKey = $"{slug}:{snapshot.PublishedDayNumber}";
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

    // ── Row builders ─────────────────────────────────────────────────────────────────────────────────

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

    // Maps OrientDesk's FinishStatus + readout state onto the spectator status vocabulary the frontend
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

    // ── PostgREST upsert ─────────────────────────────────────────────────────────────────────────────

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

        using var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Supabase {(int)resp.StatusCode} [{table}]: {body}");
        }
    }

    private static string? FormatTime(TimeSpan? t) =>
        t is { } v ? v.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
