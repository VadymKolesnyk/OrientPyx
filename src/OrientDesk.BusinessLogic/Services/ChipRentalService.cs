using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Placeholder chip rental service returning a few in-memory samples.</summary>
public sealed class ChipRentalService : IChipRentalService
{
    public Task<IReadOnlyList<ChipRental>> GetRentalsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChipRental> rentals =
        [
            new() { ChipNumber = "8000123", IsReturned = false },
            new() { ChipNumber = "8000124", IsReturned = true }
        ];

        return Task.FromResult(rentals);
    }
}
