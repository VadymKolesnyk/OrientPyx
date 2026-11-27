using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary><c>get_context</c>, <c>list_competitions</c>, <c>get_help</c>, <c>show_in_app</c>, <c>get_background_status</c>.</summary>
public sealed class ContextTools(McpContext ctx) : IMcpToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = ["get_context", "list_competitions", "get_help", "show_in_app", "get_background_status"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "get_context" => GetContextAsync(cancellationToken),
        "list_competitions" => ListAsync(args, cancellationToken),
        "get_help" => Task.FromResult(Help(args)),
        "show_in_app" => ShowAsync(args, cancellationToken),
        "get_background_status" => BackgroundAsync(args, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private async Task<object?> GetContextAsync(CancellationToken cancellationToken)
    {
        var workspace = ctx.Workspace;
        var role = workspace.Role switch
        {
            WorkspaceRole.Host => "host",
            WorkspaceRole.Station => "station",
            _ => "local",
        };
        var mode = ctx.Runtime.Mode switch
        {
            McpMode.ConfirmChanges => "confirm_changes",
            McpMode.AutoChanges => "auto_changes",
            McpMode.NoConfirmation => "no_confirmation",
            _ => "read_only",
        };
        var confirmed = ctx.Runtime.Mode switch
        {
            McpMode.ConfirmChanges => "every change waits for «Дозволити» in OrientPyx",
            McpMode.AutoChanges => "only risky changes (not undoable, bulk) wait for «Дозволити»; undoable ones run at once",
            McpMode.NoConfirmation => "nothing waits for «Дозволити»: every change runs at once — agree bulk and not-undoable changes with the user in the chat first",
            _ => "changes are switched off",
        };

        object? competition = null;
        object? days = null;
        int? windowDay = null;
        if (ctx.Session.CurrentEvent is { } ev)
        {
            var info = await ReadInfoAsync(ev, cancellationToken);
            var list = await ctx.DaysAsync(ev, cancellationToken);
            competition = new
            {
                identifier = ev.Identifier,
                name = info?.Name ?? ev.Name,
                venue = info?.Venue ?? ev.Venue,
                start_date = (info?.StartDate ?? ev.StartDate)?.ToString("yyyy-MM-dd"),
                end_date = (info?.EndDate ?? ev.EndDate)?.ToString("yyyy-MM-dd"),
                roster_enabled = info?.RosterEnabled,
            };
            days = list.Select(d => new
            {
                day = d.Number,
                date = d.Date?.ToString("yyyy-MM-dd"),
                venue = string.IsNullOrWhiteSpace(d.Venue) ? null : d.Venue,
                discipline = d.DefaultDiscipline.Value,
                locked = d.IsLocked,
            }).ToList();
            windowDay = ctx.Session.CurrentDay?.Number;
        }

        return new
        {
            competition,
            days,
            window_day = windowDay,
            role,
            host = workspace.Role == WorkspaceRole.Station ? workspace.Station?.HostName : null,
            mode,
            confirmation = confirmed,
            changes_allowed = mode != "read_only",
            app_version = McpContext.AppVersion,
            now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            note = competition is null
                ? "No competition is open in the OrientPyx window."
                : "Pass this competition's identifier as 'competition' in every change.",
        };
    }

    private async Task<Entities.CompetitionInfo?> ReadInfoAsync(Models.EventSummary ev, CancellationToken cancellationToken)
    {
        using (McpContext.Pin(ev, null))
            return await ctx.Editor.GetInfoAsync(cancellationToken);
    }

    private async Task<object?> ListAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var all = await ctx.Catalog.GetEventsAsync(cancellationToken);
        var hidden = args.Flag("include_hidden");
        var open = ctx.Session.CurrentEvent?.Identifier;
        return all
            .Where(e => hidden || !e.IsHidden)
            .OrderByDescending(e => e.StartDate ?? e.CreatedAt)
            .Select(e => new
            {
                identifier = e.Identifier,
                name = e.Name,
                venue = string.IsNullOrWhiteSpace(e.Venue) ? null : e.Venue,
                start_date = e.StartDate?.ToString("yyyy-MM-dd"),
                end_date = e.EndDate?.ToString("yyyy-MM-dd"),
                days = e.DayCount,
                hidden = e.IsHidden ? true : (bool?)null,
                open_in_window = string.Equals(e.Identifier, open, StringComparison.OrdinalIgnoreCase) ? true : (bool?)null,
            })
            .ToList();
    }

    private object? Help(McpArgs args)
    {
        var ui = ctx.Ui;
        var keys = ui.HelpKeys;
        var topic = args.Str("topic")?.Trim();
        if (string.IsNullOrEmpty(topic))
            return new { topics = keys.Select(k => k.StartsWith("Help.", StringComparison.Ordinal) ? k[5..] : k).ToList() };

        var exact = keys.FirstOrDefault(k => string.Equals(k, topic, StringComparison.OrdinalIgnoreCase)
            || string.Equals(k, "Help." + topic, StringComparison.OrdinalIgnoreCase));
        var matches = exact is not null
            ? [exact]
            : keys.Where(k => k.Contains(topic, StringComparison.OrdinalIgnoreCase)
                || (ui.GetHelp(k)?.Contains(topic, StringComparison.OrdinalIgnoreCase) ?? false)).Take(8).ToList();
        if (matches.Count == 0)
            throw new McpToolException($"No help about '{topic}'. Call get_help without a topic for the list.");
        return matches.Select(k => new { topic = k.StartsWith("Help.", StringComparison.Ordinal) ? k[5..] : k, text = ui.GetHelp(k) }).ToList();
    }

    private async Task<object?> ShowAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var page = args.ReqStr("page").Trim().ToLowerInvariant();
        if (!McpToolCatalog.ShowPages.Contains(page))
            throw new McpToolException($"Unknown page '{page}'; pages: {string.Join(", ", McpToolCatalog.ShowPages)}.");
        var competition = ctx.OpenCompetition();
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        Guid? participantId = null;
        string? shownName = null;
        if (args.Str("participant") is { } who && who.Trim().Length > 0)
        {
            using (McpContext.Pin(competition, day))
            {
                var person = McpContext.ResolveParticipant(await ctx.Editor.GetParticipantRosterAsync(cancellationToken), who, day.Id);
                participantId = person.ParticipantId;
                shownName = person.FullName;
            }
        }
        var shown = await ctx.Ui.ShowAsync(new McpShowTarget(page, args.Int("day") ?? day.Number, participantId), cancellationToken);
        return shown
            ? new { shown = true, page, day = day.Number, participant = shownName }
            : throw new McpToolException($"OrientPyx could not show '{page}' now (the page may not apply to this competition or day).");
    }

    // ── get_background_status: what runs in the window and how the finish desk stands (read only)

    private async Task<object?> BackgroundAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var activities = await ctx.Ui.ActivitiesAsync(cancellationToken);
        object? readouts = null;
        if (ctx.Session.CurrentEvent is { } competition)
        {
            var day = await ctx.DayAsync(competition, args, cancellationToken);
            Models.FinishReadoutLog log;
            using (McpContext.Pin(competition, day))
                log = await ctx.Editor.GetFinishReadoutLogAsync(cancellationToken, day.Id);
            var last = log.Rows.OrderByDescending(r => r.Order).FirstOrDefault();
            readouts = new
            {
                day = day.Number,
                read_outs = log.Rows.Count,
                runners_read = log.Counts.ReadOut,
                on_course = log.Counts.OnCourse,
                unknown_chips = log.Counts.Unknown,
                last_read = last is null ? null : new
                {
                    id = last.Id,
                    chip = last.ChipNumber,
                    participant = last.IsKnown ? last.FullName : null,
                    finish_time = last.FinishTime is { } f ? McpTime.Clock(f.TimeOfDay) : null,
                    status = McpJson.StatusText(last.Status),
                },
            };
        }

        return new
        {
            activities = activities.Select(a => new
            {
                title = a.Title,
                state = a.State,
                status = string.IsNullOrWhiteSpace(a.Status) ? null : a.Status,
                problem = a.Problem ? true : (bool?)null,
            }).ToList(),
            readouts,
            note = activities.Count == 0
                ? "Nothing runs in the background now (no finish auto-read, monitor or online results). They are started only by the user in OrientPyx."
                : "Read only: starting, pausing or stopping a process is done by the user in OrientPyx (the «Процеси» block of the top bar).",
        };
    }
}
