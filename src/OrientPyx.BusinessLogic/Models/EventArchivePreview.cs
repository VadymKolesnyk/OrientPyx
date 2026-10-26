namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The outcome of inspecting a competition archive before importing it: the identifier (folder name)
/// the archive would import as, and whether a competition with that identifier already exists.
/// </summary>
/// <param name="MissingPlugins">Plugins the archived competition uses that are not available here (not installed or
/// disabled); the import is still allowed — those disciplines then show «Потрібен плагін» (Р-2).</param>
public sealed record EventArchivePreview(string Identifier, bool IdentifierExists)
{
    public IReadOnlyList<RequiredPlugin> MissingPlugins { get; init; } = [];
}

/// <summary>A plugin a competition needs (from the archive manifest): its id, the version it was exported with,
/// and the discipline ids it provides there.</summary>
public sealed record RequiredPlugin(string Id, string Version, IReadOnlyList<string> Disciplines);
