using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services.Mcp;

namespace OrientPyx.Presentation.ViewModels.Shared;

/// <summary>One line of «Деталі» of the confirmation bar.</summary>
public sealed record McpChangeRowItem(string Subject, string Field, string Old, string New);

/// <summary>
/// The non-modal bar at the top of the window where the user allows or refuses a change of the assistant (plan §7.3).
/// Requests queue up; the bar shows the oldest. «Дозволяти такі зміни до кінця сесії» allows the same tool without asking
/// until the assistant is switched off or the program closes. A request that expires (10 min) leaves the bar by itself.
/// All members run on the UI thread.
/// </summary>
public sealed partial class McpConfirmBarViewModel : ObservableObject
{
    private readonly List<Pending> _queue = [];

    public McpConfirmBarViewModel(ILocalizationService localization)
    {
        Localization = localization;
        Localization.PropertyChanged += (_, _) => Refresh();
    }

    public ILocalizationService Localization { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Headline), nameof(Subline), nameof(Rows), nameof(HasRows), nameof(QueueText))]
    private Pending? _current;

    [ObservableProperty]
    private bool _showDetails;

    [ObservableProperty]
    private bool _rememberForSession;

    public bool IsVisible => Current is not null;

    public string Headline => Current is null
        ? string.Empty
        : string.Format(Localization.Get("Mcp.Confirm.Wants"), ChangeSummaryText.Format(Current.Request.Summary, Localization));

    public string Subline => Current is null
        ? string.Empty
        : string.Join(" · ", new[]
        {
            Current.Request.CompetitionName,
            Current.Request.Undoable ? Localization.Get("Mcp.Confirm.Undoable") : Localization.Get("Mcp.Confirm.NotUndoable"),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string QueueText => _queue.Count > 1 ? string.Format(Localization.Get("Mcp.Confirm.Queue"), _queue.Count - 1) : string.Empty;

    public IReadOnlyList<McpChangeRowItem> Rows => Current?.Request.Summary.Rows
        .Select(r => new McpChangeRowItem(r.Subject, ChangeSummaryText.Field(r.Field, Localization), r.Old ?? "—", r.New ?? "—"))
        .ToList() ?? [];

    public bool HasRows => Current?.Request.Summary.Rows.Count > 0;

    /// <summary>Adds a request; completes with the user's answer (or «Відхилити» when it expires).</summary>
    public Task<McpDecision> Enqueue(McpChangeRequest request, CancellationToken cancellationToken)
    {
        var pending = new Pending(request);
        _queue.Add(pending);
        cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Complete(pending, McpDecision.Rejected)));
        Refresh();
        return pending.Answer.Task;
    }

    [RelayCommand]
    private void Allow()
    {
        if (Current is { } p)
            Complete(p, new McpDecision(true, RememberForSession));
    }

    [RelayCommand]
    private void Reject()
    {
        if (Current is { } p)
            Complete(p, McpDecision.Rejected);
    }

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    /// <summary>Refuses every waiting request (the assistant was switched off).</summary>
    public void RejectAll()
    {
        foreach (var p in _queue.ToList())
            Complete(p, McpDecision.Rejected);
    }

    private void Complete(Pending pending, McpDecision decision)
    {
        pending.Answer.TrySetResult(decision);
        if (_queue.Remove(pending))
        {
            ShowDetails = false;
            RememberForSession = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        Current = _queue.FirstOrDefault();
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Subline));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(Rows));
    }

    public sealed class Pending(McpChangeRequest request)
    {
        public McpChangeRequest Request { get; } = request;
        public TaskCompletionSource<McpDecision> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
