using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for mapping a CSV file's columns onto our participant fields before importing. One row per
/// import target (<see cref="ImportTarget"/>: the built-in fields, then the competition's additional fields); each
/// row carries a dropdown of the file's columns plus a leading "— do not import —" sentinel. The mapping the user
/// last confirmed for the same set of headers is restored (<paramref name="template"/>); otherwise a best-effort
/// auto-guess pre-selects a column whose header looks like the field (or names an additional field). The trailing
/// «+ Нове додаткове поле…» row turns a file column into a new additional field named after it, mapped at once.
/// A single "clear existing participants first" toggle mirrors the XML import. Callers <c>await</c>
/// <see cref="Completion"/> for the chosen mapping, or null on cancel.
/// </summary>
public sealed partial class CsvMappingViewModel : ObservableObject
{
    private readonly TaskCompletionSource<CsvMappingResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="header">The CSV column captions, in file order.</param>
    /// <param name="rowCount">How many data rows the file holds (shown in the modal's message).</param>
    /// <param name="scope">Optional day-scope + link-field chooser (null hides that section).</param>
    /// <param name="fields">The competition's additional participant fields (one mapping row each).</param>
    /// <param name="template">The last confirmed mapping for this header set (target key → header caption); null
    /// when there is none, in which case the columns are guessed from their headers.</param>
    /// <param name="includeRelay">The day runs a relay: list the relay targets (team number / name / leg).</param>
    public CsvMappingViewModel(
        ILocalizationService localization,
        IReadOnlyList<string> header,
        int rowCount,
        ImportScopeChoice? scope = null,
        IReadOnlyList<CustomField>? fields = null,
        IReadOnlyDictionary<string, string>? template = null,
        bool includeRelay = false)
    {
        Localization = localization;
        RowCount = rowCount;
        Scope = scope;

        // The shared column choices: a "(skip)" sentinel at index 0, then one per file column.
        var columns = new List<CsvColumnOption> { CsvColumnOption.Skip(Localization) };
        for (var i = 0; i < header.Count; i++)
            columns.Add(CsvColumnOption.ForColumn(i, header[i]));
        Columns = columns;

        // One mapping row per target, each pre-selecting the remembered column, else its best header guess.
        var rows = ImportTarget.Builtins
            .Concat(includeRelay ? ImportTarget.Relay : [])
            .Select(t => new CsvFieldMapping(Localization, t, columns,
                Pick(t, template, header, columns, () => Guess(Hints(t), header, columns))))
            .ToList();
        foreach (var field in fields ?? [])
        {
            var target = ImportTarget.Field(field.Key);
            rows.Add(new CsvFieldMapping(Localization, target, columns,
                Pick(target, template, header, columns, () => GuessByName(field.Label, header, columns)),
                field.Label));
        }
        Fields = new ObservableCollection<CsvFieldMapping>(rows);
        NewFieldColumns = columns;
        _newFieldColumn = columns[0];

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(ClearFirstLabel));
            OnPropertyChanged(nameof(NewFieldLabel));
            foreach (var f in Fields)
                f.Refresh();
            Scope?.Refresh(Localization);
        };

        Scope?.Refresh(Localization);
    }

    public ILocalizationService Localization { get; }

    /// <summary>Optional day-scope + link-field chooser (participant imports); null hides the section.</summary>
    public ImportScopeChoice? Scope { get; }

    /// <summary>Whether to render the scope section (bound in XAML).</summary>
    public bool HasScope => Scope is not null;

    public string Title => Localization.Get("CsvImport.Title");

    public string Message => string.Format(Localization.Get("CsvImport.Message"), RowCount);

    public string ClearFirstLabel => Localization.Get("ParticipantsImport.ClearFirst");

    public int RowCount { get; }

    /// <summary>The column choices shared by every field's dropdown ("(skip)" first, then file columns).</summary>
    public IReadOnlyList<CsvColumnOption> Columns { get; }

    /// <summary>One mapping row per importable field, in display order.</summary>
    public ObservableCollection<CsvFieldMapping> Fields { get; }

    /// <summary>The choices of the «+ Нове додаткове поле…» row (same list as the field rows).</summary>
    public IReadOnlyList<CsvColumnOption> NewFieldColumns { get; }

    public string NewFieldLabel => Localization.Get("CsvImport.NewField");

    /// <summary>The column picked in the «+ Нове додаткове поле…» row: picking one adds a row for a new additional
    /// field named after the column, mapped to it, then the picker resets.</summary>
    [ObservableProperty]
    private CsvColumnOption _newFieldColumn;

    partial void OnNewFieldColumnChanged(CsvColumnOption value)
    {
        if (value is null || value.IsSkip)
            return;
        var label = value.Label.Trim();
        if (label.Length > 0 && !Fields.Any(f => f.IsNewField && string.Equals(f.Label, label, StringComparison.CurrentCultureIgnoreCase)))
            Fields.Add(CsvFieldMapping.NewField(Localization, label, Columns, value));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => NewFieldColumn = Columns[0]);
    }

    /// <summary>Whether to wipe the participant database before importing (default off).</summary>
    [ObservableProperty]
    private bool _clearFirst;

    /// <summary>Completes with the chosen mapping on confirm, or null on cancel/close.</summary>
    public Task<CsvMappingResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        // Collect target → column-index for every mapped row (skip the "(skip)" rows); new fields go separately —
        // the import flow creates them first and maps them under their generated key.
        var map = new Dictionary<ImportTarget, int>();
        var newFields = new List<CsvNewField>();
        foreach (var f in Fields)
        {
            if (f.SelectedColumn is not { IsSkip: false } col)
                continue;
            if (f.IsNewField)
                newFields.Add(new CsvNewField(f.Label, col.Index));
            else
                map[f.Target] = col.Index;
        }

        _completion.TrySetResult(new CsvMappingResult(map, ClearFirst, Scope?.ToScope()) { NewFields = newFields });
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);

    // The remembered column when there is a template (a target the template leaves out stays unmapped — that was
    // the user's choice), else the guess.
    private static CsvColumnOption Pick(
        ImportTarget target,
        IReadOnlyDictionary<string, string>? template,
        IReadOnlyList<string> header,
        IReadOnlyList<CsvColumnOption> columns,
        Func<CsvColumnOption> guess)
    {
        if (template is null || template.Count == 0)
            return guess();
        if (!template.TryGetValue(target.Key, out var caption))
            return columns[0];
        for (var i = 0; i < header.Count; i++)
            if (TextKey.Same(header[i], caption))
                return columns[i + 1]; // +1: index 0 is the "(skip)" sentinel
        return columns[0];
    }

    // Best-effort header match: pick the first column whose caption contains one of the keyword hints
    // (case/space-insensitive). Returns "(skip)" when nothing looks right.
    private static CsvColumnOption Guess(
        string[] hints,
        IReadOnlyList<string> header,
        IReadOnlyList<CsvColumnOption> columns)
    {
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

    // An additional field is guessed by its name: the column headed exactly like it.
    private static CsvColumnOption GuessByName(
        string label,
        IReadOnlyList<string> header,
        IReadOnlyList<CsvColumnOption> columns)
    {
        for (var i = 0; i < header.Count; i++)
            if (TextKey.Same(header[i], label))
                return columns[i + 1];
        return columns[0];
    }

    private static string Normalize(string s) =>
        new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    // Lowercase, whitespace-free substrings (Ukrainian + English) hinting at each field.
    private static string[] Hints(ImportTarget target) => target.Key switch
    {
        nameof(ImportTarget.FullName) => ["прізвище", "ім'я", "піб", "fio", "name", "учасник"],
        nameof(ImportTarget.Number) => ["номер", "№", "нагрудний", "стартовийномер", "bib", "number", "startno", "start#"],
        nameof(ImportTarget.Team) => ["команд", "team", "екіпаж"],
        nameof(ImportTarget.BirthDate) => ["народж", "рікнар", "датанар", "birth", "born", "ддр", "дн"],
        nameof(ImportTarget.Group) => ["група", "group", "категор"],
        nameof(ImportTarget.Region) => ["регіон", "область", "region"],
        nameof(ImportTarget.Club) => ["клуб", "club", "колектив", "команд"],
        nameof(ImportTarget.Dussh) => ["дюсш", "сдюшор", "школа", "dussh"],
        nameof(ImportTarget.Chip) => ["чіп", "чип", "card", "chip", "si"],
        nameof(ImportTarget.Rank) => ["розряд", "кваліфік", "rank", "qualif"],
        nameof(ImportTarget.Representative) => ["представник", "predst"],
        nameof(ImportTarget.FsouCode) => ["кодфсоу", "foucode", "fsoucode", "кодфоу"],
        nameof(ImportTarget.IsFsouMember) => ["членфсоу", "членфоу", "fsou"],
        nameof(ImportTarget.Payment) => ["оплата", "сплата", "внесок", "payment", "pay"],
        nameof(ImportTarget.Coach) => ["тренер", "trener", "coach"],
        "relay.teamNumber" => ["номеркоманди", "№команди", "teamno", "teamnumber", "teamnr"],
        "relay.teamName" => ["назвакоманди", "teamname"],
        "relay.leg" => ["етап", "leg"],
        _ => []
    };
}

