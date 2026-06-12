using System.Collections.Generic;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// The set of rental-chip numbers for the current competition, shared by every chip-bearing row/cell
/// on the participants page so they can flag a chip that is NOT in the rental database (rendered bold
/// red). A single instance is held by the page and handed to each cell; mutating it (after a
/// double-click toggle) raises <see cref="Changed"/> so every realized chip cell re-evaluates its
/// highlight live, without a full reload.
/// </summary>
public sealed class RentalChipRegistry
{
    private HashSet<string> _numbers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the set of numbers changes (reset, add, or remove).</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces the whole set (e.g. after a reload) and notifies observers.</summary>
    public void Reset(IEnumerable<string> numbers)
    {
        _numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in numbers)
        {
            var trimmed = (n ?? string.Empty).Trim();
            if (trimmed.Length > 0)
                _numbers.Add(trimmed);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when <paramref name="chip"/> is a (non-blank) number present in the rental database.</summary>
    public bool Contains(string? chip)
    {
        var trimmed = (chip ?? string.Empty).Trim();
        return trimmed.Length > 0 && _numbers.Contains(trimmed);
    }

    /// <summary>
    /// True when <paramref name="chip"/> is a non-blank number NOT in the rental database — the cell
    /// highlights it. A blank chip is never flagged (nothing to rent).
    /// </summary>
    public bool IsNonRental(string? chip)
    {
        var trimmed = (chip ?? string.Empty).Trim();
        return trimmed.Length > 0 && !_numbers.Contains(trimmed);
    }

    /// <summary>Adds a number to the set (no-op when blank/already present) and notifies observers.</summary>
    public void Add(string? chip)
    {
        var trimmed = (chip ?? string.Empty).Trim();
        if (trimmed.Length > 0 && _numbers.Add(trimmed))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a number from the set (no-op when absent) and notifies observers.</summary>
    public void Remove(string? chip)
    {
        var trimmed = (chip ?? string.Empty).Trim();
        if (trimmed.Length > 0 && _numbers.Remove(trimmed))
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
