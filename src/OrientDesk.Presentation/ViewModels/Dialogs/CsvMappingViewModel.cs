using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for mapping a CSV file's columns onto our participant fields before importing. One row per
/// importable field (<see cref="CsvParticipantField"/>); each row carries a dropdown of the file's
/// columns plus a leading "— do not import —" sentinel. A best-effort auto-guess pre-selects a column
/// whose header looks like the field, so a tidy file needs no manual work. A single "clear existing
/// participants first" toggle mirrors the XML import. Callers <c>await</c> <see cref="Completion"/> for
/// the chosen mapping, or null on cancel.
/// </summary>
public sealed partial class CsvMappingViewModel : ObservableObject
{
    private readonly TaskCompletionSource<CsvMappingResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="header">The CSV column captions, in file order.</param>
    /// <param name="rowCount">How many data rows the file holds (shown in the modal's message).</param>
    public CsvMappingViewModel(ILocalizationService localization, IReadOnlyList<string> header, int rowCount)
    {
        Localization = localization;
        RowCount = rowCount;

        // The shared column choices: a "(skip)" sentinel at index 0, then one per file column.
        var columns = new List<CsvColumnOption> { CsvColumnOption.Skip(Localization) };
        for (var i = 0; i < header.Count; i++)
            columns.Add(CsvColumnOption.ForColumn(i, header[i]));
        Columns = columns;

        // One mapping row per field, each pre-selecting its best header guess (or "(skip)").
        Fields = new ObservableCollection<CsvFieldMapping>(
            ImportableFields.Select(f => new CsvFieldMapping(
                Localization, f, columns, Guess(f, header, columns))));

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(ClearFirstLabel));
            foreach (var f in Fields)
                f.Refresh();
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("CsvImport.Title");

    public string Message => string.Format(Localization.Get("CsvImport.Message"), RowCount);

    public string ClearFirstLabel => Localization.Get("ParticipantsImport.ClearFirst");

    public int RowCount { get; }

    /// <summary>The column choices shared by every field's dropdown ("(skip)" first, then file columns).</summary>
    public IReadOnlyList<CsvColumnOption> Columns { get; }

    /// <summary>One mapping row per importable field, in display order.</summary>
    public ObservableCollection<CsvFieldMapping> Fields { get; }

    /// <summary>Whether to wipe the participant database before importing (default off).</summary>
    [ObservableProperty]
    private bool _clearFirst;

    /// <summary>Completes with the chosen mapping on confirm, or null on cancel/close.</summary>
    public Task<CsvMappingResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        // Collect field → column-index for every mapped field (skip the "(skip)" rows).
        var map = new Dictionary<CsvParticipantField, int>();
        foreach (var f in Fields)
            if (f.SelectedColumn is { IsSkip: false } col)
                map[f.Field] = col.Index;