/// <summary>One row in the mapping modal: a target caption + a chosen source column.</summary>
public sealed partial class CsvFieldMapping : ObservableObject
{
    private readonly ILocalizationService _localization;
    // The caption of an additional field (its own name, not a localization key); null for a built-in target.
    private readonly string? _fixedLabel;

    public CsvFieldMapping(
        ILocalizationService localization,
        ImportTarget target,
        IReadOnlyList<CsvColumnOption> columns,
        CsvColumnOption selected,
        string? fixedLabel = null)
    {
        _localization = localization;
        Target = target;
        Columns = columns;
        _selectedColumn = selected;
        _fixedLabel = fixedLabel;
        _label = fixedLabel ?? localization.Get(LabelKey(target));
    }

    /// <summary>A row for a new additional field to create from a file column (key assigned on import).</summary>
    public static CsvFieldMapping NewField(
        ILocalizationService localization, string label, IReadOnlyList<CsvColumnOption> columns, CsvColumnOption selected) =>
        new(localization, ImportTarget.Field(string.Empty), columns, selected, label) { IsNewField = true };

    public ImportTarget Target { get; }

    /// <summary>True for a field the import will create (from «+ Нове додаткове поле…»).</summary>
    public bool IsNewField { get; private init; }

    /// <summary>The shared column choices (same instance list for every row).</summary>
    public IReadOnlyList<CsvColumnOption> Columns { get; }

