using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>
/// What every tool works with: the services of the program (resolved lazily from the app's container — the tool layer
/// is its composition root) and the shared rules of a call — which competition (pinned, plan М1), which day, who a
/// participant / group / club named by the assistant is.
/// </summary>
public sealed class McpContext
{
    private readonly IServiceProvider _services;

    public McpContext(IServiceProvider services, McpRuntime runtime, McpConfirmations confirmations, McpPlanStore plans)
    {
        _services = services;
        Runtime = runtime;
        Confirmations = confirmations;
        Plans = plans;
        Writer = new McpWriter(this);
    }

    public McpRuntime Runtime { get; }
    public McpConfirmations Confirmations { get; }
    public McpPlanStore Plans { get; }
    public McpWriter Writer { get; }

    public ICompetitionEditorService Editor => Get<ICompetitionEditorService>();
    public ISessionService Session => Get<ISessionService>();
    public IEventCatalogService Catalog => Get<IEventCatalogService>();
    public IWorkspaceMode Workspace => Get<IWorkspaceMode>();
    public IDataChangeBus Bus => Get<IDataChangeBus>();
    public IEditHistoryService History => Get<IEditHistoryService>();
    public IMcpUi Ui => Get<IMcpUi>();
    public IActivityLog? Log => _services.GetService<IActivityLog>();
    public IDisciplineStrategyProvider Strategies => Get<IDisciplineStrategyProvider>();
    public IAnalyticsSandbox Sandbox => Get<IAnalyticsSandbox>();
    public IEventFiles Files => Get<IEventFiles>();

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public T? Find<T>() => _services.GetService<T>();

