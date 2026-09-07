using Avalonia.Interactivity;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// Raised by a cell the user tried to edit while its day is closed. Bubbles to the
/// <see cref="SheetTable"/>, which forwards it to the page so it can explain the lock.
///
/// The day number travels with the event because a locked cell is not always the session's current
/// day: in the roster ("Мандатка") each column is a different day, and a merged (collapsed) cell
/// covers several at once — the explanation has to name the day that is actually holding it shut.
/// </summary>
public sealed class SheetLockedEditEventArgs(RoutedEvent routedEvent, int dayNumber, bool merged)
    : RoutedEventArgs(routedEvent)
{
    /// <summary>The closed day blocking the edit, or 0 when the caller only knows "the current day".</summary>
    public int DayNumber { get; } = dayNumber;

    /// <summary>
    /// True when the refusal came from a merged roster cell that writes to several days at once — the
    /// explanation then also says the block can be expanded to edit the open days one by one.
    /// </summary>
    public bool Merged { get; } = merged;
}
