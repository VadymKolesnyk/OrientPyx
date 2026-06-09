using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Edits the current competition's metadata: name, venue, organisation and start/end dates.
/// Opened from the "Competition → Information" top menu.
/// </summary>
public sealed partial class CompetitionInfoViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;

    private CompetitionInfo? _info;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private string _organisation = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _startDate;

    [ObservableProperty]
    private DateTimeOffset? _endDate;

    [ObservableProperty]
    private bool _saved;

    public CompetitionInfoViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        // Singleton VM: re-read the form whenever the competition changes so a switched event
        // never leaves the previous competition's metadata on screen.
        _session.SessionChanged += (_, _) => _ = LoadAsync();
    }

    public override string NavKey => "Nav.CompetitionInfo";
    public override string TitleKey => "Page.CompetitionInfo.Title";
    public override string TextKey => "Page.CompetitionInfo.Text";

    /// <summary>Re-reads the current competition into the form. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        Saved = false;
        _info = await _editor.GetInfoAsync();
        Name = _info?.Name ?? string.Empty;
        Venue = _info?.Venue ?? string.Empty;
        Organisation = _info?.Organisation ?? string.Empty;
        StartDate = _info?.StartDate;
        EndDate = _info?.EndDate;
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
    }

    partial void OnNameChanged(string value) => Saved = false;
    partial void OnVenueChanged(string value) => Saved = false;
    partial void OnOrganisationChanged(string value) => Saved = false;
    partial void OnStartDateChanged(DateTimeOffset? value) => Saved = false;
    partial void OnEndDateChanged(DateTimeOffset? value) => Saved = false;
}