    /// <summary>Localized field caption shown at the left of the row.</summary>
    [ObservableProperty]
    private string _label;

    /// <summary>The source column chosen for this field ("(skip)" = not imported).</summary>
    [ObservableProperty]
    private CsvColumnOption _selectedColumn;

    /// <summary>Re-resolves the caption on a language switch.</summary>
    public void Refresh() => Label = _fixedLabel ?? _localization.Get(LabelKey(Target));

    private static string LabelKey(ImportTarget target) => "CsvImport.Field." + target.Key;
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

/// <summary>A new additional field the user asked to create from a file column (named after it).</summary>
public sealed record CsvNewField(string Label, int ColumnIndex);

/// <summary>
/// The confirmed CSV mapping: our target → its 0-based source column index (only mapped targets appear),
/// the new fields to create first, plus whether to clear the participant database first.
/// </summary>
public sealed class CsvMappingResult
{
    public CsvMappingResult(
        IReadOnlyDictionary<ImportTarget, int> map,
        bool clearFirst,
        ParticipantImportScope? scope = null)
    {
        Map = map;
        ClearFirst = clearFirst;
        Scope = scope;
    }

    public IReadOnlyDictionary<ImportTarget, int> Map { get; }

    /// <summary>Additional fields to create (Text) and map to their column.</summary>
    public IReadOnlyList<CsvNewField> NewFields { get; init; } = [];

    public bool ClearFirst { get; }

    /// <summary>The chosen import scope, when the modal showed a scope chooser; otherwise null.</summary>
    public ParticipantImportScope? Scope { get; }
}
