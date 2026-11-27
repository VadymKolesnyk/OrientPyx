using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Shared;

namespace OrientPyx.Presentation.Services.Mcp;

/// <summary>
/// The window's side of the assistant (<see cref="IMcpUi"/>): the confirmation bar (with the taskbar flash and a toast —
/// the window may be behind the chat), opening a competition as the picker does, navigation for «show_in_app», the
/// program's texts, the agent journal and the «MCP» activity. Called from the server's threads; marshals to the UI thread.
/// </summary>
public sealed class McpUi : IMcpUi
{
    private readonly IServiceProvider _services;
    private readonly ILocalizationService _localization;
    private readonly McpConfirmBarViewModel _bar;
    private readonly McpActionJournal _journal;
    private readonly IDesktopAlerts _alerts;
    private readonly IToastService _toasts;
    private readonly IBackgroundActivityService _activities;
    private readonly McpActivity _activity;
    private readonly McpOutputLabels _labels;
    private IMcpServer? _server;
    private bool _registered;
    private int _calls;

    public McpUi(
        IServiceProvider services,
        ILocalizationService localization,
        McpConfirmBarViewModel bar,
        McpActionJournal journal,
        IDesktopAlerts alerts,
        IToastService toasts,
        IBackgroundActivityService activities,
        IDisciplineStrategyProvider strategies)
    {
        _services = services;
        _localization = localization;
        _bar = bar;
        _journal = journal;
        _alerts = alerts;
        _toasts = toasts;
        _activities = activities;
        _labels = new McpOutputLabels(localization, strategies);
        _activity = new McpActivity(localization, () => _ = SwitchOffAsync(), OpenSettings);
        _journal.Changed += (_, _) => UpdateActivity();
        _localization.PropertyChanged += (_, _) => UpdateActivity();
    }

    /// <summary>Follows the server's state: the activity while it runs, the waiting bars dropped when it stops.</summary>
    public void Attach(IMcpServer server)
    {
        _server = server;
        server.Changed += (_, _) => Dispatcher.UIThread.Post(OnServerChanged);
        Dispatcher.UIThread.Post(OnServerChanged);
    }

    private void OnServerChanged()
    {
        if (_server is not { } server)
            return;
        var running = server.State == McpServerState.Running;
        if (running && !_registered)
        {
            _activities.Register(_activity);
            _registered = true;
        }
        else if (!running && _registered)
        {
            _activities.Unregister(_activity);
            _registered = false;
        }
        if (server.State == McpServerState.Off)
            _bar.RejectAll();
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var last = _journal.Items.FirstOrDefault();
        _activity.StatusText = string.Format(_localization.Get("Activity.Mcp.Status"),
            _server?.ConnectedClients ?? 0, _journal.Count, _calls) +
            (last is null ? string.Empty : Environment.NewLine + last.Time + " " + last.Text);
    }

    private async Task SwitchOffAsync()
    {
        var store = _services.GetRequiredService<IAppStore>();
        await Task.Run(async () =>
        {
            var settings = await store.GetMcpSettingsAsync();
            settings.Enabled = false;
            await store.SaveMcpSettingsAsync(settings);
            if (_server is not null)
                await _server.ApplySettingsAsync();
        });
        _services.GetService<ViewModels.Pages.McpSettingsViewModel>()?.Reload();
    }

    private void OpenSettings() => _services.GetRequiredService<MainWindowViewModel>().OpenSettingsCommand.Execute(null);

    // ── IMcpUi

    public async Task<McpDecision> ConfirmAsync(McpChangeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            Task<McpDecision>? answer = null;
            await Dispatcher.UIThread.InvokeAsync(() => { answer = _bar.Enqueue(request, cancellationToken); });
            _alerts.RequestAttention();
            Dispatcher.UIThread.Post(() => _toasts.Show(
                _localization.Get("Mcp.Confirm.ToastTitle"), ChangeSummaryText.Format(request.Summary, _localization), BringToFront));
            return await answer!;
        }
        catch (Exception)
        {
            return McpDecision.Rejected;
        }
    }

    public async Task<bool> OpenCompetitionAsync(EventSummary competition, int? dayNumber, CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(() => _services.GetRequiredService<EventSelectionViewModel>().OpenAsync(competition, dayNumber));

    public async Task<bool> ShowAsync(McpShowTarget target, CancellationToken cancellationToken)
    {
        var session = _services.GetRequiredService<ISessionService>();
        var editor = _services.GetRequiredService<ICompetitionEditorService>();
        if (session.CurrentEvent is null)
            return false;
        var days = await editor.GetDaysAsync(cancellationToken);
        var day = target.DayNumber is { } n ? days.FirstOrDefault(d => d.Number == n) : null;
        Guid? link = null;
        if (target.ParticipantId is { } pid)
        {
            var dayId = (day ?? session.CurrentDay)?.Id;
            link = (await editor.GetParticipantRosterAsync(cancellationToken))
                .FirstOrDefault(p => p.ParticipantId == pid)?.Days.FirstOrDefault(d => d.DayId == dayId)?.LinkId;
        }
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (day is not null && session.CurrentDay?.Id != day.Id)
                await session.SetCurrentDayAsync(day);
            var shown = await _services.GetRequiredService<MainWindowViewModel>().ShowForAssistantAsync(target.Page, link);
            if (shown)
                BringToFront();
            return shown;
        });
    }

    public string? GetHelp(string key)
    {
        var text = _localization.Get(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? null : text;
    }

    public IReadOnlyList<string> HelpKeys => _localization.KeysStartingWith("Help.");

    public string Text(string key) => _localization.Get(key);

    public void Record(McpActionRecord record) => Dispatcher.UIThread.Post(() => _journal.Add(record));

    public void CallFinished(McpCallRecord record) => Dispatcher.UIThread.Post(() =>
    {
        _calls++;
        UpdateActivity();
    });

    public async Task<IReadOnlyList<McpActivityInfo>> ActivitiesAsync(CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(() => (IReadOnlyList<McpActivityInfo>)_activities.Activities
            .Select(a => new McpActivityInfo(a.Title, a.StatusText, a.State switch
            {
                BackgroundActivityState.Paused => "paused",
                BackgroundActivityState.Failing => "failing",
                _ => "running",
            }, a.IsPaused, a.HasProblem))
            .ToList());

    public IOutputLabels Labels => _labels;

    private static void BringToFront()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Activate();
        }
    }
}
