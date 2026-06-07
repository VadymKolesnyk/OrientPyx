using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.BusinessLogic.Interfaces;

public interface IChipRentalService
{
    Task<IReadOnlyList<ChipRental>> GetRentalsAsync(CancellationToken cancellationToken = default);
}
