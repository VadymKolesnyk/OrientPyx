using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services.Mcp;

/// <summary>One done change of the assistant in the journal; «Скасувати» reverts its undo step.</summary>
public sealed partial class McpJournalItem : ObservableObject
{
    private readonly Func<McpJournalItem, Task> _undo;

    public McpJournalItem(McpActionRecord record, string text, Func<McpJournalItem, Task> undo)
    {
        Record = record;
        Text = text;
        _undo = undo;
    }

    public McpActionRecord Record { get; }

    public string Time => Record.At.ToString("HH:mm:ss");

    public string Text { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _undone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    private string _outcome = string.Empty;

    public bool HasOutcome => Outcome.Length > 0;

    public bool CanUndo => Record.Undoable && Record.StepId is not null && !Undone;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync() => _undo(this);
}

/// <summary>
/// The agent journal (plan §7.4): the latest changes of the assistant, newest first, with «Скасувати» for the undoable ones
/// (the assistant's steps are not in the pages' Ctrl+Z histories). UI thread.
/// </summary>
public sealed class McpActionJournal
{
    private const int Max = 100;

    private readonly ILocalizationService _localization;
    private readonly IEditHistoryService _history;
    private readonly IBusyService _busy;
    private readonly IActivityLog _log;

    public McpActionJournal(ILocalizationService localization, IEditHistoryService history, IBusyService busy, IActivityLog log)
    {
        _localization = localization;
        _history = history;
        _busy = busy;
        _log = log;
    }

    public ObservableCollection<McpJournalItem> Items { get; } = [];

    public int Count => Items.Count;

    public event EventHandler? Changed;

    public void Add(McpActionRecord record)
    {
        Items.Insert(0, new McpJournalItem(record, ChangeSummaryText.Format(record.Summary, _localization), UndoAsync));
        while (Items.Count > Max)
            Items.RemoveAt(Items.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task UndoAsync(McpJournalItem item)
    {
        if (item.Record.StepId is not { } step)
            return;
        try
        {
            var outcome = await _busy.RunAsync(() => _history.RevertAsync(step));
            var status = outcome.Result.Status;
            item.Undone = status is UndoRevertStatus.Done or UndoRevertStatus.AlreadyReverted;
            item.Outcome = _localization.Get("Mcp.Journal.Undo." + status);
            _log.Action($"MCP journal: undo «{item.Text}» → {status}");
        }
        catch (Exception ex)
        {
            item.Outcome = ex.Message;
            _log.Error("MCP journal: undo", ex);
        }
    }
}
