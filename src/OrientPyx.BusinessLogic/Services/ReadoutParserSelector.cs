using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IReadoutParserSelector"/>: maps the app-level <see cref="ReadoutType"/> setting to
/// the concrete parser. Both parsers are injected so the choice is a simple lookup, and the current type
/// is read fresh each time so a Settings change applies without a restart.
/// </summary>
public sealed class ReadoutParserSelector : IReadoutParserSelector
{
    private readonly IAppSettingsService _settings;
    private readonly SportIdentCsvReadoutParser _sportIdent;
    private readonly SportTimeCsvReadoutParser _sportTime;

    public ReadoutParserSelector(
        IAppSettingsService settings,
        SportIdentCsvReadoutParser sportIdent,
        SportTimeCsvReadoutParser sportTime)
    {
        _settings = settings;
        _sportIdent = sportIdent;
        _sportTime = sportTime;
    }

    public async Task<IReadoutParser> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var type = await _settings.GetReadoutTypeAsync(cancellationToken);
        return For(type);
    }

    public IReadoutParser For(ReadoutType type) => type switch
    {
        ReadoutType.SportTime => _sportTime,
        _ => _sportIdent
    };
}
