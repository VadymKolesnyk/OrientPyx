using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IParticipantExportFlow"/>. Shows the format modal, then serialises the captured
/// view with the <see cref="ITabularWriter"/> registered for the chosen format (CSV in BusinessLogic,
/// .xlsx in DataAccess — resolved here from the injected set). Writing runs under the busy overlay. The
/// flow never touches the file system; it returns the bytes for the view to save.
/// </summary>
public sealed class ParticipantExportFlow : IParticipantExportFlow
{
    private readonly ILocalizationService _localization;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;
    private readonly IReadOnlyDictionary<ExportFormat, ITabularWriter> _writers;

    public ParticipantExportFlow(
        ILocalizationService localization,
        ISessionService session,
        IDialogService dialogs,
        IBusyService busy,
        IEnumerable<ITabularWriter> writers)
    {
        _localization = localization;
        _session = session;
        _dialogs = dialogs;
        _busy = busy;
        // One writer per format; the last registration wins if a format were ever registered twice.
        _writers = writers.ToDictionary(w => w.Format);
    }

    public async Task<ParticipantExportResult?> RunAsync(CsvParticipantData view)
    {
        if (_session.CurrentEvent is null || view.Header.Count == 0)
            return null;

        var format = await _dialogs.ShowExportFormatAsync(new ExportFormatViewModel(_localization, view.Rows.Count));
        if (format is null)
            return null; // cancelled

        if (!_writers.TryGetValue(format.Value, out var writer))
            return null;

        var bytes = await _busy.RunAsync(() => Task.FromResult(writer.Write(view)));

        var (extension, mime) = format.Value switch
        {
            ExportFormat.Excel => ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => ("csv", "text/csv")
        };

        return new ParticipantExportResult(format.Value, bytes, SuggestedFileName(extension), extension, mime);
    }

    // "<competition> — учасники <date>.<ext>", sanitised of path-illegal characters so the save dialog
    // accepts it as a default name.
    private string SuggestedFileName(string extension)
    {
        var competition = _session.CurrentEvent?.Name;
        if (string.IsNullOrWhiteSpace(competition))
            competition = _localization.Get("Export.DefaultName");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var baseName = $"{competition} — {_localization.Get("Export.NamePart")} {stamp}";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        return $"{baseName}.{extension}";
    }
}
