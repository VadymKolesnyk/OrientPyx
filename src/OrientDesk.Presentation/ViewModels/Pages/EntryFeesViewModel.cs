using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// «Стартові внески» — the money side of a competition (competition-level, so there is no day picker).
/// It holds four sections: a per-group entry fee shared across days, a standalone raised (late) fee, a
/// chip-rental base price plus note-keyed price overrides, and a list of percentage discounts. Cells
/// auto-save in the background (debounced) like the other grid pages. NOTE: this is data entry only —
/// no fee calculation/application is wired yet (intentionally deferred).
/// </summary>
public sealed partial class EntryFeesViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;

    private readonly Dictionary<Guid, CancellationTokenSource> _groupFeeTimers = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _chipPriceTimers = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _discountTimers = new();
    private CancellationTokenSource? _settingsTimer;

    // The current competition's metadata, kept so a settings save preserves the unrelated fields.
    private CompetitionInfo? _info;

    // Suppresses settings saves while LoadAsync seeds the scalar fields.
    private bool _loadingSettings;

    public EntryFeesViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;

        // Singleton VM: reload when the competition changes (the event can be raised on a pool thread).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.EntryFees";
    public override string TitleKey => "Page.EntryFees.Title";
    public override string TextKey => "Page.EntryFees.Text";

    public ObservableCollection<GroupFeeRowViewModel> GroupFees { get; } = [];
    public ObservableCollection<ChipPriceOverrideRowViewModel> ChipPriceOverrides { get; } = [];
    public ObservableCollection<EntryFeeDiscountRowViewModel> Discounts { get; } = [];

    /// <summary>Rows selected in each grid; the Delete key acts on them.</summary>
    [ObservableProperty]
    private ChipPriceOverrideRowViewModel? _selectedChipPrice;

    [ObservableProperty]
    private EntryFeeDiscountRowViewModel? _selectedDiscount;

    // --- Standalone settings (saved into CompetitionInfo) ------------------------------------------

    /// <summary>Whether a raised (late) start-entry fee applies after the deadline.</summary>
    [ObservableProperty]
    private bool _raisedFeeEnabled;

    /// <summary>The raised fee amount, as editable text (blank = unset).</summary>
    [ObservableProperty]
    private string _raisedFeeAmountText = string.Empty;

    /// <summary>Date after which the raised fee applies.</summary>
    [ObservableProperty]
    private DateTimeOffset? _raisedFeeDeadline;

    /// <summary>Base rental-chip price per day, as editable text (blank = unset).</summary>
    [ObservableProperty]
    private string _chipBasePriceText = string.Empty;

    /// <summary>Reloads everything for the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var hasEvent = _session.CurrentEvent is not null;
        var groups = hasEvent
            ? await _busy.RunAsync(() => _editor.GetGroupsAsync())
            : (IReadOnlyList<Group>)[];
        var chipPrices = hasEvent
            ? await _busy.RunAsync(() => _editor.GetChipPriceOverridesAsync())
            : (IReadOnlyList<ChipPriceOverride>)[];
        var discounts = hasEvent
            ? await _busy.RunAsync(() => _editor.GetEntryFeeDiscountsAsync())
            : (IReadOnlyList<EntryFeeDiscount>)[];
        _info = hasEvent ? await _busy.RunAsync(() => _editor.GetInfoAsync()) : null;

        // Seed the standalone settings without triggering a save.
        _loadingSettings = true;
        try
        {
            RaisedFeeEnabled = _info?.RaisedFeeEnabled ?? false;
            RaisedFeeAmountText = FormatDecimal(_info?.RaisedFeeAmount);
            RaisedFeeDeadline = _info?.RaisedFeeDeadline;
            ChipBasePriceText = FormatDecimal(_info?.ChipRentalPricePerDay);
        }
        finally
        {
            _loadingSettings = false;
        }

        foreach (var existing in GroupFees)
            existing.PropertyChanged -= OnGroupFeeRowChanged;
        GroupFees.Clear();
        foreach (var group in groups)
            GroupFees.Add(new GroupFeeRowViewModel(group, Localization, RequestGroupFeeSave));

        ChipPriceOverrides.Clear();
        foreach (var price in chipPrices)
            ChipPriceOverrides.Add(new ChipPriceOverrideRowViewModel(price, Localization, RequestChipPriceSave));

        Discounts.Clear();
        foreach (var discount in discounts)
            Discounts.Add(new EntryFeeDiscountRowViewModel(discount, Localization, RequestDiscountSave));
    }

    // The group-fee table only edits an existing group's fee — rows aren't added/deleted here
    // (groups are managed on the Groups page). PropertyChanged is unused but kept for symmetry.
    private void OnGroupFeeRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
    }

    // --- Add commands ------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddChipPriceAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var priceOverride = await _busy.RunAsync(() => _editor.AddChipPriceOverrideRowAsync());
        ChipPriceOverrides.Add(new ChipPriceOverrideRowViewModel(priceOverride, Localization, RequestChipPriceSave));
    }

    [RelayCommand]
    private async Task AddDiscountAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var discount = await _busy.RunAsync(() => _editor.AddEntryFeeDiscountRowAsync());
        Discounts.Add(new EntryFeeDiscountRowViewModel(discount, Localization, RequestDiscountSave));
    }

    // --- Chip-price delete -------------------------------------------------------------------------

    [RelayCommand]
    private Task DeleteChipPriceAsync(ChipPriceOverrideRowViewModel? row) => RemoveChipPriceAsync(row, skipConfirm: false);

    public Task DeleteChipPriceNoConfirmAsync(ChipPriceOverrideRowViewModel? row) => RemoveChipPriceAsync(row, skipConfirm: true);

    public Task DeleteSelectedChipPriceAsync(bool skipConfirm) => RemoveChipPriceAsync(SelectedChipPrice, skipConfirm);

    private async Task RemoveChipPriceAsync(ChipPriceOverrideRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "EntryFees.ChipPrice.Delete.ConfirmTitle",
                messageKey: "EntryFees.ChipPrice.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_chipPriceTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _chipPriceTimers.Remove(row.Id);
        }

        if (ReferenceEquals(SelectedChipPrice, row))
            SelectedChipPrice = GridSelection.NeighbourAfterRemoval(ChipPriceOverrides, row);
        ChipPriceOverrides.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteChipPriceOverrideAsync(id));

        if (confirmed)
            RequestGridFocus();
    }

    // --- Discount delete ---------------------------------------------------------------------------

    [RelayCommand]
    private Task DeleteDiscountAsync(EntryFeeDiscountRowViewModel? row) => RemoveDiscountAsync(row, skipConfirm: false);

    public Task DeleteDiscountNoConfirmAsync(EntryFeeDiscountRowViewModel? row) => RemoveDiscountAsync(row, skipConfirm: true);

    public Task DeleteSelectedDiscountAsync(bool skipConfirm) => RemoveDiscountAsync(SelectedDiscount, skipConfirm);

    private async Task RemoveDiscountAsync(EntryFeeDiscountRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "EntryFees.Discount.Delete.ConfirmTitle",
                messageKey: "EntryFees.Discount.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_discountTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _discountTimers.Remove(row.Id);
        }

        if (ReferenceEquals(SelectedDiscount, row))
            SelectedDiscount = GridSelection.NeighbourAfterRemoval(Discounts, row);
        Discounts.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteEntryFeeDiscountAsync(id));

        if (confirmed)
            RequestGridFocus();
    }

    // --- Standalone settings save ------------------------------------------------------------------

    partial void OnRaisedFeeEnabledChanged(bool value) => QueueSettingsSave();
    partial void OnRaisedFeeAmountTextChanged(string value) => QueueSettingsSave();
    partial void OnRaisedFeeDeadlineChanged(DateTimeOffset? value) => QueueSettingsSave();
    partial void OnChipBasePriceTextChanged(string value) => QueueSettingsSave();

    private void QueueSettingsSave()
    {
        if (_loadingSettings || _session.CurrentEvent is null)
            return;

        _settingsTimer?.Cancel();
        var cts = new CancellationTokenSource();
        _settingsTimer = cts;
        _ = SaveSettingsDebouncedAsync(cts.Token);
    }

    private async Task SaveSettingsDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);

            // Reuse the loaded metadata so unrelated fields are preserved; fall back to a fresh row.
            var info = _info ?? new CompetitionInfo();
            info.RaisedFeeEnabled = RaisedFeeEnabled;
            info.RaisedFeeAmount = ParseDecimalOrNull(RaisedFeeAmountText);
            info.RaisedFeeDeadline = RaisedFeeDeadline;
            info.ChipRentalPricePerDay = ParseDecimalOrNull(ChipBasePriceText);

            await Task.Run(() => _editor.SaveInfoAsync(info, token), token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit (or the page reloaded) — ignore.
        }
        catch
        {
            // Background save failed; never crash the UI over an autosave.
        }
    }

    // --- Per-row debounced saves -------------------------------------------------------------------

    private void RequestGroupFeeSave(GroupFeeRowViewModel row)
        => Debounce(_groupFeeTimers, row.Id, token => SaveGroupFeeAsync(row, token));

    private void RequestChipPriceSave(ChipPriceOverrideRowViewModel row)
        => Debounce(_chipPriceTimers, row.Id, token => Task.Run(() => _editor.UpdateChipPriceOverrideAsync(row.ToEntity(), token), token));

    private void RequestDiscountSave(EntryFeeDiscountRowViewModel row)
        => Debounce(_discountTimers, row.Id, token => Task.Run(() => _editor.UpdateEntryFeeDiscountAsync(row.ToEntity(), token), token));

    private Task SaveGroupFeeAsync(GroupFeeRowViewModel row, CancellationToken token)
        => Task.Run(() => _editor.UpdateGroupEntryFeeAsync(row.Id, row.Fee, token), token);

    // Shared debounce: reset the row's timer and, after the delay, run the save (swallowing
    // cancellation/background errors), mirroring the other grid pages.
    private static void Debounce(Dictionary<Guid, CancellationTokenSource> timers, Guid id, Func<CancellationToken, Task> save)
    {
        if (timers.TryGetValue(id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        timers[id] = cts;
        _ = RunDebouncedAsync(save, cts.Token);
    }

    private static async Task RunDebouncedAsync(Func<CancellationToken, Task> save, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            await save(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit (or the page reloaded) — ignore.
        }
        catch
        {
            // Background save failed; never crash the UI over an autosave.
        }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _groupFeeTimers.Values) cts.Cancel();
        foreach (var cts in _chipPriceTimers.Values) cts.Cancel();
        foreach (var cts in _discountTimers.Values) cts.Cancel();
        _groupFeeTimers.Clear();
        _chipPriceTimers.Clear();
        _discountTimers.Clear();
        _settingsTimer?.Cancel();
        _settingsTimer = null;
    }

    private static string FormatDecimal(decimal? value)
        => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal? ParseDecimalOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
