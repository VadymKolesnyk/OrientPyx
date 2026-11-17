using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «OrientPyx оновлено»: shown once, on the launch that follows an update (Velopack's restart hook, see
/// <c>Program.UpdatedToVersion</c>). Says the update worked and the data is kept, and links to this version's section
/// of «Що нового» on the documentation site. Closed with the one button or Esc.
/// </summary>
public sealed partial class AppUpdatedViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AppUpdatedViewModel(ILocalizationService localization, string version)
    {
        Localization = localization;
        Version = version;
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Message));
    }

    public ILocalizationService Localization { get; }

    /// <summary>The version the app was updated to, e.g. <c>0.8.7</c>.</summary>
    public string Version { get; }

    /// <summary>«Програму успішно оновлено до версії 0.8.7.»</summary>
    public string Message => string.Format(Localization.Get("AppUpdated.Message"), Version);

    /// <summary>This version's changes in «Що нового» — the «Що нового в цій версії» button.</summary>
    public Uri ChangelogUri => Services.DocsLinks.Changelog(Version);

    /// <summary>Completes when the user closes the dialog (the result is unused).</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);
}
