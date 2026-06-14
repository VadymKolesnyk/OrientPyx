using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using OrientDesk.Presentation.Controls;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class EntryFeesView : UserControl
{
    private EntryFeesViewModel? _vm;
    private bool _deleteCtrlDown;

    public EntryFeesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before a delete button consumes the press, so Ctrl+Click on
        // Delete can skip the confirmation prompt (see ChipsView for the same approach).
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    // --- Chip-price table delete -------------------------------------------------------------------

    private void OnChipPriceDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not ChipPriceOverrideRowViewModel row)
            return;
        DeleteChipPrice(row, e.SkipConfirm);
    }

    private void OnChipPriceDeleteButton(object row)
    {
        if (_vm is null || row is not ChipPriceOverrideRowViewModel price)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteChipPrice(price, skipConfirm);
    }

    private void DeleteChipPrice(ChipPriceOverrideRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteChipPriceNoConfirmAsync(row);
        else
            _ = _vm!.DeleteChipPriceCommand.ExecuteAsync(row);
    }

    // --- Discount table delete ---------------------------------------------------------------------

    private void OnDiscountDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not EntryFeeDiscountRowViewModel row)
            return;
        DeleteDiscount(row, e.SkipConfirm);
    }

    private void OnDiscountDeleteButton(object row)
    {
        if (_vm is null || row is not EntryFeeDiscountRowViewModel discount)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteDiscount(discount, skipConfirm);
    }

    private void DeleteDiscount(EntryFeeDiscountRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteDiscountNoConfirmAsync(row);
        else
            _ = _vm!.DeleteDiscountCommand.ExecuteAsync(row);
    }

    // --- Lifecycle ---------------------------------------------------------------------------------

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as EntryFeesViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // After a delete-confirmation modal closes, return focus to the chip-price grid (the only table
    // with a confirm-delete path that the keyboard reaches first); posted so it runs once the overlay
    // has been torn down.
    private void OnFocusGridRequested(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => ChipPricesSheet.Focus());

    // Builds each table's columns. Headers are baked into the band model, so a language switch is
    // handled by rebuilding. The group-fee table is name (read-only) + fee; rows aren't added/deleted
    // here (groups live on the Groups page), so it has no delete column.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        GroupFeesSheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("EntryFees.Col.Group", nameof(GroupFeeRowViewModel.Name), minWidth: 220)
            .Text("EntryFees.Col.Fee", nameof(GroupFeeRowViewModel.FeeText),
                  editPath: nameof(GroupFeeRowViewModel.FeeText), minWidth: 140,
                  mask: SheetColumnBuilder.NumericMask.Decimal, placeholder: "0")
            .Bands;

        ChipPricesSheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("EntryFees.Col.Note", nameof(ChipPriceOverrideRowViewModel.Note),
                  editPath: nameof(ChipPriceOverrideRowViewModel.Note), minWidth: 240)
            .Text("EntryFees.Col.PricePerDay", nameof(ChipPriceOverrideRowViewModel.PriceText),
                  editPath: nameof(ChipPriceOverrideRowViewModel.PriceText), minWidth: 160,
                  mask: SheetColumnBuilder.NumericMask.Decimal, placeholder: "0")
            .DeleteAction(OnChipPriceDeleteButton, "EntryFees.ChipPrice.Delete")
            .Bands;

        DiscountsSheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("EntryFees.Col.DiscountName", nameof(EntryFeeDiscountRowViewModel.Name),
                  editPath: nameof(EntryFeeDiscountRowViewModel.Name), minWidth: 220)
            .Text("EntryFees.Col.Percent", nameof(EntryFeeDiscountRowViewModel.PercentText),
                  editPath: nameof(EntryFeeDiscountRowViewModel.PercentText), minWidth: 130,
                  mask: SheetColumnBuilder.NumericMask.Decimal, placeholder: "0")
            .Custom("EntryFees.Col.AppliesToChips", BuildAppliesToChipsCell, width: 170, minWidth: 130,
                    sortPath: nameof(EntryFeeDiscountRowViewModel.AppliesToChipRental))
            .DeleteAction(OnDiscountDeleteButton, "EntryFees.Discount.Delete")
            .Bands;
    }

    // A centred checkbox bound to the discount row's "applies to chip rental" flag (TwoWay). The cell
    // inherits the row's DataContext, so the plain property-name binding resolves against the row VM.
    private static Control BuildAppliesToChipsCell() => new CheckBox
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        [!ToggleButton.IsCheckedProperty] =
            new Binding(nameof(EntryFeeDiscountRowViewModel.AppliesToChipRental)) { Mode = BindingMode.TwoWay }
    };

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
