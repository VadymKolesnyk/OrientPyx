using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One row on the «Масовий старт» page: a group with a "take part" checkbox and its editable start time (every
/// runner of the group starts at it). A relay group also carries one restart cell per leg 2…N; the table has a
/// restart column per leg up to the day's longest relay, so <see cref="Restarts"/> is as long on every row and the
/// cells a group has no leg for are disabled.
/// </summary>
public sealed partial class MassStartDrawGroupRowViewModel : ObservableObject
{
    public MassStartDrawGroupRowViewModel(MassStartGroup group, string start, string currentStartLabel,
        IReadOnlyList<MassStartRestartCellViewModel> restarts)
    {
        Group = group;
        _start = start;
        CurrentStartLabel = currentStartLabel;
        Restarts = restarts;
        foreach (var cell in restarts)
            cell.Changed = () => SettingsChanged?.Invoke();
    }

    public MassStartGroup Group { get; }

    public Guid GroupId => Group.GroupId;
    public string Name => Group.Name;
    public bool IsRelay => Group.IsRelay;
    public int MemberCount => Group.MemberCount;

    public string CountLabel => $"×{MemberCount}";

    /// <summary>Relay: the number of legs; blank for any other group.</summary>
    public string LegCountLabel => IsRelay ? Group.LegCount.ToString() : string.Empty;

    /// <summary>The start the group's runners have now («10:00:00», «різні» or blank) — read-only.</summary>
    public string CurrentStartLabel { get; }

    /// <summary>The restart cells, one per leg 2…(the day's longest relay); see the class remarks.</summary>
    public IReadOnlyList<MassStartRestartCellViewModel> Restarts { get; }

    /// <summary>Whether this group gets the mass start when it is applied.</summary>
    [ObservableProperty]
    private bool _selected;

    /// <summary>Editable "hh:mm:ss" start of every runner of the group (relay: of leg 1).</summary>
    [ObservableProperty]
    private string _start;

    partial void OnSelectedChanged(bool value) => SettingsChanged?.Invoke();

    partial void OnStartChanged(string value) => SettingsChanged?.Invoke();

    /// <summary>Raised when an editable value changes so the page can persist the row onto the day.</summary>
    public Action? SettingsChanged;
}

/// <summary>
/// The restart time of one relay leg on a mass-start row. Disabled (and read as blank) when the row's group is not
/// a relay or has fewer legs than <see cref="Leg"/>; a paste onto a disabled cell is ignored.
/// </summary>
public sealed class MassStartRestartCellViewModel : ObservableObject
{
    private string _text;

    public MassStartRestartCellViewModel(int leg, bool isEnabled, string text)
    {
        Leg = leg;
        IsEnabled = isEnabled;
        _text = isEnabled ? text : string.Empty;
    }

    /// <summary>The leg this cell restarts (2…N).</summary>
    public int Leg { get; }

    public bool IsEnabled { get; }

    /// <summary>Editable "hh:mm:ss" restart time; blank = no restart for this leg.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!IsEnabled)
                return;
            if (SetProperty(ref _text, value ?? string.Empty))
                Changed?.Invoke();
        }
    }

    /// <summary>Raised when <see cref="Text"/> changes (wired by the row).</summary>
    public Action? Changed;
}
