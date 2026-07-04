using System;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// A <see cref="LazyEditCell"/> whose editor is a <see cref="SearchableComboBox"/>: the resting cell
/// shows the selected option's label and only builds the combo when the cell is entered, dropping its
/// list open on the first click / Down / Enter. Once the combo is live, further clicks toggle the
/// dropdown via the combo's own handling — we don't re-assert "open" (see <see cref="ReassertsOpenOnClick"/>),
/// so a click reliably opens or closes it instead of double-toggling. See <see cref="LazyEditCell"/>
/// for the shared lifecycle.
/// </summary>
internal sealed class LazyComboCell : LazyEditCell
{
    private readonly Func<SearchableComboBox> _comboFactory;

    /// <param name="comboFactory">Builds the real combo, already bound to ItemsSource/SelectedItem.</param>
    /// <param name="selectedLabelPath">Path to the selected option's display text shown on the label.</param>
    /// <param name="restingDangerPath">Optional bool path: when true the resting label is tinted red
    /// (DangerBrush) — used to red-flag a non-OK finish status.</param>
    public LazyComboCell(Func<SearchableComboBox> comboFactory, string? selectedLabelPath, string? restingDangerPath = null)
        : base(selectedLabelPath)
    {
        _comboFactory = comboFactory;
        if (restingDangerPath is not null)
            Label[!TextBlock.ForegroundProperty] = new Binding(restingDangerPath) { Converter = DangerBrush };
    }

    private static readonly IValueConverter DangerBrush = new Converters.BoolToDangerBrushConverter();

    protected override Control CreateEditor()
    {
        var combo = _comboFactory();
        combo.DropDownClosed += OnDropDownClosed;
        return combo;
    }

    protected override void OpenEditor(Control editor)
    {
        if (editor is ComboBox combo)
            combo.IsDropDownOpen = true;
    }

    // A live combo toggles its own dropdown when its header is clicked; let it. Re-asserting "open" on
    // such a click would fight that toggle and the dropdown would never open (or close) by clicking.
    protected override bool ReassertsOpenOnClick => false;

    protected override bool IsEditorBusy(Control editor)
        => editor is ComboBox { IsDropDownOpen: true };

    protected override void DetachEditor(Control editor)
    {
        if (editor is ComboBox combo)
            combo.DropDownClosed -= OnDropDownClosed;
    }

    private void OnDropDownClosed(object? sender, EventArgs e) => OnEditorIdle();
}
