namespace OrientPyx.BusinessLogic.Models;

/// <summary>The imported competition plus what its app-level data (points rules, ranks) changed locally.</summary>
public sealed record EventArchiveImportResult(EventSummary Competition, EventSharedDataImportResult SharedData);
