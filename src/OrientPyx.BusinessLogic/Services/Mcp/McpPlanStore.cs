using System.Collections.Concurrent;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>
/// What a preview computed: what the assistant shows the user (<paramref name="Preview"/>), a fingerprint of the change
/// set (two drafts with the same fingerprint change the same), the summary for the confirmation and how to apply it.
/// </summary>
public sealed record McpPlanDraft(
    object Preview,
    string Fingerprint,
    ChangeSummary Summary,
    Func<CancellationToken, Task<object?>> Apply,
    bool Undoable = false);

/// <summary>The answer of a preview.</summary>
public sealed record McpPlanAnswer(Guid PlanId, int ExpiresInSeconds, string Summary, object Preview, string Next);

/// <summary>
/// Previews kept for <c>apply_plan</c> (plan М6): <c>apply_plan</c> applies exactly what the user saw. When the data
/// changed since (another station, the operator), the plan is computed again — the same change set goes through, a
/// different one is refused with the new preview.
/// </summary>
public sealed class McpPlanStore
{
    private const int MaxPlans = 20;

    private readonly ConcurrentDictionary<Guid, Plan> _plans = new();

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Builds the draft now, remembers how to build it again, answers with the plan id.</summary>
    public async Task<McpPlanAnswer> CreateAsync(
        McpContext ctx, string tool, EventSummary competition, EventDay? day,
        Func<CancellationToken, Task<McpPlanDraft>> build, CancellationToken cancellationToken)
    {
        McpPlanDraft draft;
        using (McpContext.Pin(competition, day))
            draft = await build(cancellationToken);
        return Remember(ctx, tool, competition, day, build, draft);
    }

    /// <summary>Applies a remembered plan through the writer (confirmation as a risky change).</summary>
    public async Task<object?> ApplyAsync(McpContext ctx, Guid planId, McpCall call, CancellationToken cancellationToken)
    {
        Sweep();
        if (!_plans.TryGetValue(planId, out var plan))
            throw new McpToolException("Unknown or expired plan_id (plans live 15 minutes). Run the preview again.");

        var open = ctx.OpenCompetition();
        if (!McpContext.SameCompetition(open, plan.Competition))
            throw new McpToolException(
                $"The plan is for competition '{plan.Competition.Identifier}', but the window now shows '{open.Identifier}'. Nothing was changed.");

        var draft = plan.Draft;
        if (ctx.DataVersion(plan.Competition) != plan.Version)
        {
            McpPlanDraft fresh;
            using (McpContext.Pin(plan.Competition, plan.Day))
                fresh = await plan.Build(cancellationToken);
            if (!string.Equals(fresh.Fingerprint, draft.Fingerprint, StringComparison.Ordinal))
            {
                _plans.TryRemove(planId, out _);
                var again = Remember(ctx, plan.Tool, plan.Competition, plan.Day, plan.Build, fresh);
                throw new McpToolException(
                    "The data changed since the preview and the plan would now be different, so nothing was changed. New preview: " +
                    McpJson.Serialize(again));
            }
            draft = fresh;
        }

        _plans.TryRemove(planId, out _);
        var write = new McpWrite(plan.Tool, McpToolClass.Risky, plan.Competition, plan.Day, draft.Summary, draft.Undoable);
        return await ctx.Writer.RunAsync(write, draft.Apply, call, cancellationToken);
    }

    private McpPlanAnswer Remember(
        McpContext ctx, string tool, EventSummary competition, EventDay? day,
        Func<CancellationToken, Task<McpPlanDraft>> build, McpPlanDraft draft)
    {
        Sweep();
        while (_plans.Count >= MaxPlans && _plans.Values.OrderBy(p => p.Created).FirstOrDefault() is { } oldest)
            _plans.TryRemove(oldest.Id, out _);

        var plan = new Plan(Guid.NewGuid(), tool, competition, day, build, draft, ctx.DataVersion(competition), DateTimeOffset.Now);
        _plans[plan.Id] = plan;
        return new McpPlanAnswer(
            plan.Id,
            (int)Lifetime.TotalSeconds,
            draft.Summary.Text,
            draft.Preview,
            "Nothing changed yet. Show the user what would change; if they agree, call apply_plan with this plan_id " +
            "(the user also confirms it in the OrientPyx window).");
    }

    private void Sweep()
    {
        var now = DateTimeOffset.Now;
        foreach (var (id, p) in _plans)
            if (now - p.Created > Lifetime)
                _plans.TryRemove(id, out _);
    }

    private sealed record Plan(
        Guid Id,
        string Tool,
        EventSummary Competition,
        EventDay? Day,
        Func<CancellationToken, Task<McpPlanDraft>> Build,
        McpPlanDraft Draft,
        long Version,
        DateTimeOffset Created);
}
