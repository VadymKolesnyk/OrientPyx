using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Builds the protocol's page footer (нижній колонтитул) from the localized label parts. Shared by the results
/// and both start protocols so all three print the same footer. Returns <c>null</c> when no software name is
/// configured (so a caller that doesn't want a footer simply leaves the label blank). The actual page number
/// and generation timestamp are added by the renderer, not here — this only carries the localized captions.
/// </summary>
public static class ProtocolFooterFactory
{
    public static ProtocolFooter? Build(string softwareName, string generatedLabel, string pageLabel)
    {
        var name = (softwareName ?? string.Empty).Trim();
        if (name.Length == 0)
            return null;
        return new ProtocolFooter(name, (generatedLabel ?? string.Empty).Trim(), (pageLabel ?? string.Empty).Trim());
    }
}
