using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The per-page day picker: the "Day N" dropdown plus the lock button that closes/opens that day for
/// editing. Every per-day page used to paste the same <see cref="SearchableComboBox"/> block into its
/// own header; they share this control instead, so the lock sits next to the day on every screen the
/// user works on rather than only on the days page.
///
/// The dropdown half is the same <see cref="SearchableComboBox"/> as before (search row past five
/// days), bound to the page's DayOptions/SelectedDay. The lock half is shown only when the page can
/// write to the day (<see cref="ShowLock"/>); read-only screens (protocols, monitor, publishing) still
/// show the day's state, but as a plain indicator with no click target.
/// </summary>
public sealed class DaySelector : Control
{
    private readonly SearchableComboBox _combo = new();
    private readonly Button _lock = new();
    private readonly Icon _lockIcon = new() { Kind = "LockOpen", Size = 14 };
    private StackPanel? _root;

    public static readonly StyledProperty<IEnumerable?> DayOptionsProperty =
        AvaloniaProperty.Register<DaySelector, IEnumerable?>(nameof(DayOptions));

    public static readonly StyledProperty<object?> SelectedDayProperty =
        AvaloniaProperty.Register<DaySelector, object?>(
            nameof(SelectedDay), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Hides the dropdown when the competition has a single day (matches the old ShowDaySelector).</summary>
    public static readonly StyledProperty<bool> ShowDaysProperty =
        AvaloniaProperty.Register<DaySelector, bool>(nameof(ShowDays), true);

    /// <summary>True on pages that write to the day, so the lock is clickable rather than a read-only mark.</summary>
    public static readonly StyledProperty<bool> ShowLockProperty =
        AvaloniaProperty.Register<DaySelector, bool>(nameof(ShowLock), true);

    /// <summary>Whether the selected day is currently closed for editing.</summary>
    public static readonly StyledProperty<bool> IsDayLockedProperty =
        AvaloniaProperty.Register<DaySelector, bool>(nameof(IsDayLocked));

    /// <summary>Invoked when the lock is clicked; the page decides (and confirms) the new state.</summary>
    public static readonly StyledProperty<ICommand?> ToggleLockCommandProperty =
        AvaloniaProperty.Register<DaySelector, ICommand?>(nameof(ToggleLockCommand));

    public static readonly StyledProperty<string> SearchWatermarkProperty =
        AvaloniaProperty.Register<DaySelector, string>(nameof(SearchWatermark), "Search");

    /// <summary>Tooltip shown on the lock while the day is open ("close the day").</summary>
    public static readonly StyledProperty<string> LockTooltipProperty =
        AvaloniaProperty.Register<DaySelector, string>(nameof(LockTooltip), string.Empty);

    /// <summary>Tooltip shown on the lock while the day is closed ("open the day").</summary>
    public static readonly StyledProperty<string> UnlockTooltipProperty =
        AvaloniaProperty.Register<DaySelector, string>(nameof(UnlockTooltip), string.Empty);

    public IEnumerable? DayOptions
    {
        get => GetValue(DayOptionsProperty);
        set => SetValue(DayOptionsProperty, value);
    }

    public object? SelectedDay
    {
        get => GetValue(SelectedDayProperty);
        set => SetValue(SelectedDayProperty, value);
    }

    public bool ShowDays
    {
        get => GetValue(ShowDaysProperty);
        set => SetValue(ShowDaysProperty, value);
    }

    public bool ShowLock
    {
        get => GetValue(ShowLockProperty);
        set => SetValue(ShowLockProperty, value);
    }

    public bool IsDayLocked
    {
        get => GetValue(IsDayLockedProperty);
        set => SetValue(IsDayLockedProperty, value);
    }

    public ICommand? ToggleLockCommand
    {
        get => GetValue(ToggleLockCommandProperty);
        set => SetValue(ToggleLockCommandProperty, value);
    }

    public string SearchWatermark
    {
        get => GetValue(SearchWatermarkProperty);
        set => SetValue(SearchWatermarkProperty, value);
    }

    public string LockTooltip
    {
        get => GetValue(LockTooltipProperty);
        set => SetValue(LockTooltipProperty, value);
    }

    public string UnlockTooltip
    {
        get => GetValue(UnlockTooltipProperty);
        set => SetValue(UnlockTooltipProperty, value);
    }

    public DaySelector()
    {
        _combo.MinWidth = 140;
        _combo.VerticalAlignment = VerticalAlignment.Center;
        _combo.ItemTemplate = new FuncDataTemplate<DayOption>((_, _) =>
        {
            var text = new TextBlock();
            text.Bind(TextBlock.TextProperty, new Binding(nameof(DayOption.Label)));
            return text;
        });

        _combo.Bind(ItemsControl.ItemsSourceProperty,
            new Binding(nameof(DayOptions)) { Source = this });
        _combo.Bind(SearchableComboBox.SearchWatermarkProperty,
            new Binding(nameof(SearchWatermark)) { Source = this });
        _combo.Bind(SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(SelectedDay)) { Source = this, Mode = BindingMode.TwoWay });
        _combo.Bind(IsVisibleProperty, new Binding(nameof(ShowDays)) { Source = this });

        _lock.Classes.Add("ghost");
        _lock.Padding = new Thickness(6, 4);
        _lock.VerticalAlignment = VerticalAlignment.Center;
        _lock.Content = _lockIcon;
        _lock.Bind(Button.CommandProperty,
            new Binding(nameof(ToggleLockCommand)) { Source = this });

        BuildTree();
        UpdateLockVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsDayLockedProperty || change.Property == ShowLockProperty ||
            change.Property == LockTooltipProperty || change.Property == UnlockTooltipProperty ||
            change.Property == ShowDaysProperty)
        {
            UpdateLockVisual();
        }
    }

    // A closed day has to stay obvious, so the lock is tinted when locked and quiet when open.
    private void UpdateLockVisual()
    {
        _lockIcon.Kind = IsDayLocked ? "Lock" : "LockOpen";
        _lock.IsVisible = ShowLock || IsDayLocked;
        // Read-only pages show the state but must not offer the toggle.
        _lock.IsEnabled = ShowLock;
        ToolTip.SetTip(_lock, ShowLock ? (IsDayLocked ? UnlockTooltip : LockTooltip) : UnlockTooltip);

        // A closed day blocks every edit on the page, so the whole button goes red (see Button.locked
        // in App.axaml) — loud enough to explain why the grid stopped taking input.
        _lock.Classes.Set("locked", IsDayLocked);
        if (IsDayLocked && this.TryFindResource("DangerBrush", ActualThemeVariant, out var danger) && danger is IBrush brush)
            _lockIcon.Foreground = brush;
        else
            _lockIcon.ClearValue(Icon.ForegroundProperty);

        // A single-day competition hides the dropdown, but a closed day still shows its lock.
        IsVisible = ShowDays || IsDayLocked;
    }

    private void BuildTree()
    {
        _root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _root.Children.Add(_combo);
        _root.Children.Add(_lock);

        ((ISetLogicalParent)_root).SetParent(this);
        VisualChildren.Add(_root);
        LogicalChildren.Add(_root);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_root is null)
            return default;

        _root.Measure(availableSize);
        return _root.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
