using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Dialogs;

/// <summary>
/// A read-only "what is this screen" help modal shown from the «?» button in every page header.
/// It resolves its text from a per-screen key prefix: <c>{prefix}.Title</c>, plus three sections
/// — «Що це» (<c>.What</c>), «Для чого» (<c>.Why</c>) and «Як користуватися» (<c>.How</c>). The How
/// section is a single string whose lines (split on newlines) render as bullet points.
///
/// Opened via <see cref="Services.IDialogService.ShowScreenHelpAsync"/>; closed with a single
/// button or Esc. All text resolves through the localization indexer in XAML so it re-localizes on a
/// language switch.
/// </summary>
public sealed partial class ScreenHelpViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="keyPrefix">
    /// Screen-specific localization key prefix, e.g. <c>Help.ControlPoints</c>. The dialog reads
    /// <c>{keyPrefix}.Title</c>, <c>.What</c>, <c>.Why</c> and <c>.How</c>.
    /// </param>
    public ScreenHelpViewModel(ILocalizationService localization, string keyPrefix)
    {
        Localization = localization;
        KeyPrefix = keyPrefix;

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(What));
            OnPropertyChanged(nameof(Why));
            OnPropertyChanged(nameof(HowLines));
        };
    }

    public ILocalizationService Localization { get; }

    /// <summary>Screen-specific key prefix; used to resolve the section keys below.</summary>
    public string KeyPrefix { get; }

    public string Title => Localization.Get($"{KeyPrefix}.Title");
    public string What => Localization.Get($"{KeyPrefix}.What");
    public string Why => Localization.Get($"{KeyPrefix}.Why");

    /// <summary>
    /// The «Як користуватися» steps, one per line. The resource stores them as a single string with
    /// <c>\n</c> separators; each line renders as a bullet in the dialog.
    /// </summary>
    public IReadOnlyList<string> HowLines =>
        Localization.Get($"{KeyPrefix}.How")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Completes when the user closes the dialog (the result is unused).</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);
}
