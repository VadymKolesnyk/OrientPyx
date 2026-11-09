using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Edits the current competition's metadata: name, venue, organisation and start/end dates.
/// Opened from the "Competition → Information" top menu.
/// </summary>
public sealed partial class CompetitionInfoViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IEventCatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly IActivityLog _log;
    private readonly IBackgroundActivityService _activities;

    private CompetitionInfo? _info;

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// The competition's identifier — the name of its folder under the events path. Shown read-only;
    /// changed through the rename modal, which also moves the folder.
    /// </summary>
    [ObservableProperty]
    private string _identifier = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private string _organisation = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _startDate;

    [ObservableProperty]
    private DateTimeOffset? _endDate;

    [ObservableProperty]
    private string _courseSetter = string.Empty;

    [ObservableProperty]
    private string _courseSetterCategory = string.Empty;

    [ObservableProperty]
    private string _chiefJudge = string.Empty;

    [ObservableProperty]
    private string _chiefJudgeCategory = string.Empty;

    [ObservableProperty]
    private string _chiefSecretary = string.Empty;

    [ObservableProperty]
    private string _chiefSecretaryCategory = string.Empty;

    [ObservableProperty]
    private string _jury = string.Empty;

    [ObservableProperty]
    private bool _saved;

    public CompetitionInfoViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IEventCatalogService catalog,
        IDialogService dialogs,
        IActivityLog log,
        IBackgroundActivityService activities,
        IDataChangeBus bus,
        IWorkspaceMode mode)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        _catalog = catalog;
        _dialogs = dialogs;
        _log = log;
        _activities = activities;
        // Singleton VM: re-read the form whenever the competition changes so a switched event
        // never leaves the previous competition's metadata on screen. The event may arrive on a
        // pool thread (session writes run inside RunAsync), so marshal LoadAsync onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Competition);
    }

    public override string NavKey => "Nav.CompetitionInfo";
    public override string TitleKey => "Page.CompetitionInfo.Title";
    public override string TextKey => "Page.CompetitionInfo.Text";

    // True while LoadAsync fills the form, so the field setters don't count as the operator's edits.
    private bool _loading;

    // True once the operator changed a field after the last load/save: a live reload must not replace their
    // typing with what another station saved — their own Save decides.
    private bool _dirty;

    private void MarkEdited()
    {
        Saved = false;
        if (!_loading)
            _dirty = true;
    }

    /// <inheritdoc />
    /// <remarks>The form is re-read only while it holds no unsaved edits of its own.</remarks>
    protected override Task ReloadAsync(CancellationToken cancellationToken = default)
        => _dirty ? Task.CompletedTask : LoadAsync();

    /// <summary>Re-reads the current competition into the form. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        Saved = false;
        // BD read runs off the UI thread; the form fields are set afterwards on the UI thread.
        _info = await _busy.RunAsync(() => _editor.GetInfoAsync());
        _loading = true;
        Name = _info?.Name ?? string.Empty;
        Identifier = _session.CurrentEvent?.Identifier ?? _info?.Identifier ?? string.Empty;
        Venue = _info?.Venue ?? string.Empty;
        Organisation = _info?.Organisation ?? string.Empty;
        StartDate = _info?.StartDate;
        EndDate = _info?.EndDate;
        CourseSetter = _info?.CourseSetter ?? string.Empty;
        CourseSetterCategory = _info?.CourseSetterCategory ?? string.Empty;
        ChiefJudge = _info?.ChiefJudge ?? string.Empty;
        ChiefJudgeCategory = _info?.ChiefJudgeCategory ?? string.Empty;
        ChiefSecretary = _info?.ChiefSecretary ?? string.Empty;
        ChiefSecretaryCategory = _info?.ChiefSecretaryCategory ?? string.Empty;
        Jury = _info?.Jury ?? string.Empty;
        _loading = false;
        _dirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_info is null)
            return;

        _info.Name = (Name ?? string.Empty).Trim();
        _info.Venue = (Venue ?? string.Empty).Trim();
        _info.Organisation = (Organisation ?? string.Empty).Trim();
        _info.StartDate = StartDate;
        _info.EndDate = EndDate;
        _info.CourseSetter = (CourseSetter ?? string.Empty).Trim();
        _info.CourseSetterCategory = (CourseSetterCategory ?? string.Empty).Trim();
        _info.ChiefJudge = (ChiefJudge ?? string.Empty).Trim();
        _info.ChiefJudgeCategory = (ChiefJudgeCategory ?? string.Empty).Trim();
        _info.ChiefSecretary = (ChiefSecretary ?? string.Empty).Trim();
        _info.ChiefSecretaryCategory = (ChiefSecretaryCategory ?? string.Empty).Trim();
        _info.Jury = (Jury ?? string.Empty).Trim();

        await _busy.RunAsync(() => _editor.SaveInfoAsync(_info));

        // Reflect a possibly changed name/venue in the session (window title, context strip).
        if (_session.CurrentEvent is { } current)
        {
            _session.UpdateCurrentEvent(new EventSummary
            {
                Identifier = current.Identifier,
                Name = _info.Name,
                Venue = _info.Venue,
                FolderPath = current.FolderPath,
                CreatedAt = current.CreatedAt,
                DayCount = current.DayCount
            });
        }

        Saved = true;
        _dirty = false;
    }

    /// <summary>Opens the «Додаткові поля» modal (the competition's additional participant fields, Р-13).</summary>
    [RelayCommand]
    private async Task EditCustomFieldsAsync()
    {
        if (_session.CurrentEvent is null)
            return;
        var dialog = new CustomFieldsViewModel(Localization, _editor, _busy);
        await dialog.LoadAsync();
        await _dialogs.ShowCustomFieldsAsync(dialog);
    }

    /// <summary>
    /// Opens the rename modal and applies the chosen identifier: the catalog moves the competition
    /// folder and rewrites the identifier in its database, then the session is re-pointed at the new
    /// folder. Blocked while a background process is running — those keep writing into the folder that
    /// is about to move.
    /// </summary>
    [RelayCommand]
    private async Task RenameIdentifierAsync()
    {
        if (_session.CurrentEvent is not { } current)
            return;

        if (_activities.IsAnyActive)
        {
            await ShowMessageAsync("CompetitionInfo.Rename.BusyActivities");
            return;
        }

        var dialog = new RenameEventViewModel(
            Localization, current.Identifier, candidate => _catalog.IsIdentifierAvailableAsync(candidate));
        var newIdentifier = await _dialogs.ShowRenameEventAsync(dialog);
        if (newIdentifier is null)
            return;

        // The log writes into the competition folder; move it out first so no line lands in (and
        // re-creates) the folder while it is being renamed. The session points it at the new folder after.
        _log.UseSharedFolder();

        EventSummary renamed;
        try
        {
            renamed = await _busy.RunAsync(() => _catalog.RenameIdentifierAsync(current, newIdentifier));
        }
        catch (Exception ex)
        {
            _log.UseEventFolder(current.FolderPath);
            _log.Error("Не вдалося змінити ідентифікатор змагань", ex);
            await ShowMessageAsync(ex is StationsActiveException
                ? "CompetitionInfo.Rename.StationsActive"
                : "CompetitionInfo.Rename.Error");
            return;
        }

        await _busy.RunAsync(() => _session.RenameCurrentEventAsync(renamed));
        Identifier = renamed.Identifier;
    }

    // Reuses the import-options modal as a plain "title + message + OK" box (no toggles).
    private Task ShowMessageAsync(string messageKey) =>
        _dialogs.ShowImportOptionsAsync(new ImportOptionsViewModel(
            Localization,
            titleKey: "CompetitionInfo.Rename.Title",
            messageKey: messageKey,
            options: []));

    partial void OnNameChanged(string value) => MarkEdited();
    partial void OnVenueChanged(string value) => MarkEdited();
    partial void OnOrganisationChanged(string value) => MarkEdited();
    partial void OnStartDateChanged(DateTimeOffset? value) => MarkEdited();
    partial void OnEndDateChanged(DateTimeOffset? value) => MarkEdited();
    partial void OnCourseSetterChanged(string value) => MarkEdited();
    partial void OnCourseSetterCategoryChanged(string value) => MarkEdited();
    partial void OnChiefJudgeChanged(string value) => MarkEdited();
    partial void OnChiefJudgeCategoryChanged(string value) => MarkEdited();
    partial void OnChiefSecretaryChanged(string value) => MarkEdited();
    partial void OnChiefSecretaryCategoryChanged(string value) => MarkEdited();
    partial void OnJuryChanged(string value) => MarkEdited();
}
