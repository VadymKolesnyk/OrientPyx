using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The participant tables' columns for the competition's additional fields (Р-13): one single-column identity band
/// per field, keyed <c>field.&lt;key&gt;</c> so a hidden/reordered column survives rebuilds and languages. Cells bind
/// the row's <c>Fields[key]</c> cell view model: text/number → an editable text cell, flag → a check box, choice →
/// a dropdown; a value outside the choice list / a non-number reads red (Р-18). Shared by the day grid and the
/// roster (both rows expose <c>Fields</c>).
/// </summary>
internal static class CustomFieldColumns
{
    public static void Append(List<SheetBand> bands, IReadOnlyList<CustomField>? fields, ILocalizationService loc)
    {
        foreach (var field in fields ?? [])
        {
            var cell = $"{nameof(ParticipantDayRowViewModel.Fields)}[{field.Key}]";
            var text = $"{cell}.{nameof(ParticipantFieldCellViewModel.Text)}";
            var isFlag = field.Kind == CustomFieldKind.Bool;
            var column = new SheetColumn(SheetCellKind.Custom)
            {
                Header = field.Label,
                PickerLabel = field.Label,
                Key = CustomField.ColumnPrefix + field.Key,
                SortPath = isFlag ? $"{cell}.{nameof(ParticipantFieldCellViewModel.Checked)}" : text,
                // Fill-down paste writes the text (normalised on save) or, for a flag, the bool; the choice cell
                // takes single pastes.
                PastePath = field.Kind is CustomFieldKind.Text or CustomFieldKind.Number ? text
                    : isFlag ? $"{cell}.{nameof(ParticipantFieldCellViewModel.Checked)}" : null,
                CheckPaste = isFlag,
                CellBuilder = () => BuildCell(field, cell, loc),
            };
            if (isFlag)
            {
                column.Width = 110;
                column.WidthCapped = true;
            }
            bands.Add(new SheetBand(SheetBand.BandKind.Identity, [column]) { Header = column.Header });
        }
    }

    private static Control BuildCell(CustomField field, string cell, ILocalizationService loc)
    {
        var invalid = $"{cell}.{nameof(ParticipantFieldCellViewModel.IsInvalid)}";
        switch (field.Kind)
        {
            case CustomFieldKind.Bool:
            {
                var box = new CheckBox
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    [!ToggleButton.IsCheckedProperty] = new Binding($"{cell}.{nameof(ParticipantFieldCellViewModel.Checked)}")
                    {
                        Mode = BindingMode.TwoWay
                    }
                };
                SheetLock.DisableWhenLocked(box);
                return box;
            }
            case CustomFieldKind.Choice:
            {
                var selected = $"{cell}.{nameof(ParticipantFieldCellViewModel.Selected)}";
                return new LazyComboCell(
                    () =>
                    {
                        var combo = new SearchableComboBox
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Stretch,
                            SearchWatermark = loc.Get("Common.Search"),
                            ItemTemplate = new FuncDataTemplate<FieldChoiceOption>((_, _) =>
                                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(FieldChoiceOption.Label)) })
                        };
                        combo[!ItemsControl.ItemsSourceProperty] =
                            new Binding($"{cell}.{nameof(ParticipantFieldCellViewModel.Options)}");
                        combo[!SelectingItemsControl.SelectedItemProperty] = new Binding(selected) { Mode = BindingMode.TwoWay };
                        return combo;
                    },
                    $"{selected}.{nameof(FieldChoiceOption.Label)}",
                    restingDangerPath: invalid,
                    selectedPath: null);
            }
            default:
            {
                var text = $"{cell}.{nameof(ParticipantFieldCellViewModel.Text)}";
                return new LazyTextCell(text, text, new SheetTextOptions
                {
                    // Commit on leaving the cell: the stored value is normalised (e.g. "1,5" → "1.5"), which
                    // must not rewrite the text mid-typing.
                    CommitOnLostFocus = true,
                    // No numeric mask on a number field: a non-number is kept and shown red (Р-19).
                    DangerPath = invalid,
                });
            }
        }
    }
}
