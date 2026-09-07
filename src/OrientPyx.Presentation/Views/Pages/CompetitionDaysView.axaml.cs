using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

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
            // Each row IS a day, so the lock is per row (lockedDayPath) rather than table-wide: a closed
            // day's date/venue/discipline rest read-only while its neighbours stay editable. Changing a
            // finished day's discipline would recompute its results, which is exactly what the lock guards.
            .Date("CompetitionDays.Col.Date", nameof(DayRowViewModel.Date), width: 160, minWidth: 140,
                  lockedDayPath: nameof(DayRowViewModel.LockedDayNumber))
            .Text("CompetitionDays.Col.Venue", nameof(DayRowViewModel.Venue),
                  editPath: nameof(DayRowViewModel.Venue), minWidth: 120,
                  placeholderPath: nameof(DayRowViewModel.VenuePlaceholder),
                  lockedDayPath: nameof(DayRowViewModel.LockedDayNumber))
            .Combo("CompetitionDays.Col.Discipline",
                   nameof(DayRowViewModel.DisciplineOptions),
                   nameof(DayRowViewModel.SelectedDiscipline),
                   nameof(DisciplineTypeOption.Label),
                   width: 180, minWidth: 160,
                   sortPath: $"{nameof(DayRowViewModel.SelectedDiscipline)}.Value",
                   lockedDayPath: nameof(DayRowViewModel.LockedDayNumber))
            // Actions: Save / Change-number / Delete. A custom cell so the Save/ChangeNumber buttons
            // can bind to the VM's commands with the row as parameter.
            // Closed-for-editing lock. The days grid is the only place every day is visible at once, so
            // this is where the whole competition's state can be seen and set; the same lock also sits
            // next to the day selector on each per-day page.
            .Custom("DayLock.Col.Locked", BuildLockCell, width: 110)
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

    // A single toggle button showing the day's lock state: a closed padlock (tinted) when the day is
    // closed, an open one when it isn't. The VM confirms before opening a closed day.
    private Control BuildLockCell()
    {
        var icon = new Icon { Size = 15 };
        icon.Bind(Icon.KindProperty, new Binding(nameof(DayRowViewModel.IsLocked))
        {
            Converter = new FuncValueConverter<bool, string>(locked => locked ? "Lock" : "LockOpen")
        });
        icon.Bind(Icon.ForegroundProperty, new Binding(nameof(DayRowViewModel.IsLocked))
        {
            Converter = new FuncValueConverter<bool, IBrush?>(locked => locked
                ? (IBrush?)Application.Current!.FindResource("AccentBrush")
                : (IBrush?)Application.Current!.FindResource("TextMuted"))
        });

        return new Button
        {
            Classes = { "ghost", "small" },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Command = _vm!.ToggleDayLockCommand,
            Content = icon,
            [!Button.CommandParameterProperty] = new Binding(),
            [!ToolTip.TipProperty] = new Binding(nameof(DayRowViewModel.IsLocked))
            {
                Converter = new FuncValueConverter<bool, string>(locked => _vm.Localization.Get(
                    locked ? "DayLock.Unlock.Tooltip" : "DayLock.Lock.Tooltip"))
            }
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
            [!InputElement.IsEnabledProperty] = new Binding(nameof(DayRowViewModel.CanSave))
        };

        var changeNumber = new Button
        {
            Classes = { "ghost", "small" },
            Command = _vm.ChangeDayNumberCommand,
            [!Button.ContentProperty] = new Binding("Localization[CompetitionDays.ChangeNumber]"),
            [!Button.CommandParameterProperty] = new Binding(),
            [!InputElement.IsEnabledProperty] = new Binding(nameof(DayRowViewModel.CanEdit)),
            [ToolTip.TipProperty] = _vm.Localization.Get("CompetitionDays.ChangeNumber.Hint")
        };

        // Click confirms before deleting; Ctrl+Click deletes immediately. Off for the active day and
        // for a closed one.
        var delete = new Button
        {
            Classes = { "danger", "small" },
            [ToolTip.TipProperty] = _vm.Localization.Get("CompetitionDays.Delete"),
            [!InputElement.IsEnabledProperty] = new Binding(nameof(DayRowViewModel.CanDelete)),
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

    // A cell of a closed day refused to enter edit — say why (see GroupsView.OnLockedEditAttempted).
    private void OnLockedEditAttempted(object? sender, SheetLockedEditEventArgs e)
    {
        if (_vm is not null)
            _ = _vm.ExplainDayLockAsync(e.DayNumber, e.Merged);
    }

    private void DeleteRow(DayRowViewModel row, bool skipConfirm)
    {
        // The keyboard Delete reaches here too, and the table has no lock of its own on this page
        // (its rows are the days), so the row's own state is what decides.
        if (row.IsLocked)
        {
            _ = _vm!.ExplainDayLockAsync(row.Number);
            return;
        }

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
