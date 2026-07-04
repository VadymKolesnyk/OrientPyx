using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Resolves the <see cref="IReadoutParser"/> to use for the app's currently-selected timing system
/// (<see cref="ReadoutType"/>, an application-level Settings choice). Consumers ask for the current
/// parser per read rather than taking a fixed parser at construction, so switching the setting takes
/// effect without a restart.
/// </summary>
public interface IReadoutParserSelector
{
    /// <summary>The parser for the currently-configured readout format.</summary>
    Task<IReadoutParser> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>The parser for a specific format.</summary>
    IReadoutParser For(ReadoutType type);
}
