using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A <see cref="LazyEditCell"/> whose editor is a <see cref="SearchableComboBox"/>: the resting cell
/// shows the selected option's label and only builds the combo when the cell is entered, dropping its
/// list open on a click / Down / Enter. See <see cref="LazyEditCell"/> for the shared lifecycle.
/// </summary>
internal sealed class LazyComboCell : LazyEditCell
{
    private readonly Func<SearchableComboBox> _comboFactory;

    /// <param name="comboFactory">Builds the real combo, already bound to ItemsSource/SelectedItem.</param>
    /// <param name="selectedLabelPath">Path to the selected option's display text shown on the label.</param>
    public LazyComboCell(Func<SearchableComboBox> comboFactory, string? selectedLabelPath)
        : base(selectedLabelPath)
    {
        _comboFactory = comboFactory;
    }

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

    protected override bool IsEditorBusy(Control editor)
        => editor is ComboBox { IsDropDownOpen: true };

    protected override void DetachEditor(Control editor)
    {
        if (editor is ComboBox combo)
            combo.DropDownClosed -= OnDropDownClosed;
    }

    private void OnDropDownClosed(object? sender, EventArgs e) => OnEditorIdle();
}
