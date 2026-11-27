using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.DataAccess.Mcp;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.Services.Mcp;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>A choice of the assistant mode.</summary>
public sealed record McpModeOption(McpMode Mode, string Label);

/// <summary>A chat client to copy the settings for.</summary>
public sealed record McpClientOption(string Key, string Label);

/// <summary>
/// The «Асистент (MCP)» block of «Налаштування» (plan §9): the switch (off by default; the first switch-on asks about the
/// data going to the model's provider), the mode, the state, the Claude Desktop connection, the settings to copy for the
/// other clients, a new token, the agent journal. Singleton: the block and the «MCP» activity share it.
/// </summary>
public sealed partial class McpSettingsViewModel : ObservableObject
{
    private readonly IAppStore _store;
    private readonly IMcpServer _server;
    private readonly IMcpClientSetup _setup;
    private readonly IDialogService _dialogs;
    private readonly IActivityLog _log;
    private McpSettings _settings = new();
    private bool _loading;

    public McpSettingsViewModel(
        ILocalizationService localization,
        IAppStore store,
        IMcpServer server,
        IMcpClientSetup setup,
        IDialogService dialogs,
        IActivityLog log,
        McpActionJournal journal)
    {
        Localization = localization;
        _store = store;
        _server = server;
        _setup = setup;
        _dialogs = dialogs;
        _log = log;
        Journal = journal;
        _server.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshState);
        Journal.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJournal));
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ModeOptions));
            OnPropertyChanged(nameof(ModeDescription));
            OnPropertyChanged(nameof(ClientOptions));
            RefreshState();
        };
        Reload();
    }

    public ILocalizationService Localization { get; }

    public McpActionJournal Journal { get; }

    public ObservableCollection<McpJournalItem> JournalItems => Journal.Items;

    public bool HasJournal => Journal.Items.Count > 0;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private McpModeOption? _selectedMode;

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private bool _hasProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenShown))]
    private bool _revealToken;

    [ObservableProperty]
    private McpClientOption? _selectedClient;

    public bool HasMessage => Message.Length > 0;

    public bool IsRunning => _server.State == McpServerState.Running;

    public string Url => $"http://127.0.0.1:{(_server.Port is > 0 ? _server.Port : _settings.Port is > 0 ? _settings.Port : McpHttpServer.DefaultPort)}/mcp";

    public string Token => _settings.Token;

    public string TokenShown => RevealToken ? _settings.Token : new string('•', Math.Min(24, Math.Max(8, _settings.Token.Length)));

    public bool HasToken => _settings.Token.Length > 0;

    public IReadOnlyList<McpModeOption> ModeOptions =>
    [
        new(McpMode.ReadOnly, Localization.Get("Mcp.Settings.Mode.ReadOnly")),
        new(McpMode.ConfirmChanges, Localization.Get("Mcp.Settings.Mode.Confirm")),
        new(McpMode.AutoChanges, Localization.Get("Mcp.Settings.Mode.Auto")),
        new(McpMode.NoConfirmation, Localization.Get("Mcp.Settings.Mode.NoConfirm")),
    ];

    /// <summary>What the chosen mode lets the assistant do without asking (the line under the mode list).</summary>
    public string ModeDescription => Localization.Get((SelectedMode?.Mode ?? McpMode.ReadOnly) switch
    {
        McpMode.ConfirmChanges => "Mcp.Settings.Mode.Desc.Confirm",
        McpMode.AutoChanges => "Mcp.Settings.Mode.Desc.Auto",
        McpMode.NoConfirmation => "Mcp.Settings.Mode.Desc.NoConfirm",
        _ => "Mcp.Settings.Mode.Desc.ReadOnly",
    });

    public IReadOnlyList<McpClientOption> ClientOptions =>
    [
        new("claude_code", "Claude Code"),
        new("gemini_cli", "Gemini CLI"),
        new("vscode", "VS Code"),
        new("other", Localization.Get("Mcp.Settings.Client.Other")),
    ];

    /// <summary>Reads the settings again (after the activity switched the assistant off, or on open).</summary>
    public void Reload() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            // SQLite completes synchronously: never on the UI thread (CLAUDE.md «Coding style»).
            var settings = await Task.Run(() => _store.GetMcpSettingsAsync());
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loading = true;
                _settings = settings;
                IsEnabled = settings.Enabled;
                SelectedMode = ModeOptions.First(o => o.Mode == settings.Mode);
                SelectedClient ??= ClientOptions[0];
                _loading = false;
                RefreshState();
            });
        }
        catch (Exception ex)
        {
            _log.Error("MCP settings: loading", ex);
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading)
            return;
        _ = ApplyEnabledAsync(value);
    }

    partial void OnSelectedModeChanged(McpModeOption? value)
    {
        if (_loading || value is null || value.Mode == _settings.Mode)
            return;
        _ = ApplyModeAsync(value.Mode);
    }

    private async Task ApplyModeAsync(McpMode mode)
    {
        // «Без підтвердження» lets the assistant run imports, draws and settings changes nobody can undo: ask once.
        if (mode == McpMode.NoConfirmation)
        {
            var agreed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization, "Mcp.Settings.NoConfirm.Title", "Mcp.Settings.NoConfirm.Message", "Mcp.Settings.NoConfirm.Confirm", "Common.Cancel"));
            if (!agreed)
            {
                _loading = true;
                SelectedMode = ModeOptions.First(o => o.Mode == _settings.Mode);
                _loading = false;
                return;
            }
        }
        await SaveAsync(s => s.Mode = mode, $"mode → {mode}");
    }

    private async Task ApplyEnabledAsync(bool enabled)
    {
        if (enabled && !_settings.Enabled && _settings.Token.Length == 0)
        {
            // The first switch-on: the competitions' data (names, birth dates…) goes to the model provider of the
            // user's own chat client — say so once, before anything listens.
            var agreed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization, "Mcp.Settings.Privacy.Title", "Mcp.Settings.Privacy.Message", "Mcp.Settings.Privacy.Confirm", "Common.Cancel"));
            if (!agreed)
            {
                _loading = true;
                IsEnabled = false;
                _loading = false;
                return;
            }
        }
        await SaveAsync(s => s.Enabled = enabled, enabled ? "switched on" : "switched off");
    }

    private async Task SaveAsync(Action<McpSettings> change, string what)
    {
        try
        {
            await Task.Run(async () =>
            {
                var settings = await _store.GetMcpSettingsAsync();
                change(settings);
                await _store.SaveMcpSettingsAsync(settings);
                _log.Action("MCP: " + what);
                await _server.ApplySettingsAsync();
            });
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _log.Error("MCP settings: saving", ex);
            Message = ex.Message;
        }
    }

    private void RefreshState()
    {
        HasProblem = _server.State is McpServerState.PortBusy or McpServerState.Failed;
        StateText = _server.State switch
        {
            McpServerState.Running => string.Format(Localization.Get("Mcp.Settings.State.Running"), $"127.0.0.1:{_server.Port}", _server.ConnectedClients),
            McpServerState.ServedElsewhere => string.Format(Localization.Get("Mcp.Settings.State.Elsewhere"), _server.ServedBy ?? "—"),
            McpServerState.PortBusy => string.Format(Localization.Get("Mcp.Settings.State.PortBusy"), _server.Port),
            McpServerState.Failed => string.Format(Localization.Get("Mcp.Settings.State.Failed"), _server.Error),
            _ => Localization.Get("Mcp.Settings.State.Off"),
        };
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(Token));
        OnPropertyChanged(nameof(TokenShown));
        OnPropertyChanged(nameof(HasToken));
    }

    [RelayCommand]
    private void ToggleRevealToken() => RevealToken = !RevealToken;

    [RelayCommand]
    private async Task NewTokenAsync()
    {
        var agreed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization, "Mcp.Settings.NewToken.Title", "Mcp.Settings.NewToken.Message", "Mcp.Settings.NewToken.Confirm", "Common.Cancel"));
        if (!agreed)
            return;
        // A running server keeps the old token until it restarts: stop, change, start.
        await Task.Run(async () =>
        {
            var settings = await _store.GetMcpSettingsAsync();
            var wasOn = settings.Enabled;
            if (wasOn)
            {
                settings.Enabled = false;
                await _store.SaveMcpSettingsAsync(settings);
                await _server.ApplySettingsAsync();
            }
            settings.Token = McpHttpServer.NewToken();
            settings.Enabled = wasOn;
            await _store.SaveMcpSettingsAsync(settings);
            await _server.ApplySettingsAsync();
        });
        _log.Action("MCP: a new token");
        Message = Localization.Get("Mcp.Settings.NewToken.Done");
        await LoadAsync();
    }

    /// <summary>Picks another port (the remembered one is taken by another program).</summary>
    [RelayCommand]
    private async Task ChoosePortAsync() => await SaveAsync(s => s.Port = 0, "another port");

    [RelayCommand]
    private async Task ConnectClaudeDesktopAsync()
    {
        try
        {
            var path = await Task.Run(() => _setup.CreateClaudeDesktopExtensionAsync());
            Message = FileReveal.TryOpenFile(path)
                ? Localization.Get("Mcp.Settings.Claude.Opened")
                : string.Format(Localization.Get("Mcp.Settings.Claude.Saved"), path);
            _log.Action("MCP: Claude Desktop extension " + path);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            _log.Error("MCP: Claude Desktop extension", ex);
        }
    }

    [RelayCommand]
    private async Task AddToClaudeConfigAsync()
    {
        try
        {
            var path = await Task.Run(() => _setup.AddToClaudeDesktopConfigAsync());
            Message = string.Format(Localization.Get("Mcp.Settings.Claude.ConfigDone"), path);
            _log.Action("MCP: Claude Desktop config " + path);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            _log.Error("MCP: Claude Desktop config", ex);
        }
    }

    /// <summary>The text «Скопіювати налаштування для…» puts on the clipboard (the view copies it).</summary>
    public string ClientConfig() => McpClientConfigs.For(SelectedClient?.Key ?? "other", Url, _settings.Token, _setup.BridgeCommand);

    public void Copied() => Message = Localization.Get("Mcp.Settings.Copied");
}
