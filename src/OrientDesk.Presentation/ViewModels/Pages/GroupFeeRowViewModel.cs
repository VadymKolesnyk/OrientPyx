using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One row in the per-group entry-fee table: a group's name (read-only here — groups are created on
/// the Groups page) and its editable base entry fee, shared across every day. The fee edits in the
/// background (debounced per row) via the page-supplied <c>requestSave</c> callback, mirroring
/// <see cref="RegionRowViewModel"/>. The fee is held as text so a blank cell means "unset".
/// </summary>
public sealed partial class GroupFeeRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly Action<GroupFeeRowViewModel> _requestSave;
    private readonly bool _initialized;

    /// <summary>Group display name (read-only on this page).</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>Entry fee as editable text (blank = unset). Parsed back to <see cref="decimal"/> on save.</summary>
    [ObservableProperty]
    private string _feeText;

    public GroupFeeRowViewModel(
        Group group,
        ILocalizationService localization,
        Action<GroupFeeRowViewModel> requestSave)
    {
        _id = group.Id;
        _requestSave = requestSave;
        Localization = localization;

        _name = group.Name;
        _feeText = FormatDecimal(group.EntryFee);

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    /// <summary>The parsed fee value for persistence (null when blank/invalid).</summary>
    public decimal? Fee => ParseDecimal(FeeText);

    partial void OnFeeTextChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    private static string FormatDecimal(decimal? value)
        => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