        _completion.TrySetResult(new CsvMappingResult(map, ClearFirst));
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);

    // The fields we let a CSV map onto, in the order shown in the modal. FullName leads (it's required).
    private static readonly CsvParticipantField[] ImportableFields =
    [
        CsvParticipantField.FullName,
        CsvParticipantField.Number,
        CsvParticipantField.BirthDate,
        CsvParticipantField.Group,
        CsvParticipantField.Team,
        CsvParticipantField.Region,
        CsvParticipantField.Club,
        CsvParticipantField.Dussh,
        CsvParticipantField.Chip,
        CsvParticipantField.Rank,
        CsvParticipantField.Representative,
        CsvParticipantField.FsouCode,
        CsvParticipantField.IsFsouMember,
        CsvParticipantField.Payment,
        CsvParticipantField.Coach
    ];

    // Best-effort header match: pick the first column whose caption contains one of the field's
    // keyword hints (case/space-insensitive). Returns "(skip)" when nothing looks right.
    private static CsvColumnOption Guess(
        CsvParticipantField field,
        IReadOnlyList<string> header,
        IReadOnlyList<CsvColumnOption> columns)
    {
        var hints = Hints(field);
        for (var i = 0; i < header.Count; i++)
        {
            var caption = Normalize(header[i]);
            if (caption.Length == 0)
                continue;
            if (hints.Any(h => caption.Contains(h, StringComparison.Ordinal)))
                return columns[i + 1]; // +1: index 0 is the "(skip)" sentinel
        }
        return columns[0];
    }

    private static string Normalize(string s) =>
        new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    // Lowercase, whitespace-free substrings (Ukrainian + English) hinting at each field.
    private static string[] Hints(CsvParticipantField field) => field switch
    {
        CsvParticipantField.FullName => ["прізвище", "ім'я", "піб", "fio", "name", "учасник"],
        CsvParticipantField.Number => ["номер", "№", "нагрудний", "стартовийномер", "bib", "number", "startno", "start#"],
        CsvParticipantField.Team => ["команд", "team", "екіпаж"],
        CsvParticipantField.BirthDate => ["народж", "рікнар", "датанар", "birth", "born", "ддр", "дн"],
        CsvParticipantField.Group => ["група", "group", "категор"],
        CsvParticipantField.Region => ["регіон", "область", "region"],
        CsvParticipantField.Club => ["клуб", "club", "колектив", "команд"],
        CsvParticipantField.Dussh => ["дюсш", "сдюшор", "школа", "dussh"],
        CsvParticipantField.Chip => ["чіп", "чип", "card", "chip", "si"],
        CsvParticipantField.Rank => ["розряд", "кваліфік", "rank", "qualif"],
        CsvParticipantField.Representative => ["представник", "predst"],
        CsvParticipantField.FsouCode => ["кодфсоу", "foucode", "fsoucode", "кодфоу"],
        CsvParticipantField.IsFsouMember => ["членфсоу", "членфоу", "fsou"],
        CsvParticipantField.Payment => ["оплата", "сплата", "внесок", "payment", "pay"],
        CsvParticipantField.Coach => ["тренер", "trener", "coach"],
        _ => []
    };
}

/// <summary>One row in the mapping modal: a fixed field caption + a chosen source column.</summary>
public sealed partial class CsvFieldMapping : ObservableObject
{
    private readonly ILocalizationService _localization;

    public CsvFieldMapping(
        ILocalizationService localization,
        CsvParticipantField field,
        IReadOnlyList<CsvColumnOption> columns,
        CsvColumnOption selected)
    {
        _localization = localization;
        Field = field;
        Columns = columns;
        _selectedColumn = selected;
        _label = localization.Get(LabelKey(field));
    }

    public CsvParticipantField Field { get; }

    /// <summary>The shared column choices (same instance list for every row).</summary>
    public IReadOnlyList<CsvColumnOption> Columns { get; }

    /// <summary>Localized field caption shown at the left of the row.</summary>
    [ObservableProperty]
    private string _label;

    /// <summary>The source column chosen for this field ("(skip)" = not imported).</summary>
    [ObservableProperty]
    private CsvColumnOption _selectedColumn;

    /// <summary>Re-resolves the caption on a language switch.</summary>
    public void Refresh() => Label = _localization.Get(LabelKey(Field));

    private static string LabelKey(CsvParticipantField field) => "CsvImport.Field." + field;
}

/// <summary>One choice in a field's column dropdown: the "(skip)" sentinel or a specific file column.</summary>
public sealed class CsvColumnOption
{
    private CsvColumnOption(bool isSkip, int index, string label)
    {
        IsSkip = isSkip;
        Index = index;
        Label = label;
    }

    /// <summary>True for the leading "— do not import —" sentinel.</summary>
    public bool IsSkip { get; }

    /// <summary>0-based column index in the file. Unused when <see cref="IsSkip"/>.</summary>
    public int Index { get; }

    /// <summary>Text shown in the dropdown (the column caption, or the localized skip label).</summary>
    public string Label { get; }

    public static CsvColumnOption Skip(ILocalizationService localization) =>
        new(isSkip: true, index: -1, localization.Get("CsvImport.Skip"));

    public static CsvColumnOption ForColumn(int index, string caption) =>
        new(isSkip: false, index, string.IsNullOrWhiteSpace(caption) ? $"#{index + 1}" : caption);
}

/// <summary>
/// The confirmed CSV mapping: our field → its 0-based source column index (only mapped fields appear),
/// plus whether to clear the participant database first.
/// </summary>
public sealed class CsvMappingResult
{
    public CsvMappingResult(IReadOnlyDictionary<CsvParticipantField, int> map, bool clearFirst)
    {
        Map = map;
        ClearFirst = clearFirst;
    }

    public IReadOnlyDictionary<CsvParticipantField, int> Map { get; }

    public bool ClearFirst { get; }
}