    /// <summary>The version of the program the window runs (from the entry assembly).</summary>
    public static string AppVersion { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    // ── Competition and day

    /// <summary>The competition open in the window, or a refusal the assistant understands.</summary>
    public EventSummary OpenCompetition() =>
        Session.CurrentEvent
        ?? throw new McpToolException("No competition is open in the OrientPyx window. Ask the user to open one (or use open_competition).");

    /// <summary>
    /// A write names its competition; it must be the one open in the window (the user may have switched in the middle
    /// of the conversation — plan §3.3). The returned summary is the one the call pins.
    /// </summary>
    public EventSummary RequireCompetition(McpArgs args)
    {
        var open = OpenCompetition();
        var named = args.Str("competition")?.Trim();
        if (string.IsNullOrEmpty(named))
            throw new McpToolException($"'competition' is required: the identifier of the competition you change (now open: '{open.Identifier}').");
        if (!string.Equals(named, open.Identifier, StringComparison.OrdinalIgnoreCase))
            throw new McpToolException(
                $"The OrientPyx window now shows competition '{open.Identifier}' ({open.Name}), not '{named}'. Nothing was changed — call get_context.");
        return open;
    }

    /// <summary>The competition a read works on: the one open in the window (a 'competition' argument must match it).</summary>
    public EventSummary ReadCompetition(McpArgs args) =>
        args.Has("competition") ? RequireCompetition(args) : OpenCompetition();

    public async Task<IReadOnlyList<EventDay>> DaysAsync(EventSummary competition, CancellationToken cancellationToken)
    {
        using (EditorScope.Enter(competition, null))
            return (await Editor.GetDaysAsync(cancellationToken)).OrderBy(d => d.Number).ToList();
    }

    /// <summary>The day named by <paramref name="number"/>, else the window's day (for the window's competition), else day 1.</summary>
    public async Task<EventDay> DayAsync(EventSummary competition, int? number, CancellationToken cancellationToken)
    {
        var days = await DaysAsync(competition, cancellationToken);
        if (days.Count == 0)
            throw new McpToolException("The competition has no days.");
        if (number is { } n)
            return days.FirstOrDefault(d => d.Number == n)
                ?? throw new McpToolException($"There is no day {n}; days: {string.Join(", ", days.Select(d => d.Number))}.");
        var sessionDay = Session.CurrentDay;
        if (sessionDay is not null && SameCompetition(competition, Session.CurrentEvent))
            return days.FirstOrDefault(d => d.Id == sessionDay.Id) ?? days[0];
        return days[0];
    }

    public Task<EventDay> DayAsync(EventSummary competition, McpArgs args, CancellationToken cancellationToken, string name = "day") =>
        DayAsync(competition, args.Int(name), cancellationToken);

    /// <summary>Pins the competition (and day) for everything the editor does until disposed.</summary>
    public static IDisposable Pin(EventSummary competition, EventDay? day) => EditorScope.Enter(competition, day);

    public static bool SameCompetition(EventSummary? a, EventSummary? b) =>
        a is not null && b is not null && string.Equals(a.Identifier, b.Identifier, StringComparison.OrdinalIgnoreCase);

    /// <summary>The data version of the competition (a preview remembers it; the analytics cache is keyed on it).</summary>
    public long DataVersion(EventSummary competition) => Bus.CurrentVersion(competition.FolderPath);

    /// <summary>
    /// The summary («Підсумковий протокол») template as the page opens it: the competition's own, else the app default
    /// with this competition's days filled in (all counted, the first one the priority day) — the app default carries
    /// no day list, so without this the document lists nothing. Call inside a Pin.
    /// </summary>
    public async Task<SummaryProtocolSettings> SummarySettingsAsync(EventSummary competition, CancellationToken cancellationToken)
    {
        if (await Editor.GetSummaryProtocolSettingsAsync(cancellationToken) is { } own)
            return own;
        var settings = await Get<IAppSettingsService>().GetSummaryProtocolSettingsAsync(cancellationToken);
        var days = await DaysAsync(competition, cancellationToken);
        settings.Days = days.Select(d => new SummaryDaySetting { DayId = d.Id, DayNumber = d.Number, Counted = true }).ToList();
        settings.PriorityDayId = days.Count > 0 ? days[0].Id : null;
        settings.Groups = [];
        return settings;
    }

    // ── Who / what the assistant named (call inside a Pin)

    /// <summary>
    /// A participant by id, bib, relay leg number («12.2», on <paramref name="dayId"/> when given) or exact full name
    /// (TextKey: case, spaces and Latin/Cyrillic look-alikes ignored). Several people with that name is a refusal listing
    /// them — the assistant must ask the user, never guess.
    /// </summary>
    public static ParticipantRosterRow ResolveParticipant(IReadOnlyList<ParticipantRosterRow> roster, string reference, Guid? dayId = null)
    {
        var r = (reference ?? string.Empty).Trim();
        if (r.Length == 0)
            throw new McpToolException("A participant reference is empty.");
        if (Guid.TryParse(r, out var id))
            return roster.FirstOrDefault(p => p.ParticipantId == id)
                ?? throw new McpToolException($"No participant with id {id}.");

        var byBib = roster.Where(p => p.Number.Trim().Length > 0 && TextKey.Same(p.Number.Trim(), r)).ToList();
        if (byBib.Count == 1)
            return byBib[0];

        // A relay runner's number is the team number + leg («12.2», never stored as the bib).
        if (byBib.Count == 0)
        {
            var byLeg = roster.Where(p => p.Days.Any(d => (dayId is null || d.DayId == dayId) && d.IsMember && IsLegNumber(d, r))).ToList();
            if (byLeg.Count == 1)
                return byLeg[0];
            if (byLeg.Count > 1)
                throw new McpToolException($"Relay number {r} belongs to several runners (on different days): " +
                    string.Join("; ", byLeg.Select(p => $"{p.ParticipantId} ({p.FullName})")) + ". Give 'day' or use an id.");
        }

        var byName = roster.Where(p => TextKey.Same(Collapse(p.FullName), Collapse(r))).ToList();
        if (byName.Count == 1)
            return byName[0];
        if (byName.Count > 1)
            throw new McpToolException(
                $"{byName.Count} participants are named '{r}': " +
                string.Join("; ", byName.Select(p => $"{p.ParticipantId} (bib {Blank(p.Number)}, {Blank(p.ClubName)}, born {p.BirthDate?.Year.ToString() ?? "?"})")) +
                ". Ask the user which one and use the id.");
        if (byBib.Count > 1)
            throw new McpToolException($"Several participants have bib '{r}'. Use an id.");
        throw new McpToolException($"No participant '{r}' (by id, bib or exact name). Use find.");
    }

    /// <summary>True when <paramref name="text"/> is the runner's relay leg number on that day («12.2»).</summary>
    public static bool IsLegNumber(RosterDayCell cell, string text) =>
        cell.Relay is { Leg: not null } relay && relay.Bib.Length > 0 && TextKey.Same(relay.Bib, text.Trim());

    /// <summary>A group of the day by name (TextKey).</summary>
    public static GroupDayRow ResolveGroup(IReadOnlyList<GroupDayRow> groups, string name) =>
        groups.FirstOrDefault(g => TextKey.Same(g.Name.Trim(), name.Trim()))
        ?? throw new McpToolException($"No group '{name}' on this day; groups: {string.Join(", ", groups.Select(g => g.Name))}.");

    /// <summary>A club / region / ДЮСШ id by name; unknown → created when <paramref name="createMissing"/>, else a refusal.</summary>
    public async Task<Guid?> ResolveDirectoryAsync(string kind, string? name, bool createMissing, CancellationToken cancellationToken)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0)
            return null;
        var editor = Editor;
        switch (kind)
        {
            case "club":
            {
                var found = (await editor.GetClubsAsync(cancellationToken)).FirstOrDefault(c => TextKey.Same(c.Name, n));
                if (found is not null)
                    return found.Id;
                Missing(kind, n, createMissing);
                return (await editor.AddClubAsync(n, cancellationToken))?.Id;
            }
            case "region":
            {
                var found = (await editor.GetRegionsAsync(cancellationToken)).FirstOrDefault(c => TextKey.Same(c.Name, n));
                if (found is not null)
                    return found.Id;
                Missing(kind, n, createMissing);
                return (await editor.AddRegionAsync(n, cancellationToken))?.Id;
            }
            case "dussh":
            {
                var found = (await editor.GetDusshesAsync(cancellationToken)).FirstOrDefault(c => TextKey.Same(c.Name, n));
                if (found is not null)
                    return found.Id;
                Missing(kind, n, createMissing);
                return (await editor.AddDusshAsync(n, cancellationToken))?.Id;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        static void Missing(string kind, string name, bool create)
        {
            if (!create)
                throw new McpToolException($"There is no {kind} '{name}'. Use the exact name (find) or pass create_missing=true.");
        }
    }

    /// <summary>A discipline id the program knows (built-in or an enabled plugin's), matched case-insensitively.</summary>
    public DisciplineId ResolveDiscipline(string text)
    {
        var t = (text ?? string.Empty).Trim();
        var known = Strategies.All.Select(s => s.Id).ToList();
        foreach (var id in known)
            if (string.Equals(id.Value, t, StringComparison.OrdinalIgnoreCase))
                return id;
        throw new McpToolException($"Unknown discipline '{t}'; known: {string.Join(", ", known.Select(k => k.Value))}.");
    }

    private static string Collapse(string s) => string.Join(' ', (s ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
}
