namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The outcome of inspecting a competition archive before importing it: the identifier (folder name)
/// the archive would import as, and whether a competition with that identifier already exists.
/// </summary>
public sealed record EventArchivePreview(string Identifier, bool IdentifierExists);
