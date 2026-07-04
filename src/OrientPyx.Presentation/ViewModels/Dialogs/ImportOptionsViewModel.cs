using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// A reusable confirmation modal for "Import from XML…" flows. It shows a title, a short message,
/// a configurable list of yes/no <see cref="ImportOption"/>s, and OK/Cancel buttons.
///
/// The set of options is passed in by the caller, so the same modal serves different imports:
/// the control-points import shows one toggle ("replace all"), while the future course/group
/// import can show two — without changing this class. Callers <c>await</c>
/// <see cref="Completion"/> to learn whether OK was pressed and how the toggles were set.
/// </summary>
public sealed partial class ImportOptionsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ImportOptionsResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ImportOptionsViewModel(
        ILocalizationService localization,
        string titleKey,
        string messageKey,
        IReadOnlyList<ImportOption> options)
    {
        Localization = localization;
        TitleKey = titleKey;
        MessageKey = messageKey;
        Options = new ObservableCollection<ImportOption>(options);

        // Title/message use the Localization indexer in XAML; options carry dynamic keys, so they
        // resolve their own captions and we re-raise them here on a language switch.
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            foreach (var option in Options)
                option.Refresh(Localization);
        };

        foreach (var option in Options)
            option.Refresh(Localization);
    }

    public ILocalizationService Localization { get; }

    public string TitleKey { get; }

    public string MessageKey { get; }

    public string Title => Localization.Get(TitleKey);

    public string Message => Localization.Get(MessageKey);

    /// <summary>The toggles shown in the modal, in order. Bound to a checkbox list.</summary>
    public ObservableCollection<ImportOption> Options { get; }

    /// <summary>Completes when the user confirms (result) or cancels/closes (null).</summary>
    public Task<ImportOptionsResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        var values = Options.ToDictionary(o => o.Key, o => o.IsChecked);
        _completion.TrySetResult(new ImportOptionsResult(values));
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One labelled toggle inside an <see cref="ImportOptionsViewModel"/>.</summary>
public sealed partial class ImportOption : ObservableObject
{
    /// <param name="key">Stable identifier used to read the value back from the result.</param>
    /// <param name="labelKey">Localization key for the checkbox caption.</param>
    /// <param name="isChecked">Initial state.</param>
    public ImportOption(string key, string labelKey, bool isChecked)
    {
        Key = key;
        LabelKey = labelKey;
        _isChecked = isChecked;
    }

    public string Key { get; }

    public string LabelKey { get; }

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Localized caption; refreshed by the owner on language change.</summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>Re-resolves <see cref="Label"/> from the current language.</summary>
    public void Refresh(ILocalizationService localization) => Label = localization.Get(LabelKey);
}

/// <summary>The confirmed toggle values, keyed by each option's <see cref="ImportOption.Key"/>.</summary>
public sealed class ImportOptionsResult
{
    private readonly IReadOnlyDictionary<string, bool> _values;

    public ImportOptionsResult(IReadOnlyDictionary<string, bool> values) => _values = values;

    /// <summary>Reads a toggle by key; returns <paramref name="fallback"/> when it is absent.</summary>
    public bool Get(string key, bool fallback = false) =>
        _values.TryGetValue(key, out var value) ? value : fallback;
}
