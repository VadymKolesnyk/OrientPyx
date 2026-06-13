using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OrientDesk.Presentation.Controls;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class CompetitionDaysView : UserControl
{
    private CompetitionDaysViewModel? _vm;

    public CompetitionDaysView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press (it marks
        // PointerPressed handled), so Ctrl+Click on Delete can skip the confirmation prompt.
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as CompetitionDaysViewModel;
        if (_vm is null)
            return;

        // Column headers are baked into the band model at build time, so a language switch is
        // handled by rebuilding the bands.
        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // After the delete-confirmation modal closes, return keyboard focus to the table (on its new
    // selected row). Posted so it runs once the overlay has been torn down and the table is live again.
    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    private void BuildBands()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        Sheet.Bands = new SheetColumnBuilder(loc)
            // Day number: read-only, sorts by number. Styled stronger than a plain read-only cell.
            .Custom("CompetitionDays.Col.Day", BuildNumberCell, width: 120,
                    sortPath: nameof(DayRowViewModel.Number))
            .Date("CompetitionDays.Col.Date", nameof(DayRowViewModel.Date), width: 160, minWidth: 140)
            .Text("CompetitionDays.Col.Venue", nameof(DayRowViewModel.Venue),
                  editPath: nameof(DayRowViewModel.Venue), minWidth: 120)
            .Combo("CompetitionDays.Col.Discipline",
                   nameof(DayRowViewModel.DisciplineOptions),
                   nameof(DayRowViewModel.SelectedDiscipline),
                   nameof(DisciplineTypeOption.Label),
                   width: 180, minWidth: 160,
                   sortPath: $"{nameof(DayRowViewModel.SelectedDiscipline)}.Value")
            // Actions: Save / Change-number / Delete. A custom cell so the Save/ChangeNumber buttons
            // can bind to the VM's commands with the row as parameter.
            .Custom("CompetitionDays.Col.Actions", BuildActionsCell, minWidth: 220)
            .Bands;
    }

    private Control BuildNumberCell()
    {
        return new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
            FontWeight = FontWeight.Medium,
            Foreground = (IBrush?)Application.Current!.FindResource("TextPrimary"),
            [!TextBlock.TextProperty] = new Binding(nameof(DayRowViewModel.NumberLabel))
        };
    }

    private Control BuildActionsCell()
    {
        var save = new Button
        {
            Classes = { "ghost", "small" },
            Command = _vm!.SaveDayCommand,
            [!Button.ContentProperty] = new Binding("Localization[CompetitionDays.Save]"),
            [!Button.CommandParameterProperty] = new Binding(),
            [!InputElement.IsEnabledProperty] = new Binding(nameof(DayRowViewModel.IsDirty))
        };

        var changeNumber = new Button
        {
            Classes = { "ghost", "small" },
            Command = _vm.ChangeDayNumberCommand,
            [!Button.ContentProperty] = new Binding("Localization[CompetitionDays.ChangeNumber]"),
            [!Button.CommandParameterProperty] = new Binding(),
            [ToolTip.TipProperty] = _vm.Localization.Get("CompetitionDays.ChangeNumber.Hint")
        };

        // Click confirms before deleting; Ctrl+Click deletes immediately. Disabled for the active day.
        var delete = new Button
        {
            Classes = { "danger", "small" },
            [ToolTip.TipProperty] = _vm.Localization.Get("CompetitionDays.Delete"),
            [!InputElement.IsEnabledProperty] = new Binding(nameof(DayRowViewModel.IsActive))
            {
                Converter = Avalonia.Data.Converters.BoolConverters.Not
            },
            Content = new PathIcon
            {
                Data = Geometry.Parse("M6,7 h12 M9,7 v-2 h6 v2 M8,7 l1,13 h6 l1,-13"),
                Width = 15,
                Height = 15
            }
        };
        delete.Click += (_, _) =>
        {
            if (delete.DataContext is DayRowViewModel row)
                DeleteRow(row, _deleteCtrlDown);
            _deleteCtrlDown = false;
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
            Children = { save, changeNumber, delete }
        };
    }

    // The table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not DayRowViewModel row)
            return;
        DeleteRow(row, e.SkipConfirm);
    }

    // The per-row delete button. Button.Click doesn't carry key modifiers, and the button marks its
    // own PointerPressed handled — so we capture the Ctrl state in the tunnel phase. A plain click
    // confirms first; Ctrl+Click deletes immediately.
    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void DeleteRow(DayRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteDayNoConfirmAsync(row);
        else
            _ = _vm!.DeleteDayCommand.ExecuteAsync(row);
    }

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
