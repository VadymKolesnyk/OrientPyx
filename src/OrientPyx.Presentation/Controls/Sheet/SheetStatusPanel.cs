using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The per-column sums row of a <see cref="SheetTable"/>'s status bar: a one-tier grid that mirrors the
/// header/body leaf-column layout (each grid column's width two-way bound to the same
/// <see cref="SheetColumn.Width"/>) so a column's total renders right under the column it sums. Only
/// columns with a <see cref="SheetColumn.HasSummary"/> show a value; the rest are blank spacers. Lives
/// in its own horizontally-scrolled viewport, slaved to the body's offset like the header.
/// </summary>
internal sealed class SheetStatusPanel : Grid
{
    public SheetStatusPanel()
    {
        RowDefinitions = new RowDefinitions("Auto");
        HorizontalAlignment = HorizontalAlignment.Left;
        ClipToBounds = true;
    }

    /// <summary>Rebuilds the spacer grid for the given visible bands (column widths only).</summary>
    public void Rebuild(IReadOnlyList<SheetBand> bands)
    {
        Children.Clear();
        ColumnDefinitions.Clear();

        var leaves = new List<SheetColumn>();
        foreach (var band in bands)
            leaves.AddRange(band.Columns);

        for (var i = 0; i < leaves.Count; i++)
        {
            var def = new ColumnDefinition { MinWidth = leaves[i].MinWidth };
            def[!ColumnDefinition.WidthProperty] = new Binding(nameof(SheetColumn.Width))
            {
                Source = leaves[i],
                Mode = BindingMode.TwoWay,
                Converter = PixelToGridLength.Instance
            };
            ColumnDefinitions.Add(def);

            var leaf = leaves[i];

            // The count cell (under «Номер»): left-aligned, compact text with the full text on hover.
            if (leaf.ShowCount)
            {
                var count = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(10, 0, 8, 0),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 14,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (IBrush?)this.FindResource("TextPrimary")
                };
                _countCell = count;
                SetColumn(count, i);
                Children.Add(count);
                continue;
            }

            if (!leaf.HasSummary)
                continue;

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 10, 0),
                FontWeight = FontWeight.SemiBold,
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (IBrush?)this.FindResource("TextPrimary")
            };
            // Tag the cell with its column key so the table can write the computed sum into it.
            _cells[leaf.Key] = label;
            SetColumn(label, i);
            Children.Add(label);
        }
    }

    private readonly Dictionary<string, TextBlock> _cells = new();
    private TextBlock? _countCell;

    /// <summary>True when a <see cref="SheetColumn.ShowCount"/> column gave the panel a count cell.</summary>
    public bool HasCountCell => _countCell is not null;

    /// <summary>
    /// Writes the formatted sum text into the cell under the given column key (no-op if absent), and an
    /// optional hover tooltip (e.g. the payment column's paid/owed breakdown); pass null to clear it.
    /// </summary>
    public void SetSum(string columnKey, string text, string? tooltip = null)
    {
        if (!_cells.TryGetValue(columnKey, out var label))
            return;
        label.Text = text;
        ToolTip.SetTip(label, tooltip);
    }

    /// <summary>Sets the compact count text (and its full-text hover tooltip) under the count column.</summary>
    public void SetCount(string text, string tooltip)
    {
        if (_countCell is null)
            return;
        _countCell.Text = text;
        ToolTip.SetTip(_countCell, tooltip);
    }
}
